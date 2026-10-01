using System.IO.Compression;
using System.Text;
using AltiumWorkspaceMCP.Vault;

namespace AltiumWorkspaceMCP.Tests;

/// <summary>Editing the .cmpt file of a template: only the needed field changes, the formatting stays intact.</summary>
public sealed class TemplateCmptEditorTests
{
    private const string OldType = "24C51BB7-57FA-4BFA-94AC-977A056405C3";
    private const string NewType = "C80A8DDE-A3B2-412C-ACA2-DB29E708C128";
    private const string OldFolder = "1964CE07-BD25-4089-B43A-468A4977C91D";
    private const string NewFolder = "5C9C34E1-A01F-4F67-B876-9F8395BF4EE8";

    // A single-line file, as Altium releases it.
    private static readonly string Compact =
        "{\"Parameters\":[{\"DefaultValue\":{\"ParameterValue\":\"" + OldType + "\"},\"Name\":\"Decoy\"}],"
        + "\"ModelLinks\":[{\"DefaultValue\":\"" + OldType + "\",\"Name\":\"SCHLIB\"}],"
        + "\"DefaultFolder\":{\"AllowOverride\":true,\"DefaultFolderGUID\":\"" + OldFolder + "\"},"
        + "\"ComponentTypes\":[{\"DefaultValue\":\"" + OldType + "\",\"IsRequired\":true,\"Name\":\"ComponentType\"}],"
        + "\"Version\":\"2.0.2\"}";

