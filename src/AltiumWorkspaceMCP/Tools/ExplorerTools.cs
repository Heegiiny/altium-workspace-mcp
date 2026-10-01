using System.ComponentModel;
using AltiumWorkspaceMCP.Auth;
using AltiumWorkspaceMCP.Configuration;
using AltiumWorkspaceMCP.Mcp;
using AltiumWorkspaceMCP.Vault;
using AltiumWorkspaceMCP.Soap.Vault;
using ModelContextProtocol.Server;

namespace AltiumWorkspaceMCP.Tools;

/// <summary>
/// Navigation and reading — what the Explorer panel does in Altium Designer:
/// the folder tree, the component table, the part card, types and templates.
/// </summary>
[McpServerToolType]
public sealed class ExplorerTools
{
    private readonly VaultWorkspace _workspace;

    public ExplorerTools(VaultWorkspace workspace) => _workspace = workspace;

    [McpServerTool(Name = "vault_status", ReadOnly = true)]
    [Description("""
        State of the connection to the Altium vault: server version, address, account, sign-in method
        and state, write mode and vault size. With browser login and no login yet there are no
        vault totals: the response says to call vault_session action=login.
        """)]
    public async Task<object> GetStatusAsync(CancellationToken cancellationToken)
    {
        SessionSnapshot session = await _workspace.Session.DescribeAsync(cancellationToken);

        if (_workspace.Options.UsesTokenAuthentication && session.Token is { HasTokens: false })
        {
            return new
            {
                version = ServerVersion.Current,
                workspace = _workspace.Options.BaseUrl.ToString(),
                session = DescribeSignIn(_workspace.Options, session),
                write = new
                {
                    mode = _workspace.Guard.Mode.ToString(),
                    confirmationsRequired = _workspace.Guard.Mode == WriteMode.Guarded,
                },
                note = "Not logged in, so the vault totals are not read. Call vault_session action=login.",
            };
        }

        int items = await _workspace.Gateway.GetItemCountAsync(cancellationToken: cancellationToken);
        var folders = await _workspace.Catalog.GetFoldersAsync(cancellationToken);

        return new
        {
            version = ServerVersion.Current,
            workspace = _workspace.Options.BaseUrl.ToString(),
            account = _workspace.Options.UsesTokenAuthentication
                ? session.Token?.Account ?? "token login"
                : _workspace.Options.UsesWindowsAuthentication
                    ? "Windows account (single sign-on)"
                    : _workspace.Options.UserName,
            session = DescribeSignIn(_workspace.Options, session),
            write = new
            {
                mode = _workspace.Guard.Mode.ToString(),
                confirmationsRequired = _workspace.Guard.Mode == WriteMode.Guarded,
                writableFolders = _workspace.Guard.WritableFolders,
            },
            totals = new { items, folders = folders.Count },
            auditLog = _workspace.Audit.FilePath,
        };
    }

    /// <summary>How many characters of a vault_folders listing are considered safe (the response size limit is 20 000).</summary>
    private const int FolderBudgetChars = 15000;

    /// <summary>From this many folders on, includeTemplates is not performed: a template is read by one request per folder.</summary>
    private const int TemplateLookupLimit = 100;

