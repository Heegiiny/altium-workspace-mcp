using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Serialization;
using AltiumWorkspaceMCP.Soap.Vault;

namespace AltiumWorkspaceMCP.Vault;

/// <summary>Result of running a vault script.</summary>
public sealed record ScriptResult(bool Success, string? Message, string? TaskGuid);

/// <summary>A revision content file; goes into its <c>Released</c> directory.</summary>
/// <param name="Name">File name in the vault.</param>
/// <param name="SourcePath">Where to take the file from.</param>
public sealed record ReleaseFile(string Name, string SourcePath);

/// <summary>A revision, the links to fix to it on release, and the files of its content.</summary>
/// <param name="Files">
/// The payload is a symbol or footprint library. A component has none:
/// its content is parameters and links.
/// </param>
public sealed record RevisionRelease(
    ALU_ItemRevision Revision,
    IReadOnlyCollection<ALU_ItemRevisionLink> Links,
    IReadOnlyList<ReleaseFile>? Files = null);

/// <summary>
/// A component created and released by one atomic script group — this is how
/// Single Component Editor saves it: the item with the first revision, the release with links, the item
/// links (datasheets) and the type assignment.
/// </summary>
/// <param name="Item">The item with the first revision inside (<c>Revisions</c>).</param>
/// <param name="Links">Revision links — symbol, footprint, template.</param>
/// <param name="ItemLinks">Item links to datasheets (<c>AddALU_ItemLinks</c>).</param>
/// <param name="Tags">Component type assignments (<c>AssignALU_ItemTags</c>).</param>
public sealed record ComponentCreation(
    ALU_Item Item,
    IReadOnlyCollection<ALU_ItemRevisionLink> Links,
    IReadOnlyList<ALU_ItemLink> ItemLinks,
    IReadOnlyList<ALU_ItemTag> Tags)
{
    /// <summary>The first revision of the item being created.</summary>
    public ALU_ItemRevision Revision => Item.Revisions[0];
}

/// <summary>An atomic group command: the operation code and a record of its <c>Records</c> list.</summary>
internal sealed record ScriptCommandSpec(string Operation, Action<XmlWriter> WriteRecords);

/// <summary>
/// Running vault scripts — the only way to release a revision.
/// </summary>
/// <remarks>
/// Ordinary SOAP operations cannot release a revision: the server accepts the release
/// date field but ignores it. Altium releases revisions by sending a ZIP package
/// with a script by the PUT method to <c>…/VaultService.svc/ExecuteScript</c>. This
/// exact format is reproduced here.
///
/// The release is mandatory: an unreleased revision becomes the active one for the item
/// but stays without a payload, and the component looks unfinished in Altium.
/// </remarks>
public sealed class VaultScriptExecutor
{
    private const string ScriptEntryName = "ExecuteScript.xml";

    private static readonly XmlWriterSettings WriterSettings = new()
    {
        Indent = false,
        OmitXmlDeclaration = false,
        Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
    };

    private readonly VaultEndpoints _endpoints;
    private readonly VaultSession _session;
    private readonly HttpClient _http;

    public VaultScriptExecutor(VaultEndpoints endpoints, VaultSession session, HttpClient http)
    {
        _endpoints = endpoints;
        _session = session;
        _http = http;
    }

    /// <summary>Releases one revision together with its model links.</summary>
    public Task<ScriptResult> ReleaseRevisionAsync(
        ALU_ItemRevision revision,
        IReadOnlyCollection<ALU_ItemRevisionLink> links,
        string releaseNote,
        CancellationToken cancellationToken) =>
        ReleaseRevisionsAsync([new RevisionRelease(revision, links)], releaseNote, cancellationToken);

    /// <summary>
    /// Releases several revisions by one script.
    /// </summary>
    /// <remarks>
    /// A vault script accepts any number of release records, and this is
    /// the decisive circumstance for large edits: releasing two thousand revisions one
    /// by one is two thousand server calls and almost half an hour of waiting, while
    /// one package fits in seconds. The commands inside the script form one
    /// atomic group, so the package is applied in full or not at all.
    /// </remarks>
    public Task<ScriptResult> ReleaseRevisionsAsync(
        IReadOnlyList<RevisionRelease> releases,
        string releaseNote,
        CancellationToken cancellationToken) =>
        ReleaseRevisionsAsync(releases, [], releaseNote, cancellationToken);

