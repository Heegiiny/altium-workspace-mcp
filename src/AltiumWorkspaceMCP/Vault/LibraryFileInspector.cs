using System.Text;

namespace AltiumWorkspaceMCP.Vault;

/// <summary>
/// What is in a .PcbLib / .SchLib library file: the names of footprints or symbols.
/// Altium files are OLE compound documents (CFB): each library element is its own storage
/// in the root with its own <c>Data</c> stream. Only the directory is read, the stream content is not touched.
/// </summary>
/// <remarks>
/// Needed so that <c>vault_model_files create</c> can say: "the file has several footprints —
/// the vault has one model per file". Parsing is best-effort; an unreadable file is not a refusal
/// but an empty result.
/// </remarks>
public static class LibraryFileInspector
{
    private const uint EndOfChain = 0xFFFFFFFE;
    private const uint FreeSector = 0xFFFFFFFF;

    /// <summary>Root storages of service data that are not library elements.</summary>
    private static readonly HashSet<string> ServiceStorages = new(StringComparer.OrdinalIgnoreCase)
    {
        "Library", "FileHeader", "SectionKeys", "Storage", "Models", "ModelsNoEmbed", "Textures", "PadViaLibrary",
    };

    /// <summary>Names of library elements; empty if the file was not parsed.</summary>
    public static IReadOnlyList<string> ListItems(byte[] file, bool footprints)
    {
        try
        {
            return ListItemsCore(file, footprints);
        }
        catch (Exception exception) when (exception is IndexOutOfRangeException or ArgumentException or InvalidDataException or OverflowException)
        {
            return [];
        }
    }

    private static IReadOnlyList<string> ListItemsCore(byte[] file, bool footprints)
    {
        if (file.Length < 512 || BitConverter.ToUInt64(file, 0) != 0xE11AB1A1E011CFD0)
        {
            return [];
        }

        int sectorSize = 1 << BitConverter.ToUInt16(file, 30);
        uint firstDirectory = BitConverter.ToUInt32(file, 48);
        uint fatCount = BitConverter.ToUInt32(file, 44);
        uint difatStart = BitConverter.ToUInt32(file, 68);
        uint difatCount = BitConverter.ToUInt32(file, 72);

        int Offset(uint sector) => checked((int)((sector + 1L) * sectorSize));

        var fatSectors = new List<uint>();
        for (int index = 0; index < 109 && fatSectors.Count < fatCount; index++)
        {
            fatSectors.Add(BitConverter.ToUInt32(file, 76 + index * 4));
        }

        uint difat = difatStart;
        for (uint block = 0; block < difatCount && difat < EndOfChain && fatSectors.Count < fatCount; block++)
        {
            int start = Offset(difat);
            for (int index = 0; index < sectorSize / 4 - 1 && fatSectors.Count < fatCount; index++)
            {
                fatSectors.Add(BitConverter.ToUInt32(file, start + index * 4));
            }

            difat = BitConverter.ToUInt32(file, start + sectorSize - 4);
        }

        var fat = new List<uint>();
        foreach (uint sector in fatSectors)
        {
            int start = Offset(sector);
            for (int index = 0; index < sectorSize / 4; index++)
            {
                fat.Add(BitConverter.ToUInt32(file, start + index * 4));
            }
        }

        var directory = new List<byte[]>();
        uint current = firstDirectory;
        int guard = 0;

        while (current < EndOfChain && guard++ < fat.Count + 1)
        {
            int start = Offset(current);
            for (int entry = 0; entry < sectorSize / 128; entry++)
            {
                directory.Add(file.AsSpan(start + entry * 128, 128).ToArray());
            }

            current = fat[(int)current];
        }

        if (directory.Count == 0)
        {
            return [];
        }

        string Name(byte[] entry)
        {
            int length = Math.Max(0, BitConverter.ToUInt16(entry, 64) - 2);
            return Encoding.Unicode.GetString(entry, 0, Math.Min(length, 64));
        }

        IEnumerable<int> Walk(int root)
        {
            var stack = new Stack<int>();
            var seen = new HashSet<int>();
            if (root >= 0 && root < directory.Count && BitConverter.ToUInt32(directory[root], 76) != FreeSector)
            {
                stack.Push((int)BitConverter.ToUInt32(directory[root], 76));
            }

            while (stack.Count > 0)
            {
                int node = stack.Pop();
                if (node < 0 || node >= directory.Count || !seen.Add(node))
                {
                    continue;
                }

                yield return node;

                foreach (int side in new[] { 68, 72 })
                {
                    uint next = BitConverter.ToUInt32(directory[node], side);
                    if (next != FreeSector)
                    {
                        stack.Push((int)next);
                    }
                }
            }
        }

        var names = new List<string>();

        foreach (int storage in Walk(0))
        {
            byte[] entry = directory[storage];

            if (entry[66] != 1 || ServiceStorages.Contains(Name(entry)))
            {
                continue;
            }

            var children = Walk(storage).Select(child => Name(directory[child])).ToHashSet(StringComparer.OrdinalIgnoreCase);

            // A footprint has Parameters and Data; a symbol — Data.
            if (children.Contains("Data") && (!footprints || children.Contains("Parameters")))
            {
                names.Add(Name(entry));
            }
        }

        return names.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
