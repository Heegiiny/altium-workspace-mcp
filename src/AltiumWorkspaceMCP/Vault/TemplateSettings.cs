using System.IO.Compression;
using System.Text.Json;

namespace AltiumWorkspaceMCP.Vault;

/// <summary>
/// Component template settings from the <c>.cmpt</c> file of its released revision: the default
/// component type and the default folder. Altium Designer reads them.
/// </summary>
/// <param name="TypeGuid"><c>ComponentTypes[0].DefaultValue</c> — the GUID of the component type.</param>
/// <param name="DefaultFolderGuid"><c>DefaultFolder.DefaultFolderGUID</c>.</param>
/// <param name="DefaultSymbolItemGuid">
/// <c>DefaultValue</c> of the <c>ModelLinks</c> object with <c>Name: "SCHLIB"</c> — the GUID of the <b>item</b>
/// of the symbol, not of a revision; empty — not set.
/// </param>
/// <param name="DefaultFootprintItemGuid">The same for <c>Name: "PCBLIB"</c> — the footprint.</param>
public sealed record TemplateSettings(
    string? TypeGuid,
    string? DefaultFolderGuid,
    bool AllowFolderOverride,
    string? DefaultSymbolItemGuid = null,
    string? DefaultFootprintItemGuid = null)
{
    /// <summary>Parses the JSON of a <c>.cmpt</c> file; an incomplete or empty file gives empty settings.</summary>
    public static TemplateSettings Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true });
        JsonElement root = document.RootElement;

        string? type = null;

        if (root.TryGetProperty("ComponentTypes", out JsonElement types)
            && types.ValueKind == JsonValueKind.Array
            && types.GetArrayLength() > 0
            && types[0].TryGetProperty("DefaultValue", out JsonElement value)
            && value.ValueKind == JsonValueKind.String)
        {
            type = value.GetString();
        }

        string? folder = null;
        bool allowOverride = false;

        if (root.TryGetProperty("DefaultFolder", out JsonElement defaultFolder) && defaultFolder.ValueKind == JsonValueKind.Object)
        {
            if (defaultFolder.TryGetProperty("DefaultFolderGUID", out JsonElement guid) && guid.ValueKind == JsonValueKind.String)
            {
                folder = guid.GetString();
            }

            if (defaultFolder.TryGetProperty("AllowOverride", out JsonElement allow) && allow.ValueKind is JsonValueKind.True)
            {
                allowOverride = true;
            }
        }

        string? symbol = ModelLinkValue(root, LinkRole.Symbol);
        string? footprint = ModelLinkValue(root, LinkRole.Footprint);

        return new TemplateSettings(
            string.IsNullOrWhiteSpace(type) ? null : type,
            string.IsNullOrWhiteSpace(folder) ? null : folder,
            allowOverride,
            string.IsNullOrWhiteSpace(symbol) ? null : symbol,
            string.IsNullOrWhiteSpace(footprint) ? null : footprint);
    }

    /// <summary><c>DefaultValue</c> of the <c>ModelLinks</c> object with the given <c>Name</c>; no array or object — <see langword="null"/>.</summary>
    private static string? ModelLinkValue(JsonElement root, string modelLinkName)
    {
        if (!root.TryGetProperty("ModelLinks", out JsonElement links) || links.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (JsonElement link in links.EnumerateArray())
        {
            if (link.ValueKind == JsonValueKind.Object
                && link.TryGetProperty("Name", out JsonElement name)
                && name.ValueKind == JsonValueKind.String
                && string.Equals(name.GetString(), modelLinkName, StringComparison.OrdinalIgnoreCase)
                && link.TryGetProperty("DefaultValue", out JsonElement value)
                && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }

        return null;
    }

    /// <summary>Finds the <c>.cmpt</c> file in the revision ZIP package and parses it; <see langword="null"/> — there is no file.</summary>
    public static TemplateSettings? FromPackage(byte[] package)
    {
        using var archive = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);

        ZipArchiveEntry? entry = archive.Entries.FirstOrDefault(candidate =>
            candidate.FullName.EndsWith(".cmpt", StringComparison.OrdinalIgnoreCase));

        if (entry is null)
        {
            return null;
        }

        using var reader = new StreamReader(entry.Open(), System.Text.Encoding.UTF8);
        return Parse(reader.ReadToEnd().TrimStart('\uFEFF'));
    }
}

/// <summary>One source of the component type: where it came from and which type GUID it gave.</summary>
public sealed record TypeCandidate(string Source, string? TypeGuid);

/// <summary>Result of the type choice: the chosen source and those that gave a different type.</summary>
public sealed record TypeChoice(TypeCandidate? Chosen, IReadOnlyList<TypeCandidate> Disagreeing);

/// <summary>
/// Choosing the component type on creation: the first source in order that gave a type.
/// Pure logic.
/// </summary>
public static class ComponentTypeChooser
{
    /// <summary>
    /// The first candidate with a non-empty type becomes the chosen one. A disagreement is counted for
    /// the following candidates with a different type — but not on an explicit choice: the user's explicit
    /// choice is not checked against anything.
    /// </summary>
    /// <param name="candidatesInOrder">Sources in descending priority.</param>
    public static TypeChoice Choose(IReadOnlyList<TypeCandidate> candidatesInOrder)
    {
        var withType = candidatesInOrder.Where(candidate => !string.IsNullOrWhiteSpace(candidate.TypeGuid)).ToList();

        if (withType.Count == 0)
        {
            return new TypeChoice(null, []);
        }

        TypeCandidate chosen = withType[0];

        var disagreeing = chosen.Source == TypeSource.Explicit
            ? []
            : withType
                .Skip(1)
                .Where(candidate => !string.Equals(candidate.TypeGuid, chosen.TypeGuid, StringComparison.OrdinalIgnoreCase))
                .ToList();

        return new TypeChoice(chosen, disagreeing);
    }
}
