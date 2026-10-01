using System.IO.Compression;
using AltiumWorkspaceMCP.Vault;

namespace AltiumWorkspaceMCP.Tests;

/// <summary>The component type on creation: the source choice and parsing of the template settings.</summary>
public sealed class ComponentTypeChoiceTests
{
    private const string Capacitors = "24C51BB7-57FA-4BFA-94AC-977A056405C3";
    private const string Resistors = "11111111-2222-3333-4444-555555555555";
    private const string Ics = "99999999-2222-3333-4444-555555555555";

    private static TypeChoice Choose(string? explicitType, string? template, string? folderTemplate, string? source) =>
        ComponentTypeChooser.Choose(
        [
            new TypeCandidate(TypeSource.Explicit, explicitType),
            new TypeCandidate(TypeSource.Template, template),
            new TypeCandidate(TypeSource.FolderTemplate, folderTemplate),
            new TypeCandidate(TypeSource.Source, source),
        ]);

    [Fact]
    public void ExplicitWinsAndIsNotCheckedAgainstOthers()
    {
        TypeChoice choice = Choose(Ics, Capacitors, Resistors, Capacitors);

        Assert.Equal(TypeSource.Explicit, choice.Chosen!.Source);
        Assert.Equal(Ics, choice.Chosen.TypeGuid);
        Assert.Empty(choice.Disagreeing);
    }

    [Fact]
    public void TemplateBeatsFolderTemplateAndSource()
    {
        TypeChoice choice = Choose(null, Capacitors, Capacitors, Capacitors);

        Assert.Equal(TypeSource.Template, choice.Chosen!.Source);
        Assert.Empty(choice.Disagreeing);
    }

    [Fact]
    public void FolderTemplateIsTheNextFallback()
    {
        TypeChoice choice = Choose(null, null, Resistors, Capacitors);

        Assert.Equal(TypeSource.FolderTemplate, choice.Chosen!.Source);
        Assert.Equal(Resistors, choice.Chosen.TypeGuid);
    }

    [Fact]
    public void SourceIsTheLastFallback()
    {
        TypeChoice choice = Choose(null, null, null, Capacitors);

        Assert.Equal(TypeSource.Source, choice.Chosen!.Source);
    }

    [Fact]
    public void NoSourceGivesNothing()
    {
        TypeChoice choice = Choose(null, " ", "", null);

        Assert.Null(choice.Chosen);
        Assert.Empty(choice.Disagreeing);
    }

    [Fact]
    public void DisagreeingLowerSourcesAreReportedWithFirstChosen()
    {
        TypeChoice choice = Choose(null, Resistors, Capacitors, Ics);

        Assert.Equal(Resistors, choice.Chosen!.TypeGuid);
        Assert.Equal([TypeSource.FolderTemplate, TypeSource.Source], choice.Disagreeing.Select(item => item.Source));
    }

    [Fact]
    public void GuidsAreComparedIgnoringCase()
    {
        TypeChoice choice = Choose(null, Capacitors, Capacitors.ToLowerInvariant(), null);

        Assert.Empty(choice.Disagreeing);
    }

    private const string Cmpt = """
        {
          "VaultGUID": "00000000-0000-4000-8000-000000000001",
          "Parameters": [],
          "ComponentTypes": [ { "DefaultValue": "24C51BB7-57FA-4BFA-94AC-977A056405C3", "Name": "ComponentType" } ],
          "DefaultFolder": { "AllowOverride": true, "DefaultFolderGUID": "C50D6DBA-874B-4A60-98C9-CC9D269F94E2" }
        }
        """;

    [Fact]
    public void CmptJsonGivesTypeAndDefaultFolder()
    {
        TemplateSettings settings = TemplateSettings.Parse(Cmpt);

        Assert.Equal(Capacitors, settings.TypeGuid);
        Assert.Equal("C50D6DBA-874B-4A60-98C9-CC9D269F94E2", settings.DefaultFolderGuid);
        Assert.True(settings.AllowFolderOverride);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "ComponentTypes": [] }""")]
    [InlineData("""{ "ComponentTypes": [ { "Name": "x" } ], "DefaultFolder": { "DefaultFolderGUID": "" } }""")]
    public void IncompleteCmptGivesEmptySettings(string json)
    {
        TemplateSettings settings = TemplateSettings.Parse(json);

        Assert.Null(settings.TypeGuid);
        Assert.Null(settings.DefaultFolderGuid);
        Assert.False(settings.AllowFolderOverride);
    }

    [Fact]
    public void CmptIsFoundInsideRevisionPackage()
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var writer = new StreamWriter(archive.CreateEntry("Preview/readme.txt").Open()))
            {
                writer.Write("not that");
            }

            using (var writer = new StreamWriter(archive.CreateEntry("Released/Template.cmpt").Open()))
            {
                writer.Write("﻿" + Cmpt);
            }
        }

        TemplateSettings? settings = TemplateSettings.FromPackage(buffer.ToArray());

        Assert.Equal(Capacitors, settings?.TypeGuid);
    }

    [Fact]
    public void PackageWithoutCmptGivesNull()
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(archive.CreateEntry("Released/a.txt").Open());
            writer.Write("x");
        }

        Assert.Null(TemplateSettings.FromPackage(buffer.ToArray()));
    }
}