    [McpServerTool(Name = "vault_folders", ReadOnly = true)]
    [Description("""
        Vault folder tree. Start with a call without parameters: the top levels of the tree,
        one line per folder, and [+N] — how many nested folders are hidden by depth. Then under —
        for the needed branch or nameContains — to find a folder by name at any depth.
        Paths from the response are folder addresses for the other tools.

        The folder is the first of the three coordinates of a part in Altium. The other two — the component
        template and the component type — are available through vault_templates.
        """)]
    public async Task<object> ListFoldersAsync(
        [Description("""
            Tree branch: full path, path ending (Passive Components\Resistors), name
            or folder GUID. Empty — the top levels of the whole vault.
            """)]
        string? under = null,
        [Description("""
            How many levels to show, counting the listing root as the first. Default 2 without under
            and 3 with under, for example 5 — a deep branch in full.
            """)]
        int? depth = null,
        [Description("""
            Find folders whose name contains this substring, at any depth (case-insensitive),
            for example Resistor. Depth does not apply then; with under the search
            runs only in this branch.
            """)]
        string? nameContains = null,
        [Description("Show the Datasheets system folders. Hidden by default.")]
        bool includeSystem = false,
        [Description("Add the folder GUID: 'path | GUID'. GUIDs are not printed by default.")]
        bool includeGuids = false,
        [Description("Add the default component template (at most 100 folders).")]
        bool includeTemplates = false,
        CancellationToken cancellationToken = default)
    {
        var all = (await _workspace.Catalog.GetFoldersAsync(cancellationToken)).Values;

        FolderNode? root = null;
        object? resolvedFolder = null;

        if (!string.IsNullOrWhiteSpace(under))
        {
            FolderMatch match = await _workspace.Catalog.ResolveFolderMatchAsync(
                under, FolderMatchMode.Lenient, cancellationToken);

            root = match.Folder;
            resolvedFolder = match.Describe(under);
        }

        IReadOnlyList<FolderTreeLine> lines;
        int? shownDepth = null;
        bool truncated = false;
        var hints = new List<string>();

        if (!string.IsNullOrWhiteSpace(nameContains))
        {
            lines = FolderTreeView.ByName(all, root, nameContains, includeSystem, includeGuids);

            if (lines.Count > FolderTreeView.MaxNameMatches)
            {
                truncated = true;
                lines = lines.Take(FolderTreeView.MaxNameMatches).ToList();
                hints.Add($"The first {FolderTreeView.MaxNameMatches} folders are shown: refine nameContains or set under.");
            }
            else if (lines.Count == 0)
            {
                hints.Add($"No folders with '{nameContains}' in the name. Check the spelling or look at the tree: vault_folders without parameters.");
            }
        }
        else
        {
            int wanted = Math.Max(1, depth ?? (root is null ? 2 : 3));
            int current = wanted;

            lines = FolderTreeView.Levels(all, root, current, includeSystem, includeGuids);

            // The listing must fit the response budget: extra depth is dropped.
            while (lines.Sum(line => FolderTreeView.JsonLength(line.Text)) > FolderBudgetChars && current > 1)
            {
                current--;
                lines = FolderTreeView.Levels(all, root, current, includeSystem, includeGuids);
            }

            shownDepth = current;

            if (current < wanted)
            {
                hints.Add($"Depth reduced from {wanted} to {current} so that the response fits.");
            }

            FolderTreeLine? deepest = lines.OrderByDescending(line => line.Hidden).FirstOrDefault();
            if (deepest is { Hidden: > 0 })
            {
                hints.Add($"Deeper: vault_folders under=\"{deepest.Folder.Path}\".");
            }
        }

        var texts = lines.Select(line => line.Text).ToList();

        if (includeTemplates)
        {
            if (lines.Count > TemplateLookupLimit)
            {
                hints.Add($"includeTemplates was not performed: more than {TemplateLookupLimit} folders. Narrow the listing (under, nameContains).");
            }
            else
            {
                for (int index = 0; index < lines.Count; index++)
                {
                    ComponentTemplate? template = await _workspace.Templates.GetFolderTemplateAsync(
                        lines[index].Folder.Guid, cancellationToken);

                    if (template is not null)
                    {
                        texts[index] += $" | template: {template.Hrid}";
                    }
                }
            }
        }

        return new
        {
            resolvedFolder,
            root = root?.Path,
            depth = shownDepth,
            count = texts.Count,
            truncated = truncated ? true : (bool?)null,
            folders = texts,
            hint = hints.Count > 0 ? string.Join(" ", hints) : null,
        };
    }

