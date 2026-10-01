using AltiumWorkspaceMCP.Soap.Vault;

namespace AltiumWorkspaceMCP.Vault;

/// <summary>What happened to a component on change.</summary>
public sealed record RevisionChangeResult(
    string ItemGuid,
    string Hrid,
    string PreviousRevisionId,
    string NewRevisionId,
    string NewRevisionGuid,
    bool CreatedNewRevision,
    int ChangedParameters,
    int CarriedLinks,
    IReadOnlyList<string> ChangedLinks)
{
    /// <summary>
    /// What was appended by Altium rules beyond the requested edit: numbers of typed
    /// parameters, footprint data, vault GUIDs on links.
    /// </summary>
    public IReadOnlyList<string> Corrections { get; init; } = [];
}

/// <summary>
/// A component content change that follows the Altium versioning rules.
/// </summary>
/// <remarks>
/// The server forbids editing a released revision, so a change turns into a
/// new revision: it inherits from the previous one, gets its parameters and links with
/// the edits made and is released. Carrying the links over is mandatory — without them the component
/// in Altium is left without a symbol and footprint. An unreleased revision is edited
/// in place and released, so extra revisions are not multiplied.
///
/// Everything written is brought to the form in which Single Component
/// Editor saves a component: a typed parameter has its number next to the value, links have
/// a role, a vault GUID and footprint data. Otherwise Altium will not show the footprint,
/// and on a manual save of the component it will clear the values without a number.
/// </remarks>
public sealed class RevisionService
{
    private readonly VaultGateway _gateway;
    private readonly VaultScriptExecutor _scripts;
    private readonly LinkNormalizer _links;
    private readonly ParameterTypeResolver _types;

