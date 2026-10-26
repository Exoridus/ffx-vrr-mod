using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class ModPackage
{
    public static void Validate(string payload, string id, string version)
    {
        foreach (var suffix in new[] { ".dll", ".pdb", ".deps.json", ".runtimeconfig.json", ".manifest.json" })
            if (!File.Exists(Path.Combine(payload, id + suffix)))
                throw new InvalidDataException($"Missing payload file: {id}{suffix}");

        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(payload, id + ".manifest.json")));
        if (manifest.RootElement.GetProperty("Id").GetString() != id ||
            manifest.RootElement.GetProperty("Version").GetString() != version)
            throw new InvalidDataException("Generated manifest ID/version does not match the project.");

        foreach (var file in Directory.EnumerateFiles(payload, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(payload, file).Replace('\\', '/');
            var first = relative.Split('/')[0];
            if (relative.Contains('/') && first is not ("lang" or "efl"))
                throw new InvalidDataException($"Unexpected payload directory: {relative}");
            if (!relative.Contains('/') && relative != id + ".dll" && relative != id + ".pdb" &&
                relative != id + ".manifest.json" && relative != id + ".deps.json" &&
                relative != id + ".runtimeconfig.json" && !ReviewedRuntimeDependency(file, relative, id))
                throw new InvalidDataException($"Unexpected payload file: {relative}. Declare and review additional dependencies before shipping.");
            if (relative.StartsWith("lang/", StringComparison.Ordinal))
            {
                if (!relative.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || relative.Split('/').Length != 3)
                    throw new InvalidDataException($"Invalid localization path: {relative}");
                using var localization = JsonDocument.Parse(File.ReadAllText(file));
            }
        }
    }

    // A familiar DLL name alone must not authorize an unrelated payload.
    private static bool ReviewedRuntimeDependency(string file, string relative, string id)
        => ReviewedAv1Dependency(file, relative, id) || ReviewedPresentationDependency(file, relative, id) || id == "fhvrr" && relative == "TerraFX.Interop.Windows.dll"
            && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))
                == "BD40445EBECE5AA98EEF4C625C168E434B703041E43BC5D492B7687F92ABE2FC"
            || id == "fhvrr" && relative == "TerraFX.Interop.Windows.LICENSE.txt";

    private static bool ReviewedPresentationDependency(string file, string relative, string id)
    {
        if (id != "fhvrr" || relative is not ("fhpresent.dll" or "fhpresent.build.json")) return false;
        string directory = Path.GetDirectoryName(file)!;
        string record = Path.Combine(directory, "fhpresent.build.json");
        string library = Path.Combine(directory, "fhpresent.dll");
        if (!File.Exists(record) || !File.Exists(library)) return false;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(record));
            var value = document.RootElement;
            var inputs = value.GetProperty("input_sha256");
            return value.GetProperty("workflow").GetString() == "prepare-presentation"
                && value.GetProperty("architecture").GetString() == "x86" && value.GetProperty("abi").GetInt32() == 1
                && value.GetProperty("native_caller_test").GetString() == "passed"
                && value.GetProperty("managed_x86_probe").GetString() == "passed"
                && inputs.GetProperty("src/native/presentation/fhpresent.c").GetString() == "4D4A32B9BDB9513F839A7ABFEFA8374D880E414ADA1CFAD25C021D49DBA09ED3"
                && inputs.GetProperty("src/native/presentation/fhpresent.def").GetString() == "4847EAF52A2D0ED6B930DA8D30031E39689E1AA1323BB20BCDD6D86D9832B599"
                && inputs.GetProperty("src/NativeSnapshotBridge.cs").GetString() == "8B6DA7A180920CDA2E2588551051BAFE809E20E8EA28E162F9BC8E9EA57E2083"
                && value.GetProperty("output_sha256").GetProperty("fhpresent.dll").GetString()
                    == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(library)));
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }
    private static bool ReviewedAv1Dependency(string file, string relative, string id)
    {
        string[] required = ["fhav1.dll", "avcodec-62.dll", "avutil-60.dll", "dav1d.LICENSE.txt", "ffmpeg.LICENSE.txt", "ffmpeg.SOURCES.txt"];
        if (id != "fhvrr" || relative != "fhav1.build.json" && !required.Contains(relative)) return false;
        string directory = Path.GetDirectoryName(file)!;
        string record = Path.Combine(directory, "fhav1.build.json");
        if (!File.Exists(record) || required.Any(name => !File.Exists(Path.Combine(directory, name)))) return false;
        using var document = JsonDocument.Parse(File.ReadAllText(record));
        var value = document.RootElement;
        if (value.GetProperty("source_sha256").GetString() != "8CADC9C4D64FE491BD825137A70DDD1B76CEAB0BF899E2CB3B97B2E9B600587B"
            || value.GetProperty("dav1d_version").GetString() != "1.5.3"
            || value.GetProperty("ffmpeg_version").GetString() != "8.0.1"
            || value.GetProperty("ffmpeg_source_sha256").GetString() != "05EE0B03119B45C0BDB4DF654B96802E909E0A752F72E4FE3794F487229E5A41"
            || value.GetProperty("architecture").GetString() != "x86" || value.GetProperty("abi").GetInt32() != 2) return false;
        var outputs = value.GetProperty("outputs");
        return outputs.EnumerateObject().Count() == required.Length
            && required.All(name => outputs.GetProperty(name).GetString() == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(directory, name)))))
            && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(directory, "dav1d.LICENSE.txt")))) == "DD92C3C2247C5651606FC23A5E2D6A1EBC5ACE9A3E49CBDE0E12F05AD1CB1EE5"
            && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(directory, "ffmpeg.LICENSE.txt")))) == "246041B6ECF9BC32D718A62C57877C78B5EB397B6467E74ED7AE2626AB189C30";
    }
    public static string Create(string payload, string id, string version, string output, string tag)
    {
        Validate(payload, id, version);
        return WriteArchive(payload, id + "/", output, $"{id}-mod-{tag}.zip");
    }

    public static string CreateFull(string stage, string id, string version, string output, string tag)
    {
        Validate(Path.Combine(stage, "fahrenheit", "mods", id), id, version);
        foreach (var relative in new[] { "fhstage0.exe", "fhstage1.dll", "fh.dll", "fhr.dll", "fhr.manifest.json", "fhr.runtimeconfig.json", "fhr.deps.json", "nethost.dll" })
            if (!File.Exists(Path.Combine(stage, "fahrenheit", "bin", relative)))
                throw new InvalidDataException($"Full package is missing host file: {relative}");
        var bin = Path.Combine(stage, "fahrenheit", "bin");
        if (Directory.GetFiles(bin, "*", SearchOption.AllDirectories).Any(f => ExcludeHostFile(Path.GetRelativePath(bin, f).Replace('\\', '/'))))
            throw new InvalidDataException("Full package contains editor tools, link artifacts or native assets for another platform.");
        foreach (var relative in new[] { "start-ffx-with-mods.cmd", "fahrenheit/mods/loadorder" })
            if (!File.Exists(Path.Combine(stage, relative))) throw new InvalidDataException($"Full package is missing {relative}");
        return WriteArchive(stage, "", output, $"{id}-full-{tag}.zip");
    }

    public static void CopyHost(string source, string destination)
    {
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file).Replace('\\', '/');
            if (ExcludeHostFile(relative)) continue;
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }

    private static bool ExcludeHostFile(string relative)
    {
        var name = Path.GetFileName(relative);
        if (Path.GetExtension(name) is ".lib" or ".exp") return true;
        if (new[] { "fhatelier.", "fhdedit.", "fheedit.", "fhmodmgr." }.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) return true;
        var parts = relative.Split('/');
        return parts.Length > 1 && parts[0] == "runtimes" && parts[1] is not ("win-x86" or "win");
    }

    private static string WriteArchive(string payload, string prefix, string output, string name)
    {
        Directory.CreateDirectory(output);
        var path = Path.Combine(output, name);
        var temporary = path + ".tmp";
        try
        {
            using (var archive = new ZipArchive(File.Create(temporary), ZipArchiveMode.Create))
            {
                foreach (var file in Directory.EnumerateFiles(payload, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                {
                    var relative = Path.GetRelativePath(payload, file).Replace('\\', '/');
                    var entry = archive.CreateEntry(prefix + relative, CompressionLevel.Optimal);
                    entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                    using var source = File.OpenRead(file);
                    using var destination = entry.Open();
                    source.CopyTo(destination);
                }
                foreach (var directory in Directory.EnumerateDirectories(payload, "*", SearchOption.AllDirectories)
                    .Where(d => !Directory.EnumerateFileSystemEntries(d).Any()).Order(StringComparer.Ordinal))
                    archive.CreateEntry(prefix + Path.GetRelativePath(payload, directory).Replace('\\', '/') + "/")
                        .LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
            }
            using (var archive = ZipFile.OpenRead(temporary))
            {
                if (archive.Entries.Any(e => !e.FullName.StartsWith(prefix, StringComparison.Ordinal) || e.FullName.Contains("..")))
                    throw new InvalidDataException("Archive must extract directly into Fahrenheit's mods directory.");
            }
            var budgetName = prefix.Length == 0 ? "MAX_FULL_ZIP_MB" : "MAX_MOD_ZIP_MB";
            var configured = Environment.GetEnvironmentVariable(budgetName);
            var budget = configured is null ? (prefix.Length == 0 ? 200 : 64) : int.Parse(configured, System.Globalization.CultureInfo.InvariantCulture);
            if (budget <= 0 || new FileInfo(temporary).Length > budget * 1024L * 1024L)
                throw new InvalidDataException($"Archive exceeds {budgetName} ({budget} MiB).");
            File.Move(temporary, path, true);
            using var stream = File.OpenRead(path);
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            File.WriteAllText(path + ".sha256", $"{hash}  {Path.GetFileName(path)}\n", new UTF8Encoding(false));
            return path;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