    [McpServerTool(Name = "vault_table", ReadOnly = true)]
    [Description("""
        Components as a table: a list of columns and rows of values — a direct analog of Batch Edit
        in Altium. The resulting table can be edited and returned whole through
        vault_table_write, so the whole edit takes two calls.

        The columns hrid, folder, revision, comment and description are attributes; comment is
        the component name that Altium puts into the schematic. The other columns are
        component parameters.

        Selection: by folder, by identifier pattern, by description substring and by parameter
        value. The parameter selection is done by the server, so it is fast even over the whole
        vault; use it instead of walking folders.

        parameterEquals=["Name="] (an empty value) finds a part whose parameter EXISTS
        and is empty — this is not the same as the parameter being absent. To find parts where the
        parameter is absent altogether, use parameterMissing; it requires folder (without a folder
        the whole vault would have to be read) and, unlike parameterEquals, is not sped up by the
        server — on large folders it can take several seconds.
        """)]
    public async Task<object> ReadTableAsync(
        [Description("""
            Folder: full path, path ending (Passive Components\Resistors), name or GUID.
            An inexact address (Passive\Resistors) is matched loosely — the response has the field
            resolvedFolder. Empty — the whole vault.
            """)]
        string? folder = null,
        [Description("Include nested folders.")]
        bool recursive = false,
        [Description("Selection by identifier; a pattern with % is allowed (for example CMP-009-%).")]
        string? hrid = null,
        [Description("Selection by description substring, case-insensitive.")]
        string? descriptionContains = null,
        [Description("Selection by parameter value as 'Name=Value'; % is allowed in the value.")]
        string[]? parameterEquals = null,
        [Description("Names of parameters that the part must not have at all. Requires folder.")]
        string[]? parameterMissing = null,
        [Description("Content type: altium-component (default), altium-symbol, altium-pcb-component and others.")]
        string? contentType = null,
        [Description("Which parameters to show as columns. Empty — all that were found.")]
        string[]? columns = null,
        [Description("""
            Maximum rows per call (default 100). If the rows do not fit the response, fewer
            are returned, and truncated=true and nextOffset show where to continue.
            """)]
        int limit = 100,
        [Description("""
            How many first rows to skip to page a long selection: take nextOffset
            from the previous response. The server reads offset+limit records and drops the beginning,
            so far pages are slower.
            """)]
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        var criteria = BuildCriteria(
            folder, recursive, hrid, descriptionContains, parameterEquals, contentType, limit,
            FolderMatchMode.Lenient, offset) with
        {
            ParameterMissing = parameterMissing ?? [],
        };

        // A typo in a parameter name would silently give an empty selection or an empty column.
        await _workspace.ParameterNames.EnsureKnownAsync(
            criteria.Parameters.Select(parameter => parameter.Name), "parameterEquals", null, cancellationToken);
        await _workspace.ParameterNames.EnsureKnownAsync(
            criteria.ParameterMissing, "parameterMissing", null, cancellationToken);
        await _workspace.ParameterNames.EnsureKnownAsync(
            (columns ?? []).Where(name => !ComponentTable.FixedColumns.Contains(name, StringComparer.OrdinalIgnoreCase)),
            "columns", null, cancellationToken);

        var (found, plan) = await _workspace.Components.SearchAsync(criteria, cancellationToken);

        // Within a portion rows go by identifier; the order of the portions themselves is set by the server.
        var records = found.OrderBy(record => record.Hrid, StringComparer.OrdinalIgnoreCase).ToList();
        ComponentTable table = ComponentTable.Build(records, columns);

        // A heterogeneous selection gives a wide table with mostly empty cells:
        // the parameter sets of a resistor and a connector hardly overlap.
        int parameterColumns = table.Columns.Count - ComponentTable.FixedColumns.Length;
        var hints = new List<string>();

        if (columns is null && parameterColumns > 40)
        {
            hints.Add($"Heterogeneous selection: {parameterColumns} parameter columns, most cells are empty. "
                + "Narrow the selection or list the needed parameters in columns.");
        }

        // Rows are taken while they fit the response budget: the header and columns are in reserve.
        int reserved = ResponseBudget.SizeOf(table.Columns) + 1500;
        var budget = new ResponseBudget(_workspace.Options.MaxResponseChars, reserved);
        var rows = budget.TakeFitting(table.Rows);

        bool cut = rows.Count < table.Rows.Count;
        bool truncated = cut || plan.HasMore;
        int? nextOffset = truncated ? criteria.Offset + (cut ? SkippedInServerOrder(found, records, rows.Count) : found.Count) : null;

        if (cut)
        {
            hints.Add($"The response is limited by a budget of {_workspace.Options.MaxResponseChars} characters: shown {rows.Count} "
                + $"of {table.Rows.Count} rows of the portion. To continue: offset={nextOffset} and limit about {rows.Count} "
                + "(the server reads offset+limit records — an excess limit slows it down), or list the needed parameters in columns.");
        }
        else if (plan.HasMore)
        {
            hints.Add($"There are more rows: continue with offset={nextOffset}.");
        }

        if (found.Count == 0 && criteria.Parameters.Count > 0)
        {
            hints.Add("Nothing found. A parameter value is compared as text: write it as Altium shows it "
                + "(10k, 100nF), for part of a value use %, for example Value=10k%.");
        }

        return new
        {
            resolvedFolder = folder is null ? null : plan.ResolvedFolder?.Describe(folder),
            count = rows.Count,
            search = new { strategy = plan.Strategy, candidates = plan.Candidates, elapsedMs = plan.ElapsedMs },
            truncated,
            nextOffset,
            hint = hints.Count > 0 ? string.Join(" ", hints) : null,
            columns = table.Columns,
            rows,
        };
    }

