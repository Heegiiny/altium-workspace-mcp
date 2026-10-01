using System.Xml.Linq;
using AltiumWorkspaceMCP.Soap.Vault;
using AltiumWorkspaceMCP.Vault;

namespace AltiumWorkspaceMCP.Tests;

/// <summary>
/// A script creating a component by one atomic group. The command order was checked against
/// the requests Altium Designer sends when copying a component.
/// </summary>
public sealed class ComponentCreationScriptTests
{
    private const string ItemGuid = "D45F20DB-6EB5-419A-99F1-1A7E732465B7";
    private const string RevisionGuid = "B9DB0302-5C7C-417A-9AE0-7B228C747174";
    private const string DatasheetGuid = "7133D117-8DA1-480A-8174-C28615796926";
    private const string TypeGuid = "24C51BB7-57FA-4BFA-94AC-977A056405C3";

    private static ComponentCreation Creation(bool withItemLinks = true, bool withTag = true)
    {
        var revision = new ALU_ItemRevision
        {
            GUID = RevisionGuid,
            ItemGUID = ItemGuid,
            ItemHRID = "CMP-000-00001",
            RevisionId = "01",
            Comment = "Copy of TestCap",
            Description = "New Test Description",
            RevisionParameters = new _ALU_ItemRevisionParameterList(),
        };

        var item = new ALU_Item
        {
            GUID = ItemGuid,
            HRID = "CMP-000-00001",
            Revisions = new _ALU_ItemRevisionList { revision },
        };

        var link = new ALU_ItemRevisionLink
        {
            GUID = "11111111-1111-1111-1111-111111111111",
            HRID = LinkRole.Symbol,
            ParentItemRevisionGUID = RevisionGuid,
            ChildItemRevisionGUID = "22222222-2222-2222-2222-222222222222",
        };

        var itemLinks = withItemLinks
            ? new List<ALU_ItemLink>
            {
                new()
                {
                    GUID = string.Empty,
                    ParentItemGUID = ItemGuid,
                    ChildItemGUID = DatasheetGuid,
                    LinkTypeGUID = "3F4AE0F7-CE34-4626-B5AE-C87D2E3AE56D",
                    LinkParameters = new _ALU_ItemLinkParameterList
                    {
                        new ALU_ItemLinkParameter { HRID = "FileName", ParameterValue = "DOC.pdf" },
                    },
                },
            }
            : [];

        var tags = withTag
            ? new List<ALU_ItemTag> { new() { GUID = string.Empty, ItemGUID = ItemGuid, TagGUID = TypeGuid } }
            : [];

        return new ComponentCreation(item, [link], itemLinks, tags);
    }

    private static XElement Group(string script) =>
        XDocument.Parse(script).Root!.Element("AtomicGroups")!.Elements("item").Single();

    private static List<string> Operations(XElement group) =>
        group.Element("Commands")!.Elements("item").Select(command => command.Element("Operation")!.Value).ToList();

    [Fact]
    public void OneAtomicGroupWithAltiumCommandOrder()
    {
        string script = VaultScriptExecutor.BuildScript(
            "SESSION", VaultScriptExecutor.CreationGroup,
            VaultScriptExecutor.CreationCommands(Creation(), "SESSION", "note"));

        XElement group = Group(script);

        Assert.Equal("Release single component", group.Element("HRID")!.Value);
        Assert.Equal(
            ["AddALU_Items", "ReleaseALU_ItemRevisions", "AddALU_ItemLinks", "AssignALU_ItemTags"],
            Operations(group));
    }