    /// <summary>
    /// Creates revisions and releases them at once by one script.
    /// </summary>
    /// <remarks>
    /// Creation and release are two commands of one atomic group, so the server
    /// runs them in one call and in one transaction. A separate
    /// AddALU_ItemRevisions call followed by a script would give two calls and
    /// an intermediate state in which the revision is created but not released.
    /// </remarks>
    /// <param name="revisionsToCreate">
    /// Revisions that do not exist on the server yet. Empty if existing ones are edited.
    /// </param>
    /// <remarks>
    /// The link list in a release record sets their full set for the revision:
    /// an empty list erases the existing links, and the component loses its symbol
    /// and footprint.
    /// </remarks>
    public Task<ScriptResult> ReleaseRevisionsAsync(
        IReadOnlyList<RevisionRelease> releases,
        IReadOnlyList<ALU_ItemRevision> revisionsToCreate,
        string releaseNote,
        CancellationToken cancellationToken)
    {
        if (releases.Count == 0 && revisionsToCreate.Count == 0)
        {
            return Task.FromResult(new ScriptResult(true, "nothing to release", null));
        }

        if (DryRun.IsActive)
        {
            var created = revisionsToCreate
                .Select(revision => revision.GUID)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            DryRun.Intercept(
                "ExecuteScript: ReleaseALU_ItemRevisions",
                releases.Select(release => (object)new
                {
                    createdByScript = created.Contains(release.Revision.GUID),
                    revision = DryRunView.Revision(release.Revision),
                    links = release.Links.Select(DryRunView.Link).ToList(),
                    dataFolder = release.Files is { Count: > 0 } ? release.Revision.GUID : null,
                    files = release.Files?.Select(file => new
                    {
                        path = $"{release.Revision.GUID}/Released/{file.Name}",
                        source = file.SourcePath,
                        size = new FileInfo(file.SourcePath).Length,
                    }).ToList(),
                }).ToList(),
                $"create revisions: {revisionsToCreate.Count}, release: {releases.Count}, note '{releaseNote}'");

            return Task.FromResult(new ScriptResult(true, "dry run: the script was not sent", null));
        }

        return _session.ExecuteAsync(
            async (sessionId, ct) =>
            {
                byte[] package = BuildReleasePackage(sessionId, releases, revisionsToCreate, releaseNote);
                return await SendAsync(package, ct);
            },
            cancellationToken);
    }

    /// <summary>
    /// Creates and releases a component by one atomic group <c>Release single component</c>:
    /// <c>AddALU_Items</c> → <c>ReleaseALU_ItemRevisions</c> → <c>AddALU_ItemLinks</c> →
    /// <c>AssignALU_ItemTags</c> — the order captured from Altium Designer traffic.
    /// </summary>
    /// <remarks>
    /// All or nothing: if any command is refused, the server rolls the group back, and there remains neither an
    /// item without a released revision nor a released component without a type. The item and revision
    /// GUIDs are set by the client in advance.
    /// </remarks>
    public Task<ScriptResult> CreateComponentAsync(
        ComponentCreation creation,
        string releaseNote,
        CancellationToken cancellationToken)
    {
        if (DryRun.IsActive)
        {
            DryRun.Intercept(
                "ExecuteScript: Release single component",
                new List<object>
                {
                    new
                    {
                        commands = CreationCommands(creation, string.Empty, releaseNote).Select(command => command.Operation).ToList(),
                        item = DryRunView.Item(creation.Item),
                        links = creation.Links.Select(DryRunView.Link).ToList(),
                        // A part may have dozens of datasheets: the count and the first three are shown.
                        itemLinks = new
                        {
                            count = creation.ItemLinks.Count,
                            sample = creation.ItemLinks.Take(3).Select(link => new
                            {
                                child = link.ChildItemGUID,
                                parameters = link.LinkParameters?.Select(parameter => $"{parameter.HRID} = {parameter.ParameterValue}").ToList(),
                            }).ToList(),
                        },
                        tags = creation.Tags.Select(tag => new { item = tag.ItemGUID, tag = tag.TagGUID }).ToList(),
                    },
                },
                $"create {creation.Item.HRID} and release as one group, note '{releaseNote}'");

            return Task.FromResult(new ScriptResult(true, "dry run: the script was not sent", null));
        }

        return _session.ExecuteAsync(
            async (sessionId, ct) =>
            {
                string script = BuildScript(sessionId, CreationGroup, CreationCommands(creation, sessionId, releaseNote));
                return await SendAsync(PackScript(script, []), ct);
            },
            cancellationToken);
    }