    [McpServerTool(Name = "vault_component_detail", ReadOnly = true)]
    [Description("""
        Full part card: parameters of the active revision, revision history with the
        release flag, the linked symbol, footprints and component template, and the objects
        where the part is used. There can be several footprints: in models each has
        footprintIndex (number; 0 — primary) and isDefaultFootprint (primary flag), the role
        "additional footprint n" for PCBLIB n links.
        """)]
    public async Task<object> GetComponentAsync(
        [Description("Identifier (for example CMP-000-00046) or item GUID.")]
        string component,
        CancellationToken cancellationToken = default)
    {
        var found = await _workspace.Components.ReadByIdsAsync([component], cancellationToken);
        if (found.Count == 0)
        {
            throw new InvalidOperationException(
                await _workspace.Components.DescribeMissingAsync([component], cancellationToken));
        }

        ComponentRecord record = found[0];
        var revisions = await _workspace.Components.GetRevisionsAsync(record.ItemGuid, cancellationToken);

        var links = record.RevisionGuid is null
            ? []
            : await _workspace.Gateway.GetItemRevisionLinksAsync(
                VaultFilter.Equal("ParentItemRevisionGUID", record.RevisionGuid),
                cancellationToken: cancellationToken);

        var models = await DescribeModelsAsync(links, cancellationToken);
        WhereUsedResult whereUsed = await _workspace.Gateway.GetWhereUsedAsync(
            record.ItemGuid, cancellationToken: cancellationToken);

        var issues = ComponentHealthService.Inspect(
            record,
            links,
            revisions.OrderBy(revision => revision.CreatedAt).ThenBy(revision => revision.RevisionId).ToList());

        // The component type is stored as a tag separate from the revision; without it the part is not visible in the Components panel tree.
        var assigned = await _workspace.ComponentTypes.GetAssignedAsync([record.ItemGuid], cancellationToken);

        return new
        {
            component = Describe(record),
            componentType = assigned.TryGetValue(record.ItemGuid, out string? typePath) ? typePath : null,
            // Typed parameters and their units: values are written in the same format.
            parameterTypes = (record.LatestRevision?.RevisionParameters ?? [])
                .Where(parameter => !ParameterValueCodec.IsText(parameter.ParameterTypeGUID) && !string.IsNullOrEmpty(parameter.HRID))
                .GroupBy(parameter => parameter.HRID, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => ParameterValueCodec.DescribeType(group.First().ParameterTypeGUID)
                        + (ParameterValueCodec.UnitOf(group.First().ParameterTypeGUID) is { Length: > 0 } unit ? $", {unit}" : string.Empty),
                    StringComparer.OrdinalIgnoreCase),
            issues = issues.Count == 0
                ? null
                : issues.Select(issue => new { kind = issue.Kind, detail = issue.Detail, fixable = issue.Fixable }).ToList(),
            issuesHint = issues.Any(issue => issue.Fixable)
                ? "What can be fixed is fixed by vault_check_components with fix=true."
                : null,
            revisions = revisions.Select(revision => new
            {
                revisionId = revision.RevisionId,
                guid = revision.GUID,
                released = RevisionService.IsReleased(revision),
                releaseDate = RevisionService.IsReleased(revision)
                    ? new DateTimeOffset(revision.ReleaseDate, TimeSpan.Zero)
                    : (DateTimeOffset?)null,
                comment = revision.Comment,
                parameters = revision.RevisionParameters?.Count ?? 0,
            }).ToList(),
            models,
            whereUsed = new
            {
                total = whereUsed.TotalCount,
                parents = whereUsed.ParentItems
                    .Select(item => new { hrid = item.HRID, guid = item.GUID, description = item.Description })
                    .ToList(),
            },
        };
    }

