namespace Fahrenheit.Mods.Shared;

internal sealed class MenuWaterExpansion
{
    internal sealed record Write(int Rva, byte[] Bytes);
    internal static IReadOnlyList<Write> Plan(uint imageBase, uint relocatedTable, Func<int, int, byte[]> read)
    {
        uint originalTable = checked(imageBase + 0x859c40);
        uint originalEnd = checked(imageBase + 0x859d58);
        var sites = new (int instruction, byte[] prefix, uint expected, uint replacement)[] {
            (0x25b62f, [0xff, 0x34, 0x85], originalTable, relocatedTable),
            (0x27cba1, [0xbe], originalTable, relocatedTable),
            (0x27cbc0, [0x81, 0xfe], originalEnd, checked(relocatedTable + 140 * 4)),
            (0x27efb5, [0xbe], originalTable, relocatedTable),
            (0x27f015, [0x81, 0xfe], originalEnd, checked(relocatedTable + 140 * 4))
        };
        var writes = new List<Write>();
        foreach (var site in sites)
        {
            byte[] actual = read(site.instruction, site.prefix.Length + 4);
            if (!actual.AsSpan(0, site.prefix.Length).SequenceEqual(site.prefix)
                || BitConverter.ToUInt32(actual, site.prefix.Length) != site.expected)
                throw new InvalidDataException($"Menu water instruction mismatch at RVA 0x{site.instruction:X}.");
            writes.Add(new(site.instruction + site.prefix.Length, BitConverter.GetBytes(site.replacement)));
        }
        if (!read(0x25b6d9, 3).AsSpan().SequenceEqual(new byte[] { 0x80, 0xf9, 0x46 }))
            throw new InvalidDataException("Menu water wrap instruction mismatch.");
        writes.Add(new(0x25b6db, [0x8c]));
        return writes;
    }

    internal static string[] ValidatePack(string root)
    {
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "menu-water-pack.json")));
        var pack = document.RootElement;
        if (pack.GetProperty("schema_version").GetInt32() != 1 || pack.GetProperty("frame_count").GetInt32() != 140
            || pack.GetProperty("fps").GetInt32() != 60 || pack.GetProperty("width").GetInt32() != 1024
            || pack.GetProperty("height").GetInt32() != 256 || pack.GetProperty("format").GetString() != "DXT5"
            || pack.GetProperty("mip_count").GetInt32() != 1 || pack.GetProperty("files").GetArrayLength() != 140)
            throw new InvalidDataException("Unsupported menu water pack.");
        var paths = new string[140];
        foreach (var file in pack.GetProperty("files").EnumerateArray())
        {
            int slot = file.GetProperty("slot").GetInt32();
            if (slot < 0 || slot >= 140 || paths[slot] is not null) throw new InvalidDataException("Invalid menu water slot.");
            string expected = $"ffx_data/gamedata/ps3data/menu/titl/d3d11/water_{slot:00}.dds.phyre";
            if (file.GetProperty("path").GetString() != expected) throw new InvalidDataException("Unexpected menu water path.");
            string path = Path.Combine(root, expected.Replace('/', Path.DirectorySeparatorChar));
            string hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
            if (hash != file.GetProperty("sha256").GetString()) throw new InvalidDataException($"Menu water hash differs at slot {slot}.");
            paths[slot] = "/FFX_Data/GameData/PS3Data/menu/titl/D3D11/Water_" + slot.ToString("00") + ".dds.phyre";
        }
        return paths;
    }
    internal static void ValidateEflRoot(string packRoot, IEnumerable<string> orderedRoots)
    {
        packRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(packRoot));
        string[] roots = orderedRoots.Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))).ToArray();
        if (!roots.Contains(packRoot, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("Expanded pack must be placed in an installed mod's efl/x directory before game startup.");
        for (int slot = 0; slot < 140; slot++)
        {
            string relative = $"ffx_data/gamedata/ps3data/menu/titl/d3d11/water_{slot:00}.dds.phyre";
            string? selected = roots.LastOrDefault(root => File.Exists(Path.Combine(root, relative)));
            if (!string.Equals(selected, packRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Expanded menu slot {slot} is missing or shadowed by another EFL root.");
        }
    }
}