    public RevisionService(
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

    /// <summary>A revision is considered released if the server set the release date.</summary>
    public static bool IsReleased(ALU_ItemRevision revision) => revision.ReleaseDate != default;

    /// <summary>Applies a change to a component, creating a new revision if needed.</summary>
    public async Task<RevisionChangeResult> ApplyAsync(
        ALU_ItemRevision revision,
        string hrid,
        ComponentChange change,
        string releaseNote,
        CancellationToken cancellationToken)
    {
        int changedParameters = CountEffectiveChanges(revision, change);
        var newTypes = await _types.ResolveNewAsync(revision, change.Parameters.Keys, cancellationToken);

        return IsReleased(revision)
            ? await CreateReleasedRevisionAsync(revision, hrid, change, newTypes, releaseNote, changedParameters, cancellationToken)
            : await EditInPlaceAsync(revision, hrid, change, newTypes, releaseNote, changedParameters, cancellationToken);
    }

    /// <summary>
    /// Edits an unreleased revision directly and releases it.
    /// </summary>
    /// <remarks>
    /// The release is mandatory: an unreleased revision becomes the active one for the item,
    /// and the component looks unfinished in Altium. Such a revision remains, in particular,
    /// after an interrupted change — this path brings it to a consistent state.
    ///
    /// The set of links is computed in memory: the release script sets the revision's links as a whole,
    /// an empty list erases the existing ones, and it rejects the old identifiers as
    /// duplicates. So the old links are deleted and their copies go into the script.
    /// </remarks>
    private async Task<RevisionChangeResult> EditInPlaceAsync(
        ALU_ItemRevision revision,
        string hrid,
        ComponentChange change,
        IReadOnlyDictionary<string, ParameterTypeInfo> newTypes,
        string releaseNote,
        int changedParameters,
        CancellationToken cancellationToken)
    {
        var corrections = new List<string>();
        ApplyToRevision(revision, change, newTypes, corrections);

        var existing = await LoadLinksAsync(revision.GUID, cancellationToken);

        // Roles are set before matching with assignments: a link with an empty role
        // written by an older Altium would otherwise not be found and would be duplicated.
        corrections.AddRange((await _links.RestoreRolesAsync(existing, cancellationToken)).Select(fix => fix.Text));

        var inherited = await LoadInheritedLinksAsync(revision, existing, change.Links, cancellationToken);
        var (links, changedLinks) = BuildLinks(existing.Concat(inherited).ToList(), change, revision.GUID);

        // Normalization goes before deleting the old links: a failure at this step must not
        // leave the revision without links.
        var linkFixes = await _links.NormalizeAsync(links, cancellationToken);
        corrections.AddRange(linkFixes.Select(fix => fix.Text));

        await _gateway.UpdateItemRevisionsAsync(
            [revision],
            VaultRequestOptions.Of(VaultRequestOptions.SupportDeleteRevisionParameters),
            cancellationToken);

        if (existing.Count > 0)
        {
            await _gateway.DeleteItemRevisionLinksAsync(
                existing.Select(link => link.GUID).ToList(), cancellationToken);
        }

        await _scripts.ReleaseRevisionAsync(revision, links, releaseNote, cancellationToken);

        return new RevisionChangeResult(
            ItemGuid: revision.ItemGUID,
            Hrid: hrid,
            PreviousRevisionId: revision.RevisionId ?? string.Empty,
            NewRevisionId: revision.RevisionId ?? string.Empty,
            NewRevisionGuid: revision.GUID,
            CreatedNewRevision: false,
            ChangedParameters: changedParameters,
            CarriedLinks: links.Count,
            ChangedLinks: changedLinks)
        {
            Corrections = corrections,
        };
    }

    /// <summary>Creates the next revision and releases it with the links by one script.</summary>
    private async Task<RevisionChangeResult> CreateReleasedRevisionAsync(
        ALU_ItemRevision revision,
        string hrid,
        ComponentChange change,
        IReadOnlyDictionary<string, ParameterTypeInfo> newTypes,
        string releaseNote,
        int changedParameters,
        CancellationToken cancellationToken)
    {
        string nextRevisionId = NextRevisionId(revision);
        string newGuid = NewGuid();

        var corrections = new List<string>();

        var sourceLinks = await LoadLinksAsync(revision.GUID, cancellationToken);
        corrections.AddRange((await _links.RestoreRolesAsync(sourceLinks, cancellationToken)).Select(fix => fix.Text));
        var (links, changedLinks) = BuildLinks(sourceLinks, change, newGuid);

        ALU_ItemRevision next = CreateNextRevision(revision, change, nextRevisionId, newGuid, newTypes, corrections);

        var linkFixes = await _links.NormalizeAsync(links, cancellationToken);
        corrections.AddRange(linkFixes.Select(fix => fix.Text));

        // Creation and release are one atomic group of the script: there is no intermediate
        // state in which the revision is created but not released. The links are created by the same
        // script; a separate creation would lead to the refusal "Duplicate Item Revision Link GUID".
        await _scripts.ReleaseRevisionsAsync([new RevisionRelease(next, links)], [next], releaseNote, cancellationToken);

        return new RevisionChangeResult(
            ItemGuid: revision.ItemGUID,
            Hrid: hrid,
            PreviousRevisionId: revision.RevisionId ?? string.Empty,
            NewRevisionId: nextRevisionId,
            NewRevisionGuid: newGuid,
            CreatedNewRevision: true,
            ChangedParameters: changedParameters,
            CarriedLinks: links.Count,
            ChangedLinks: changedLinks)
        {
            Corrections = corrections,
        };
    }

    /// <summary>
    /// Builds the next revision from the current one without calling the server.
    /// Extracted so that the batch edit uses the same code.
    /// </summary>
    /// <param name="newTypes">Types of parameters the source revision does not have.</param>
    /// <param name="corrections">Where to write the corrections beyond the requested edit.</param>
    internal static ALU_ItemRevision CreateNextRevision(
        ALU_ItemRevision source,
        ComponentChange change,
        string nextRevisionId,
        string newGuid,
        IReadOnlyDictionary<string, ParameterTypeInfo> newTypes,
        List<string>? corrections) =>
        new()
        {
            GUID = newGuid,
            ItemGUID = source.ItemGUID,
            // Altium releases a new revision without a reference to the previous one (seen in the requests Altium Designer sends);
            // only the completion of links of an interrupted revision depends on the ancestor (LoadInheritedLinksAsync),
            // while creation and release are now one atomic group — no interrupted revisions remain.
            AncestorItemRevisionGUID = string.Empty,
            RevisionId = nextRevisionId,
            RevisionIdLevels = SplitLevels(nextRevisionId),
            RevisionIdSeparators = source.RevisionIdSeparators ?? new _StringList(),
            ContentTypeGUID = source.ContentTypeGUID,
            FolderGUID = source.FolderGUID,
            LifeCycleStateGUID = source.LifeCycleStateGUID,
            Description = change.Description ?? source.Description,
            Comment = change.Comment ?? source.Comment,
            ItemHRID = source.ItemHRID,
            ItemDescription = source.ItemDescription,
            IsVisible = true,
            IsApplicable = true,
            IsActive = true,
            RevisionParameters = MergeParameters(
                source, change.Parameters, change.DeleteParameters, newGuid, reuseGuids: false, newTypes, corrections),
        };

    /// <summary>Applies the change to the revision itself — for the in-place edit case.</summary>
    internal static void ApplyToRevision(
        ALU_ItemRevision revision,
        ComponentChange change,
        IReadOnlyDictionary<string, ParameterTypeInfo> newTypes,
        List<string>? corrections)
    {
        revision.RevisionParameters = MergeParameters(
            revision, change.Parameters, change.DeleteParameters, revision.GUID, reuseGuids: true, newTypes, corrections);

        if (change.Description is not null)
        {
            revision.Description = change.Description;
        }

        if (change.Comment is not null)
        {
            revision.Comment = change.Comment;
        }
    }

    /// <summary>
    /// Prepares the set of links for a revision: copies the old ones, moves links to the new
    /// model revisions and applies replacements by role.
    /// </summary>
    internal static (List<ALU_ItemRevisionLink> Links, List<string> Changed) BuildLinks(
        IReadOnlyList<ALU_ItemRevisionLink> source,
        ComponentChange change,
        string targetRevisionGuid)
    {
        var links = source.Select(link => CopyLink(link, targetRevisionGuid)).ToList();
        var changed = new List<string>();

        // Link data (the footprint number and so on) is kept on a move:
        // only the model revision the link points to changes.
        foreach (LinkRetarget retarget in change.Retargets)
        {
            foreach (ALU_ItemRevisionLink link in links.Where(link =>
                         retarget.FromRevisionGuids.Contains(link.ChildItemRevisionGUID ?? string.Empty)
                         && !string.Equals(link.ChildItemRevisionGUID, retarget.ToRevisionGuid, StringComparison.OrdinalIgnoreCase)))
            {
                link.ChildItemRevisionGUID = retarget.ToRevisionGuid;
                changed.Add($"{LinkRole.Describe(link.HRID)}: moved to {retarget.Label}");
            }
        }

        ValidateFootprintAssignments(change.Links);

        foreach (LinkAssignment assignment in change.Links)
        {
            string role = LinkRole.Normalize(assignment.Role);

            // Footprints are separate logic: a part may have several of them (FootprintLinks).
            if (role == LinkRole.Footprint || role == LinkRole.Footprints)
            {
                if (FootprintLinks.Apply(links, assignment, targetRevisionGuid) is { } description)
                {
                    changed.Add(description);
                }

                continue;
            }

            ALU_ItemRevisionLink? existing = links
                .FirstOrDefault(link => string.Equals(link.HRID, role, StringComparison.OrdinalIgnoreCase));

            if (string.IsNullOrWhiteSpace(assignment.TargetRevisionGuid))
            {
                if (existing is not null)
                {
                    links.Remove(existing);
                    changed.Add($"{LinkRole.Describe(role)}: link removed");
                }

                continue;
            }

            if (existing is not null)
            {
                if (!string.Equals(existing.ChildItemRevisionGUID, assignment.TargetRevisionGuid, StringComparison.OrdinalIgnoreCase))
                {
                    changed.Add(
                        $"{LinkRole.Describe(role)}: {existing.ChildItemRevisionGUID} → {assignment.TargetRevisionGuid}");
                    existing.ChildItemRevisionGUID = assignment.TargetRevisionGuid;
                }
            }
            else
            {
                links.Add(new ALU_ItemRevisionLink
                {
                    GUID = string.Empty,
                    HRID = role,
                    ParentItemRevisionGUID = targetRevisionGuid,
                    ChildItemRevisionGUID = assignment.TargetRevisionGuid,
                });
                changed.Add($"{LinkRole.Describe(role)}: link added to {assignment.TargetRevisionGuid}");
            }
        }

        return (links, changed);
    }

    /// <summary>
    /// Footprint roles in one edit must not contradict each other: the set (footprints) replaces
    /// all footprints, the single footprint role — one, and "PCBLIB n" is not assigned directly.
    /// </summary>
    internal static void ValidateFootprintAssignments(IReadOnlyList<LinkAssignment> assignments)
    {
        var roles = assignments.Select(assignment => LinkRole.Normalize(assignment.Role)).ToList();

        if (roles.Contains(LinkRole.Footprints) && roles.Contains(LinkRole.Footprint))
        {
            throw new InvalidOperationException(
                "The footprint and footprints roles cannot be assigned together: footprints sets the whole footprint set "
                + "(the first is primary), footprint — only the primary one. Keep one of the roles.");
        }

        if (roles.Count(role => role == LinkRole.Footprints) > 1)
        {
            throw new InvalidOperationException(
                "The footprints role is given several times: the whole footprint set is set by one targets list.");
        }

        string? direct = roles.FirstOrDefault(role => role != LinkRole.Footprint && LinkRole.IsFootprint(role));

        if (direct is not null)
        {
            throw new InvalidOperationException(
                $"The role '{direct}' is not assigned directly: the number of an additional footprint is issued in order. "
                + "Set the whole set with the footprints role and a targets list (the first is primary).");
        }
    }

    /// <summary>
    /// Links of the previous revision that the given one does not have.
    /// </summary>
    /// <remarks>
    /// An unreleased revision could have lost some links after an interrupted edit.
    /// Matching goes by the child revision: it determines which model
    /// is linked. Roles that the current change sets are not carried over from the ancestor.
    /// </remarks>
    private async Task<List<ALU_ItemRevisionLink>> LoadInheritedLinksAsync(
        ALU_ItemRevision revision,
        IReadOnlyList<ALU_ItemRevisionLink> existing,
        IReadOnlyList<LinkAssignment> assignments,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(revision.AncestorItemRevisionGUID))
        {
            return [];
        }

        var source = await LoadLinksAsync(revision.AncestorItemRevisionGUID, cancellationToken);
        await _links.RestoreRolesAsync(source, cancellationToken);

        var overridden = assignments
            .Select(assignment => LinkRole.Normalize(assignment.Role))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // A footprint set sets all "PCBLIB" and "PCBLIB n" at once; adding one (Append) cancels nothing.
        bool setsFootprints = overridden.Contains(LinkRole.Footprints);
        bool addsFootprint = assignments.Any(assignment =>
            assignment.Append && LinkRole.Normalize(assignment.Role) == LinkRole.Footprint);

        if (addsFootprint)
        {
            overridden.Remove(LinkRole.Footprint);
        }

        var alreadyLinked = existing
            .Select(link => link.ChildItemRevisionGUID)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return source
            .Where(link => !alreadyLinked.Contains(link.ChildItemRevisionGUID))
            .Where(link => !overridden.Contains(link.HRID ?? string.Empty))
            .Where(link => !(setsFootprints && LinkRole.IsFootprint(link.HRID)))
            .ToList();
    }