    [McpServerTool(Name = "vault_revision_parameters", ReadOnly = true)]
    [Description("""
        Component parameters in earlier revisions. Needed when values were damaged or
        wiped: this listing shows what was there before the edit, and vault_restore_from_revision
        puts the earlier values back.

        Without revision it shows all revisions, from newest to oldest.
        """)]
    public async Task<object> GetRevisionParametersAsync(
        [Description("Component identifier or item GUID.")]
        string component,
        [Description("Revision number, for example '5'. Empty — all revisions.")]
        string? revision = null,
        CancellationToken cancellationToken = default)
    {
        var found = await _workspace.Components.ReadByIdsAsync([component], cancellationToken);
        if (found.Count == 0)
        {
            throw new InvalidOperationException(
                await _workspace.Components.DescribeMissingAsync([component], cancellationToken));
        }

        var revisions = await _workspace.Components.GetRevisionsAsync(found[0].ItemGuid, cancellationToken);

        var selected = string.IsNullOrWhiteSpace(revision)
            ? revisions
            : revisions.Where(candidate =>
                string.Equals(candidate.RevisionId, revision, StringComparison.OrdinalIgnoreCase)).ToList();

        if (selected.Count == 0)
        {
            throw new InvalidOperationException(
                $"Component {found[0].Hrid} has no revision '{revision}'. "
                + $"Available: {string.Join(", ", revisions.Select(r => r.RevisionId))}.");
        }

        return new
        {
            component = found[0].Hrid,
            revisions = selected.Select(item => new
            {
                revisionId = item.RevisionId,
                released = RevisionService.IsReleased(item),
                releaseDate = RevisionService.IsReleased(item)
                    ? new DateTimeOffset(item.ReleaseDate, TimeSpan.Zero)
                    : (DateTimeOffset?)null,
                comment = item.Comment,
                description = item.Description,
                parameters = ComponentService.ExtractParameters(item),
            }).ToList(),
        };
    }

    [McpServerTool(Name = "vault_content_types", ReadOnly = true)]
    [Description("""
        Vault content types: altium-component for parts, altium-symbol for symbols,
        altium-pcb-component for footprints, altium-component-template for templates.
        The type name is passed in contentType of the read and move tools.
        """)]
    public async Task<object> ListContentTypesAsync(
        [Description("Show all types, not only the Altium ones.")]
        bool all = false,
        CancellationToken cancellationToken = default)
    {
        var types = await _workspace.Catalog.GetContentTypesAsync(cancellationToken);

        var listed = types.Values
            .Where(type => all || type.HRID?.StartsWith("altium-", StringComparison.OrdinalIgnoreCase) == true)
            .OrderBy(type => type.HRID, StringComparer.OrdinalIgnoreCase)
            .Select(type => new { name = type.HRID, guid = type.GUID, description = type.Description })
            .ToList();

        return new { count = listed.Count, contentTypes = listed };
    }

