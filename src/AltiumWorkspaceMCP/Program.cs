using AltiumWorkspaceMCP.Configuration;
using AltiumWorkspaceMCP.Diagnostics;
using AltiumWorkspaceMCP.Mcp;
using AltiumWorkspaceMCP.Vault;

// Diagnostics may print non-ASCII text (e.g. Russian part names): without UTF-8 the Windows console garbles it in the OEM code page.
Console.OutputEncoding = System.Text.Encoding.UTF8;

// The server works over stdio: standard output is taken by the MCP protocol,
// so all diagnostics go to stderr.
try
{
    VaultOptions options = VaultOptions.FromEnvironment();

    string command = args.Length > 0 ? args[0].ToLowerInvariant() : "serve";

    return command switch
    {
        "serve" => await McpHost.RunAsync(options),
        "bridge" => await BridgeHost.RunAsync(options, args.Skip(1).ToArray()),
        "param-audit" => await ParameterAuditCommand.RunAsync(options, args.Skip(1).ToArray()),
        "codec-check" => await CodecCheckCommand.RunAsync(options),
        "dump" => await ComponentDumpCommand.RunAsync(options, args.Skip(1).ToArray()),
        "probe" => await ProbeCommand.RunAsync(options, args.Skip(1).ToArray()),
        "logout" => await ProbeCommand.LogoutAsync(options),
        "login" => await LoginCommand.RunAsync(options),
        "bench" => await ProbeCommand.BenchAsync(options, args.Length > 1 ? args[1] : "Components"),
        "folder-params" => await ProbeCommand.FolderParamsAsync(options),
        "usage-check" => await ProbeCommand.UsageCheckAsync(options, args[1]),
        "lost-links" => await ProbeCommand.FindLostLinksAsync(options, args.Length > 1 ? args[1] : "Components"),
        "deleted-targets" => await ProbeCommand.FindDeletedTargetsAsync(options, args.Length > 1 ? args[1] : "Components"),
        "make-unreleased" => await ProbeCommand.MakeUnreleasedAsync(options, args[1]),
        "datasheets" => await ProbeCommand.DatasheetsAsync(options),
        "models" => await ProbeCommand.ModelsAsync(options),
        "tags" => await ProbeCommand.TagsAsync(options),
        "states" => await ProbeCommand.StatesAsync(options),
        "wire-dump" => await WireDumpCommand.RunAsync(),
        "services" => await ServicesCommand.RunAsync(options),
        "search-raw" => await ServicesCommand.RawSearchAsync(options, args[1]),
        "system-folders" => await ProbeCommand.SystemFoldersAsync(options),
        "inspect-model" => await ProbeCommand.InspectModelAsync(options, args.Length > 1 ? args[1] : "R 2512"),
        "--help" or "-h" or "help" => PrintUsage(),
        _ => PrintUnknown(command),
    };
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Error: {exception.Message}");
    return 1;
}

static int PrintUsage()
{
    Console.Error.WriteLine(
        """
        altium-vault-mcp — access to an Altium Workspace over MCP.

        Commands:
          serve            Run the MCP server over stdio (default).
          bridge [address] [port]
                           The same server over TCP — for clients that cannot launch
                           the Windows process directly. Default 127.0.0.1:18792,
                           "any" opens the port to the network.
          probe [path]     Check the connection and show the folder contents.
          logout           Close the saved session and free the server slot
                           (with token login — forget the tokens).
          login            Log in by token: prints a browser link, waits for the login and
                           saves the tokens to a file (requires ALTIUM_TOKEN_STORE=file).
          services         Show the REST service addresses of the workspace and check the search
                           service: total component count and the largest types.
          search-raw <file.json>
                           Send the search service the request body from the file as is and print
                           the response to stdout (format research).
          states           Show lifecycle states with the visible/applicable flags
                           (the Components panel hides states with applicable=false).
          system-folders   Compare all Datasheets folders with the Altium reference (type, Attributes,
                           naming scheme) and print the ones that differ.

        Environment variables:
          ALTIUM_BASE_URL            Workspace address, required.
          ALTIUM_AUTH                windows (default on Windows) | token (default elsewhere).
          ALTIUM_OAUTH_CLIENT_ID     client_id of the application for token login (required with token).
          ALTIUM_TOKEN_STORE         memory (default) | file — where to keep the tokens.
          ALTIUM_USERNAME            User name; empty — log in with the Windows account.
          ALTIUM_PASSWORD            Password, if a user name is set.
          ALTIUM_WRITE_MODE          guarded (default) | unguarded | readonly.
          ALTIUM_CONFIRM_THRESHOLD   How many objects change without confirmation in guarded mode (default 4).
          ALTIUM_WRITABLE_FOLDERS    Folder paths separated by ";"; writes outside them are forbidden.
          ALTIUM_MAX_WRITE_BATCH     How many components change in one call (default 500).
          ALTIUM_STATE_DIR           Directory of the session file and the audit log.
          ALTIUM_EXCHANGE_DIR        Directory for exchanging model files.
          ALTIUM_EXCHANGE_AGENT_DIR  The same directory as the agent sees it, if the paths differ.
        """);
    return 0;
}

static int PrintUnknown(string command)
{
    Console.Error.WriteLine($"Unknown command '{command}'. Run with --help.");
    return 2;
}