    // With indents, CRLF line breaks and spaces before the colon: all this must survive.
    private static readonly string Pretty =
        "{\r\n"
        + "    \"Version\" : \"2.0.2\",\r\n"
        + "    \"DefaultFolder\" : {\r\n        \"AllowOverride\" : true ,\r\n        \"DefaultFolderGUID\" : \"" + OldFolder + "\"\r\n    },\r\n"
        + "    \"ComponentTypes\" : [\r\n        {\r\n            \"IsRequired\" : true,\r\n            \"DefaultValue\" : \"" + OldType + "\"\r\n        },\r\n"
        + "        {\r\n            \"DefaultValue\" : \"second\"\r\n        }\r\n    ]\r\n"
        + "}\r\n";

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes);

    [Fact]
    public void SetTypeChangesOnlyTheTypeValue()
    {
        CmptEdit edit = TemplateCmptEditor.SetType(Bytes(Compact), NewType);

        Assert.Equal(Compact.Replace(
            "\"ComponentTypes\":[{\"DefaultValue\":\"" + OldType + "\"",
            "\"ComponentTypes\":[{\"DefaultValue\":\"" + NewType + "\""), Text(edit.Content));
        Assert.Equal(OldType, edit.Previous);
    }

    [Fact]
    public void SetTypeLeavesSameGuidElsewhereAlone()
    {
        string result = Text(TemplateCmptEditor.SetType(Bytes(Compact), NewType).Content);

        // The type GUID occurs in Parameters and ModelLinks too — editing them is not allowed.
        Assert.Equal(2, Count(result, OldType));
        Assert.Contains("\"ParameterValue\":\"" + OldType + "\"", result);
        Assert.Contains("\"DefaultValue\":\"" + OldType + "\",\"Name\":\"SCHLIB\"", result);
    }

    [Fact]
    public void SetTypeKeepsFormattingAndSecondArrayElement()
    {
        string result = Text(TemplateCmptEditor.SetType(Bytes(Pretty), NewType).Content);

        Assert.Equal(Pretty.Replace("\"DefaultValue\" : \"" + OldType + "\"", "\"DefaultValue\" : \"" + NewType + "\""), result);
        Assert.Contains("\"second\"", result);
    }

    [Fact]
    public void SetDefaultFolderChangesOnlyTheFolder()
    {
        CmptEdit edit = TemplateCmptEditor.SetDefaultFolder(Bytes(Compact), NewFolder);

        Assert.Equal(Compact.Replace("\"DefaultFolderGUID\":\"" + OldFolder + "\"", "\"DefaultFolderGUID\":\"" + NewFolder + "\""), Text(edit.Content));
        Assert.Equal(OldFolder, edit.Previous);
    }

    [Fact]
    public void GuidIsWrittenInUpperCase()
    {
        Assert.Equal(
            Compact.Replace("\"DefaultFolderGUID\":\"" + OldFolder + "\"", "\"DefaultFolderGUID\":\"" + NewFolder + "\""),
            Text(TemplateCmptEditor.SetDefaultFolder(Bytes(Compact), NewFolder.ToLowerInvariant()).Content));
    }

    [Fact]
    public void SetDefaultFolderKeepsFormatting()
    {
        string result = Text(TemplateCmptEditor.SetDefaultFolder(Bytes(Pretty), NewFolder).Content);

        Assert.Equal(Pretty.Replace(OldFolder, NewFolder), result);
    }

    [Fact]
    public void BomAndNonAsciiTextSurvive()
    {
        string json = "{\"Description\":\"\u0420\u0435\u0437\u0438\u0441\u0442\u043e\u0440\u044b \u2014 \u0431\u043e\u043b\u044c\u0448\u0438\u0435\",\"ComponentTypes\":[{\"DefaultValue\":\"" + OldType + "\"}]}";
        byte[] withBom = [0xEF, 0xBB, 0xBF, .. Bytes(json)];

        byte[] result = TemplateCmptEditor.SetType(withBom, NewType).Content;

        Assert.Equal([0xEF, 0xBB, 0xBF], result[..3]);
        Assert.Equal(json.Replace(OldType, NewType), Text(result[3..]));
    }

    [Fact]
    public void EmptyAndNullValuesAreReplaceable()
    {
        Assert.Equal(
            "{\"ComponentTypes\":[{\"DefaultValue\":\"" + NewType + "\"}]}",
            Text(TemplateCmptEditor.SetType(Bytes("{\"ComponentTypes\":[{\"DefaultValue\":\"\"}]}"), NewType).Content));

        CmptEdit fromNull = TemplateCmptEditor.SetType(Bytes("{\"ComponentTypes\":[{\"DefaultValue\":null,\"X\":1}]}"), NewType);

        Assert.Equal("{\"ComponentTypes\":[{\"DefaultValue\":\"" + NewType + "\",\"X\":1}]}", Text(fromNull.Content));
        Assert.Null(fromNull.Previous);
    }

    [Theory]
    [InlineData("{\"Version\":\"2.0.2\"}")]
    [InlineData("{\"ComponentTypes\":[]}")]
    [InlineData("{\"ComponentTypes\":[{\"Name\":\"ComponentType\"}]}")]
    [InlineData("{\"ComponentTypes\":[{\"DefaultValue\":5}]}")]
    [InlineData("{\"ComponentTypes\":{}}")]
    public void MissingTypeFieldIsRefusedNotComposed(string json)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => TemplateCmptEditor.SetType(Bytes(json), NewType));

        Assert.Contains("ComponentTypes[0].DefaultValue", exception.Message);
        Assert.Contains("must not be added", exception.Message);
    }

    [Fact]
    public void MissingFolderFieldIsRefused()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => TemplateCmptEditor.SetDefaultFolder(Bytes("{\"DefaultFolder\":{\"AllowOverride\":true}}"), NewFolder));

        Assert.Contains("DefaultFolder.DefaultFolderGUID", exception.Message);
    }

    [Fact]
    public void BrokenJsonIsRefused()
    {
        Assert.Throws<InvalidOperationException>(() => TemplateCmptEditor.SetType(Bytes("{\"ComponentTypes\":[{\"DefaultValue\":"), NewType));
    }

    [Fact]
    public void NonGuidValueIsRejected()
    {
        Assert.Throws<ArgumentException>(() => TemplateCmptEditor.SetType(Bytes(Compact), "Passive\\Resistors"));
    }

    [Fact]
    public void EditedFileIsStillReadByTemplateSettings()
    {
        TemplateSettings settings = TemplateSettings.Parse(Text(TemplateCmptEditor.SetType(Bytes(Compact), NewType).Content));

        Assert.Equal(NewType, settings.TypeGuid);
        Assert.Equal(OldFolder, settings.DefaultFolderGuid);
    }

    [Fact]
    public void PackageWithoutCmptHasNoCmptFile()
    {
        byte[] package = Zip(("Released/model.SchLib", "x"));

        IReadOnlyList<PackageFile> files = TemplateCmptEditor.ReadPackage(package);

        Assert.Single(files);
        Assert.DoesNotContain(files, file => file.IsCmpt);
        Assert.Null(TemplateSettings.FromPackage(package));
    }

    [Fact]
    public void PackageKeepsEveryFileAndSkipsDirectories()
    {
        byte[] package = Zip(("Released/", ""), ("Released/CMPT-1-3.CMPT", Compact), ("Released/preview.png", "png"), ("Other/note.txt", "n"));

        IReadOnlyList<PackageFile> files = TemplateCmptEditor.ReadPackage(package);

        Assert.Equal(3, files.Count);
        Assert.Single(files, file => file.IsCmpt);
        Assert.Equal("CMPT-1-3.CMPT", files.Single(file => file.IsCmpt).ReleasedName);
        Assert.False(PackageFile.InReleased(files.Single(file => file.FullName.StartsWith("Other")).FullName));
    }

    [Fact]
    public void CmptFileNameFollowsRevisionNumber()
    {
        var cmpt = new PackageFile("Released/CMPT-0001-13.CMPT", []);
        var other = new PackageFile("Released/CMPT-0001-13.png", []);
        var foreign = new PackageFile("Released/custom.cmpt", []);

        Assert.Equal("CMPT-0001-14.CMPT", TemplateService.ReleasedNameFor(cmpt, "CMPT-0001", "13", "14"));
        Assert.Equal("CMPT-0001-13.png", TemplateService.ReleasedNameFor(other, "CMPT-0001", "13", "14"));
        Assert.Equal("custom.cmpt", TemplateService.ReleasedNameFor(foreign, "CMPT-0001", "13", "14"));
    }

    // A whole sample, as in production templates: identity, name template, folder and type.
    private const string SampleItem = "15E8A93F-1111-4222-8333-444455556666";
    private const string SampleRevision = "8C6914E5-1111-4222-8333-444455556666";
    private const string OwnItem = "AAAAAAAA-1111-4222-8333-444455556666";
    private const string OwnRevision = "BBBBBBBB-1111-4222-8333-444455556666";

    private static readonly string Sample =
        "﻿{\"Parameters\":[{\"DefaultValue\":{\"ParameterValue\":\"" + SampleItem + "\"},\"Name\":\"Decoy\"}],"
        + "\"VaultGUID\":\"00000000-0000-4000-8000-000000000002\",\"ItemGUID\":\"" + SampleItem + "\","
        + "\"RevisionGUID\":\"" + SampleRevision + "\",\"RevisionNamingScheme\":{\"Name\":\"Numeric\"},"
        + "\"ItemNamingTemplate\":{\"AllowOverride\":true,\"ItemNamingTemplate\":\"CMP-001-{00000}\"},"
        + "\"DefaultFolder\":{\"AllowOverride\":true,\"DefaultFolderGUID\":\"" + OldFolder + "\"},"
        + "\"ComponentTypes\":[{\"DefaultValue\":\"" + OldType + "\",\"IsRequired\":true}],\"Version\":\"2.0.2\"}";

    [Fact]
    public void SampleCopyChangesExactlyTheListedFields()
    {
        byte[] content = Bytes(Sample);

        content = TemplateCmptEditor.SetItemGuid(content, OwnItem).Content;
        content = TemplateCmptEditor.SetRevisionGuid(content, OwnRevision).Content;
        content = TemplateCmptEditor.SetItemNamingTemplate(content, "CMP-064-{00000}").Content;
        content = TemplateCmptEditor.SetType(content, NewType).Content;
        content = TemplateCmptEditor.SetDefaultFolder(content, NewFolder).Content;

        string expected = Sample
            .Replace("\"ItemGUID\":\"" + SampleItem + "\"", "\"ItemGUID\":\"" + OwnItem + "\"")
            .Replace("\"RevisionGUID\":\"" + SampleRevision + "\"", "\"RevisionGUID\":\"" + OwnRevision + "\"")
            .Replace("CMP-001-{00000}", "CMP-064-{00000}")
            .Replace("\"DefaultFolderGUID\":\"" + OldFolder + "\"", "\"DefaultFolderGUID\":\"" + NewFolder + "\"")
            .Replace("\"ComponentTypes\":[{\"DefaultValue\":\"" + OldType + "\"", "\"ComponentTypes\":[{\"DefaultValue\":\"" + NewType + "\"");

        Assert.Equal(expected, Text(content));
        Assert.Equal(0xEF, content[0]);

        // The sample's GUID in Parameters (a foreign one) is untouched.
        Assert.Contains("\"ParameterValue\":\"" + SampleItem + "\"", Text(content));
    }

    [Fact]
    public void ItemGuidEditChangesOnlyTopLevelItemGuid()
    {
        CmptEdit edit = TemplateCmptEditor.SetItemGuid(Bytes(Sample), OwnItem);

        Assert.Equal(SampleItem, edit.Previous);
        Assert.Equal(1, Count(Text(edit.Content), SampleItem));
        Assert.Equal(1, Count(Text(edit.Content), OwnItem));
    }

    [Fact]
    public void NamingTemplateIsEscapedAsJson()
    {
        string result = Text(TemplateCmptEditor.SetItemNamingTemplate(Bytes(Sample), "\u041a\u043e\u043d\"\u0434-{0000}").Content);

        Assert.Contains("\"ItemNamingTemplate\":\"\u041a\u043e\u043d\\\"\u0434-{0000}\"", result);
    }

    // ModelLinks in the order of a production .cmpt: DefaultValue before Name, PCBLIB is empty, SCHLIB is set.
    private const string OldSymbolItem = "9FCE35E2-F8C9-4D3B-8A96-AA628B25D822";
    private const string NewSymbolItem = "11111111-2222-3333-4444-555555555555";
    private const string NewFootprintItem = "66666666-7777-8888-9999-AAAAAAAAAAAA";

    private static readonly string WithModelLinks =
        "{\"ModelLinks\":[{\"DefaultValue\":\"\",\"IsRequired\":false,\"IsReadOnly\":false,\"Description\":\"\","
        + "\"DataType\":\"CB09A478-E317-11DF-B822-12313F0024A2\",\"Name\":\"PCBLIB\",\"IsHidden\":false},"
        + "{\"DefaultValue\":\"" + OldSymbolItem + "\",\"IsRequired\":false,\"IsReadOnly\":false,\"Description\":\"\","
        + "\"DataType\":\"CB22DA24-E317-11DF-B822-12313F0024A2\",\"Name\":\"SCHLIB\",\"IsHidden\":false}],"
        + "\"ComponentTypes\":[{\"DefaultValue\":\"" + OldType + "\"}]}";

    [Fact]
    public void SetModelLinkChangesOnlyTheNamedEntry()
    {
        CmptEdit edit = TemplateCmptEditor.SetModelLink(Bytes(WithModelLinks), "SCHLIB", NewSymbolItem);

        Assert.Equal(
            WithModelLinks.Replace("\"DefaultValue\":\"" + OldSymbolItem + "\"", "\"DefaultValue\":\"" + NewSymbolItem + "\""),
            Text(edit.Content));
        Assert.Equal(OldSymbolItem, edit.Previous);
    }

    [Fact]
    public void SetModelLinkOnEmptyPcblibEntryLeavesSchlibAlone()
    {
        string result = Text(TemplateCmptEditor.SetModelLink(Bytes(WithModelLinks), "PCBLIB", NewFootprintItem).Content);

        Assert.Contains("\"DefaultValue\":\"" + NewFootprintItem + "\",\"IsRequired\":false,\"IsReadOnly\":false,\"Description\":\"\",\"DataType\":\"CB09A478-E317-11DF-B822-12313F0024A2\",\"Name\":\"PCBLIB\"", result);
        // The symbol (the second ModelLinks object) is untouched.
        Assert.Contains("\"DefaultValue\":\"" + OldSymbolItem + "\",\"IsRequired\":false,\"IsReadOnly\":false,\"Description\":\"\",\"DataType\":\"CB22DA24-E317-11DF-B822-12313F0024A2\",\"Name\":\"SCHLIB\"", result);
    }

    [Fact]
    public void SetModelLinkWithEmptyStringClearsLink()
    {
        CmptEdit edit = TemplateCmptEditor.SetModelLink(Bytes(WithModelLinks), "SCHLIB", string.Empty);

        Assert.Contains("\"DefaultValue\":\"\",\"IsRequired\":false,\"IsReadOnly\":false,\"Description\":\"\",\"DataType\":\"CB22DA24-E317-11DF-B822-12313F0024A2\",\"Name\":\"SCHLIB\"", Text(edit.Content));
        Assert.Equal(OldSymbolItem, edit.Previous);
    }

    [Fact]
    public void SetModelLinkWithoutModelLinksArrayIsRefused()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => TemplateCmptEditor.SetModelLink(Bytes(Compact.Replace("\"ModelLinks\":[{\"DefaultValue\":\"" + OldType + "\",\"Name\":\"SCHLIB\"}],", string.Empty)), "PCBLIB", NewFootprintItem));

        Assert.Contains("ModelLinks[PCBLIB].DefaultValue", exception.Message);
        Assert.Contains("must not be added", exception.Message);
    }

    [Fact]
    public void SetModelLinkWithUnknownNameIsRefused()
    {
        Assert.Throws<InvalidOperationException>(
            () => TemplateCmptEditor.SetModelLink(Bytes(WithModelLinks), "SIM3D", NewFootprintItem));
    }

    [Fact]
    public void SetModelLinkWithNonGuidValueIsRejected()
    {
        Assert.Throws<ArgumentException>(
            () => TemplateCmptEditor.SetModelLink(Bytes(WithModelLinks), "SCHLIB", "not-a-guid"));
    }

    [Fact]
    public void EditedModelLinkIsStillReadByTemplateSettings()
    {
        TemplateSettings settings = TemplateSettings.Parse(
            Text(TemplateCmptEditor.SetModelLink(Bytes(WithModelLinks), "PCBLIB", NewFootprintItem).Content));

        Assert.Equal(OldSymbolItem, settings.DefaultSymbolItemGuid);
        Assert.Equal(NewFootprintItem, settings.DefaultFootprintItemGuid);
    }

    [Fact]
    public void MissingIdentityOrNamingFieldsAreRefused()
    {
        Assert.Throws<InvalidOperationException>(() => TemplateCmptEditor.SetItemGuid(Bytes(Compact), OwnItem));
        Assert.Throws<InvalidOperationException>(() => TemplateCmptEditor.SetRevisionGuid(Bytes(Compact), OwnRevision));
        Assert.Throws<InvalidOperationException>(() => TemplateCmptEditor.SetItemNamingTemplate(Bytes(Compact), "X-{0}"));
        Assert.Throws<ArgumentException>(() => TemplateCmptEditor.SetItemGuid(Bytes(Sample), "not-a-guid"));
        Assert.Throws<ArgumentException>(() => TemplateCmptEditor.SetItemNamingTemplate(Bytes(Sample), " "));
    }

    private static int Count(string text, string part)
    {
        int count = 0;

        for (int index = text.IndexOf(part, StringComparison.Ordinal); index >= 0; index = text.IndexOf(part, index + part.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static byte[] Zip(params (string Name, string Content)[] entries)
    {
        using var buffer = new MemoryStream();

        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                ZipArchiveEntry entry = archive.CreateEntry(name);

                if (!name.EndsWith('/'))
                {
                    using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                    writer.Write(content);
                }
            }
        }

        return buffer.ToArray();
    }
}
