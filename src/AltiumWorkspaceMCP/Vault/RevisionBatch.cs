using AltiumWorkspaceMCP.Soap.Vault;

namespace AltiumWorkspaceMCP.Vault;

/// <summary>One edit within a batch.</summary>
public sealed record BatchEdit(ALU_ItemRevision Revision, string Hrid, ComponentChange Change);

/// <summary>Result of a batch edit.</summary>
public sealed record BatchResult(
    IReadOnlyList<RevisionChangeResult> Applied,
    IReadOnlyList<(string Hrid, string Error)> Failures);

/// <summary>
/// Batch change of components.
/// </summary>
/// <remarks>
/// A one-by-one edit costs about a second per component: reading links, creating
/// a revision and releasing it are three server calls. On two thousand components
/// that adds up to about half an hour, and the call does not fit the time given to the client.
///
/// Here the same actions are done in batches: the links of all revisions are read by one
/// request, and all revisions of a portion are created and released by one script.
/// The number of calls stops depending on the number of components and is determined only
/// by the portion size.
///
/// An empty edit makes sense too: the new revision gets the same values, but with the parameter numbers
/// and links in the Altium format — this is how components written earlier without them
/// are repaired.
/// </remarks>
public sealed class RevisionBatch
{
    /// <summary>
    /// How many components are processed per portion.
    /// </summary>
    /// <remarks>
    /// The portion is limited not by the protocol but by a reasonable size of one transaction:
    /// the release script is applied in full, and on a refusal the whole portion is rolled back.
    /// Two hundred components give a gain from batch processing and at the same time leave
    /// a clear unit of rollback.
    /// </remarks>
    public const int DefaultChunkSize = 200;

    private readonly VaultGateway _gateway;
    private readonly VaultScriptExecutor _scripts;
    private readonly LinkNormalizer _links;
    private readonly ParameterTypeResolver _types;

    public RevisionBatch(
        VaultGateway gateway,
        VaultScriptExecutor scripts,
        LinkNormalizer links,
        ParameterTypeResolver types)
    {
        _gateway = gateway;
        _scripts = scripts;
        _links = links;
        _types = types;
    }

