using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AltiumWorkspaceMCP.Configuration;
using AltiumWorkspaceMCP.Vault;
using AltiumWorkspaceMCP.Soap.Vault;

namespace AltiumWorkspaceMCP.Diagnostics;

/// <summary>
/// A full dump of components: all revisions with parameters and their types, links with all
/// fields and the content of the revision files. Needed to compare a revision released
/// by the MCP server with a revision saved from Altium Designer.
/// </summary>
public static class ComponentDumpCommand
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly string[] TextExtensions = [".cmplib", ".xml", ".json", ".txt", ".ini"];

    public static async Task<int> RunAsync(VaultOptions options, string[] arguments)
    {
        if (arguments.Length < 2)
        {
            Console.Error.WriteLine("Usage: dump <directory> <component> [component ...]");
            return 2;
        }

        string outputDirectory = arguments[0];
        Directory.CreateDirectory(outputDirectory);

        var endpoints = new VaultEndpoints(options);
        await using var session = new VaultSession(options, endpoints);
        var gateway = new VaultGateway(endpoints, session);
        var catalog = new VaultCatalog(gateway);
        var components = new ComponentService(gateway, catalog);
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        {
            Timeout = TimeSpan.FromMinutes(5),
        };

        foreach (string identifier in arguments.Skip(1))
        {
            var found = await components.ReadByIdsAsync([identifier], CancellationToken.None);
            if (found.Count == 0)
            {
                Console.Error.WriteLine($"{identifier}: not found");
                continue;
            }

            ComponentRecord record = found[0];
            Console.Error.WriteLine();
            Console.Error.WriteLine($"══════ {record.Hrid}  '{record.Comment}'  folder '{record.FolderPath}'");

            var itemLinks = await gateway.GetItemLinksAsync(
                VaultFilter.Equal("ParentItemGUID", record.ItemGuid), cancellationToken: CancellationToken.None);
            Console.Error.WriteLine($"item links (datasheets): {itemLinks.Count}");

            var revisions = await components.GetRevisionsAsync(record.ItemGuid, CancellationToken.None);
            var dump = new List<object>();

            foreach (ALU_ItemRevision revision in revisions.OrderBy(item => item.CreatedAt))
            {
                var links = await gateway.GetItemRevisionLinksAsync(
                    VaultFilter.Equal("ParentItemRevisionGUID", revision.GUID),
                    cancellationToken: CancellationToken.None);

                var parameters = revision.RevisionParameters ?? [];
                int untyped = parameters.Count(parameter => string.IsNullOrEmpty(parameter.ParameterTypeGUID));

                Console.Error.WriteLine();
                Console.Error.WriteLine($"── rev.{revision.RevisionId}  released={RevisionService.IsReleased(revision)}  "
                    + $"created {revision.CreatedAt:yyyy-MM-dd HH:mm:ss} by '{revision.CreatedByName}'");
                Console.Error.WriteLine($"   parameters {parameters.Count}, of them without a type {untyped}");

                foreach (ALU_ItemRevisionLink link in links)
                {
                    Console.Error.WriteLine($"   link HRID='{link.HRID}' → {link.ChildItemRevisionGUID}");
                    Console.Error.WriteLine($"       Data=«{link.Data}»");
                    Console.Error.WriteLine($"       ParentVault={link.ParentVaultGUID ?? "—"}  ChildVault={link.ChildVaultGUID ?? "—"}  "
                        + $"LinkType={link.LinkTypeGUID ?? "—"}  link parameters {link.LinkParameters?.Count ?? 0}");
                }

                string zipPath = Path.Combine(outputDirectory, $"{record.Hrid}-rev{revision.RevisionId}.zip");
                var files = await DownloadAsync(gateway, http, revision.GUID, zipPath);

                Console.Error.WriteLine($"   revision file: {(files is null ? "not downloaded" : $"{files.Count} entries")}");
                foreach (var (name, size) in files ?? [])
                {
                    Console.Error.WriteLine($"       {name}  ({size} bytes)");
                }

                ExtractText(zipPath, Path.Combine(outputDirectory, $"{record.Hrid}-rev{revision.RevisionId}"));

                dump.Add(new { revision, links });
            }

            string jsonPath = Path.Combine(outputDirectory, $"{record.Hrid}.json");
            await File.WriteAllTextAsync(
                jsonPath,
                JsonSerializer.Serialize(new { record.Hrid, record.ItemGuid, itemLinks, revisions = dump }, Json),
                Encoding.UTF8);

            Console.Error.WriteLine();
            Console.Error.WriteLine($"full dump: {jsonPath}");
        }

        return 0;
    }

    private static async Task<List<(string Name, long Size)>?> DownloadAsync(
        VaultGateway gateway,
        HttpClient http,
        string revisionGuid,
        string zipPath)
    {
        var urls = await gateway.GetRevisionDownloadUrlsAsync([revisionGuid], CancellationToken.None);
        string? url = urls.FirstOrDefault(candidate => !string.IsNullOrEmpty(candidate.URL))?.URL;

        if (url is null)
        {
            return null;
        }

        using HttpResponseMessage response = await http.GetAsync(url);
        if (!response.IsSuccessStatusCode)
        {
            Console.Error.WriteLine($"   download: HTTP {(int)response.StatusCode}");
            return null;
        }

        await File.WriteAllBytesAsync(zipPath, await response.Content.ReadAsByteArrayAsync());

        try
        {
            using ZipArchive archive = ZipFile.OpenRead(zipPath);
            return archive.Entries
                .Where(entry => entry.Length > 0)
                .Select(entry => (entry.FullName, entry.Length))
                .ToList();
        }
        catch (InvalidDataException)
        {
            Console.Error.WriteLine("   what was downloaded is not a ZIP archive");
            return [];
        }
    }

    /// <summary>Unpacks the text files of a revision next to the archive so that they can be compared.</summary>
    private static void ExtractText(string zipPath, string directory)
    {
        if (!File.Exists(zipPath))
        {
            return;
        }

        try
        {
            using ZipArchive archive = ZipFile.OpenRead(zipPath);

            foreach (ZipArchiveEntry entry in archive.Entries.Where(entry => entry.Length > 0))
            {
                string extension = Path.GetExtension(entry.Name).ToLowerInvariant();
                if (!TextExtensions.Contains(extension))
                {
                    continue;
                }

                Directory.CreateDirectory(directory);
                entry.ExtractToFile(Path.Combine(directory, entry.Name), overwrite: true);
            }
        }
        catch (InvalidDataException)
        {
            // Not an archive — nothing to unpack.
        }
    }
}