    [Fact]
    public void ItemCarriesItsFirstRevisionAndNoSeparateRevisionCommand()
    {
        string script = VaultScriptExecutor.BuildScript(
            "SESSION", VaultScriptExecutor.CreationGroup,
            VaultScriptExecutor.CreationCommands(Creation(), "SESSION", "note"));

        XElement add = Group(script).Element("Commands")!.Elements("item").First();
        XElement item = add.Element("Records")!.Elements("item").Single();

        Assert.Equal(ItemGuid, item.Element("GUID")!.Value, ignoreCase: true);
        Assert.Equal(RevisionGuid, item.Element("Revisions")!.Elements().Single().Element("GUID")!.Value, ignoreCase: true);
        Assert.DoesNotContain("AddALU_ItemRevisions", script);
    }

    [Fact]
    public void ReleaseRecordPointsAtTheNewRevisionAndCarriesLinksAndNote()
    {
        string script = VaultScriptExecutor.BuildScript(
            "SESSION", VaultScriptExecutor.CreationGroup,
            VaultScriptExecutor.CreationCommands(Creation(), "SESSION", "Copy"));

        XElement release = Group(script).Element("Commands")!.Elements("item").ElementAt(1)
            .Element("Records")!.Elements("item").Single();

        Assert.Equal(RevisionGuid, release.Element("ItemRevisionGUID")!.Value, ignoreCase: true);
        Assert.Equal("Copy", release.Element("ReleaseNote")!.Value);
        Assert.Single(release.Element("ItemRevisionLinks")!.Elements("item"));
        Assert.Equal("Copy of TestCap", release.Element("UpdateItemRevision")!.Elements().Single().Element("Comment")!.Value);
    }

    [Fact]
    public void ItemLinkAndTagCommandsCarryEmptyGuidsForTheServerToAssign()
    {
        string script = VaultScriptExecutor.BuildScript(
            "SESSION", VaultScriptExecutor.CreationGroup,
            VaultScriptExecutor.CreationCommands(Creation(), "SESSION", "note"));

        var commands = Group(script).Element("Commands")!.Elements("item").ToList();

        XElement link = commands[2].Element("Records")!.Elements("item").Single();
        Assert.Equal(string.Empty, link.Element("GUID")!.Value);
        Assert.Equal(ItemGuid, link.Element("ParentItemGUID")!.Value, ignoreCase: true);
        Assert.Equal(DatasheetGuid, link.Element("ChildItemGUID")!.Value, ignoreCase: true);

        XElement tag = commands[3].Element("Records")!.Elements("item").Single();
        Assert.Equal(string.Empty, tag.Element("GUID")!.Value);
        Assert.Equal(TypeGuid, tag.Element("TagGUID")!.Value, ignoreCase: true);
    }

    [Fact]
    public void EmptyItemLinksAndTagsProduceNoCommands()
    {
        string script = VaultScriptExecutor.BuildScript(
            "SESSION", VaultScriptExecutor.CreationGroup,
            VaultScriptExecutor.CreationCommands(Creation(withItemLinks: false, withTag: false), "SESSION", "note"));

        Assert.Equal(["AddALU_Items", "ReleaseALU_ItemRevisions"], Operations(Group(script)));
    }

    [Fact]
    public async Task DryRunRecordsOneScriptAndNoSeparateItemWrite()
    {
        var executor = new VaultScriptExecutor(
            new VaultEndpoints(new Configuration.VaultOptions
            {
                BaseUrl = new Uri("http://localhost:9780"),
                StateDirectory = Path.GetTempPath(),
                ExchangeDirectory = Path.GetTempPath(),
            }),
            null!,
            new HttpClient());

        await DryRun.RunAsync("vault_copy_components", dryRun: true, async () =>
        {
            await executor.CreateComponentAsync(Creation(), "note", CancellationToken.None);

            var writes = DryRun.Current!.Writes;
            Assert.Equal("ExecuteScript: Release single component", Assert.Single(writes).Operation);
            Assert.DoesNotContain(writes, write => write.Operation.Contains("AddALU_Items", StringComparison.Ordinal)
                && !write.Operation.StartsWith("ExecuteScript", StringComparison.Ordinal));

            return new { };
        });
    }
}