    /// <summary>Name of the atomic group under which Altium saves a new component.</summary>
    internal const string CreationGroup = "Release single component";

    /// <summary>Commands of the component creation group in Altium's order; empty command lists do not produce any.</summary>
    internal static IReadOnlyList<ScriptCommandSpec> CreationCommands(
        ComponentCreation creation,
        string sessionId,
        string releaseNote)
    {
        var commands = new List<ScriptCommandSpec>
        {
            new("AddALU_Items", writer => WriteObject(writer, creation.Item, "item")),
            new("ReleaseALU_ItemRevisions", writer => WriteReleaseInfo(
                writer, sessionId, new RevisionRelease(creation.Revision, creation.Links), releaseNote)),
        };

        if (creation.ItemLinks.Count > 0)
        {
            commands.Add(new("AddALU_ItemLinks", writer =>
            {
                foreach (ALU_ItemLink link in creation.ItemLinks)
                {
                    WriteObject(writer, link, "item");
                }
            }));
        }

        if (creation.Tags.Count > 0)
        {
            commands.Add(new("AssignALU_ItemTags", writer =>
            {
                foreach (ALU_ItemTag tag in creation.Tags)
                {
                    WriteObject(writer, tag, "item");
                }
            }));
        }

        return commands;
    }

    /// <summary>
    /// Builds the ZIP package: the release script and the revision content.
    /// </summary>
    /// <remarks>
    /// The layout follows Altium's release format: the revision files lie
    /// in a directory named by its GUID, inside — <c>Released/file name</c>, and the release record
    /// references this directory by the DataFolder field. There must be no files in the root of the directory —
    /// only subdirectories, as in the downloaded revision content.
    /// </remarks>
    private static byte[] BuildReleasePackage(
        string sessionId,
        IReadOnlyList<RevisionRelease> releases,
        IReadOnlyList<ALU_ItemRevision> revisionsToCreate,
        string releaseNote) =>
        PackScript(BuildReleaseScript(sessionId, releases, revisionsToCreate, releaseNote), releases);

    /// <summary>ZIP package: the script and the content files of those revisions that have them.</summary>
    private static byte[] PackScript(string script, IReadOnlyList<RevisionRelease> releases)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry scriptEntry = archive.CreateEntry(ScriptEntryName, CompressionLevel.Optimal);
            using (Stream stream = scriptEntry.Open())
            {
                byte[] payload = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(script);
                stream.Write(payload, 0, payload.Length);
            }