    private Task<List<ALU_ItemRevisionLink>> LoadLinksAsync(
        string revisionGuid,
        CancellationToken cancellationToken) =>
        _gateway.GetItemRevisionLinksAsync(
            VaultFilter.Equal("ParentItemRevisionGUID", revisionGuid),
            cancellationToken: cancellationToken);

    /// <summary>
    /// A copy of a link for another parent revision. The HRID identifier is mandatory:
    /// it holds the link role, and without it the server rejects it with a column validation error.
    /// </summary>
    internal static ALU_ItemRevisionLink CopyLinkFor(ALU_ItemRevisionLink source, string parentRevisionGuid) =>
        CopyLink(source, parentRevisionGuid);

    private static ALU_ItemRevisionLink CopyLink(ALU_ItemRevisionLink source, string parentRevisionGuid)
    {
        // The link GUID is empty, as in Altium: the server assigns it. Its own GUID is not needed, and
        // reusing another's gave "Duplicate Item Revision Link GUID".
        return new ALU_ItemRevisionLink
        {
            GUID = string.Empty,
            HRID = string.IsNullOrEmpty(source.HRID) ? NewGuid() : source.HRID,
            ParentItemRevisionGUID = parentRevisionGuid,
            ChildItemRevisionGUID = source.ChildItemRevisionGUID,
            ChildVaultGUID = source.ChildVaultGUID,
            ParentVaultGUID = source.ParentVaultGUID,
            LinkTypeGUID = source.LinkTypeGUID,
            Data = source.Data,
            LinkParameters = source.LinkParameters,
        };
    }

