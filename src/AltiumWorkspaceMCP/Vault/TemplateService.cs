using AltiumWorkspaceMCP.Soap.Vault;

namespace AltiumWorkspaceMCP.Vault;

/// <summary>Component template: the layout and component type for new parts.</summary>
public sealed record ComponentTemplate(
    string Hrid,
    string ItemGuid,
    string RevisionGuid,
    string RevisionId,
    string FolderPath,
    string? Description,
    string? ComponentTypeGuid,
    IReadOnlyDictionary<string, string> Parameters,
    string? Comment = null);

/// <summary>Result of releasing a template with a corrected <c>.cmpt</c> file.</summary>
/// <param name="Files">All package files carried into the new revision.</param>
/// <param name="CmptBefore">Text of <c>.cmpt</c> of the active revision.</param>
/// <param name="CmptAfter">Text of <c>.cmpt</c> of the new revision.</param>
public sealed record TemplateRelease(
    string Hrid,
    string PreviousRevision,
    string NewRevision,
    string NewRevisionGuid,
    string? FileName,
    IReadOnlyList<string> Files,
    string? PreviousType,
    string? PreviousDefaultFolder,
    string? CmptBefore,
    string? CmptAfter,
    string? PreviousComment = null,
    string? PreviousDescription = null,
    string? PreviousSymbolItemGuid = null,
    string? PreviousFootprintItemGuid = null);

/// <summary>What to take as its own when creating a template from a sample; <see langword="null"/> — leave as in the sample.</summary>
/// <param name="BasedOn">Sample template: identifier CMPT-… or GUID.</param>
/// <param name="SymbolItemGuid">Item GUID of the default symbol; an empty string removes the sample's link.</param>
/// <param name="FootprintItemGuid">Item GUID of the default footprint; an empty string removes the sample's link.</param>
public sealed record TemplateSample(
    string BasedOn,
    string? TypeGuid,
    string? DefaultFolderGuid,
    string? NamingTemplate,
    string? SymbolItemGuid = null,
    string? FootprintItemGuid = null);

/// <summary>Result of creating a template.</summary>
/// <param name="BasedOn">The sample (<see langword="null"/> — the template was created without a file).</param>
/// <param name="FileName">The <c>.cmpt</c> file of the new revision.</param>
/// <param name="SkippedFiles">Other files of the sample's package — they are not carried over.</param>
/// <param name="CmptBefore">Text of <c>.cmpt</c> of the sample.</param>
/// <param name="CmptAfter">Text of <c>.cmpt</c> of the new template.</param>
public sealed record TemplateCreation(
    ComponentTemplate Template,
    string? BasedOn,
    string? FileName,
    IReadOnlyList<string> SkippedFiles,
    string? CmptBefore,
    string? CmptAfter);

/// <summary>Component type of a template from both sources.</summary>
/// <param name="TypeGuid">The resulting type: the .cmpt file, or the revision parameter if there is none.</param>
/// <param name="FromFile">The type from the .cmpt file — what Altium reads.</param>
/// <param name="FromParameter">The type from the revision parameter ComponentTypeGuid.</param>
/// <param name="Disagree">The file and the parameter name different types.</param>
public sealed record TemplateType(string? TypeGuid, string? FromFile, string? FromParameter, bool Disagree);

/// <summary>
/// Working with component templates — the third coordinate of a part in Altium along with
/// the folder and the component type.
/// </summary>
/// <remarks>
/// A template is an ordinary vault item with the content type
/// <c>altium-component-template</c>. It is linked to a component through a revision link with the
/// role <c>ComponentTemplate</c>, and it sets the component type with its parameter
/// <c>ComponentTypeGuid</c>. A folder keeps the default template in its own
/// parameters: <c>TemplateItemGUID</c>, <c>TemplateRevisionGUID</c> and
/// <c>TemplateVaultGUID</c>. A new component created in Altium inside such a folder
/// gets this template automatically.
/// </remarks>
public sealed class TemplateService
{
    /// <summary>Content type of component templates.</summary>
    internal const string ContentTypeName = "altium-component-template";
    private const string ComponentTypeParameter = "ComponentTypeGuid";

    private const string TemplateItemParameter = "TemplateItemGUID";
    private const string TemplateRevisionParameter = "TemplateRevisionGUID";
    private const string TemplateVaultParameter = "TemplateVaultGUID";

