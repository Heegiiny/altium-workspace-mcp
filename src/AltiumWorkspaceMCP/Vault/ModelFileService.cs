using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AltiumWorkspaceMCP.Configuration;
using AltiumWorkspaceMCP.Safety;
using AltiumWorkspaceMCP.Soap.Vault;

namespace AltiumWorkspaceMCP.Vault;

/// <summary>A model file exported from the vault to the exchange directory.</summary>
public sealed record ModelFile(
    string Role,
    string ItemHrid,
    string RevisionId,
    string? LatestRevisionId,
    string RevisionGuid,
    string? ContentType,
    string Path,
    string? AgentPath,
    long Size);

/// <summary>An export already in the exchange directory.</summary>
public sealed record ExchangeEntry(
    string Item,
    string? Revision,
    string? Role,
    string? LinkedFrom,
    DateTimeOffset? DownloadedAt,
    IReadOnlyList<ExchangeFile> Files);

/// <summary>An exported file and a flag that it was edited after the export.</summary>
public sealed record ExchangeFile(string Path, string? AgentPath, long Size, bool ModifiedAfterDownload);

/// <summary>Result of exporting the models of one object.</summary>
public sealed record ModelDownload(
    string Source,
    IReadOnlyList<ModelFile> Files,
    IReadOnlyList<string> Notes);

/// <summary>Which components to move to the new model revision after a file upload.</summary>
public enum RelinkScope
{
    /// <summary>None: components stay on the old revisions.</summary>
    None,

    /// <summary>The component the model was exported for.</summary>
    Source,

    /// <summary>All components whose active revisions reference the old model revisions.</summary>
    All,
}

/// <summary>Moving components to the new model revision.</summary>
/// <param name="StillOnOlderRevisions">Components that still reference the old revisions.</param>
/// <param name="AwaitingConfirmation">
/// Components whose move exceeds the guarded-mode threshold: they are not moved and wait for
/// confirmation by token. Empty if no confirmation is needed.
/// </param>
public sealed record RelinkResult(
    IReadOnlyList<RevisionChangeResult> Relinked,
    IReadOnlyList<(string Hrid, string Error)> Failures,
    IReadOnlyList<string> StillOnOlderRevisions,
    IReadOnlyList<string>? AwaitingConfirmation = null,
    IReadOnlyList<string>? Folders = null);

/// <summary>Result of uploading an edited model file.</summary>
public sealed record ModelUpload(
    string Item,
    string PreviousRevision,
    string NewRevision,
    string NewRevisionGuid,
    string FileName,
    long Size,
    RelinkResult Relink,
    IReadOnlyList<string> Notes);

/// <summary>Result of moving components to the active model revision.</summary>
public sealed record ModelRelink(string Item, string Revision, RelinkResult Relink, IReadOnlyList<string> Notes);

/// <summary>
/// Symbol and footprint files: export to the exchange directory and upload of the edited
/// file back — together with altium-designer-mcp this is the full model editing cycle.
/// </summary>
/// <remarks>
/// A symbol and a footprint are separate vault items, and their content lies in the
/// revision payload: a ZIP archive with the file <c>Released/*.SchLib</c> or
/// <c>Released/*.PcbLib</c> and preview images. The files are exported to the exchange
/// directory, where altium-designer-mcp opens them, and <c>vault-source.json</c> is put next to them
/// with the origin of the file. By it the edited file returns to the same item as a new
/// revision, after which the components are moved to it: a component link points to
/// a specific model revision and will not update by itself.
/// </remarks>
public sealed partial class ModelFileService
{
    /// <summary>Name of the file with the export origin.</summary>
    public const string ManifestName = "vault-source.json";

    private const string Operation = "vault_model_files";

    /// <summary>Operation of moving components to a new model revision: a confirmation token is issued under it.</summary>
    public const string RelinkOperation = "vault_model_files:relink";

    /// <summary>Operation of moving parts to the active template revision: a confirmation token is issued under it.</summary>
    public const string TemplateRelinkOperation = "vault_template:relink";