    /// <summary>Applies the edits in batches and returns the result for each component.</summary>
    public async Task<BatchResult> ApplyAsync(
        IReadOnlyList<BatchEdit> edits,
        string releaseNote,
        int chunkSize,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        var applied = new List<RevisionChangeResult>(edits.Count);
        var failures = new List<(string, string)>();

        foreach (BatchEdit[] chunk in edits.Chunk(Math.Max(1, chunkSize)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            int failuresBefore = failures.Count;

            try
            {
                applied.AddRange(await ApplyChunkAsync(chunk, releaseNote, failures, cancellationToken));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A portion is applied in full, so a server refusal applies to all its edits,
                // except those already rejected during preparation.
                var rejected = failures
                    .Skip(failuresBefore)
                    .Select(failure => failure.Item1)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                foreach (BatchEdit edit in chunk.Where(edit => !rejected.Contains(edit.Hrid)))
                {
                    failures.Add((edit.Hrid, exception.Message));
                }
            }

            progress?.Report(applied.Count + failures.Count);
        }

        return new BatchResult(applied, failures);
    }

    private sealed record Prepared(RevisionRelease Release, RevisionChangeResult Result, List<string> Corrections);

    private async Task<IReadOnlyList<RevisionChangeResult>> ApplyChunkAsync(
        IReadOnlyList<BatchEdit> chunk,
        string releaseNote,
        List<(string, string)> failures,
        CancellationToken cancellationToken)
    {
        // The links of all affected revisions are read by one request instead of a request per component.
        var linksByRevision = await LoadLinksAsync(
            chunk.Select(edit => edit.Revision.GUID), cancellationToken);

        // Roles are set before matching with assignments: a link with an empty role
        // written by an older Altium would otherwise not be found by role.
        var roleFixes = (await _links.RestoreRolesAsync(
                linksByRevision.Values.SelectMany(links => links).ToList(), cancellationToken))
            .GroupBy(fix => fix.RevisionGuid, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(fix => fix.Text).ToList(), StringComparer.OrdinalIgnoreCase);

        // The types of new parameters are determined once per portion.
        var newTypes = await _types.ResolveAsync(
            chunk.SelectMany(edit => edit.Change.Parameters.Keys
                .Where(name => ParameterTypeResolver.Find(edit.Revision, name) is null)),
            cancellationToken);

        var newRevisions = new List<ALU_ItemRevision>();
        var updatedRevisions = new List<ALU_ItemRevision>();
        var linksToDrop = new List<string>();
        var prepared = new List<Prepared>(chunk.Count);

        foreach (BatchEdit edit in chunk)
        {
            // A bad value of one component must not break the whole portion:
            // such a component is rejected with an explanation, the others go on.
            try
            {
                prepared.Add(Prepare(edit, linksByRevision, roleFixes, newTypes, newRevisions, updatedRevisions, linksToDrop));
            }
            catch (InvalidOperationException exception)
            {
                failures.Add((edit.Hrid, exception is FootprintSetException footprintSet
                    ? footprintSet.DescribeFor(edit.Hrid)
                    : exception.Message));
            }
        }

        if (prepared.Count == 0)
        {
            return [];
        }

        // Normalization goes before deleting the old links: its failure must not leave
        // the revision without links.
        var linkFixes = await _links.NormalizeAsync(
            prepared.SelectMany(item => item.Release.Links).ToList(), cancellationToken);

        foreach (var group in linkFixes.GroupBy(fix => fix.RevisionGuid, StringComparer.OrdinalIgnoreCase))
        {
            prepared
                .FirstOrDefault(item => string.Equals(item.Release.Revision.GUID, group.Key, StringComparison.OrdinalIgnoreCase))
                ?.Corrections.AddRange(group.Select(fix => fix.Text));
        }

        if (updatedRevisions.Count > 0)
        {
            await _gateway.UpdateItemRevisionsAsync(
                updatedRevisions,
                VaultRequestOptions.Of(VaultRequestOptions.SupportDeleteRevisionParameters),
                cancellationToken);
        }

        if (linksToDrop.Count > 0)
        {
            await _gateway.DeleteItemRevisionLinksAsync(linksToDrop, cancellationToken);
        }

        // Creation of new revisions goes into the same script as the release: one
        // server call instead of two and one transaction instead of two.
        await _scripts.ReleaseRevisionsAsync(
            prepared.Select(item => item.Release).ToList(), newRevisions, releaseNote, cancellationToken);

        return prepared
            .Select(item => item.Result with { Corrections = item.Corrections })
            .ToList();
    }

    /// <summary>Prepares the revision and links of one edit without calling the server.</summary>
    private static Prepared Prepare(
        BatchEdit edit,
        IReadOnlyDictionary<string, List<ALU_ItemRevisionLink>> linksByRevision,
        IReadOnlyDictionary<string, List<string>> roleFixes,
        IReadOnlyDictionary<string, ParameterTypeInfo> newTypes,
        List<ALU_ItemRevision> newRevisions,
        List<ALU_ItemRevision> updatedRevisions,
        List<string> linksToDrop)
    {
        ALU_ItemRevision source = edit.Revision;
        var sourceLinks = linksByRevision.GetValueOrDefault(source.GUID, []);
        var corrections = new List<string>(roleFixes.GetValueOrDefault(source.GUID, []));

        if (RevisionService.IsReleased(source))
        {
            string nextId = RevisionService.NextRevisionId(source);
            string newGuid = NewGuid();

            ALU_ItemRevision next = RevisionService.CreateNextRevision(
                source, edit.Change, nextId, newGuid, newTypes, corrections);
            var (links, changedLinks) = RevisionService.BuildLinks(sourceLinks, edit.Change, newGuid);

            newRevisions.Add(next);

            return new Prepared(
                new RevisionRelease(next, links),
                new RevisionChangeResult(
                    ItemGuid: source.ItemGUID,
                    Hrid: edit.Hrid,
                    PreviousRevisionId: source.RevisionId ?? string.Empty,
                    NewRevisionId: nextId,
                    NewRevisionGuid: newGuid,
                    CreatedNewRevision: true,
                    ChangedParameters: edit.Change.Parameters.Count,
                    CarriedLinks: links.Count,
                    ChangedLinks: changedLinks),
                corrections);
        }

        // An unreleased revision is edited in place. The release script recreates its links
        // as a whole: an empty list erases the existing ones, and a list with the old
        // identifiers is rejected as a duplicate. So the old links are deleted,
        // and their copies with new identifiers go into the script.
        RevisionService.ApplyToRevision(source, edit.Change, newTypes, corrections);
        var (reissued, changed) = RevisionService.BuildLinks(sourceLinks, edit.Change, source.GUID);

        updatedRevisions.Add(source);
        linksToDrop.AddRange(sourceLinks.Select(link => link.GUID));

        return new Prepared(
            new RevisionRelease(source, reissued),
            new RevisionChangeResult(
                ItemGuid: source.ItemGUID,
                Hrid: edit.Hrid,
                PreviousRevisionId: source.RevisionId ?? string.Empty,
                NewRevisionId: source.RevisionId ?? string.Empty,
                NewRevisionGuid: source.GUID,
                CreatedNewRevision: false,
                ChangedParameters: edit.Change.Parameters.Count,
                CarriedLinks: reissued.Count,
                ChangedLinks: changed),
            corrections);
    }

    /// <summary>Links of all the given revisions, grouped by parent revision.</summary>
    private async Task<Dictionary<string, List<ALU_ItemRevisionLink>>> LoadLinksAsync(
        IEnumerable<string> revisionGuids,
        CancellationToken cancellationToken)
    {
        var links = await VaultGateway.ReadInChunksAsync(
            revisionGuids.Distinct(StringComparer.OrdinalIgnoreCase),
            chunk => _gateway.GetItemRevisionLinksAsync(
                VaultFilter.In("ParentItemRevisionGUID", chunk),
                limit: 100000,
                cancellationToken: cancellationToken));

        return links
            .GroupBy(link => link.ParentItemRevisionGUID ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
    }

    private static string NewGuid() => Guid.NewGuid().ToString("D").ToUpperInvariant();
}