    private static readonly IReadOnlySet<string> NoDeletes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private readonly VaultGateway _gateway;
    private readonly VaultCatalog _catalog;
    private readonly ComponentService _components;
    private readonly VaultScriptExecutor _scripts;
    private readonly ParameterTypeResolver _types;
    private readonly LinkNormalizer _links;
    private readonly HttpClient _http;

    /// <summary>Settings from .cmpt by template revision: a released revision is immutable, so the cache is safe.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TemplateSettings?> _settings =
        new(StringComparer.OrdinalIgnoreCase);

    public TemplateService(
        VaultGateway gateway,
        VaultCatalog catalog,
        ComponentService components,
        VaultScriptExecutor scripts,
        ParameterTypeResolver types,
        LinkNormalizer links,
        HttpClient http)
    {
        _gateway = gateway;
        _catalog = catalog;
        _components = components;
        _scripts = scripts;
        _types = types;
        _links = links;
        _http = http;
    }

    /// <summary>
    /// Template settings from the <c>.cmpt</c> file of its revision — what Altium Designer reads.
    /// <see langword="null"/> — the file could not be downloaded or the package has none.
    /// </summary>
    public async Task<TemplateSettings?> ReadSettingsAsync(string revisionGuid, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(revisionGuid))
        {
            return null;
        }

        if (_settings.TryGetValue(revisionGuid, out TemplateSettings? cached))
        {
            return cached;
        }

        TemplateSettings? settings = null;

        try
        {
            byte[]? package = await DownloadPackageAsync(revisionGuid, cancellationToken);

            if (package is not null)
            {
                settings = TemplateSettings.FromPackage(package);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or System.Text.Json.JsonException)
        {
            // No file or it is damaged — the fallback source of the type: the revision parameter.
        }

        return _settings[revisionGuid] = settings;
    }

    /// <summary>ZIP package of a released revision; <see langword="null"/> — the server gave no address or refused.</summary>
    private async Task<byte[]?> DownloadPackageAsync(string revisionGuid, CancellationToken cancellationToken)
    {
        var urls = await _gateway.GetRevisionDownloadUrlsAsync([revisionGuid], cancellationToken);
        string? url = urls.FirstOrDefault(candidate => !string.IsNullOrEmpty(candidate.URL))?.URL;

        if (url is null)
        {
            return null;
        }

        using HttpResponseMessage response = await _http.GetAsync(url, cancellationToken);

        return response.IsSuccessStatusCode
            ? await response.Content.ReadAsByteArrayAsync(cancellationToken)
            : null;
    }