    private static readonly Dictionary<string, string> RolesByContentType = new(StringComparer.OrdinalIgnoreCase)
    {
        ["altium-symbol"] = LinkRole.Symbol,
        ["altium-pcb-component"] = LinkRole.Footprint,
        ["altium-simulation-model"] = LinkRole.Simulation,
    };

    /// <summary>
    /// Parameters that describe the old content and are not carried into the new revision:
    /// it has no preview images — only Altium can draw them — and the hash
    /// belongs to the old file.
    /// </summary>
    private static readonly HashSet<string> ContentParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        "Available Preview Images",
        "altium.hash",
    };

    private static readonly JsonSerializerOptions ManifestJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly VaultGateway _gateway;
    private readonly ComponentService _components;
    private readonly ExchangePaths _exchange;
    private readonly HttpClient _http;
    private readonly VaultScriptExecutor _scripts;
    private readonly RevisionBatch _batch;
    private readonly LinkNormalizer _links;
    private readonly ChangeGuard _guard;
    private readonly VaultOptions _options;
    private readonly VaultCatalog _catalog;

    public ModelFileService(
        VaultGateway gateway,
        ComponentService components,
        ExchangePaths exchange,
        HttpClient http,
        VaultScriptExecutor scripts,
        RevisionBatch batch,
        LinkNormalizer links,
        ChangeGuard guard,
        VaultOptions options,
        VaultCatalog catalog)
    {
        _gateway = gateway;
        _components = components;
        _exchange = exchange;
        _http = http;
        _scripts = scripts;
        _batch = batch;
        _links = links;
        _guard = guard;
        _options = options;
        _catalog = catalog;
    }

    /// <summary>
    /// Exports the models of an object: for a component — the symbol and footprint linked
    /// to it, for a model itself — its active revision.
    /// </summary>
    /// <param name="roles">Which roles to export for a component; empty — all models.</param>
    public async Task<ModelDownload> DownloadAsync(
        string identifier,
        IReadOnlyCollection<string> roles,
        CancellationToken cancellationToken)
    {
        var found = await _components.ReadByIdsAsync([identifier], cancellationToken);
        ComponentRecord record = found.Count == 1
            ? found[0]
            : throw new InvalidOperationException(await _components.DescribeMissingAsync([identifier], cancellationToken));

        var notes = new List<string>();
        var targets = new List<(string Role, string RevisionGuid)>();

        if (record.ContentType is not null && RolesByContentType.TryGetValue(record.ContentType, out string? ownRole))
        {
            targets.Add((ownRole, record.RevisionGuid
                ?? throw new InvalidOperationException($"{record.Hrid} has no revision.")));
        }
        else
        {
            var wanted = roles.Select(LinkRole.Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var links = await _gateway.GetItemRevisionLinksAsync(
                VaultFilter.Equal("ParentItemRevisionGUID", record.RevisionGuid ?? string.Empty),
                cancellationToken: cancellationToken);

            // Links written by an older Altium may have no role: it is visible from the model type.
            await _links.RestoreRolesAsync(links, cancellationToken);

            foreach (ALU_ItemRevisionLink link in links)
            {
                // Additional footprints ('PCBLIB 1'…) are the same models, and in roles the footprint role selects them.
                bool isModel = RolesByContentType.Values.Contains(link.HRID ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    || LinkRole.IsFootprint(link.HRID);
                bool isWanted = wanted.Count == 0
                    || wanted.Contains(link.HRID!)
                    || (LinkRole.IsFootprint(link.HRID) && (wanted.Contains(LinkRole.Footprint) || wanted.Contains(LinkRole.Footprints)));

                if (isModel && isWanted && !string.IsNullOrEmpty(link.ChildItemRevisionGUID))
                {
                    targets.Add((link.HRID!, link.ChildItemRevisionGUID));
                }
            }

            if (targets.Count == 0)
            {
                notes.Add($"{record.Hrid} has no linked models"
                    + (wanted.Count > 0 ? $" with roles {string.Join(", ", wanted)}" : string.Empty) + ".");
            }
        }

        var files = new List<ModelFile>();

        foreach (var (role, revisionGuid) in targets)
        {
            files.AddRange(await DownloadRevisionAsync(record, role, revisionGuid, notes, cancellationToken));
        }

        return new ModelDownload(record.Hrid, files, notes);
    }

    /// <summary>
    /// Releases the edited file as a new revision of the model it was exported from,
    /// and moves the components to it.
    /// </summary>
    /// <param name="file">Path to the file — in agent or server paths.</param>
    /// <param name="force">Release even if the model got a new revision after the export.</param>
    public async Task<ModelUpload> UploadAsync(
        string file,
        RelinkScope relink,
        bool force,
        string releaseNote,
        CancellationToken cancellationToken)
    {
        string path = _exchange.ToWindowsPath(file);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"File '{file}' not found (server path: {path}).");
        }

        string directory = Path.GetDirectoryName(path)!;
        ModelManifest manifest = await ReadManifestAsync(Path.Combine(directory, ManifestName), cancellationToken);

        string fileName = Path.GetFileName(path);
        var knownExtensions = manifest.Files.Select(Path.GetExtension).ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (knownExtensions.Count > 0 && !knownExtensions.Contains(Path.GetExtension(fileName)))
        {
            throw new InvalidOperationException(
                $"'{fileName}' does not fit model {manifest.Item}: {string.Join(", ", manifest.Files)} was exported.");
        }

        var found = await _components.ReadByIdsAsync([manifest.ItemGuid], cancellationToken);
        ComponentRecord model = found.Count == 1
            ? found[0]
            : throw new InvalidOperationException($"Model {manifest.Item} not found in the vault.");

        ALU_ItemRevision current = model.LatestRevision
            ?? throw new InvalidOperationException($"Model {model.Hrid} has no revision.");

        var notes = new List<string>();

        if (!string.Equals(current.GUID, manifest.RevisionGuid, StringComparison.OrdinalIgnoreCase))
        {
            if (!force)
            {
                throw new InvalidOperationException(
                    $"{model.Hrid} changed after the export: rev. {manifest.Revision} was exported, "
                    + $"but the active one is rev. {current.RevisionId}. Export the model again and carry over the edits "
                    + "or pass force=true to release this file over it.");
            }

            notes.Add($"The file was released over rev. {current.RevisionId}, although rev. {manifest.Revision} was exported.");
        }

        if (!manifest.Files.Contains(fileName, StringComparer.OrdinalIgnoreCase) && manifest.Files.Count > 0)
        {
            notes.Add($"The file name changed: {string.Join(", ", manifest.Files)} → {fileName}.");
        }

        _guard.EnsureAllowed(Operation, 1, [model.FolderPath]);

        string nextId = RevisionService.NextRevisionId(current);
        string newGuid = NewGuid();

        ALU_ItemRevision next = RevisionService.CreateNextRevision(
            current, new ComponentChange(), nextId, newGuid, new Dictionary<string, ParameterTypeInfo>(), corrections: null);

        // Altium releases model revisions without a reference to the previous one.
        next.AncestorItemRevisionGUID = string.Empty;
        next.RevisionParameters.RemoveAll(parameter => ContentParameters.Contains(parameter.HRID ?? string.Empty));

        var sourceLinks = await _gateway.GetItemRevisionLinksAsync(
            VaultFilter.Equal("ParentItemRevisionGUID", current.GUID), cancellationToken: cancellationToken);
        var (links, _) = RevisionService.BuildLinks(sourceLinks, new ComponentChange(), newGuid);
        await _links.NormalizeAsync(links, cancellationToken);

        await _scripts.ReleaseRevisionsAsync(
            [new RevisionRelease(next, links, [new ReleaseFile(fileName, path)])],
            [next],
            releaseNote,
            cancellationToken);

        if (!DryRun.IsActive)
        {
            await WriteManifestAsync(directory, manifest with
            {
                Revision = nextId,
                RevisionGuid = newGuid,
                DownloadedAt = DateTimeOffset.Now,
                UploadedAt = DateTimeOffset.Now,
                Files = [fileName],
            }, cancellationToken);
        }

        // Components reference a specific model revision and will not move to the new one by themselves.
        bool sourceMissing = false;

        RelinkResult relinked = await RelinkUsersAsync(
            model.ItemGuid,
            model.Hrid,
            RelinkOperation,
            newGuid,
            nextId,
            users =>
            {
                IReadOnlyList<ComponentRecord> selected = relink switch
                {
                    RelinkScope.All => users,
                    RelinkScope.Source => users
                        .Where(user => string.Equals(user.Hrid, manifest.LinkedFrom, StringComparison.OrdinalIgnoreCase))
                        .ToList(),
                    _ => new List<ComponentRecord>(),
                };

                sourceMissing = relink == RelinkScope.Source && selected.Count == 0;
                return selected;
            },
            releaseNote,
            notes,
            approved: null,
            cancellationToken);

        if (sourceMissing)
        {
            notes.Add(manifest.LinkedFrom is null
                ? "The model was exported by itself, not from a component, so components were not moved."
                : $"{manifest.LinkedFrom} no longer references the old revisions of {model.Hrid} — nothing to move.");
        }

        return new ModelUpload(
            Item: model.Hrid,
            PreviousRevision: current.RevisionId ?? string.Empty,
            NewRevision: nextId,
            NewRevisionGuid: newGuid,
            FileName: fileName,
            Size: new FileInfo(path).Length,
            Relink: relinked,
            Notes: notes);
    }

    /// <summary>
    /// Moves components to the active model revision — for example, if the model
    /// was uploaded without a move or a new revision was released in Altium itself.
    /// </summary>
    /// <param name="components">Which components to move; empty — all on old revisions of the model.</param>
    /// <param name="approved">
    /// How many components are confirmed by token; empty — no confirmation, and a move above
    /// the guarded-mode threshold is returned in <see cref="RelinkResult.AwaitingConfirmation"/>.
    /// </param>
    public async Task<ModelRelink> RelinkAsync(
        string modelIdentifier,
        IReadOnlyCollection<string> components,
        string releaseNote,
        int? approved,
        CancellationToken cancellationToken)
    {
        var found = await _components.ReadByIdsAsync([modelIdentifier], cancellationToken);
        ComponentRecord model = found.Count == 1
            ? found[0]
            : throw new InvalidOperationException($"Model '{modelIdentifier}' not found in the vault.");

        if (model.ContentType is null || !RolesByContentType.ContainsKey(model.ContentType))
        {
            throw new InvalidOperationException(
                $"{model.Hrid} is not a symbol or a footprint ({model.ContentType}): there is nothing to move to it.");
        }

        ALU_ItemRevision latest = model.LatestRevision
            ?? throw new InvalidOperationException($"Model {model.Hrid} has no revision.");

        var notes = new List<string>();
        RelinkResult result = await RelinkUsersAsync(
            model.ItemGuid,
            model.Hrid,
            RelinkOperation,
            latest.GUID,
            latest.RevisionId ?? string.Empty,
            users =>
            {
                (var selected, var absent) = TemplateRelinkPlan.Select(users, components);

                if (absent.Count > 0)
                {
                    notes.Add($"Not referencing old revisions of {model.Hrid}: {string.Join(", ", absent)}.");
                }

                return selected;
            },
            releaseNote,
            notes,
            approved,
            cancellationToken);

        return new ModelRelink(model.Hrid, latest.RevisionId ?? string.Empty, result, notes);
    }

    /// <summary>
    /// Parts whose active revisions reference a template revision other than <paramref name="currentRevisionGuid"/> —
    /// those lagging behind the template. For a dry run of a template edit, the GUID of the future
    /// revision is passed in <paramref name="currentRevisionGuid"/>: then everyone referencing the template lags.
    /// </summary>
    public async Task<IReadOnlyList<ComponentRecord>> FindLaggingTemplateUsersAsync(
        string templateItemGuid,
        string currentRevisionGuid,
        CancellationToken cancellationToken) =>
        await FindCurrentUsersAsync(
            await OlderRevisionsAsync(templateItemGuid, currentRevisionGuid, cancellationToken), cancellationToken);

    /// <summary>
    /// Moves parts to the template revision <paramref name="targetRevisionGuid"/> in one batch (<see cref="RevisionBatch"/>).
    /// </summary>
    /// <param name="components">Which parts to move; empty — all lagging.</param>
    /// <param name="approved">How many parts are confirmed by token; empty — no confirmation.</param>
    public async Task<ModelRelink> RelinkTemplateUsersAsync(
        ComponentTemplate template,
        string targetRevisionGuid,
        string targetRevisionId,
        IReadOnlyCollection<string> components,
        string releaseNote,
        int? approved,
        CancellationToken cancellationToken)
    {
        var notes = new List<string>();

        RelinkResult result = await RelinkUsersAsync(
            template.ItemGuid,
            template.Hrid,
            TemplateRelinkOperation,
            targetRevisionGuid,
            targetRevisionId,
            users =>
            {
                (var selected, var absent) = TemplateRelinkPlan.Select(users, components);

                if (absent.Count > 0)
                {
                    notes.Add($"Not lagging behind {template.Hrid} (already on the active revision or not referencing it): {string.Join(", ", absent)}.");
                }

                return selected;
            },
            releaseNote,
            notes,
            approved,
            cancellationToken);

        return new ModelRelink(template.Hrid, targetRevisionId, result, notes);
    }

    private async Task<HashSet<string>> OlderRevisionsAsync(
        string itemGuid,
        string targetRevisionGuid,
        CancellationToken cancellationToken)
    {
        var history = await _gateway.GetItemRevisionsAsync(
            VaultFilter.Equal("ItemGUID", itemGuid), limit: 100000, cancellationToken: cancellationToken);

        return history
            .Select(revision => revision.GUID)
            .Where(guid => !string.Equals(guid, targetRevisionGuid, StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Finds components on old model revisions and moves the chosen ones to the given revision.</summary>
    private async Task<RelinkResult> RelinkUsersAsync(
        string itemGuid,
        string itemHrid,
        string operation,
        string targetRevisionGuid,
        string targetRevisionId,
        Func<IReadOnlyList<ComponentRecord>, IReadOnlyList<ComponentRecord>> select,
        string releaseNote,
        List<string> notes,
        int? approved,
        CancellationToken cancellationToken)
    {
        var olderRevisions = await OlderRevisionsAsync(itemGuid, targetRevisionGuid, cancellationToken);

        var found = await FindCurrentUsersAsync(olderRevisions, cancellationToken);

        // A template that references a model is not moved: a release through RevisionBatch would wipe its .cmpt.
        var skippedTemplates = found
            .Where(user => string.Equals(user.ContentType, TemplateService.ContentTypeName, StringComparison.OrdinalIgnoreCase))
            .Select(user => user.Hrid)
            .ToList();

        if (skippedTemplates.Count > 0)
        {
            notes.Add($"Templates ({string.Join(", ", skippedTemplates)}) were not moved: releasing a revision without the .cmpt file would wipe the "
                + "template's type and default folder.");
        }

        var users = found.Where(user => !skippedTemplates.Contains(user.Hrid, StringComparer.OrdinalIgnoreCase)).ToList();
        var wanted = select(users);
        var now = wanted.Take(_options.MaxWriteBatch).ToList();

        if (wanted.Count > now.Count)
        {
            notes.Add($"At most {_options.MaxWriteBatch} components are moved per call — "
                + "a repeated relink will move the rest.");
        }

        BatchResult batch = new([], []);
        List<string>? awaiting = null;

        if (now.Count > 0)
        {
            var folders = now.Select(user => user.FolderPath).Distinct().ToList();
            bool requiresConfirmation = _guard.RequiresConfirmation(operation, now.Count, folders);

            if (approved is { } confirmed && now.Count > confirmed)
            {
                throw new ChangeRejectedException(
                    $"The move now affects {now.Count} components, but {confirmed} were confirmed: "
                    + "the data changed after the preview. Repeat relink without confirmToken.");
            }

            if (approved is not null || !requiresConfirmation)
            {
                var retarget = new LinkRetarget(olderRevisions, targetRevisionGuid, $"{itemHrid} rev. {targetRevisionId}");

                batch = await _batch.ApplyAsync(
                    now.Select(user => new BatchEdit(
                        user.LatestRevision!, user.Hrid, new ComponentChange { Retargets = [retarget] })).ToList(),
                    releaseNote,
                    RevisionBatch.DefaultChunkSize,
                    progress: null,
                    cancellationToken);
            }
            else
            {
                awaiting = now.Select(user => user.Hrid).ToList();
            }
        }

        var relinked = batch.Applied.Select(applied => applied.Hrid).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new RelinkResult(
            batch.Applied,
            batch.Failures,
            users.Select(user => user.Hrid).Where(hrid => !relinked.Contains(hrid)).ToList(),
            awaiting,
            now.Select(user => user.FolderPath).Distinct().ToList());
    }

    /// <summary>Exports lying in the exchange directory, with a mark for files changed after the export.</summary>
    public async Task<IReadOnlyList<ExchangeEntry>> ListAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_exchange.Root))
        {
            return [];
        }

        var entries = new List<ExchangeEntry>();

        foreach (string manifestPath in Directory.EnumerateFiles(_exchange.Root, ManifestName, SearchOption.AllDirectories))
        {
            string directory = Path.GetDirectoryName(manifestPath)!;
            ModelManifest manifest = await ReadManifestAsync(manifestPath, cancellationToken);

            var files = new List<ExchangeFile>();

            foreach (string name in manifest.Files)
            {
                string path = Path.GetFullPath(Path.Combine(directory, name));

                if (!File.Exists(path))
                {
                    continue;
                }

                var info = new FileInfo(path);

                // An unpacked file keeps the time from the archive, so a later
                // modification time means an edit after the export.
                bool modified = info.LastWriteTimeUtc > manifest.DownloadedAt.UtcDateTime.AddSeconds(2);

                files.Add(new ExchangeFile(path, _exchange.ToAgentPath(path), info.Length, modified));
            }

            entries.Add(new ExchangeEntry(
                manifest.Item, manifest.Revision, manifest.Role, manifest.LinkedFrom, manifest.DownloadedAt, files));
        }

        return entries.OrderBy(entry => entry.Item, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task<IReadOnlyList<ModelFile>> DownloadRevisionAsync(
        ComponentRecord owner,
        string role,
        string revisionGuid,
        List<string> notes,
        CancellationToken cancellationToken)
    {
        var revisions = await _gateway.GetItemRevisionsAsync(
            VaultFilter.Equal("GUID", revisionGuid), limit: 1, cancellationToken: cancellationToken);

        ALU_ItemRevision revision = revisions.Count == 1
            ? revisions[0]
            : throw new InvalidOperationException($"Model revision {revisionGuid} not found.");

        var models = await _components.ReadByIdsAsync([revision.ItemGUID], cancellationToken);
        ComponentRecord? model = models.Count == 1 ? models[0] : null;
        string itemHrid = model?.Hrid ?? revision.ItemHRID ?? revision.ItemGUID;

        if (model?.RevisionId is { } latest && !string.Equals(latest, revision.RevisionId, StringComparison.OrdinalIgnoreCase))
        {
            notes.Add($"{owner.Hrid} references {itemHrid} rev. {revision.RevisionId}, but the active model revision is {latest}.");
        }

        var urls = await _gateway.GetRevisionDownloadUrlsAsync([revisionGuid], cancellationToken);
        string? url = urls.FirstOrDefault(candidate => !string.IsNullOrEmpty(candidate.URL))?.URL;

        if (url is null)
        {
            notes.Add($"{itemHrid} rev. {revision.RevisionId}: the server gave no download address.");
            return [];
        }

        using HttpResponseMessage response = await _http.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            notes.Add($"{itemHrid} rev. {revision.RevisionId}: the download returned HTTP {(int)response.StatusCode}.");
            return [];
        }

        byte[] package = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        string directory = Path.Combine(_exchange.Root, Safe(itemHrid), $"rev-{Safe(revision.RevisionId ?? "0")}");
        Directory.CreateDirectory(directory);

        var files = new List<ModelFile>();

        using (var archive = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read))
        {
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                const string released = "Released/";

                string name = entry.FullName.Replace('\\', '/');
                if (entry.Length == 0 || !name.StartsWith(released, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string relative = name[released.Length..];
                string target = Path.GetFullPath(Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar)));

                // The archive comes from the server, but it is not allowed to go outside the export directory.
                if (!target.StartsWith(Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);

                files.Add(new ModelFile(
                    Role: role,
                    ItemHrid: itemHrid,
                    RevisionId: revision.RevisionId ?? string.Empty,
                    LatestRevisionId: model?.RevisionId,
                    RevisionGuid: revisionGuid,
                    ContentType: model?.ContentType,
                    Path: target,
                    AgentPath: _exchange.ToAgentPath(target),
                    Size: entry.Length));
            }
        }

        if (files.Count == 0)
        {
            notes.Add($"{itemHrid} rev. {revision.RevisionId}: no library files in the payload.");
            return files;
        }

        await WriteManifestAsync(directory, new ModelManifest
        {
            Item = itemHrid,
            ItemGuid = revision.ItemGUID,
            Revision = revision.RevisionId,
            RevisionGuid = revisionGuid,
            ContentType = model?.ContentType,
            Role = role,
            LinkedFrom = string.Equals(owner.Hrid, itemHrid, StringComparison.OrdinalIgnoreCase) ? null : owner.Hrid,
            DownloadedAt = DateTimeOffset.Now,
            Files = files.Select(file => Path.GetRelativePath(directory, file.Path)).ToList(),
        }, cancellationToken);

        return files;
    }

    /// <summary>
    /// Components whose active revisions reference one of the given model revisions.
    /// Old component revisions do not count: their content is no longer in effect.
    /// </summary>
    private async Task<List<ComponentRecord>> FindCurrentUsersAsync(
        IReadOnlySet<string> modelRevisions,
        CancellationToken cancellationToken)
    {
        var links = await VaultGateway.ReadInChunksAsync(
            modelRevisions,
            chunk => _gateway.GetItemRevisionLinksAsync(
                VaultFilter.In("ChildItemRevisionGUID", chunk), limit: 100000, cancellationToken: cancellationToken));

        var parentRevisions = links
            .Select(link => link.ParentItemRevisionGUID)
            .Where(guid => !string.IsNullOrEmpty(guid))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (parentRevisions.Count == 0)
        {
            return [];
        }

        var parents = await VaultGateway.ReadInChunksAsync(
            parentRevisions,
            chunk => _gateway.GetItemRevisionsAsync(
                VaultFilter.In("GUID", chunk), limit: 100000, cancellationToken: cancellationToken));

        var records = await _components.ReadByIdsAsync(
            parents.Select(revision => revision.ItemGUID).Where(guid => !string.IsNullOrEmpty(guid)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            cancellationToken);

        return records
            .Where(record => record.RevisionGuid is not null && parentRevisions.Contains(record.RevisionGuid))
            .OrderBy(record => record.Hrid, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Origin of the export; by it the file returns to its own item.</summary>
    private sealed record ModelManifest
    {
        public required string Item { get; init; }

        public required string ItemGuid { get; init; }

        public string? Revision { get; init; }

        public required string RevisionGuid { get; init; }

        public string? ContentType { get; init; }

        public string? Role { get; init; }

        public string? LinkedFrom { get; init; }

        public DateTimeOffset DownloadedAt { get; init; }

        public DateTimeOffset? UploadedAt { get; init; }

        public IReadOnlyList<string> Files { get; init; } = [];
    }

    private static async Task<ModelManifest> ReadManifestAsync(string manifestPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(manifestPath))
        {
            throw new InvalidOperationException(
                $"There is no {ManifestName} next to the file. Only a file exported by "
                + "vault_model_files download can be uploaded: this description shows which model it returns to.");
        }

        await using FileStream stream = File.OpenRead(manifestPath);

        return await JsonSerializer.DeserializeAsync<ModelManifest>(stream, ManifestJson, cancellationToken)
            ?? throw new InvalidOperationException($"{manifestPath} is empty.");
    }

    private static Task WriteManifestAsync(string directory, ModelManifest manifest, CancellationToken cancellationToken) =>
        File.WriteAllTextAsync(
            Path.Combine(directory, ManifestName),
            JsonSerializer.Serialize(manifest, ManifestJson),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken);

    /// <summary>Name of a directory without characters that are invalid in Windows and Linux paths.</summary>
    private static string Safe(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().Append('/').Append('\\').ToHashSet();
        return new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
    }

    private static string NewGuid() => Guid.NewGuid().ToString("D").ToUpperInvariant();
}
