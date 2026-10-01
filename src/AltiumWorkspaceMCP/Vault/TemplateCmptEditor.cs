using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace AltiumWorkspaceMCP.Vault;

/// <summary>A revision package file: the full archive entry name and the content.</summary>
public sealed record PackageFile(string FullName, byte[] Content)
{
    private const string ReleasedPrefix = "Released/";

    /// <summary>The name inside the <c>Released</c> directory under which the file goes into the new revision.</summary>
    public string ReleasedName => FullName[ReleasedPrefix.Length..];

    public bool IsCmpt => FullName.EndsWith(".cmpt", StringComparison.OrdinalIgnoreCase);

    public static bool InReleased(string fullName) =>
        fullName.StartsWith(ReleasedPrefix, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Result of editing one field of the <c>.cmpt</c> file.</summary>
/// <param name="Content">The new file content: everything except the field value — byte for byte.</param>
/// <param name="Previous">The previous field value (<see langword="null"/> — it was empty).</param>
public sealed record CmptEdit(byte[] Content, string? Previous);

/// <summary>
/// Editing the <c>.cmpt</c> file of a component template: the default component type and the default
/// folder. Pure logic without server calls.
/// </summary>
/// <remarks>
/// The file is edited as text: the position of the needed field value is found, and the new
/// one is put there. The document is not parsed into a model and not rebuilt, so the other fields,
/// their order, indents and line breaks stay as they were — Altium itself edits the file the same way.
/// If the needed field is not in the file, it is not
/// added: a foreign format must not be made up.
/// </remarks>
public static class TemplateCmptEditor
{
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    private static readonly JsonSerializerOptions TextEncoding = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonReaderOptions ReaderOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>A revision package → files; directory entries are skipped.</summary>
    public static IReadOnlyList<PackageFile> ReadPackage(byte[] package)
    {
        using var archive = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
        var files = new List<PackageFile>();

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string name = entry.FullName.Replace('\\', '/');

            if (name.EndsWith('/'))
            {
                continue;
            }

            using Stream stream = entry.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            files.Add(new PackageFile(name, buffer.ToArray()));
        }

        return files;
    }

    /// <summary>Sets the template's component type: <c>ComponentTypes[0].DefaultValue</c>.</summary>
    public static CmptEdit SetType(byte[] content, string typeGuid) =>
        Replace(content, typeGuid, ["ComponentTypes", 0, "DefaultValue"], "ComponentTypes[0].DefaultValue");

    /// <summary>Sets the default folder: <c>DefaultFolder.DefaultFolderGUID</c>.</summary>
    public static CmptEdit SetDefaultFolder(byte[] content, string folderGuid) =>
        Replace(content, folderGuid, ["DefaultFolder", "DefaultFolderGUID"], "DefaultFolder.DefaultFolderGUID");

    /// <summary>The template identity: the top-level <c>ItemGUID</c> — the GUID of the item the file belongs to.</summary>
    public static CmptEdit SetItemGuid(byte[] content, string itemGuid) =>
        Replace(content, itemGuid, ["ItemGUID"], "ItemGUID");

    /// <summary>The template identity: the top-level <c>RevisionGUID</c> — the GUID of the revision the file belongs to.</summary>
    public static CmptEdit SetRevisionGuid(byte[] content, string revisionGuid) =>
        Replace(content, revisionGuid, ["RevisionGUID"], "RevisionGUID");

    /// <summary>
    /// Sets the default symbol or footprint: <c>DefaultValue</c> of the <c>ModelLinks</c> array object
    /// with <c>Name</c> equal to <paramref name="modelLinkName"/> (<see cref="LinkRole.Symbol"/>
    /// "SCHLIB" or <see cref="LinkRole.Footprint"/> "PCBLIB"). <c>DefaultValue</c> comes in the file in the object
    /// before <c>Name</c>, so the object is searched whole by name, not the first matching value.
    /// </summary>
    /// <param name="itemGuidOrEmpty">GUID of the model item; an empty string removes the link.</param>
    public static CmptEdit SetModelLink(byte[] content, string modelLinkName, string itemGuidOrEmpty)
    {
        object[] path = ["ModelLinks", new ArrayWhere("Name", modelLinkName), "DefaultValue"];
        string fieldName = $"ModelLinks[{modelLinkName}].DefaultValue";

        return string.IsNullOrEmpty(itemGuidOrEmpty)
            ? ReplaceRaw(content, "\"\"", path, fieldName)
            : Replace(content, itemGuidOrEmpty, path, fieldName);
    }

    /// <summary>The part name template: <c>ItemNamingTemplate.ItemNamingTemplate</c> (for example <c>CMP-001-{00000}</c>).</summary>
    public static CmptEdit SetItemNamingTemplate(byte[] content, string namingTemplate)
    {
        if (string.IsNullOrWhiteSpace(namingTemplate))
        {
            throw new ArgumentException("The name template is empty.", nameof(namingTemplate));
        }

        return ReplaceRaw(
            content,
            JsonSerializer.Serialize(namingTemplate, TextEncoding),
            ["ItemNamingTemplate", "ItemNamingTemplate"],
            "ItemNamingTemplate.ItemNamingTemplate");
    }

    /// <summary>The file text for showing to a person: without BOM.</summary>
    public static string AsText(byte[] content)
    {
        int skip = HasBom(content) ? Utf8Bom.Length : 0;
        return Encoding.UTF8.GetString(content, skip, content.Length - skip);
    }

    private static bool HasBom(byte[] content) =>
        content.Length >= Utf8Bom.Length && content.AsSpan(0, Utf8Bom.Length).SequenceEqual(Utf8Bom);

    private static CmptEdit Replace(byte[] content, string newValue, object[] path, string fieldName)
    {
        if (!Guid.TryParse(newValue, out Guid guid))
        {
            throw new ArgumentException($"'{newValue}' is not a GUID; only a GUID is written to {fieldName}.", nameof(newValue));
        }

        // The GUID is upper case, as in all .cmpt files released by Altium; the vault catalog
        // returns some folder GUIDs in lower case.
        return ReplaceRaw(content, $"\"{guid.ToString("D").ToUpperInvariant()}\"", path, fieldName);
    }

    /// <summary>Puts a ready JSON literal (with quotes) in place of a string value by path.</summary>
    private static CmptEdit ReplaceRaw(byte[] content, string literal, object[] path, string fieldName)
    {
        int offset = HasBom(content) ? Utf8Bom.Length : 0;
        (int Start, int Length, string? Previous)? found;

        try
        {
            found = Locate(content.AsSpan(offset), path);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"The .cmpt file does not parse as JSON ({exception.Message}) — it cannot be edited.");
        }

        if (found is not { } target)
        {
            throw new InvalidOperationException(
                $"The .cmpt file has no field {fieldName} (or it is not a string): nothing to edit, and a foreign "
                + "format must not be added. Set up such a template in Altium Designer.");
        }

        // The quotes are written here and not taken from the file: for a string and for null the result is the same.
        byte[] replacement = Encoding.UTF8.GetBytes(literal);
        int start = offset + target.Start;

        byte[] result = new byte[content.Length - target.Length + replacement.Length];
        Buffer.BlockCopy(content, 0, result, 0, start);
        Buffer.BlockCopy(replacement, 0, result, start, replacement.Length);
        Buffer.BlockCopy(content, start + target.Length, result, start + replacement.Length, content.Length - start - target.Length);

        return new CmptEdit(result, string.IsNullOrWhiteSpace(target.Previous) ? null : target.Previous);
    }