    [McpServerTool(Name = "vault_templates", ReadOnly = true)]
    [Description("""
        Component templates — the second and third coordinates of a part in Altium. A template sets
        the layout of a new part, the component type (by which the part is found in the Components panel)
        and the default symbol and footprint — a part created from the template later gets them. Each templates row is one compact record "HRID: type | SYM: symbol |
        PCB: footprint", so that the state of all templates (revision, folder — vault_template show)
        reads in one call; the folder is shown only for templates outside commonFolder. A reference to a
        deleted or missing item shows "in trash: …" instead of the HRID
        or "not in vault: …" — the list of all problem references at once is given by vault_template
        action=check.

        A template is linked to a part through vault_set_links with the template role, and to a folder
        as the default template — through vault_folder with the set_template action.
        """)]
    public async Task<object> ListTemplatesAsync(CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var templates = await _workspace.Templates.ListAsync(cancellationToken);
        var types = await _workspace.ComponentTypes.ListAsync(cancellationToken);

        var settingsByItem = new Dictionary<string, TemplateSettings?>(StringComparer.OrdinalIgnoreCase);
        foreach (ComponentTemplate template in templates)
        {
            settingsByItem[template.ItemGuid] = await _workspace.Templates.ReadSettingsAsync(template.RevisionGuid, cancellationToken);
        }

        var modelGuids = settingsByItem.Values
            .SelectMany(settings => new[] { settings?.DefaultSymbolItemGuid, settings?.DefaultFootprintItemGuid })
            .Where(guid => !string.IsNullOrEmpty(guid))
            .Select(guid => guid!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var models = modelGuids.Count == 0
            ? []
            : await _workspace.Components.ReadByIdsAsync(modelGuids, cancellationToken);
        var modelsByGuid = models.ToDictionary(record => record.ItemGuid, record => record, StringComparer.OrdinalIgnoreCase);

        // Not found among the live ones — one trash check for all templates at once, not
        // a request per template: instead of a raw GUID the row says where the item is.
        var stillMissing = modelGuids.Where(guid => !modelsByGuid.ContainsKey(guid)).ToList();
        var trashByGuid = stillMissing.Count == 0
            ? new Dictionary<string, TrashItemLocation>(StringComparer.OrdinalIgnoreCase)
            : await _workspace.Trash.FindItemsAsync(stillMissing, cancellationToken);

        string TypeLabel(string? guid) => guid is null
            ? "type not set"
            : types.FirstOrDefault(node => string.Equals(node.Guid, guid, StringComparison.OrdinalIgnoreCase))?.Path ?? guid;

        string ModelLabel(string? guid)
        {
            if (string.IsNullOrEmpty(guid))
            {
                return "—";
            }

            if (modelsByGuid.TryGetValue(guid, out ComponentRecord? record))
            {
                return record.Hrid;
            }

            return trashByGuid.TryGetValue(guid, out TrashItemLocation? location)
                ? ModelLinkLabels.InTrash(location.Hrid, location.DeletedAt)
                : ModelLinkLabels.Missing(guid);
        }

        // Almost all vault templates live in one folder (checked live: 105 of 107 —
        // in the templates system folder); showing it in every row costs extra thousands of characters.
        string? commonFolder = templates
            .GroupBy(template => template.FolderPath, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .FirstOrDefault()?.Key;

        var list = templates
            .OrderBy(template => template.Hrid, StringComparer.OrdinalIgnoreCase)
            .Select(template =>
            {
                TemplateSettings? settings = settingsByItem.GetValueOrDefault(template.ItemGuid);
                string? typeGuid = settings?.TypeGuid ?? template.ComponentTypeGuid;

                string folderPrefix = string.Equals(template.FolderPath, commonFolder, StringComparison.OrdinalIgnoreCase)
                    ? string.Empty
                    : $"[{template.FolderPath}] ";

                return $"{folderPrefix}{template.Hrid}: {TypeLabel(typeGuid)} "
                    + $"| SYM: {ModelLabel(settings?.DefaultSymbolItemGuid)} "
                    + $"| PCB: {ModelLabel(settings?.DefaultFootprintItemGuid)}";
            }).ToList();

        return new
        {
            count = list.Count,
            elapsedMs = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 1),
            commonFolder,
            templates = list,
        };
    }

    /// <summary>
    /// Offset inside a portion after trimming by the budget. The rows of a portion are shown in the order of
    /// identifiers, but continuing must follow the server order: the length of the prefix of the server
    /// order that fully fit into the shown rows is skipped.
    /// </summary>
    private static int SkippedInServerOrder(
        IReadOnlyList<ComponentRecord> serverOrder,
        IReadOnlyList<ComponentRecord> shownOrder,
        int shownCount)
    {
        var shown = shownOrder.Take(shownCount).Select(record => record.ItemGuid).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int prefix = 0;

        while (prefix < serverOrder.Count && shown.Contains(serverOrder[prefix].ItemGuid))
        {
            prefix++;
        }

        return prefix;
    }

    internal static ComponentCriteria BuildCriteria(
        string? folder,
        bool recursive,
        string? hrid,
        string? descriptionContains,
        string[]? parameterEquals,
        string? contentType,
        int limit,
        FolderMatchMode folderMatch = FolderMatchMode.Strict,
        int offset = 0) =>
        new()
        {
            Offset = Math.Max(offset, 0),
            Folder = folder,
            FolderMatch = folderMatch,
            Recursive = recursive,
            Hrid = hrid,
            DescriptionContains = descriptionContains,
            Parameters = (parameterEquals ?? []).Select(ParameterCriterion.Parse).ToList(),
            ContentType = contentType,
            Limit = limit,
        };

    /// <summary>
    /// The sign-in block of vault_status. With browser login there is no server session to describe,
    /// only the tokens; the session origin ("resumed", "opened") belongs to the other methods.
    /// </summary>
    internal static object DescribeSignIn(VaultOptions options, SessionSnapshot session)
    {
        if (options.UsesTokenAuthentication)
        {
            TokenInfo? info = session.Token;

            return new
            {
                method = "token (browser login)",
                loggedIn = info?.HasTokens ?? false,
                tokenExpiresAt = info?.ExpiresAt,
                canRefresh = info?.HasRefreshToken ?? false,
                loginPending = info?.LoginPending ?? false,
            };
        }

        return new
        {
            method = options.UsesWindowsAuthentication ? "windows (NTLM)" : "user name and password",
            loggedIn = session.IsEstablished,
            origin = DescribeOrigin(session.Origin),
            establishedAt = session.EstablishedAt,
        };
    }

    internal static string DescribeOrigin(SessionOrigin origin) => origin switch
    {
        SessionOrigin.Reused => "saved session resumed",
        SessionOrigin.Created => "new session opened",
        _ => "session not opened yet",
    };

    private async Task<List<object>> DescribeModelsAsync(
        IReadOnlyList<ALU_ItemRevisionLink> links,
        CancellationToken cancellationToken)
    {
        var childGuids = links
            .Select(link => link.ChildItemRevisionGUID)
            .Where(guid => !string.IsNullOrEmpty(guid))
            .ToList();

        if (childGuids.Count == 0)
        {
            return [];
        }

        var childRevisions = await _workspace.Gateway.GetItemRevisionsAsync(
            VaultFilter.In("GUID", childGuids),
            cancellationToken: cancellationToken);

        var contentTypes = await _workspace.Catalog.GetContentTypesAsync(cancellationToken);
        var byGuid = childRevisions
            .GroupBy(revision => revision.GUID, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        // Footprints go by number: the primary first, then 'PCBLIB 1', 'PCBLIB 2'…
        var ordered = links
            .OrderBy(link => LinkRole.IsFootprint(link.HRID) ? 1 : 0)
            .ThenBy(link => LinkRole.IsFootprint(link.HRID) ? FootprintLinks.EffectiveIndex(link) : 0)
            .ToList();

        return ordered.Select(link =>
        {
            byGuid.TryGetValue(link.ChildItemRevisionGUID ?? string.Empty, out ALU_ItemRevision? revision);
            bool hasFootprintData = LinkRole.IsFootprint(link.HRID)
                && LinkNormalizer.TryReadFootprint(link.Data, out _, out _);
            int footprintIndex = 0;
            bool isDefaultFootprint = false;

            if (hasFootprintData)
            {
                LinkNormalizer.TryReadFootprint(link.Data, out footprintIndex, out isDefaultFootprint);
            }

            contentTypes.TryGetValue(revision?.ContentTypeGUID ?? string.Empty, out ALU_ContentType? type);

            // Earlier Altium versions left the role empty: then it is visible from the model type.
            bool explicitRole = !string.IsNullOrEmpty(link.HRID) && !Guid.TryParse(link.HRID, out _);

            return (object)new
            {
                role = LinkRole.Describe(
                    explicitRole ? link.HRID : LinkNormalizer.RoleForContentType(type?.HRID) ?? link.HRID),
                roleCode = link.HRID,
                // Footprints: number (FootprintIndex, 0 — primary) and the primary flag — as Altium stores them.
                footprintIndex = hasFootprintData ? footprintIndex : (int?)null,
                isDefaultFootprint = hasFootprintData ? isDefaultFootprint : (bool?)null,
                itemHrid = revision?.ItemHRID,
                revisionId = revision?.RevisionId,
                revisionGuid = link.ChildItemRevisionGUID,
                contentType = type?.HRID,
                description = revision?.Description ?? revision?.ItemDescription,
                data = string.IsNullOrEmpty(link.Data) ? null : link.Data,
                vaultGuids = !string.IsNullOrEmpty(link.ParentVaultGUID) && !string.IsNullOrEmpty(link.ChildVaultGUID),
            };
        }).ToList();
    }

    internal static object Describe(ComponentRecord record) => new
    {
        hrid = record.Hrid,
        itemGuid = record.ItemGuid,
        description = record.Description,
        comment = record.Comment,
        folder = record.FolderPath,
        contentType = record.ContentType,
        revisionId = record.RevisionId,
        revisionGuid = record.RevisionGuid,
        lifeCycleState = record.LifeCycleState,
        modifiedAt = record.RevisionModifiedAt,
        parameters = record.Parameters,
    };
}