    /// <summary>
    /// Copies the revision parameters applying the changes and gives each its number
    /// in the Altium format.
    /// </summary>
    /// <param name="reuseGuids">
    /// On an in-place edit parameter identifiers are kept, on creating a new
    /// revision each parameter needs its own.
    /// </param>
    internal static _ALU_ItemRevisionParameterList MergeParameters(
        ALU_ItemRevision source,
        IReadOnlyDictionary<string, string> changes,
        IReadOnlySet<string> deleteParameters,
        string targetRevisionGuid,
        bool reuseGuids,
        IReadOnlyDictionary<string, ParameterTypeInfo> newTypes,
        List<string>? corrections)
    {
        var conflicts = deleteParameters.Where(changes.ContainsKey).ToList();
        if (conflicts.Count > 0)
        {
            throw new InvalidOperationException(
                "A parameter cannot be both set and deleted in one edit: "
                + string.Join(", ", conflicts) + ".");
        }

        var result = new _ALU_ItemRevisionParameterList();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (ALU_ItemRevisionParameter parameter in source.RevisionParameters ?? [])
        {
            string name = parameter.HRID ?? string.Empty;

            if (deleteParameters.Contains(name))
            {
                seen.Add(name);
                corrections?.Add($"'{name}': parameter removed");
                continue;
            }

            string? type = parameter.ParameterTypeGUID;

            string? value = changes.TryGetValue(name, out string? updated)
                ? ParameterValueCodec.NormalizeDisplay(updated, type)
                : parameter.ParameterValue;

            string? real = ParameterValueCodec.RealValueForWrite(name, value, type, parameter);

            // The value is the same, but the number was missing or stale: this is a correction, not an edit.
            if (!ParameterValueCodec.IsText(type)
                && !string.IsNullOrEmpty(real)
                && string.Equals(value, parameter.ParameterValue, StringComparison.Ordinal)
                && !string.Equals(real, parameter.ParameterRealValue, StringComparison.Ordinal))
            {
                corrections?.Add($"'{name}' = '{value}': number {real} appended");
            }

            result.Add(new ALU_ItemRevisionParameter
            {
                GUID = reuseGuids ? parameter.GUID : NewGuid(),
                HRID = name,
                ParameterValue = value,
                ParameterRealValue = real,
                ParameterTypeGUID = type,
                ItemRevisionGUID = targetRevisionGuid,
            });

            seen.Add(name);
        }

        foreach ((string name, string value) in changes.Where(change => !seen.Contains(change.Key)))
        {
            ParameterTypeInfo info = newTypes.TryGetValue(name, out ParameterTypeInfo? known)
                ? known
                : new ParameterTypeInfo(ParameterValueCodec.TextTypeGuid, null);

            string display = ParameterValueCodec.NormalizeDisplay(value, info.TypeGuid);
            string real = ParameterValueCodec.RealValueFor(display, info.TypeGuid)
                ?? (ParameterValueCodec.IsKnown(info.TypeGuid)
                    ? throw new InvalidOperationException(
                        ParameterValueCodec.Explain(name, display, info.TypeGuid, info.Example))
                    : string.Empty);

            result.Add(new ALU_ItemRevisionParameter
            {
                GUID = NewGuid(),
                HRID = name,
                ParameterValue = display,
                ParameterRealValue = real,
                ParameterTypeGUID = info.TypeGuid,
                ItemRevisionGUID = targetRevisionGuid,
            });
        }

        return result;
    }