    /// <summary>A path step "the array element whose property <see cref="PropertyName"/> equals <see cref="PropertyValue"/>".</summary>
    private sealed record ArrayWhere(string PropertyName, string PropertyValue);

    /// <summary>
    /// Finds a value by path (a property name, an array element number or <see cref="ArrayWhere"/>)
    /// and returns its position with the quotes, the length and the previous value; <see langword="null"/> — there is no such path.
    /// </summary>
    private static (int Start, int Length, string? Previous)? Locate(ReadOnlySpan<byte> json, object[] path)
    {
        var reader = new Utf8JsonReader(json, ReaderOptions);

        if (!reader.Read())
        {
            return null;
        }

        foreach (object step in path)
        {
            bool reached = step switch
            {
                string name => reader.TokenType == JsonTokenType.StartObject && MoveToProperty(ref reader, name),
                int index => reader.TokenType == JsonTokenType.StartArray && MoveToElement(ref reader, index),
                ArrayWhere where => reader.TokenType == JsonTokenType.StartArray
                    && MoveToElementWhere(ref reader, where.PropertyName, where.PropertyValue),
                _ => false,
            };

            if (!reached)
            {
                return null;
            }
        }

        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                // ValueSpan — the content between the quotes as it is in the file.
                return ((int)reader.TokenStartIndex, reader.ValueSpan.Length + 2, reader.GetString());

            case JsonTokenType.Null:
                return ((int)reader.TokenStartIndex, 4, null);

            default:
                return null;
        }
    }

    /// <summary>From the start of an object goes to the value of the property with the given name.</summary>
    private static bool MoveToProperty(ref Utf8JsonReader reader, string name)
    {
        int depth = reader.CurrentDepth;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == depth)
            {
                return false;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }

            bool match = reader.ValueTextEquals(name);
            reader.Read();

            if (match)
            {
                return true;
            }

            reader.Skip();
        }

        return false;
    }

    /// <summary>From the start of an array goes to the object whose property <paramref name="propertyName"/> equals <paramref name="propertyValue"/>.</summary>
    private static bool MoveToElementWhere(ref Utf8JsonReader reader, string propertyName, string propertyValue)
    {
        int depth = reader.CurrentDepth;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray && reader.CurrentDepth == depth)
            {
                return false;
            }

            if (reader.TokenType != JsonTokenType.StartObject)
            {
                reader.Skip();
                continue;
            }

            // The check is on a copy of the reader: if the object does not fit, the original reader goes on along the array.
            Utf8JsonReader probe = reader;

            if (ObjectHasProperty(probe, propertyName, propertyValue))
            {
                return true;
            }

            reader.Skip();
        }

        return false;
    }

    /// <summary>The reader stands at the start of an object (not read) — whether it has a property with such a value.</summary>
    private static bool ObjectHasProperty(Utf8JsonReader reader, string propertyName, string propertyValue) =>
        MoveToProperty(ref reader, propertyName)
        && reader.TokenType == JsonTokenType.String
        && reader.ValueTextEquals(propertyValue);

    /// <summary>From the start of an array goes to the element with the given number.</summary>
    private static bool MoveToElement(ref Utf8JsonReader reader, int index)
    {
        int depth = reader.CurrentDepth;

        for (int current = 0; reader.Read(); current++)
        {
            if (reader.TokenType == JsonTokenType.EndArray && reader.CurrentDepth == depth)
            {
                return false;
            }

            if (current == index)
            {
                return true;
            }

            reader.Skip();
        }

        return false;
    }
}
