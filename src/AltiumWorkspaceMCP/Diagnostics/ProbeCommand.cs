using System.Text.Json;
using AltiumWorkspaceMCP.Configuration;
using AltiumWorkspaceMCP.Vault;

namespace AltiumWorkspaceMCP.Diagnostics;

/// <summary>
/// Checking the setup without an MCP client: login, session state and reading a folder.
/// Allows separating connection problems from integration problems.
/// </summary>
public static class ProbeCommand
{
    public static async Task<int> RunAsync(VaultOptions options, string[] arguments)
    {
        var endpoints = new VaultEndpoints(options);
        await using var session = new VaultSession(options, endpoints);
        var gateway = new VaultGateway(endpoints, session);
        var catalog = new VaultCatalog(gateway);
        var components = new ComponentService(gateway, catalog);

        Console.Error.WriteLine($"Workspace            : {options.BaseUrl}");
        Console.Error.WriteLine($"Login                : {(options.UsesTokenAuthentication
            ? "token via browser (ALTIUM_AUTH=token)"
            : options.UsesWindowsAuthentication
                ? "Windows account"
                : options.UserName)}");
        Console.Error.WriteLine($"Write mode           : {options.WriteMode}");

        await session.AcquireAsync(CancellationToken.None);
        SessionSnapshot snapshot = await session.DescribeAsync(CancellationToken.None);
        Console.Error.WriteLine($"Session              : {(snapshot.Origin == SessionOrigin.Reused
            ? "saved one resumed"
            : "new one opened")}");
        Console.Error.WriteLine($"Session file         : {snapshot.StateFile ?? "none (tokens in memory only)"}");
        if (snapshot.Token is { } token)
        {
            Console.Error.WriteLine($"Account              : {token.Account ?? "not determined"}");
            Console.Error.WriteLine($"Token valid until    : {token.ExpiresAt:yyyy-MM-dd HH:mm:ss} UTC (refresh {(token.HasRefreshToken ? "possible" : "impossible")})");
        }

        int itemCount = await gateway.GetItemCountAsync(cancellationToken: CancellationToken.None);
        var folders = await catalog.GetFoldersAsync(CancellationToken.None);
        Console.Error.WriteLine($"Folders              : {folders.Count}");
        Console.Error.WriteLine($"Items                : {itemCount}");

        if (arguments.Length == 0)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("Root folders:");
            foreach (FolderNode folder in folders.Values
                .Where(node => node.ParentGuid is null)
                .OrderBy(node => node.Path))
            {
                Console.Error.WriteLine($"  {folder.Path}");
            }

            Console.Error.WriteLine();
            Console.Error.WriteLine("Give a folder path as the second argument to see its components.");
            return 0;
        }

        string target = arguments[0];
        var (records, plan) = await components.SearchAsync(
            new ComponentCriteria { Folder = target, Limit = 10 }, CancellationToken.None);
        Console.Error.WriteLine($"selection: {plan.Strategy}, {plan.ElapsedMs} ms");

        Console.Error.WriteLine();
        Console.Error.WriteLine($"Components in '{target}' (up to 10):");
        Console.Error.WriteLine(JsonSerializer.Serialize(records, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));

        return 0;
    }







    /// <summary>Shows which parameters occur in the vault folders.</summary>
    public static async Task<int> BenchAsync(VaultOptions options, string probe)
    {
        var endpoints = new VaultEndpoints(options);
        await using var session = new VaultSession(options, endpoints);
        var gateway = new VaultGateway(endpoints, session);

        var folders = await gateway.GetFoldersAsync(
            options: VaultRequestOptions.Of(
                VaultRequestOptions.IncludeFolderParameters,
                VaultRequestOptions.IncludeSystemFolders),
            cancellationToken: CancellationToken.None);

        var names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var samples = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var folder in folders)
        {
            foreach (var parameter in folder.FolderParameters ?? [])
            {
                string key = parameter.HRID ?? "<no name>";
                names[key] = names.GetValueOrDefault(key) + 1;
                if (!string.IsNullOrEmpty(parameter.DefaultValue))
                {
                    samples[key] = $"{folder.HRID}: {parameter.DefaultValue}";
                }
            }
        }

        Console.Error.WriteLine($"folders: {folders.Count}, distinct parameters: {names.Count}");
        foreach (var (name, count) in names.OrderByDescending(x => x.Value))
        {
            Console.Error.WriteLine($"  {name,-40} in {count} folders   {samples.GetValueOrDefault(name, "")}");
        }

        return 0;
    }

    /// <summary>
    /// Shows how the model is organized: content types, folder parameters and the content of
    /// a component template. Needed to understand the relation of a folder, a type and a template.
    /// </summary>
    public static async Task<int> InspectModelAsync(VaultOptions options, string folder)
    {
        var endpoints = new VaultEndpoints(options);
        await using var session = new VaultSession(options, endpoints);
        var gateway = new VaultGateway(endpoints, session);
        var catalog = new VaultCatalog(gateway);

        Console.Error.WriteLine("=== content types ===");
        var types = await catalog.GetContentTypesAsync(CancellationToken.None);
        foreach (var type in types.Values.OrderBy(t => t.HRID))
        {
            Console.Error.WriteLine($"  {type.HRID,-34} {type.GUID}");
        }

        Console.Error.WriteLine();
        Console.Error.WriteLine($"=== folder '{folder}' ===");
        FolderNode node = await catalog.ResolveFolderAsync(folder, CancellationToken.None);
        var folders = await gateway.GetFoldersAsync(
            VaultFilter.Equal("GUID", node.Guid),
            VaultRequestOptions.Of(VaultRequestOptions.IncludeFolderParameters),
            cancellationToken: CancellationToken.None);

        foreach (var f in folders)
        {
            Console.Error.WriteLine($"  HRID={f.HRID} FolderTypeGUID={f.FolderTypeGUID} Attributes={f.Attributes}");
            foreach (var parameter in f.FolderParameters ?? [])
            {
                Console.Error.WriteLine($"    parameter {parameter.HRID} = '{parameter.DefaultValue}'");
            }
        }

        Console.Error.WriteLine();
        Console.Error.WriteLine("=== folder types ===");
        var folderTypes = await gateway.GetFolderTypesAsync(CancellationToken.None);
        foreach (var ft in folderTypes)
        {
            Console.Error.WriteLine($"  {ft.HRID,-34} {ft.GUID}");
        }

        Console.Error.WriteLine();
        Console.Error.WriteLine("=== component templates ===");
        var templateType = types.Values.FirstOrDefault(t =>
            string.Equals(t.HRID, "altium-component-template", StringComparison.OrdinalIgnoreCase));
        if (templateType is not null)
        {
            var templates = await gateway.GetItemsAsync(
                VaultFilter.Equal("ContentTypeGUID", templateType.GUID),
                VaultRequestOptions.Of(
                    VaultRequestOptions.IncludeItemRevisions,
                    VaultRequestOptions.IncludeItemRevisionParameters),
                limit: 20,
                cancellationToken: CancellationToken.None);

            foreach (var template in templates)
            {
                var latest = ComponentService.SelectLatestRevision(template);
                var parameters = ComponentService.ExtractParameters(latest);
                Console.Error.WriteLine($"  {template.HRID}  folder={template.FolderGUID}  parameters={parameters.Count}");
                foreach (var (key, value) in parameters.Take(40))
                {
                    Console.Error.WriteLine($"      {key} = '{value}'");
                }
            }
        }

        Console.Error.WriteLine();
        Console.Error.WriteLine("=== reference component with all fields ===");
        var sample = await gateway.GetItemsAsync(
            VaultFilter.Equal("HRID", "CMP-000-00046"),
            VaultRequestOptions.Of(
                VaultRequestOptions.IncludeItemRevisions,
                VaultRequestOptions.IncludeItemRevisionParameters,
                VaultRequestOptions.IncludeItemParameters),
            limit: 1,
            cancellationToken: CancellationToken.None);

        foreach (var item in sample)
        {
            Console.Error.WriteLine($"  item: HRID={item.HRID} ContentTypeGUID={item.ContentTypeGUID}");
            Console.Error.WriteLine($"           LifeCycleDefinitionGUID={item.LifeCycleDefinitionGUID}");
            Console.Error.WriteLine($"           RevisionNamingSchemeGUID={item.RevisionNamingSchemeGUID}");
            foreach (var parameter in item.ItemParameters ?? [])
            {
                Console.Error.WriteLine($"    item parameter {parameter.HRID} = '{parameter.ParameterValue}'");
            }

            var latest = ComponentService.SelectLatestRevision(item);
            Console.Error.WriteLine($"  revision: {latest?.RevisionId} Comment='{latest?.Comment}'");
            foreach (var parameter in latest?.RevisionParameters ?? [])
            {
                Console.Error.WriteLine(
                    $"    revision parameter  {parameter.HRID} = '{parameter.ParameterValue}' "
                    + $"(type {parameter.ParameterTypeGUID})");
            }

            var links = await gateway.GetItemRevisionLinksAsync(
                VaultFilter.Equal("ParentItemRevisionGUID", latest!.GUID),
                cancellationToken: CancellationToken.None);
            foreach (var link in links)
            {
                Console.Error.WriteLine($"    link → {link.ChildItemRevisionGUID} type='{link.LinkTypeGUID}' "
                    + $"HRID='{link.HRID}' Data='{link.Data}'");
            }
        }

        Console.Error.WriteLine();
        Console.Error.WriteLine("=== revision link types ===");
        var linkTypes = await gateway.GetItemRevisionLinkTypesAsync(CancellationToken.None);
        foreach (var linkType in linkTypes)
        {
            Console.Error.WriteLine($"  {linkType.HRID,-40} {linkType.GUID}");
        }

        return 0;
    }

    /// <summary>Shows which parameters occur in the vault folders.</summary>
    public static async Task<int> FolderParamsAsync(VaultOptions options)
    {
        var endpoints = new VaultEndpoints(options);
        await using var session = new VaultSession(options, endpoints);
        var gateway = new VaultGateway(endpoints, session);

        var folders = await gateway.GetFoldersAsync(
            options: VaultRequestOptions.Of(
                VaultRequestOptions.IncludeFolderParameters,
                VaultRequestOptions.IncludeSystemFolders),
            cancellationToken: CancellationToken.None);

        var names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var samples = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var folder in folders)
        {
            foreach (var parameter in folder.FolderParameters ?? [])
            {
                string key = parameter.HRID ?? "<no name>";
                names[key] = names.GetValueOrDefault(key) + 1;
                if (!string.IsNullOrEmpty(parameter.DefaultValue))
                {
                    samples[key] = $"{folder.HRID} = {parameter.DefaultValue}";
                }
            }
        }

        Console.Error.WriteLine($"folders: {folders.Count}, distinct parameters: {names.Count}");
        foreach (var (name, count) in names.OrderByDescending(x => x.Value))
        {
            Console.Error.WriteLine($"  {name,-34} in {count,4} folders   {samples.GetValueOrDefault(name, "")}");
        }

        return 0;
    }

    /// <summary>
    /// Compares all Datasheets folders with the Altium reference: the folder type, the system bit, the naming
    /// scheme. The exit code is 1 if there are differing ones.
    /// </summary>
    public static async Task<int> SystemFoldersAsync(VaultOptions options)
    {
        var endpoints = new VaultEndpoints(options);
        await using var session = new VaultSession(options, endpoints);
        var gateway = new VaultGateway(endpoints, session);
        var catalog = new VaultCatalog(gateway);
        var components = new ComponentService(gateway, catalog);
        var folders = new FolderService(gateway, catalog);

        var reports = await folders.InspectSystemFoldersAsync(null, CancellationToken.None);
        var different = reports.Where(report => report.Differences.Count > 0).ToList();

        Console.Error.WriteLine($"Datasheets folders: {reports.Count}, differ from the Altium reference: {different.Count}");
        Console.Error.WriteLine($"reference: type {FolderService.DatasheetFolderTypeGuid}, Attributes = {FolderNode.SystemAttribute}, "
            + $"scheme '{FolderService.DatasheetNamingScheme}'");

        foreach (var report in different)
        {
            Console.Error.WriteLine($"  {report.Folder.Path}");
            foreach (string difference in report.Differences)
            {
                Console.Error.WriteLine($"      {difference}");
            }
        }

        return different.Count == 0 ? 0 : 1;
    }

    /// <summary>Shows the lifecycle states and their visibility and applicability flags.</summary>
    public static async Task<int> StatesAsync(VaultOptions options)
    {
        var endpoints = new VaultEndpoints(options);
        await using var session = new VaultSession(options, endpoints);
        var gateway = new VaultGateway(endpoints, session);
        foreach (var state in await gateway.GetLifeCycleStatesAsync(CancellationToken.None))
        {
            Console.Error.WriteLine($"{state.GUID}  {state.HRID,-24} idx={state.StateIndex} visible={state.IsVisible} applicable={state.IsApplicable} initial={state.IsInitialState} stage={state.LifeCycleStageGUID} def={state.LifeCycleDefinitionGUID}");
        }

        return 0;
    }

    /// <summary>Shows the tag families and the component type tree.</summary>
    public static async Task<int> TagsAsync(VaultOptions options)
    {
        var endpoints = new VaultEndpoints(options);
        await using var session = new VaultSession(options, endpoints);
        var gateway = new VaultGateway(endpoints, session);

        var families = await gateway.GetTagFamiliesAsync(cancellationToken: CancellationToken.None);
        Console.Error.WriteLine($"tag families: {families.Count}");
        foreach (var family in families)
        {
            Console.Error.WriteLine($"  {family.HRID,-28} {family.GUID}  system={family.IsSystem} "
                + $"tags={family.Tags?.Count ?? 0}");
        }

        var tags = await gateway.GetTagsAsync(
            VaultFilter.Equal("TagFamilyGUID", VaultTags.ComponentTypeFamilyGuid),
            CancellationToken.None);

        Console.Error.WriteLine();
        Console.Error.WriteLine($"component types: {tags.Count}");

        var byParent = tags
            .GroupBy(tag => tag.ParentTagGUID ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        void Print(string parent, int depth)
        {
            if (!byParent.TryGetValue(parent, out var children))
            {
                return;
            }

            foreach (var tag in children.OrderBy(tag => tag.HRID, StringComparer.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine($"  {new string(' ', depth * 2)}{tag.HRID}   [{tag.GUID}]");
                Print(tag.GUID, depth + 1);
            }
        }

        Print(string.Empty, 0);

        var sample = await gateway.GetItemTagsAsync(limit: 5, cancellationToken: CancellationToken.None);
        Console.Error.WriteLine();
        Console.Error.WriteLine($"tag assignments to items (first ones): {sample.Count}");
        foreach (var itemTag in sample.Take(5))
        {
            Console.Error.WriteLine($"  item {itemTag.ItemGUID} → tag {itemTag.TagGUID}");
        }

        return 0;
    }

    /// <summary>
    /// Looks for components whose active revision was left without links although
    /// an earlier one had them: a sign that the links were lost during an edit.
    /// </summary>
    /// <summary>
    /// Parts whose active revisions reference models from the trash: the link is intact, but the model
    /// item is deleted. Read-only.
    /// </summary>
    public static async Task<int> FindDeletedTargetsAsync(VaultOptions options, string folder)
    {
        var endpoints = new VaultEndpoints(options);
        await using var session = new VaultSession(options, endpoints);
        var gateway = new VaultGateway(endpoints, session);
        var catalog = new VaultCatalog(gateway);
        var components = new ComponentService(gateway, catalog);

        var (records, _) = await components.SearchAsync(
            new ComponentCriteria { Folder = folder, Recursive = true, Limit = 20000 },
            CancellationToken.None);
        Console.Error.WriteLine($"components checked: {records.Count}");

        var byRevision = records
            .Where(record => !string.IsNullOrEmpty(record.RevisionGuid))
            .ToDictionary(record => record.RevisionGuid!, StringComparer.OrdinalIgnoreCase);

        var links = await VaultGateway.ReadInChunksAsync(
            byRevision.Keys,
            chunk => gateway.GetItemRevisionLinksAsync(
                VaultFilter.In("ParentItemRevisionGUID", chunk), limit: 100000,
                cancellationToken: CancellationToken.None));

        var childRevisionGuids = links
            .Select(link => link.ChildItemRevisionGUID)
            .Where(guid => !string.IsNullOrEmpty(guid))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var childRevisions = await VaultGateway.ReadInChunksAsync(
            childRevisionGuids,
            chunk => gateway.GetItemRevisionsAsync(
                VaultFilter.In("GUID", chunk), limit: 100000, cancellationToken: CancellationToken.None));
        var revisionToItem = childRevisions
            .Where(revision => !string.IsNullOrEmpty(revision.ItemGUID))
            .ToDictionary(revision => revision.GUID, revision => revision.ItemGUID, StringComparer.OrdinalIgnoreCase);

        Console.Error.WriteLine($"links: {links.Count}, model revisions: {childRevisionGuids.Count}, revisions found: {revisionToItem.Count}");

        var itemGuids = revisionToItem.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var deleted = await VaultGateway.ReadInChunksAsync(
            itemGuids,
            chunk => gateway.GetItemsAsync(
                VaultFilter.In("GUID", chunk),
                VaultRequestOptions.Of(VaultRequestOptions.IncludeDeletedOnly),
                limit: 100000,
                cancellationToken: CancellationToken.None));
        var deletedByGuid = deleted.ToDictionary(item => item.GUID, StringComparer.OrdinalIgnoreCase);
        var folders = await catalog.GetFoldersAsync(CancellationToken.None);

        Console.Error.WriteLine($"models in the trash: {deletedByGuid.Count}");

        var affected = new List<(string Component, string Folder, string Role, string Model, string ModelFolder)>();

        foreach (var link in links)
        {
            if (link.ChildItemRevisionGUID is not { } child
                || !revisionToItem.TryGetValue(child, out string? itemGuid)
                || !deletedByGuid.TryGetValue(itemGuid, out AltiumWorkspaceMCP.Soap.Vault.ALU_Item? item))
            {
                continue;
            }

            ComponentRecord record = byRevision[link.ParentItemRevisionGUID];
            string modelFolder = folders.TryGetValue(item.FolderGUID ?? string.Empty, out FolderNode? node) ? node.Path : item.FolderGUID ?? "";
            affected.Add((record.Hrid, record.FolderPath, link.HRID ?? "", item.HRID ?? item.GUID, modelFolder));
        }

        Console.Error.WriteLine($"parts with a reference to a deleted model: {affected.Select(a => a.Component).Distinct().Count()}, links: {affected.Count}");
        Console.Error.WriteLine("by models (model — model folder — role — parts):");

        foreach (var group in affected.GroupBy(a => (a.Model, a.ModelFolder, a.Role)).OrderByDescending(g => g.Count()))
        {
            Console.Error.WriteLine($"  {group.Key.Model,-22} {group.Key.ModelFolder,-50} {group.Key.Role,-18} {group.Count()}");
        }

        var missingFolders = deletedByGuid.Values
            .Select(item => item.FolderGUID)
            .Where(guid => !string.IsNullOrEmpty(guid) && !folders.ContainsKey(guid!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (missingFolders.Count > 0)
        {
            var deletedFolders = await gateway.GetFoldersAsync(
                VaultFilter.In("GUID", missingFolders!),
                VaultRequestOptions.Of(VaultRequestOptions.IncludeDeletedOnly, VaultRequestOptions.IncludeSystemFolders),
                cancellationToken: CancellationToken.None);
            Console.Error.WriteLine("deleted model folders (GUID — name — parent — models):");

            foreach (var deletedFolder in deletedFolders)
            {
                string parent = folders.TryGetValue(deletedFolder.ParentFolderGUID ?? string.Empty, out FolderNode? p)
                    ? p.Path
                    : deletedFolder.ParentFolderGUID ?? "";
                int count = deletedByGuid.Values.Count(item => string.Equals(item.FolderGUID, deletedFolder.GUID, StringComparison.OrdinalIgnoreCase));
                Console.Error.WriteLine($"  {deletedFolder.GUID}  '{deletedFolder.HRID}'  in '{parent}'  {count}");
            }
        }

        Console.Error.WriteLine("by part folders:");

        foreach (var group in affected.GroupBy(a => a.Folder).OrderByDescending(g => g.Count()))
        {
            Console.Error.WriteLine($"  {group.Key,-60} {group.Select(a => a.Component).Distinct().Count()}");
        }

        return 0;
    }

    /// <summary>
    /// A check of the "in use" test before deletion: how many live parts reference an item
    /// by GetWhereUsedByItem data (as in UsageService) and how many really do — by the links
    /// of the active revisions. Read-only.
    /// </summary>
    public static async Task<int> UsageCheckAsync(VaultOptions options, string identifier)
    {
        var endpoints = new VaultEndpoints(options);
        await using var session = new VaultSession(options, endpoints);
        var gateway = new VaultGateway(endpoints, session);
        var catalog = new VaultCatalog(gateway);
        var components = new ComponentService(gateway, catalog);
        var usageService = new UsageService(gateway, components);

        var found = await components.ReadByIdsAsync([identifier], CancellationToken.None);
        if (found.Count != 1)
        {
            Console.Error.WriteLine($"{identifier}: not found");
            return 1;
        }

        ComponentRecord target = found[0];
        var whereUsed = await gateway.GetWhereUsedAsync(target.ItemGuid, limit: 5000, cancellationToken: CancellationToken.None);
        var viaService = await usageService.FindExternalUsageAsync(target.ItemGuid, [], CancellationToken.None);
        Console.Error.WriteLine($"{target.Hrid}: GetWhereUsedByItem TotalCount={whereUsed.TotalCount}, links received={whereUsed.Relations.Count}, "
            + $"with ParentRevisionGuid={whereUsed.Relations.Count(r => !string.IsNullOrEmpty(r.ParentRevisionGuid))}");
        Console.Error.WriteLine($"UsageService: live parts {viaService.Count}");

        var targetRevisions = await gateway.GetItemRevisionsAsync(
            VaultFilter.Equal("ItemGUID", target.ItemGuid), limit: 100000, cancellationToken: CancellationToken.None);
        var targetRevisionGuids = targetRevisions.Select(r => r.GUID).ToList();

        var links = await VaultGateway.ReadInChunksAsync(
            targetRevisionGuids,
            chunk => gateway.GetItemRevisionLinksAsync(
                VaultFilter.In("ChildItemRevisionGUID", chunk), limit: 1000000, cancellationToken: CancellationToken.None));
        var parentRevisions = links.Select(l => l.ParentItemRevisionGUID).Where(g => !string.IsNullOrEmpty(g))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var parentItemGuids = (await VaultGateway.ReadInChunksAsync(
                parentRevisions,
                chunk => gateway.GetItemRevisionsAsync(VaultFilter.In("GUID", chunk), limit: 1000000, cancellationToken: CancellationToken.None)))
            .Select(r => r.ItemGUID).Where(g => !string.IsNullOrEmpty(g)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var parents = await components.ReadByIdsAsync(parentItemGuids!, CancellationToken.None);
        var parentRevisionSet = parentRevisions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        int live = parents.Count(p => p.RevisionGuid is not null && parentRevisionSet.Contains(p.RevisionGuid));

        Console.Error.WriteLine($"by links: revisions of {target.Hrid} {targetRevisionGuids.Count}, links to them {links.Count}, "
            + $"parent revisions {parentRevisions.Count}, parent items {parents.Count}, live parts (active revision) {live}");

        var itemLinks = await gateway.GetItemLinksAsync(
            VaultFilter.Equal("ChildItemGUID", target.ItemGuid), cancellationToken: CancellationToken.None);
        Console.Error.WriteLine($"item links (ALU_ItemLink, like datasheets) to {target.Hrid}: {itemLinks.Count}");
        return 0;
    }

    public static async Task<int> FindLostLinksAsync(VaultOptions options, string folder)
    {
        var endpoints = new VaultEndpoints(options);
        await using var session = new VaultSession(options, endpoints);
        var gateway = new VaultGateway(endpoints, session);
        var catalog = new VaultCatalog(gateway);
        var components = new ComponentService(gateway, catalog);

        var (records, _) = await components.SearchAsync(
            new ComponentCriteria { Folder = folder, Recursive = true, Limit = 5000 },
            CancellationToken.None);

        Console.Error.WriteLine($"components checked: {records.Count}");

        var currentGuids = records
            .Select(record => record.RevisionGuid)
            .Where(guid => !string.IsNullOrEmpty(guid))
            .Select(guid => guid!)
            .ToList();

        var currentLinks = await VaultGateway.ReadInChunksAsync(
            currentGuids,
            chunk => gateway.GetItemRevisionLinksAsync(
                VaultFilter.In("ParentItemRevisionGUID", chunk), limit: 100000,
                cancellationToken: CancellationToken.None));

        var withLinks = currentLinks
            .Select(link => link.ParentItemRevisionGUID ?? string.Empty)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var suspects = records.Where(record => !withLinks.Contains(record.RevisionGuid ?? string.Empty)).ToList();
        Console.Error.WriteLine($"without links on the active revision: {suspects.Count}");

        int recoverable = 0;
        foreach (var record in suspects)
        {
            var history = await components.GetRevisionsAsync(record.ItemGuid, CancellationToken.None);
            var older = history.Where(item => item.GUID != record.RevisionGuid).ToList();

            if (older.Count == 0)
            {
                continue;
            }

            var olderLinks = await VaultGateway.ReadInChunksAsync(
                older.Select(item => item.GUID),
                chunk => gateway.GetItemRevisionLinksAsync(
                    VaultFilter.In("ParentItemRevisionGUID", chunk), limit: 100000,
                    cancellationToken: CancellationToken.None));

            if (olderLinks.Count > 0)
            {
                recoverable++;
                Console.Error.WriteLine($"  {record.Hrid,-22} rev.{record.RevisionId,-4} "
                    + $"links in earlier revisions: {olderLinks.Count}");
            }
        }

        Console.Error.WriteLine($"recoverable from earlier revisions: {recoverable}");
        return 0;
    }

    /// <summary>
    /// Creates an unreleased revision for a test component — reproduces the state
    /// after an interrupted edit, to check that the links are not lost when it is brought to completion.
    /// </summary>
    public static async Task<int> MakeUnreleasedAsync(VaultOptions options, string hrid)
    {
        var endpoints = new VaultEndpoints(options);
        await using var session = new VaultSession(options, endpoints);
        var gateway = new VaultGateway(endpoints, session);
        var catalog = new VaultCatalog(gateway);
        var components = new ComponentService(gateway, catalog);

        var found = await components.ReadByIdsAsync([hrid], CancellationToken.None);
        ComponentRecord record = found[0];
        var current = record.LatestRevision!;

        var links = await gateway.GetItemRevisionLinksAsync(
            VaultFilter.Equal("ParentItemRevisionGUID", current.GUID),
            cancellationToken: CancellationToken.None);

        Console.Error.WriteLine($"source revision {current.RevisionId}, links {links.Count}");

        string nextId = RevisionService.NextRevisionId(current);
        string newGuid = Guid.NewGuid().ToString("D").ToUpperInvariant();
        var next = RevisionService.CreateNextRevision(
            current, new ComponentChange(), nextId, newGuid, new Dictionary<string, ParameterTypeInfo>(), corrections: null);

        await gateway.AddItemRevisionsAsync([next], CancellationToken.None);

        // The links are created separately and the release is not done: this gives exactly the state
        // in which the revision exists but is not released.
        var copies = links.Select(link => new AltiumWorkspaceMCP.Soap.Vault.ALU_ItemRevisionLink
        {
            GUID = Guid.NewGuid().ToString("D").ToUpperInvariant(),
            HRID = link.HRID,
            ParentItemRevisionGUID = newGuid,
            ChildItemRevisionGUID = link.ChildItemRevisionGUID,
            ChildVaultGUID = link.ChildVaultGUID,
            ParentVaultGUID = link.ParentVaultGUID,
            LinkTypeGUID = link.LinkTypeGUID,
            Data = link.Data,
            LinkParameters = link.LinkParameters,
        }).ToList();

        if (copies.Count > 0)
        {
            await gateway.AddItemRevisionLinksAsync(copies, CancellationToken.None);
        }

        Console.Error.WriteLine($"unreleased revision {nextId} created with links {copies.Count}");
        return 0;
    }

    /// <summary>Shows how datasheets are linked to components and where they lie.</summary>
    public static async Task<int> DatasheetsAsync(VaultOptions options)
    {
        var endpoints = new VaultEndpoints(options);
        await using var session = new VaultSession(options, endpoints);
        var gateway = new VaultGateway(endpoints, session);
        var catalog = new VaultCatalog(gateway);
        var components = new ComponentService(gateway, catalog);

        var (sheets, _) = await components.SearchAsync(
            new ComponentCriteria { ContentType = "altium-datasheet", Limit = 20 },
            CancellationToken.None);

        Console.Error.WriteLine($"datasheets in the selection: {sheets.Count}");

        foreach (var sheet in sheets.Take(6))
        {
            var usage = await gateway.GetWhereUsedAsync(sheet.ItemGuid, cancellationToken: CancellationToken.None);
            Console.Error.WriteLine($"  {sheet.Hrid,-18} folder '{sheet.FolderPath}'  "
                + $"used by: {usage.TotalCount} ({string.Join(", ", usage.ParentItems.Take(4).Select(i => i.HRID))})");
        }

        // How a link looks from the component's side.
        var withSheets = sheets.FirstOrDefault();
        if (withSheets is not null)
        {
            var usage = await gateway.GetWhereUsedAsync(withSheets.ItemGuid, cancellationToken: CancellationToken.None);
            var parent = usage.ParentItems.FirstOrDefault();
            if (parent is not null)
            {
                var parentRecords = await components.ReadByIdsAsync([parent.GUID], CancellationToken.None);
                var links = await gateway.GetItemRevisionLinksAsync(
                    VaultFilter.Equal("ParentItemRevisionGUID", parentRecords[0].RevisionGuid ?? string.Empty),
                    cancellationToken: CancellationToken.None);

                Console.Error.WriteLine();
                Console.Error.WriteLine($"component {parentRecords[0].Hrid} (folder '{parentRecords[0].FolderPath}'):");
                foreach (var link in links)
                {
                    Console.Error.WriteLine($"    revision link '{link.HRID}' → {link.ChildItemRevisionGUID}");
                }

                var itemLinks = await gateway.GetItemLinksAsync(
                    VaultFilter.Equal("ParentItemGUID", parentRecords[0].ItemGuid),
                    cancellationToken: CancellationToken.None);

                foreach (var link in itemLinks)
                {
                    Console.Error.WriteLine($"    item link '{link.HRID}' → {link.ChildItemGUID} "
                        + $"type {link.LinkTypeGUID}");
                }
            }
        }

        return 0;
    }

    /// <summary>
    /// Reconnaissance for future work with symbols and footprints: what is in
    /// a revision and how its content is fetched from the server.
    /// </summary>
    public static async Task<int> ModelsAsync(VaultOptions options)
    {
        var endpoints = new VaultEndpoints(options);
        await using var session = new VaultSession(options, endpoints);
        var gateway = new VaultGateway(endpoints, session);
        var catalog = new VaultCatalog(gateway);
        var components = new ComponentService(gateway, catalog);

        foreach (string contentType in new[] { "altium-symbol", "altium-pcb-component" })
        {
            var (items, _) = await components.SearchAsync(
                new ComponentCriteria { ContentType = contentType, Limit = 3 }, CancellationToken.None);

            Console.Error.WriteLine($"=== {contentType}: {items.Count} pcs ===");

            foreach (var item in items)
            {
                Console.Error.WriteLine($"  {item.Hrid}  rev.{item.RevisionId}  folder '{item.FolderPath}'");

                var urls = await gateway.GetRevisionDownloadUrlsAsync(
                    [item.RevisionGuid ?? string.Empty], CancellationToken.None);

                foreach (var url in urls)
                {
                    Console.Error.WriteLine($"      size {url.Size} bytes");
                    Console.Error.WriteLine($"      URL  {url.URL}");
                    if (!string.IsNullOrEmpty(url.URL2))
                    {
                        Console.Error.WriteLine($"      URL2 {url.URL2}");
                    }
                }
            }
        }

        return 0;
    }

    /// <summary>Closes the saved session — frees the slot on the server.</summary>
    public static async Task<int> LogoutAsync(VaultOptions options)
    {
        var endpoints = new VaultEndpoints(options);
        var session = new VaultSession(options, endpoints);

        await session.CloseAsync();
        Console.Error.WriteLine(options.UsesTokenAuthentication
            ? "Tokens forgotten: the refresh token was revoked on the server, the token file was deleted."
            : "Session closed, the server slot was freed.");
        return 0;
    }
}
