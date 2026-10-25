namespace Fahrenheit.Mods.Vrr;

using System;
using System.IO;
using System.Text.Json;

internal sealed record FmvLabCase(string Label, string VirtualPath, string File);

internal sealed class FmvPlaybackLab
{
    private readonly FmvLabCase[] _cases;
    internal int Count => _cases.Length;
    private FmvPlaybackLab(FmvLabCase[] cases) => _cases = cases;

    internal static FmvPlaybackLab Load(string manifest)
    {
        if (new FileInfo(manifest).Length > 65536) throw new InvalidDataException("FMV lab manifest exceeds 64 KiB.");
        var cases = JsonSerializer.Deserialize<FmvLabCase[]>(System.IO.File.ReadAllText(manifest),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("FMV lab manifest is empty.");
        if (cases.Length is < 1 or > 16) throw new InvalidDataException("FMV lab needs 1..16 cases.");
        string directory = Path.GetDirectoryName(Path.GetFullPath(manifest))!;
        for (int i = 0; i < cases.Length; i++)
        {
            var item = cases[i];
            if (string.IsNullOrWhiteSpace(item.Label) || item.Label.Length > 100 || string.IsNullOrWhiteSpace(item.VirtualPath)
                || !item.VirtualPath.Replace('\\', '/').StartsWith("ffx_data/gamedata/ps3data/video/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("FMV lab needs a label and a canonical movie path.");
            string file = Path.GetFullPath(item.File, directory);
            if (!System.IO.File.Exists(file) || file.Length >= 256 || !System.Linq.Enumerable.All(file, c => c <= 0x7f))
                throw new InvalidDataException("FMV candidate is missing or exceeds the native ASCII path capacity.");
            cases[i] = item with { File = file, VirtualPath = item.VirtualPath.Replace('\\', '/') };
        }
        return new(cases);
    }

    internal string Label(int index) => index == 0 ? "Original (retail)" : _cases[index - 1].Label;

    internal string? Select(int index, string virtualPath) => index is > 0 && index <= Count
        && string.Equals(_cases[index - 1].VirtualPath, virtualPath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)
        ? _cases[index - 1].File : null;
}
