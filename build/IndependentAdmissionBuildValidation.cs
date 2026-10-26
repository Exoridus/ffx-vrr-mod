using System.Security.Cryptography;
using System.Text.Json;
using Fahrenheit.Mods.Vrr;

internal static class IndependentAdmissionBuildValidation
{
    internal static void Validate(string context)
    {
        string resource = Path.Combine(context, "src", "IndependentPresentationAdmission.json");
        if (!File.Exists(resource)) throw new InvalidDataException("Independent presentation veto resource is missing. Regenerate the reviewed projection before building.");
        using var stream = File.OpenRead(resource);
        var qualification = IndependentPresentationQualification.Evaluate(stream);
        if (qualification.ContractCount == 0 || qualification.BlockerCount == 0)
            throw new InvalidDataException(qualification.Reason);
        stream.Position = 0;
        using var document = JsonDocument.Parse(stream);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in document.RootElement.GetProperty("source_inputs").EnumerateArray())
        {
            string relative = source.GetProperty("path").GetString()!;
            if (relative.Contains('\\') || relative.Contains(':') || Path.IsPathRooted(relative)
                || relative.Split('/').Any(part => part is "" or "." or "..") || !paths.Add(relative))
                throw new InvalidDataException($"Unsafe or duplicate qualification source path: {relative}");
            string full = Path.GetFullPath(Path.Combine(context, relative));
            if (!full.StartsWith(Path.GetFullPath(context) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(full) || relative == "src/IndependentPresentationAdmission.json")
                throw new InvalidDataException($"Unknown qualification source path: {relative}");
            // Reparse points can make a lexically contained input read outside the compiled context.
            for (var part = new FileInfo(full) as FileSystemInfo; part is not null && part.FullName != Path.GetFullPath(context); part = part is FileInfo f ? f.Directory : ((DirectoryInfo)part).Parent)
                if ((part.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException($"Qualification source is a reparse point: {relative}");
            byte[] bytes = File.ReadAllBytes(full);
            string hash = Convert.ToHexString(SHA256.HashData(bytes));
            if (!hash.Equals(source.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase)
                && !LfHash(bytes).Equals(source.GetProperty("lf_sha256").GetString(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Stale qualification source hash: {relative}. Regenerate the reviewed projection before building.");
        }
    }

    private static string LfHash(ReadOnlySpan<byte> bytes)
    {
        var normalized = new byte[bytes.Length];
        int length = 0;
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == 0x0d && i + 1 < bytes.Length && bytes[i + 1] == 0x0a) continue;
            normalized[length++] = bytes[i];
        }
        return Convert.ToHexString(SHA256.HashData(normalized.AsSpan(0, length)));
    }
}