            foreach (RevisionRelease release in releases.Where(release => release.Files is { Count: > 0 }))
            {
                string folder = $"{release.Revision.GUID}/Released/";
                archive.CreateEntry($"{release.Revision.GUID}/");
                archive.CreateEntry(folder);

                foreach (ReleaseFile file in release.Files!)
                {
                    ZipArchiveEntry entry = archive.CreateEntry(folder + file.Name, CompressionLevel.Optimal);
                    using Stream target = entry.Open();
                    using FileStream source = File.OpenRead(file.SourcePath);
                    source.CopyTo(target);
                }
            }
        }

        return buffer.ToArray();
    }

    private static string BuildReleaseScript(
        string sessionId,
        IReadOnlyList<RevisionRelease> releases,
        IReadOnlyList<ALU_ItemRevision> revisionsToCreate,
        string releaseNote)
    {
        // Each revision gets its own release command. One command with several
        // records is rejected by the server ("Command ReleaseALU_ItemRevisions failed"),
        // while several commands in one group it runs in sequence and atomically.
        var commands = new List<ScriptCommandSpec>();

        // Creation goes as the first command: the release below in the list already finds the revisions.
        if (revisionsToCreate.Count > 0)
        {
            commands.Add(new("AddALU_ItemRevisions", writer =>
            {
                foreach (ALU_ItemRevision revision in revisionsToCreate)
                {
                    WriteObject(writer, revision, "item");
                }
            }));
        }

        foreach (RevisionRelease release in releases)
        {
            commands.Add(new(
                "ReleaseALU_ItemRevisions",
                writer => WriteReleaseInfo(writer, sessionId, release, releaseNote)));
        }

        return BuildScript(sessionId, "ReleaseItemRevision", commands);
    }

    /// <summary>
    /// Forms the script XML from one atomic group: the commands run in sequence, in one
    /// transaction.
    /// </summary>
    /// <remarks>
    /// All list item nodes are called <c>item</c>. The server renames them itself
    /// to class names, relying on the <c>Operation</c> value: command group → command →
    /// records → links and revision. So the <c>item</c> names here are not carelessness
    /// but the required format.
    /// </remarks>
    internal static string BuildScript(string sessionId, string groupName, IReadOnlyList<ScriptCommandSpec> commands)
    {
        var output = new StringBuilder();
        using (XmlWriter writer = XmlWriter.Create(output, WriterSettings))
        {
            writer.WriteStartElement("ExecuteScript");
            writer.WriteElementString("SessionHandle", sessionId);

            writer.WriteStartElement("AtomicGroups");
            writer.WriteStartElement("item");
            writer.WriteElementString("HRID", groupName);

            writer.WriteStartElement("Commands");
            foreach (ScriptCommandSpec command in commands)
            {
                writer.WriteStartElement("item");
                writer.WriteElementString("Operation", command.Operation);

                writer.WriteStartElement("Records");
                command.WriteRecords(writer);
                writer.WriteEndElement(); // Records

                writer.WriteEndElement(); // item (command)
            }

            writer.WriteEndElement(); // Commands
            writer.WriteEndElement(); // item
            writer.WriteEndElement(); // AtomicGroups
            writer.WriteEndElement(); // ExecuteScript
        }

        return output.ToString();
    }

    private static void WriteReleaseInfo(
        XmlWriter writer,
        string sessionId,
        RevisionRelease release,
        string releaseNote)
    {
        writer.WriteStartElement("item");
        writer.WriteElementString("ItemRevisionGUID", release.Revision.GUID);
        writer.WriteElementString("SessionHandle", sessionId);

        // The component is released without content: its content is parameters and model links.
        // Models specify the package directory with their files.
        writer.WriteElementString("DataFolder", release.Files is { Count: > 0 } ? release.Revision.GUID : string.Empty);
        writer.WriteElementString("ReleaseNote", releaseNote);

        writer.WriteStartElement("ItemRevisionLinks");
        foreach (ALU_ItemRevisionLink link in release.Links)
        {
            WriteObject(writer, link, "item");
        }

        writer.WriteEndElement(); // ItemRevisionLinks

        writer.WriteStartElement("UpdateItemRevision");
        WriteObject(writer, release.Revision, "item");
        writer.WriteEndElement(); // UpdateItemRevision

        writer.WriteEndElement(); // item (release record)
    }

    /// <summary>
    /// Writes an object with the same serializer Altium uses, so the composition
    /// and order of elements match the server's expectations. The xsi and xsd declarations
    /// are kept: they mark unfilled values.
    /// </summary>
    private static void WriteObject<T>(XmlWriter writer, T value, string elementName) =>
        new XmlSerializer(typeof(T), new XmlRootAttribute(elementName)).Serialize(writer, value);

    private async Task<ScriptResult> SendAsync(byte[] package, CancellationToken cancellationToken)
    {
        using var content = new ByteArrayContent(package);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");

        using var request = new HttpRequestMessage(HttpMethod.Put, _endpoints.ExecuteScript) { Content = content };

        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        SoapTrace.Script(package, body);

        if (!response.IsSuccessStatusCode)
        {
            throw new VaultOperationException(
                "ExecuteScript",
                ((int)response.StatusCode).ToString(),
                string.IsNullOrWhiteSpace(body) ? response.ReasonPhrase : body);
        }

        return ParseResult(body);
    }

    private static ScriptResult ParseResult(string body)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(body);
        }
        catch (XmlException)
        {
            throw new VaultOperationException("ExecuteScript", null, $"unexpected server response: {body}");
        }

        XElement? root = document.Root;
        bool success = string.Equals(
            root?.Element("Success")?.Value, "true", StringComparison.OrdinalIgnoreCase);

        string? message = root?.Element("Message")?.Value;
        string? taskGuid = root?.Element("TaskGUID")?.Value;

        // A refusal of an individual command group does not always raise the overall failure flag.
        string? groupFailure = root
            ?.Element("AtomicGroups")
            ?.Elements("item")
            .Where(item => !string.Equals(item.Element("Success")?.Value, "true", StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Element("Message")?.Value)
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));

        if (!success || groupFailure is not null)
        {
            throw new VaultOperationException(
                "ReleaseALU_ItemRevisions",
                null,
                groupFailure ?? message ?? "the server gave no reason");
        }

        return new ScriptResult(success, message, taskGuid);
    }
}
