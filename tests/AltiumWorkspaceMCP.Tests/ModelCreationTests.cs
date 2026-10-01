using System.Text;
using AltiumWorkspaceMCP.Vault;
using Xunit;

namespace AltiumWorkspaceMCP.Tests;

/// <summary>Assembling the record of a new model and reading the library contents.</summary>
public sealed class ModelCreationTests
{
    [Fact]
    public void BuildMergesFileAndFilesWithoutRepeats()
    {
        var specs = ModelCreateSpec.Build("a.PcbLib", ["b.SchLib", "A.pcblib"], null, "description");

        Assert.Equal(["b.SchLib", "A.pcblib"], specs.Select(spec => spec.File).ToList());
        Assert.All(specs, spec => Assert.Equal("description", spec.Description));
    }

    [Fact]
    public void BuildWithoutFilesIsRefused()
    {
        Assert.Throws<ArgumentException>(() => ModelCreateSpec.Build(null, null, null, null));
        Assert.Throws<ArgumentException>(() => ModelCreateSpec.Build("  ", [], "name", null));
    }

    [Fact]
    public void BuildNameWithSeveralFilesIsRefused()
    {
        Assert.Throws<ArgumentException>(() => ModelCreateSpec.Build("a.PcbLib", ["b.PcbLib"], "name", null));
    }

    [Fact]
    public void BuildNameOfOneFileIsKept()
    {
        var spec = Assert.Single(ModelCreateSpec.Build("a.PcbLib", null, "R 0402", null));

        Assert.Equal("R 0402", spec.Name);
    }

    [Fact]
    public void InspectorCountsFootprintsAndSkipsServiceStorages()
    {
        byte[] file = BuildCfb(
            ("Library", ["Data"]),
            ("R0402", ["Data", "Parameters"]),
            ("C0603", ["Data", "Parameters"]));

        Assert.Equal(["C0603", "R0402"], LibraryFileInspector.ListItems(file, footprints: true));
    }

    [Fact]
    public void InspectorDoesNotParseForeignFileAndDoesNotFail()
    {
        Assert.Empty(LibraryFileInspector.ListItems(Encoding.UTF8.GetBytes("not a library"), footprints: true));
        Assert.Empty(LibraryFileInspector.ListItems(new byte[600], footprints: false));
    }

    /// <summary>A minimal compound document: the root and storages with streams, a 512-byte sector.</summary>
    private static byte[] BuildCfb(params (string Storage, string[] Streams)[] storages)
    {
        var entries = new List<(string Name, byte Type, uint Left, uint Right, uint Child)>
        {
            ("Root Entry", 5, 0xFFFFFFFF, 0xFFFFFFFF, 1),
        };

        // Storages are a chain of right children; the streams of each are also a chain on the right.
        int next = 1 + storages.Length;
        var streamEntries = new List<(string, byte, uint, uint, uint)>();

        for (int index = 0; index < storages.Length; index++)
        {
            uint child = (uint)(next + streamEntries.Count);
            entries.Add((storages[index].Storage, 1, 0xFFFFFFFF, index + 1 < storages.Length ? (uint)(index + 2) : 0xFFFFFFFF, child));

            for (int stream = 0; stream < storages[index].Streams.Length; stream++)
            {
                uint right = stream + 1 < storages[index].Streams.Length ? (uint)(next + streamEntries.Count + 1) : 0xFFFFFFFF;
                streamEntries.Add((storages[index].Streams[stream], 2, 0xFFFFFFFF, right, 0xFFFFFFFF));
            }
        }

        entries.AddRange(streamEntries);

        int directorySectors = (entries.Count + 3) / 4;
        var file = new byte[512 * (2 + directorySectors)];

        BitConverter.GetBytes(0xE11AB1A1E011CFD0UL).CopyTo(file, 0);
        BitConverter.GetBytes((ushort)0x3E).CopyTo(file, 24);
        BitConverter.GetBytes((ushort)3).CopyTo(file, 26);
        BitConverter.GetBytes((ushort)0xFFFE).CopyTo(file, 28);
        BitConverter.GetBytes((ushort)9).CopyTo(file, 30);
        BitConverter.GetBytes(1u).CopyTo(file, 44);
        BitConverter.GetBytes(1u).CopyTo(file, 48);
        BitConverter.GetBytes(0xFFFFFFFEu).CopyTo(file, 68);
        for (int index = 0; index < 109; index++)
        {
            BitConverter.GetBytes(index == 0 ? 0u : 0xFFFFFFFFu).CopyTo(file, 76 + index * 4);
        }

        // Sector 0 is the FAT, sectors 1… are the directory as one chain.
        int fat = 512;
        BitConverter.GetBytes(0xFFFFFFFDu).CopyTo(file, fat);
        for (int sector = 1; sector <= directorySectors; sector++)
        {
            BitConverter.GetBytes(sector == directorySectors ? 0xFFFFFFFEu : (uint)(sector + 1)).CopyTo(file, fat + sector * 4);
        }

        for (int index = 0; index < entries.Count; index++)
        {
            var (name, type, left, right, child) = entries[index];
            int offset = 512 * 2 + index * 128;
            Encoding.Unicode.GetBytes(name).CopyTo(file, offset);
            BitConverter.GetBytes((ushort)((name.Length + 1) * 2)).CopyTo(file, offset + 64);
            file[offset + 66] = type;
            BitConverter.GetBytes(left).CopyTo(file, offset + 68);
            BitConverter.GetBytes(right).CopyTo(file, offset + 72);
            BitConverter.GetBytes(child).CopyTo(file, offset + 76);
        }

        return file;
    }
}
