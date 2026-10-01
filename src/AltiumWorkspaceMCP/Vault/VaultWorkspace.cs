using AltiumWorkspaceMCP.Configuration;
using AltiumWorkspaceMCP.Safety;
using AltiumWorkspaceMCP.Vault.Rest;

namespace AltiumWorkspaceMCP.Vault;

/// <summary>
/// The workspace services assembled together: connection, directories,
/// components, versioning and write safety rules.
/// </summary>
public sealed class VaultWorkspace : IAsyncDisposable
{
    private readonly HttpClient _http;

    public VaultWorkspace(VaultOptions options)
    {
        Options = options;
        Endpoints = new VaultEndpoints(options);
        Session = new VaultSession(options, Endpoints);
        Gateway = new VaultGateway(Endpoints, Session);
        Catalog = new VaultCatalog(Gateway);
        Components = new ComponentService(Gateway, Catalog);

        _http = new HttpClient { Timeout = options.Timeout };
        Scripts = new VaultScriptExecutor(Endpoints, Session, _http);
        ParameterTypes = new ParameterTypeResolver(Gateway);
        ParameterNames = new ParameterNames(Gateway);
        Links = new LinkNormalizer(Gateway, Catalog);
        Revisions = new RevisionService(Gateway, Scripts, Links, ParameterTypes);
        Batch = new RevisionBatch(Gateway, Scripts, Links, ParameterTypes);
        Templates = new TemplateService(Gateway, Catalog, Components, Scripts, ParameterTypes, Links, _http);
        Usage = new UsageService(Gateway, Components);
        Trash = new TrashService(Gateway, Catalog, Usage, Templates);
        Folders = new FolderService(Gateway, Catalog);
        Datasheets = new DatasheetService(Gateway, Catalog, Folders);
        DeletionGuard = new DeletionGuard(Usage, Templates);
        TemplateLinkChecks = new TemplateLinkCheckService(Templates, Components, Trash);
        ComponentTypes = new ComponentTypeService(Gateway);
        Copies = new ComponentCopyService(Gateway, Catalog, Components, Scripts, Links, ParameterTypes, ComponentTypes, Templates);
        Health = new ComponentHealthService(Gateway, ComponentTypes, Templates);
        Exchange = new ExchangePaths(options);

        ServiceDirectory = new ServiceDirectory(options.BaseUrl, _http);
        Rest = new WorkspaceRestClient(Session, _http);
        Search = new SearchClient(ServiceDirectory, Rest);
        Panel = new ComponentPanelService(Search, ComponentTypes, Catalog);
        Duplicates = new DuplicateFinder(Search, Catalog);

        Guard = new ChangeGuard(options);
        Audit = new AuditLog(options.StateDirectory);

        Models = new ModelFileService(Gateway, Components, Exchange, _http, Scripts, Batch, Links, Guard, options, Catalog);
    }

    public VaultOptions Options { get; }

    public VaultEndpoints Endpoints { get; }

    public VaultSession Session { get; }

    public VaultGateway Gateway { get; }

    public VaultCatalog Catalog { get; }

    public ComponentService Components { get; }

    public VaultScriptExecutor Scripts { get; }

    /// <summary>Types of new parameters — by the same parameters already found in the vault.</summary>
    public ParameterTypeResolver ParameterTypes { get; }

    /// <summary>Checking parameter names and hints on typos.</summary>
    public ParameterNames ParameterNames { get; }

    /// <summary>Bringing links to the format that Altium Designer understands.</summary>
    public LinkNormalizer Links { get; }

    public RevisionService Revisions { get; }

    /// <summary>Batch edit — the main path for large selections.</summary>
    public RevisionBatch Batch { get; }

    public FolderService Folders { get; }

    /// <summary>Live references to an object — the check before deletion.</summary>
    public UsageService Usage { get; }

    /// <summary>One "must not be deleted" check: live parts and templates.</summary>
    public DeletionGuard DeletionGuard { get; }

    /// <summary>The vault trash: the list and folder restore.</summary>
    public TrashService Trash { get; }

    /// <summary>Datasheets that follow the parts when they are moved.</summary>
    public DatasheetService Datasheets { get; }

    public TemplateService Templates { get; }

    /// <summary>Checking template references to the default symbol and footprint: alive, in the trash or gone.</summary>
    public TemplateLinkCheckService TemplateLinkChecks { get; }

    /// <summary>Component types — tags of the Altium system family.</summary>
    public ComponentTypeService ComponentTypes { get; }

    public ComponentCopyService Copies { get; }

    /// <summary>Checking components for compatibility with Altium Designer.</summary>
    public ComponentHealthService Health { get; }

    /// <summary>The model file exchange directory with altium-designer-mcp.</summary>
    public ExchangePaths Exchange { get; }

    /// <summary>Symbol and footprint files: export, upload of the edited ones and moving components.</summary>
    public ModelFileService Models { get; }

    /// <summary>Addresses of the workspace services (search, parts catalog).</summary>
    public ServiceDirectory ServiceDirectory { get; }

    /// <summary>REST client of the workspace services with a shared session.</summary>
    public WorkspaceRestClient Rest { get; }

    /// <summary>The search service — the Components panel index.</summary>
    public SearchClient Search { get; }

    /// <summary>Search for parts with the same MPN and LCSC Part#.</summary>
    public DuplicateFinder Duplicates { get; }

    /// <summary>An analog of the Components panel: types, parameters and a part table by the search index.</summary>
    public ComponentPanelService Panel { get; }

    public ChangeGuard Guard { get; }

    public AuditLog Audit { get; }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await Session.DisposeAsync();
    }
}