    /// <summary>How many edits really change data — matching values do not count.</summary>
    private static int CountEffectiveChanges(ALU_ItemRevision revision, ComponentChange change)
    {
        var current = ComponentService.ExtractParameters(revision);

        int count = change.Parameters.Count(parameter =>
            !current.TryGetValue(parameter.Key, out string? existing)
            || !string.Equals(existing, parameter.Value, StringComparison.Ordinal));

        count += change.DeleteParameters.Count(current.ContainsKey);

        if (change.Description is not null
            && !string.Equals(change.Description, revision.Description, StringComparison.Ordinal))
        {
            count++;
        }

        if (change.Comment is not null
            && !string.Equals(change.Comment, revision.Comment, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>
    /// The next revision number: the lowest level is incremented, the higher ones are kept.
    /// Leading zeros keep the original width.
    /// </summary>
    public static string NextRevisionId(ALU_ItemRevision revision)
    {
        var levels = (revision.RevisionIdLevels ?? []).Where(level => !string.IsNullOrEmpty(level)).ToList();

        if (levels.Count == 0)
        {
            levels = (revision.RevisionId ?? string.Empty)
                .Split('-', StringSplitOptions.RemoveEmptyEntries)
                .ToList();
        }

        if (levels.Count == 0)
        {
            return "1";
        }

        string last = levels[^1];
        if (!int.TryParse(last, out int numeric))
        {
            throw new InvalidOperationException(
                $"Could not compute the next revision number: level '{last}' is not numeric. "
                + "Letter numbering schemes are not supported — create the revision in Altium.");
        }

        levels[^1] = (numeric + 1).ToString(new string('0', last.Length));

        string separator = revision.RevisionIdSeparators?.FirstOrDefault() ?? "-";
        return string.Join(separator, levels);
    }

    private static _StringList SplitLevels(string revisionId)
    {
        var list = new _StringList();
        list.AddRange(revisionId.Split('-', StringSplitOptions.RemoveEmptyEntries));
        return list;
    }

    private static string NewGuid() => Guid.NewGuid().ToString("D").ToUpperInvariant();
}