    /// <summary>
    /// Releases a new revision for a template with a corrected <c>.cmpt</c> file: the type and the default
    /// folder that Altium reads are in this file, not in the revision parameters.
    /// </summary>
    /// <remarks>
    /// The package of the active revision is downloaded, only the requested fields are changed in <c>.cmpt</c>
    /// (<see cref="TemplateCmptEditor"/>), and <b>all</b> package files are carried into the new revision —
    /// the same way as releasing an edited model in <see cref="ModelFileService"/>.
    /// Revision links and parameters are carried over as is; <paramref name="parameters"/> supplement
    /// them (needed for <c>ComponentTypeGuid</c>, on which the type choice when creating parts relies).
    /// No <c>.cmpt</c> file — a refusal: the file is not made up.
    /// </remarks>
    /// <param name="typeGuid">The new component type; <see langword="null"/> — do not change.</param>
    /// <param name="defaultFolderGuid">The new default folder; <see langword="null"/> — do not change.</param>
    /// <param name="symbolItemGuid">
    /// Item GUID of the default symbol; <see langword="null"/> — do not change, an empty string removes the link.
    /// </param>
    /// <param name="footprintItemGuid">The same for the default footprint.</param>
    /// <param name="comment">The new template name (revision Comment); <see langword="null"/> — do not change. Unique within the folder.</param>
    /// <param name="description">The new revision description; <see langword="null"/> — do not change.</param>
    public async Task<TemplateRelease> ReleaseEditedAsync(
        ComponentTemplate template,
        string? typeGuid,
        string? defaultFolderGuid,
        string? symbolItemGuid,
        string? footprintItemGuid,
        IReadOnlyDictionary<string, string> parameters,
        string? comment,
        string? description,
        string releaseNote,
        CancellationToken cancellationToken)
    {
        if (comment is not null)
        {
            if (string.IsNullOrWhiteSpace(comment))
            {
                throw new ArgumentException("The template name (comment) cannot be empty.", nameof(comment));
            }

            // The template itself is not counted as a twin: the same comment without a name change is not a conflict.
            EnsureNameIsFree(await ListAsync(cancellationToken), template.FolderPath, comment, template.ItemGuid);
        }

        var found = await _components.ReadByIdsAsync([template.ItemGuid], cancellationToken);

        ALU_ItemRevision current = found.Count == 1
            ? found[0].LatestRevision ?? throw new InvalidOperationException($"Template {template.Hrid} has no revision.")
            : throw new InvalidOperationException($"Template {template.Hrid} not found.");

        bool editsFile = typeGuid is not null || defaultFolderGuid is not null
            || symbolItemGuid is not null || footprintItemGuid is not null;
        byte[]? package = await DownloadPackageAsync(current.GUID, cancellationToken);

        const string NoCmptAdvice = "Such a template must be set up in Altium Designer (Component Template Editor): "
            + "once the package has a .cmpt, the type and folder can be set from here.";

        if (package is null && editsFile)
        {
            throw new InvalidOperationException(
                $"The server gave no package of {template.Hrid} rev. {current.RevisionId}: there is no .cmpt file, nothing to edit, "
                + "and it must not be made up. " + NoCmptAdvice);
        }

        // A template without files (created via MCP) is edited by parameters only: nothing to carry over.
        IReadOnlyList<PackageFile> files = package is null ? [] : TemplateCmptEditor.ReadPackage(package);
        PackageFile? cmpt = files.FirstOrDefault(file => file.IsCmpt);

        if (cmpt is null && editsFile)
        {
            throw new InvalidOperationException(
                $"The package of {template.Hrid} rev. {current.RevisionId} has no .cmpt file — nothing to edit, "
                + "and it must not be made up. " + NoCmptAdvice);
        }

        var outside = files.Where(file => !PackageFile.InReleased(file.FullName)).Select(file => file.FullName).ToList();

        if (outside.Count > 0)
        {
            throw new InvalidOperationException(
                $"The package of {template.Hrid} has files outside the Released directory ({string.Join(", ", outside)}): "
                + "the release does not reproduce such a layout, no revision was created.");
        }

        TemplateSettings? before = cmpt is null ? null : TemplateSettings.Parse(TemplateCmptEditor.AsText(cmpt.Content));
        byte[]? content = cmpt?.Content;

        if (content is not null && typeGuid is not null)
        {
            content = TemplateCmptEditor.SetType(content, typeGuid).Content;
        }

        if (content is not null && defaultFolderGuid is not null)
        {
            content = TemplateCmptEditor.SetDefaultFolder(content, defaultFolderGuid).Content;
        }

        if (content is not null && symbolItemGuid is not null)
        {
            content = TemplateCmptEditor.SetModelLink(content, LinkRole.Symbol, symbolItemGuid).Content;
        }

        if (content is not null && footprintItemGuid is not null)
        {
            content = TemplateCmptEditor.SetModelLink(content, LinkRole.Footprint, footprintItemGuid).Content;
        }

        string nextId = RevisionService.NextRevisionId(current);
        string newGuid = NewGuid();

        var newTypes = await _types.ResolveNewAsync(current, parameters.Keys, cancellationToken);
        var change = new ComponentChange
        {
            Parameters = parameters,
            Comment = comment?.Trim(),
            Description = description,
        };

        ALU_ItemRevision next = RevisionService.CreateNextRevision(current, change, nextId, newGuid, newTypes, corrections: null);

        // Altium keeps the description on the revision (Description), and ItemDescription of its revisions is empty;
        // for templates created via create it is filled — then it follows, so that nothing stale is left.
        if (description is not null && !string.IsNullOrEmpty(next.ItemDescription))
        {
            next.ItemDescription = description;
        }

        var sourceLinks = await _gateway.GetItemRevisionLinksAsync(
            VaultFilter.Equal("ParentItemRevisionGUID", current.GUID), cancellationToken: cancellationToken);
        var (links, _) = RevisionService.BuildLinks(sourceLinks, change, newGuid);
        await _links.NormalizeAsync(links, cancellationToken);

        string staging = Path.Combine(Path.GetTempPath(), "altium-vault-template-" + newGuid);

        try
        {
            var release = new List<ReleaseFile>();

            foreach (PackageFile file in files)
            {
                string name = ReleasedNameFor(file, template.Hrid, current.RevisionId, nextId);
                string target = Path.GetFullPath(Path.Combine(staging, name.Replace('/', Path.DirectorySeparatorChar)));

                // The archive comes from the server, but it is not allowed to go outside the staging directory.
                if (!target.StartsWith(Path.GetFullPath(staging) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Invalid file name in the package: {file.FullName}.");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await File.WriteAllBytesAsync(target, ReferenceEquals(file, cmpt) ? content! : file.Content, cancellationToken);
                release.Add(new ReleaseFile(name, target));
            }

            await _scripts.ReleaseRevisionsAsync(
                [new RevisionRelease(next, links, release.Count > 0 ? release : null)], [next], releaseNote, cancellationToken);
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }

        return new TemplateRelease(
            Hrid: template.Hrid,
            PreviousRevision: current.RevisionId ?? string.Empty,
            NewRevision: nextId,
            NewRevisionGuid: newGuid,
            FileName: cmpt is null ? null : ReleasedNameFor(cmpt, template.Hrid, current.RevisionId, nextId),
            Files: files.Select(file => ReleasedNameFor(file, template.Hrid, current.RevisionId, nextId)).ToList(),
            PreviousType: before?.TypeGuid,
            PreviousDefaultFolder: before?.DefaultFolderGuid,
            CmptBefore: cmpt is null ? null : TemplateCmptEditor.AsText(cmpt.Content),
            CmptAfter: content is null ? null : TemplateCmptEditor.AsText(content),
            PreviousComment: current.Comment,
            PreviousDescription: current.Description,
            PreviousSymbolItemGuid: before?.DefaultSymbolItemGuid,
            PreviousFootprintItemGuid: before?.DefaultFootprintItemGuid);
    }

    /// <summary>
    /// Another template in the same folder with the same name (Comment) — a twin that Altium cannot tell from the new one.
    /// The comparison ignores case and edge spaces; the template <paramref name="excludeItemGuid"/> (the one being
    /// renamed) is not counted as a twin. A pure function — checked without a server.
    /// </summary>
    internal static ComponentTemplate? FindNameTwin(
        IEnumerable<ComponentTemplate> templates,
        string folderPath,
        string name,
        string? excludeItemGuid = null) =>
        templates.FirstOrDefault(candidate =>
            string.Equals(candidate.FolderPath, folderPath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(candidate.Comment?.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase)
            && !string.Equals(candidate.ItemGuid, excludeItemGuid, StringComparison.OrdinalIgnoreCase));

    /// <summary>A refusal if the folder already has a template with this name (see <see cref="FindNameTwin"/>).</summary>
    internal static void EnsureNameIsFree(
        IEnumerable<ComponentTemplate> templates,
        string folderPath,
        string name,
        string? excludeItemGuid = null)
    {
        ComponentTemplate? twin = FindNameTwin(templates, folderPath, name, excludeItemGuid);

        if (twin is not null)
        {
            throw new InvalidOperationException(
                $"Folder '{folderPath}' already has template {twin.Hrid} with the same comment '{name.Trim()}'. "
                + "Give the template another name (comment) or use the existing one.");
        }
    }

    /// <summary>
    /// A refusal for the general edit paths (vault_table_write, vault_update_parameters): a release through
    /// <c>RevisionBatch</c> goes without package files, and the template would lose <c>.cmpt</c> — the type and default folder.
    /// </summary>
    internal static void EnsureNoTemplates(IEnumerable<ComponentRecord> records)
    {
        var templates = records
            .Where(record => string.Equals(record.ContentType, ContentTypeName, StringComparison.OrdinalIgnoreCase))
            .Select(record => record.Hrid)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (templates.Count > 0)
        {
            throw new InvalidOperationException(
                $"Component templates ({string.Join(", ", templates)}) are not changed by the general table edit: it would release a revision "
                + "without the .cmpt file and wipe the type. The template name, description and parameters are edited by vault_template set "
                + "(parameters comment, description, parameters); it carries over all package files.");
        }
    }

    /// <summary>
    /// A refusal if the copy sample (<c>vault_copy_components</c>) is a component template: the copy
    /// would come out without the <c>.cmpt</c> file (<see cref="ComponentCopyService"/> does not carry package files
    /// at all), that is, without the type and layout. A template copy is <c>vault_template create basedOn=…</c>.
    /// </summary>
    internal static void EnsureNotCopySource(ComponentRecord source)
    {
        if (string.Equals(source.ContentType, ContentTypeName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"A component template ({source.Hrid}) is not copied by this tool: the copy would come out without "
                + $"the .cmpt file — without the type and layout. A template copy is vault_template create basedOn={source.Hrid}.");
        }
    }

    /// <summary>
    /// The file name in the new revision. Altium names a template file by the revision number
    /// (<c>CMPT-0001-13.CMPT</c>), so such a name is switched to the number of the new revision; the other
    /// files keep their names.
    /// </summary>
    internal static string ReleasedNameFor(PackageFile file, string hrid, string? previousRevision, string nextRevision)
    {
        if (file.IsCmpt
            && string.Equals(file.ReleasedName, $"{hrid}-{previousRevision}{Path.GetExtension(file.ReleasedName)}", StringComparison.OrdinalIgnoreCase))
        {
            return $"{hrid}-{nextRevision}{Path.GetExtension(file.ReleasedName)}";
        }

        return file.ReleasedName;
    }

    /// <summary>
    /// The component type of a template: from the <c>.cmpt</c> file (Altium reads it), the fallback source —
    /// the revision parameter <c>ComponentTypeGuid</c>. A disagreement of the sources — in <see cref="TemplateType.Disagree"/>.
    /// </summary>
    public async Task<TemplateType> ReadTypeAsync(ComponentTemplate template, CancellationToken cancellationToken)
    {
        TemplateSettings? file = await ReadSettingsAsync(template.RevisionGuid, cancellationToken);
        string? fromParameter = string.IsNullOrWhiteSpace(template.ComponentTypeGuid) ? null : template.ComponentTypeGuid;

        return new TemplateType(
            file?.TypeGuid ?? fromParameter,
            file?.TypeGuid,
            fromParameter,
            file?.TypeGuid is not null
                && fromParameter is not null
                && !string.Equals(file.TypeGuid, fromParameter, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>All component templates of the vault.</summary>
    public async Task<IReadOnlyList<ComponentTemplate>> ListAsync(CancellationToken cancellationToken)
    {
        var (records, _) = await _components.SearchAsync(
            new ComponentCriteria { ContentType = ContentTypeName, Limit = 5000 },
            cancellationToken);

        return records.Select(record => new ComponentTemplate(
            record.Hrid,
            record.ItemGuid,
            record.RevisionGuid ?? string.Empty,
            record.RevisionId ?? string.Empty,
            record.FolderPath,
            record.Description,
            record.Parameters.TryGetValue(ComponentTypeParameter, out string? type) ? type : null,
            record.Parameters,
            record.Comment)).ToList();
    }

    /// <summary>
    /// References of the active revisions of all templates to the default symbol and footprint
    /// (<c>ModelLinks</c> in <c>.cmpt</c>). Settings are taken from the cache by revision. A template whose
    /// <c>.cmpt</c> was not read is an exception: then the references cannot be checked, the object is not considered "free".
    /// </summary>
    public async Task<IReadOnlyList<TemplateModelReference>> ListModelReferencesAsync(CancellationToken cancellationToken)
    {
        var templates = await ListAsync(cancellationToken);
        var read = new List<(string Hrid, string ItemGuid, TemplateSettings Settings)>();
        var unreadable = new List<string>();

        foreach (ComponentTemplate template in templates.Where(template => !string.IsNullOrEmpty(template.RevisionGuid)))
        {
            TemplateSettings? settings = await ReadSettingsAsync(template.RevisionGuid, cancellationToken);

            if (settings is null)
            {
                unreadable.Add(template.Hrid);
            }
            else
            {
                read.Add((template.Hrid, template.ItemGuid, settings));
            }
        }

        if (unreadable.Count > 0)
        {
            throw new InvalidOperationException(
                "Could not fully check usage: the .cmpt file of the templates was not read: "
                + string.Join(", ", unreadable.Take(10)) + (unreadable.Count > 10 ? ", …" : string.Empty)
                + ". Template references to symbols and footprints are unknown — try again later.");
        }

        return TemplateReferenceIndex.Build(read);
    }

    /// <summary>
    /// References to the default symbol and footprint of one template — without reading
    /// the others. The .cmpt could not be read — a refusal, with the same text as for the full list.
    /// </summary>
    public async Task<IReadOnlyList<TemplateModelReference>> ListModelReferencesAsync(
        ComponentTemplate template, CancellationToken cancellationToken)
    {
        TemplateSettings? settings = await ReadSettingsAsync(template.RevisionGuid, cancellationToken);

        if (settings is null)
        {
            throw new InvalidOperationException(
                $"Could not check the references of template {template.Hrid}: the .cmpt file of its revision was not read.");
        }

        return TemplateReferenceIndex.Build([(template.Hrid, template.ItemGuid, settings)]);
    }

    /// <summary>Finds a template by identifier or GUID.</summary>
    public async Task<ComponentTemplate> ResolveAsync(string template, CancellationToken cancellationToken)
    {
        var templates = await ListAsync(cancellationToken);

        ComponentTemplate? match = templates.FirstOrDefault(candidate =>
            string.Equals(candidate.Hrid, template, StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidate.ItemGuid, template, StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidate.RevisionGuid, template, StringComparison.OrdinalIgnoreCase));

        return match
            ?? throw new InvalidOperationException(
                $"Component template '{template}' not found. vault_templates gives the list.");
    }

    /// <summary>
    /// Creates a new component template — a vault item with the first revision, without
    /// a sample to copy.
    /// </summary>
    /// <remarks>
    /// The template has no symbol and footprint, so creation carries over no links —
    /// unlike <see cref="ComponentCopyService"/>. The item service fields
    /// (content type, lifecycle, revision numbering scheme), common to all templates
    /// of the vault regardless of which component type they correspond to,
    /// are borrowed from the first existing template found — a freshly created item
    /// has no values of its own to take them from.
    ///
    /// The ComponentTypeGuid parameter is not accepted here: checked empirically — a value
    /// passed along with the first revision of a new item is silently erased by the server on release
    /// (other parameters of the same call do not suffer). The type is assigned by a separate
    /// call on the already existing revision (the vault_template tool, the create action
    /// does this as a second step the same way as the set_type action).
    /// </remarks>
    public async Task<TemplateCreation> CreateAsync(
        string folderPathOrGuid,
        string? description,
        string? comment,
        IReadOnlyDictionary<string, string>? parameters,
        string releaseNote,
        TemplateSample? sample,
        CancellationToken cancellationToken)
    {
        FolderNode folder = await _catalog.ResolveFolderAsync(folderPathOrGuid, cancellationToken);

        var existingTemplates = await ListAsync(cancellationToken);

        // Comment is the template name in Altium (the template list shows it); two templates with one
        // Comment in one folder cannot be told apart. If not given — the description is taken.
        string? name = string.IsNullOrWhiteSpace(comment) ? description : comment;

        if (!string.IsNullOrWhiteSpace(name))
        {
            EnsureNameIsFree(existingTemplates, folder.Path, name);
        }

        ComponentTemplate? donor = sample is null
            ? existingTemplates.FirstOrDefault()
            : await ResolveAsync(sample.BasedOn, cancellationToken);

        if (donor is null)
        {
            throw new InvalidOperationException(
                "The vault has no component template at all — there is nobody to borrow the service "
                + "fields (lifecycle, revision numbering scheme) from. Create the first template in Altium Designer, "
                + "and this tool will create the rest.");
        }

        ALU_Item donorItem = await LoadItemAsync(donor.ItemGuid, cancellationToken);

        var donorRecords = await _components.ReadByIdsAsync([donor.ItemGuid], cancellationToken);
        ALU_ItemRevision? donorRevision = donorRecords.Count == 1 ? donorRecords[0].LatestRevision : null;

        // The .cmpt file of the sample is the only source of the new template's file; it must not be made up.
        byte[]? cmptContent = null;
        string? cmptBefore = null;
        string cmptExtension = ".CMPT";
        var skippedFiles = new List<string>();

        string itemGuid = NewGuid();
        string revisionGuid = NewGuid();

        if (sample is not null)
        {
            byte[]? package = donorRevision is null ? null : await DownloadPackageAsync(donorRevision.GUID, cancellationToken);
            var files = package is null ? [] : TemplateCmptEditor.ReadPackage(package);
            PackageFile? cmpt = files.FirstOrDefault(file => file.IsCmpt);

            if (cmpt is null)
            {
                throw new InvalidOperationException(
                    $"The sample {donor.Hrid} has no .cmpt file in its package: there is nothing to base on, and the file must not be made up. "
                    + "Specify another template in basedOn (its type can be found through vault_templates).");
            }

            if (!PackageFile.InReleased(cmpt.FullName))
            {
                throw new InvalidOperationException(
                    $"The .cmpt file of the sample {donor.Hrid} lies outside the Released directory ({cmpt.FullName}): the release does not reproduce such a layout.");
            }

            skippedFiles.AddRange(files.Where(file => !ReferenceEquals(file, cmpt)).Select(file => file.ReleasedName));
            cmptBefore = TemplateCmptEditor.AsText(cmpt.Content);

            // Only what must be its own is replaced: the identity of the new item and revision,
            // and, optionally, the name template, type and default folder. The other bytes — as in the sample.
            cmptContent = TemplateCmptEditor.SetItemGuid(cmpt.Content, itemGuid).Content;
            cmptContent = TemplateCmptEditor.SetRevisionGuid(cmptContent, revisionGuid).Content;

            if (sample.NamingTemplate is not null)
            {
                cmptContent = TemplateCmptEditor.SetItemNamingTemplate(cmptContent, sample.NamingTemplate).Content;
            }

            if (sample.TypeGuid is not null)
            {
                cmptContent = TemplateCmptEditor.SetType(cmptContent, sample.TypeGuid).Content;
            }

            if (sample.DefaultFolderGuid is not null)
            {
                cmptContent = TemplateCmptEditor.SetDefaultFolder(cmptContent, sample.DefaultFolderGuid).Content;
            }

            if (sample.SymbolItemGuid is not null)
            {
                cmptContent = TemplateCmptEditor.SetModelLink(cmptContent, LinkRole.Symbol, sample.SymbolItemGuid).Content;
            }

            if (sample.FootprintItemGuid is not null)
            {
                cmptContent = TemplateCmptEditor.SetModelLink(cmptContent, LinkRole.Footprint, sample.FootprintItemGuid).Content;
            }

            cmptExtension = Path.GetExtension(cmpt.ReleasedName);
        }

        var hrids = await _gateway.GenerateItemHridsAsync(folder.Guid, donorItem.ContentTypeGUID, 1, cancellationToken);
        string hrid = hrids.Count == 1
            ? hrids[0]
            : throw new InvalidOperationException($"The server issued {hrids.Count} identifiers instead of one.");

        var allParameters = new Dictionary<string, string>(
            parameters ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);

        var newTypes = await _types.ResolveAsync(allParameters.Keys, cancellationToken);

        string desc = description ?? string.Empty;

        var revision = new ALU_ItemRevision
        {
            GUID = revisionGuid,
            ItemGUID = itemGuid,
            RevisionId = "1",
            RevisionIdLevels = new _StringList { "1" },
            RevisionIdSeparators = donorRevision?.RevisionIdSeparators ?? new _StringList(),
            ContentTypeGUID = donorItem.ContentTypeGUID,
            FolderGUID = folder.Guid,
            LifeCycleStateGUID = donorRevision?.LifeCycleStateGUID,
            Comment = name,
            Description = desc,
            ItemHRID = hrid,
            ItemDescription = desc,
            IsVisible = true,
            IsApplicable = true,
            IsActive = true,
            RevisionParameters = RevisionService.MergeParameters(
                new ALU_ItemRevision(), allParameters, NoDeletes, revisionGuid, reuseGuids: false, newTypes, corrections: null),
        };

        var item = new ALU_Item
        {
            GUID = itemGuid,
            HRID = hrid,
            Description = desc,
            FolderGUID = folder.Guid,
            ContentTypeGUID = donorItem.ContentTypeGUID,
            LifeCycleDefinitionGUID = donorItem.LifeCycleDefinitionGUID,
            RevisionNamingSchemeGUID = donorItem.RevisionNamingSchemeGUID,
            IsActive = true,
            Revisions = new _ALU_ItemRevisionList { revision },
        };

        string? fileName = cmptContent is null ? null : $"{hrid}-1{cmptExtension}";
        string staging = Path.Combine(Path.GetTempPath(), "altium-vault-template-" + revisionGuid);

        try
        {
            List<ReleaseFile>? release = null;

            if (fileName is not null)
            {
                string target = Path.Combine(staging, fileName);
                Directory.CreateDirectory(staging);
                await File.WriteAllBytesAsync(target, cmptContent!, cancellationToken);
                release = [new ReleaseFile(fileName, target)];
            }

            await _gateway.AddItemsAsync([item], cancellationToken);
            await _scripts.ReleaseRevisionsAsync(
                [new RevisionRelease(revision, [], release)], releaseNote, cancellationToken);
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }

        var created = new ComponentTemplate(
            hrid,
            itemGuid,
            revisionGuid,
            "1",
            folder.Path,
            desc,
            ComponentTypeGuid: null,
            ComponentService.ExtractParameters(revision),
            name);

        return new TemplateCreation(
            created,
            sample is null ? null : donor.Hrid,
            fileName,
            skippedFiles,
            cmptBefore,
            cmptContent is null ? null : TemplateCmptEditor.AsText(cmptContent));
    }

    private async Task<ALU_Item> LoadItemAsync(string itemGuid, CancellationToken cancellationToken)
    {
        var items = await _gateway.GetItemsAsync(
            VaultFilter.Equal("GUID", itemGuid), limit: 1, cancellationToken: cancellationToken);

        return items.Count == 1
            ? items[0]
            : throw new InvalidOperationException($"Item {itemGuid} not found.");
    }

    private static string NewGuid() => Guid.NewGuid().ToString("D").ToUpperInvariant();

    /// <summary>
    /// Assigns a default template to a folder. Components already in the folder
    /// are not changed by this — the template applies to those created in it later.
    /// </summary>
    public async Task<(FolderNode Folder, ComponentTemplate Template)> AssignToFolderAsync(
        string folderPathOrGuid,
        string template,
        CancellationToken cancellationToken)
    {
        FolderNode node = await _catalog.ResolveFolderAsync(folderPathOrGuid, cancellationToken);
        ComponentTemplate resolved = await ResolveAsync(template, cancellationToken);
        string vaultGuid = await _gateway.GetVaultGuidAsync(cancellationToken);

        ALU_Folder folder = await LoadFolderAsync(node.Guid, cancellationToken);
        var parameters = folder.FolderParameters ?? new _ALU_FolderParameterList();

        SetParameter(parameters, folder.GUID, TemplateItemParameter, resolved.ItemGuid);
        SetParameter(parameters, folder.GUID, TemplateRevisionParameter, resolved.RevisionGuid);
        SetParameter(parameters, folder.GUID, TemplateVaultParameter, vaultGuid);

        folder.FolderParameters = parameters;

        await _gateway.UpdateFoldersAsync([folder], cancellationToken);
        await _catalog.InvalidateAsync(cancellationToken);

        return (node, resolved);
    }

    /// <summary>The default template assigned to a folder, if set.</summary>
    public async Task<ComponentTemplate?> GetFolderTemplateAsync(
        string folderPathOrGuid,
        CancellationToken cancellationToken)
    {
        FolderNode node = await _catalog.ResolveFolderAsync(folderPathOrGuid, cancellationToken);
        ALU_Folder folder = await LoadFolderAsync(node.Guid, cancellationToken);

        string? itemGuid = folder.FolderParameters
            ?.FirstOrDefault(parameter =>
                string.Equals(parameter.HRID, TemplateItemParameter, StringComparison.OrdinalIgnoreCase))
            ?.DefaultValue;

        if (string.IsNullOrWhiteSpace(itemGuid))
        {
            return null;
        }

        var templates = await ListAsync(cancellationToken);
        return templates.FirstOrDefault(candidate =>
            string.Equals(candidate.ItemGuid, itemGuid, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<ALU_Folder> LoadFolderAsync(string folderGuid, CancellationToken cancellationToken)
    {
        // IncludeSystemFolders is required: without it system folders are not found,
        // and reading their properties failed with "folder not found".
        var folders = await _gateway.GetFoldersAsync(
            VaultFilter.Equal("GUID", folderGuid),
            VaultRequestOptions.Of(
                VaultRequestOptions.IncludeFolderParameters,
                VaultRequestOptions.IncludeSystemFolders),
            limit: 1,
            cancellationToken: cancellationToken);

        return folders.Count == 1
            ? folders[0]
            : throw new InvalidOperationException($"Folder {folderGuid} not found.");
    }

    /// <summary>Sets a folder parameter value, adding it if missing.</summary>
    private static void SetParameter(
        _ALU_FolderParameterList parameters,
        string folderGuid,
        string name,
        string value)
    {
        ALU_FolderParameter? existing = parameters.FirstOrDefault(parameter =>
            string.Equals(parameter.HRID, name, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            existing.DefaultValue = value;
            return;
        }

        parameters.Add(new ALU_FolderParameter
        {
            GUID = Guid.NewGuid().ToString("D").ToUpperInvariant(),
            HRID = name,
            FolderGUID = folderGuid,
            DefaultValue = value,
        });
    }
}
