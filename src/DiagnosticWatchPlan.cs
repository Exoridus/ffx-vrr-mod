namespace Fahrenheit.Mods.Vrr;

using System;
using System.Globalization;
using System.IO;
using System.Text.Json;

internal sealed record DiagnosticWatch(string Name, string Rva, int Bytes, int[]? Offsets = null);
internal sealed record DiagnosticWatchPlan(string ImageSha256, DiagnosticWatch[] Watches)
{
    internal static DiagnosticWatchPlan Load(string path, string expectedHash, int imageSize)
    {
        if (new FileInfo(path).Length > 65536) throw new InvalidDataException("Watch plan exceeds 64 KiB.");
        var plan = JsonSerializer.Deserialize<DiagnosticWatchPlan>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Watch plan is empty.");
        if (!string.Equals(plan.ImageSha256, expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Watch plan executable hash does not match the validated image.");
        if (plan.Watches is null || plan.Watches.Length > 32) throw new InvalidDataException("At most 32 watches are allowed.");
        foreach (var watch in plan.Watches)
        {
            if (string.IsNullOrWhiteSpace(watch.Name) || watch.Name.Length > 80 || watch.Bytes is < 4 or > 496 || watch.Bytes % 4 != 0
                || watch.Offsets is { Length: > 4 } || RvaValue(watch.Rva) >= imageSize
                || (long)RvaValue(watch.Rva) + (watch.Offsets is { Length: > 0 } ? 4 : watch.Bytes) > imageSize)
                throw new InvalidDataException("Invalid watch name, range, RVA or pointer depth.");
        }
        return plan;
    }

    internal static uint RvaValue(string rva) => uint.Parse(rva.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? rva[2..] : rva,
        rva.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? NumberStyles.HexNumber : NumberStyles.Integer, CultureInfo.InvariantCulture);
}
