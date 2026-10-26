using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

try
{
    return await Run(args);
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}

static async Task<int> Run(string[] args)
{
    var root = Path.GetFullPath(args[0]);
    var command = args.ElementAtOrDefault(1) ?? "help";
    if (command is "help" or "--help" or "-h")
    {
        Console.WriteLine("build.cmd mod-build|mod-verify|mod-pack [--sdk local|official|source] [--configuration Release|Debug] [--tag vX.Y.Z[-suffix]] [--out-dir path]");
        Console.WriteLine("Official is the default and uses only nuget.org and an isolated package cache. Local is for pinned development packages. Distribution is mod-only; install Fahrenheit separately. No command deploys to a game installation.");
        return 0;
    }
    if (command is not ("mod-build" or "mod-verify" or "mod-pack")) throw new ArgumentException($"Unknown command: {command}");
    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var i = 2; i < args.Length; i += 2)
    {
        if (i + 1 == args.Length || args[i] is not ("--sdk" or "--configuration" or "--tag" or "--out-dir" or "--full"))
            throw new ArgumentException($"Invalid option: {args[i]}");
        options.Add(args[i], args[i + 1]);
    }
    var mode = options.GetValueOrDefault("--sdk", "official");
    var configuration = options.GetValueOrDefault("--configuration", "Release");
    if (bool.Parse(options.GetValueOrDefault("--full", "false")))
        throw new ArgumentException("Distribution is mod-only; install Fahrenheit separately. --full true is no longer supported.");
    if (mode is not ("local" or "official" or "source")) throw new ArgumentException("SDK must be local, official or source (legacy template only).");
    if (configuration is not ("Debug" or "Release")) throw new ArgumentException("Configuration must be Debug or Release.");
    if (mode == "official" && configuration != "Release") throw new ArgumentException("Official distribution requires Release configuration.");
    var project = Directory.GetFiles(root, "Fahrenheit.Mods.*.csproj").Single();
    var xml = XDocument.Load(project);
    string Property(string name) => xml.Descendants(name).First().Value;
    var id = Property("AssemblyName");
    var version = Property("Version");
    var sdk = xml.Root!.Attribute("Sdk")!.Value;
    var legacy = sdk == "Microsoft.NET.Sdk";
    var sdkVersion = legacy ? Property("FahrenheitVersion") : sdk.Split('/')[1];
    if (legacy && !options.ContainsKey("--sdk")) mode = "source";
    if (legacy != (mode == "source")) throw new ArgumentException("The legacy project requires --sdk source; modern projects require local or official.");
    if (!Regex.IsMatch(id, @"^[A-Za-z0-9][A-Za-z0-9._-]*$") || id.Contains("..")) throw new InvalidDataException("Unsafe mod ID.");
    var tag = options.GetValueOrDefault("--tag", $"v{version}-dev");
    if (!Regex.IsMatch(tag, @"^v\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?$") || tag.Contains("..")) throw new ArgumentException("Invalid tag.");
    if (mode == "official" && command == "mod-pack" && tag != "v" + version)
        throw new ArgumentException("Official release tag must match the project Version exactly.");
    var state = Path.Combine(root, ".workspace", "mod-build", mode);
    Directory.CreateDirectory(state);
    using var buildLock = new FileStream(Path.Combine(state, "build.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    var context = Path.Combine(state, "context");
    var payload = Path.Combine(state, "payload", configuration, id);
    var output = Path.GetFullPath(options.GetValueOrDefault("--out-dir", "artifacts/packages"), root);
    var pin = File.ReadAllText(Path.Combine(root, "fahrenheit.release.ref")).Trim();
    string? localFeed = null;
    var sdkPackages = new Dictionary<string, string>();
    if (mode is "local" or "source")
    {
        if (!Regex.IsMatch(pin, "^[a-fA-F0-9]{40}$")) throw new InvalidDataException("Fahrenheit development ref must be a full commit SHA.");
        var checkout = Path.Combine(root, ".workspace", "fahrenheit");
        if (!Directory.Exists(checkout))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(checkout)!);
            Execute("git", ["clone", "https://github.com/fahrenheit-crew/fahrenheit.git", checkout], root);
        }
        var actual = Execute("git", ["-C", checkout, "rev-parse", "HEAD"], root, capture: true).Trim();
        if (actual != pin)
        {
            if (Execute("git", ["-C", checkout, "status", "--porcelain"], root, capture: true).Length > 0)
                throw new InvalidOperationException("Fahrenheit checkout has local changes; cannot switch its pinned revision.");
            Execute("git", ["-C", checkout, "fetch", "origin", pin], root);
            Execute("git", ["-C", checkout, "checkout", "--detach", pin], root);
        }
        if (Execute("git", ["-C", checkout, "status", "--porcelain", "--untracked-files=no"], root, capture: true).Length != 0)
            throw new InvalidOperationException("Pinned Fahrenheit checkout has tracked changes. Commit and pin the intended source before packaging.");
        var releaseFeed = Path.Combine(checkout, "artifacts", "pkg", "rel");
        Directory.CreateDirectory(releaseFeed);
        Directory.CreateDirectory(Path.Combine(checkout, "artifacts", "pkg", "dbg"));
        if (!legacy) localFeed = releaseFeed;
        var stamp = Path.Combine(releaseFeed, ".mod-build-source-ref");
        var packagesMatchPin = File.Exists(stamp) && File.ReadAllText(stamp).Trim() == pin;
        foreach (var item in legacy ? [] : new[] { ("sdk", "Fahrenheit.Sdk"), ("core", "Fahrenheit") })
        {
            if (!packagesMatchPin || !File.Exists(Path.Combine(releaseFeed, $"{item.Item2}.{sdkVersion}.nupkg")))
                Execute("dotnet", ["build", Path.Combine(checkout, "src", item.Item1, item.Item2 + ".csproj"), "-c", "Release", "--verbosity", "quiet", "-p:SolutionDir=" + checkout + Path.DirectorySeparatorChar], checkout);
        }
        if (!legacy) File.WriteAllText(stamp, pin + "\n");
    }
    else
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        using var available = JsonDocument.Parse(await client.GetStringAsync("https://api.nuget.org/v3-flatcontainer/fahrenheit.sdk/index.json"));
        if (!available.RootElement.GetProperty("versions").EnumerateArray().Any(v => v.GetString() == sdkVersion))
            throw new InvalidOperationException($"Official Fahrenheit.Sdk {sdkVersion} is not published on nuget.org. Use --sdk local for development; public releases remain blocked.");
    }
    Recreate(context, state);
    foreach (var file in Directory.GetFiles(root, "*", SearchOption.TopDirectoryOnly).Where(f => Path.GetExtension(f) is ".csproj" or ".props" or ".targets" || Path.GetFileName(f) is "global.json" or ".editorconfig"))
        File.Copy(file, Path.Combine(context, Path.GetFileName(file)), true);
    foreach (var directory in new[] { "src", "lang", "efl", "tests" })
        CopyTree(Path.Combine(root, directory), Path.Combine(context, directory));
    Directory.CreateDirectory(Path.Combine(context, "build"));
    foreach (var name in new[] { "SettingsMigration.cs" })
        if (File.Exists(Path.Combine(root, "build", name))) File.Copy(Path.Combine(root, "build", name), Path.Combine(context, "build", name));
    CopyTree(Path.Combine(root, "build", "tests"), Path.Combine(context, "build", "tests"));
    CopyTree(Path.Combine(root, "build", "distribution"), Path.Combine(context, "build", "distribution"));
    foreach (var name in new[] { "ModPackage.cs", "ModBuild.cs", "IndependentAdmissionBuildValidation.cs" }) File.Copy(Path.Combine(root, "build", name), Path.Combine(context, "build", name), true);
    IndependentAdmissionBuildValidation.Validate(context);
    var sources = new XElement("packageSources", new XElement("clear"), new XElement("add", new XAttribute("key", "nuget.org"), new XAttribute("value", "https://api.nuget.org/v3/index.json")));
    var mapping = new XElement("packageSourceMapping", new XElement("clear"), new XElement("packageSource", new XAttribute("key", "nuget.org"), new XElement("package", new XAttribute("pattern", "*"))));
    var cacheIdentity = legacy ? "source-" + pin : "official";
    if (localFeed is not null)
    {
        sources.Add(new XElement("add", new XAttribute("key", "FahrenheitLocal"), new XAttribute("value", localFeed)));
        mapping.Add(new XElement("packageSource", new XAttribute("key", "FahrenheitLocal"), new XElement("package", new XAttribute("pattern", "Fahrenheit")), new XElement("package", new XAttribute("pattern", "Fahrenheit.*"))));
        sdkPackages = new[] { "Fahrenheit.Sdk", "Fahrenheit" }.ToDictionary(name => name, name => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(localFeed, $"{name}.{sdkVersion}.nupkg")))));
        cacheIdentity = "local-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("", sdkPackages.Values))))[..16];
    }
    new XDocument(new XElement("configuration", sources, mapping)).Save(Path.Combine(context, "NuGet.config"));
    var inputs = Directory.GetFiles(context, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
        .ToDictionary(f => Path.GetRelativePath(context, f).Replace('\\', '/'), f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));
    var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FahrenheitModBuild", "packages", sdkVersion, cacheIdentity);
    var environment = new Dictionary<string, string> { ["NUGET_PACKAGES"] = cache };
    Recreate(payload, state);
    var buildArguments = new List<string> { "build", Path.Combine(context, Path.GetFileName(project)), "-c", configuration, "--verbosity", "minimal", "-p:PublishDir=" + payload + Path.DirectorySeparatorChar, "-p:ContinuousIntegrationBuild=true", "-p:Deterministic=true", "-p:PathMap=" + context + "=/_/" };
    if (legacy)
    {
        buildArguments.Add("-p:FahrenheitSourceDir=" + Path.Combine(root, ".workspace", "fahrenheit"));
        buildArguments.Add("-p:SolutionDir=" + Path.Combine(root, ".workspace", "fahrenheit") + Path.DirectorySeparatorChar);
    }
    Execute("dotnet", buildArguments, context, environment);
    if (legacy)
    {
        var manifest = new { Id = id, Name = Property("AssemblyTitle"), Desc = Property("Description"), Authors = Property("Authors"), Version = version,
            Link = Property("RepositoryUrl"), Dependencies = Array.Empty<string>(), Flags = Property("FhManifestFlags") };
        File.WriteAllText(Path.Combine(payload, id + ".manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }
    Execute("dotnet", ["run", "--file", Path.Combine(root, "build", "PrepareAv1.cs"), "--", root, payload], root);
    foreach (var license in Directory.GetFiles(Path.Combine(root, "build", "licenses")))
        File.Copy(license, Path.Combine(payload, Path.GetFileName(license)), true);
    // Ships empty: players drop optional replacement movies here.
    Directory.CreateDirectory(Path.Combine(payload, "video"));
    ModPackage.Validate(payload, id, version);
    foreach (var asset in new[] { "lang", "efl" })
        foreach (var file in Directory.Exists(Path.Combine(root, asset)) ? Directory.EnumerateFiles(Path.Combine(root, asset), "*", SearchOption.AllDirectories) : [])
        {
            if (Path.GetFileName(file) == ".gitkeep") continue;
            var deployed = Path.Combine(payload, Path.GetRelativePath(root, file));
            if (!File.Exists(deployed) || !SHA256.HashData(File.ReadAllBytes(file)).SequenceEqual(SHA256.HashData(File.ReadAllBytes(deployed))))
                throw new InvalidDataException($"Missing or changed runtime asset: {Path.GetRelativePath(root, file)}");
        }
    if (command != "mod-build")
        foreach (var folder in new[] { "tests", "build/tests" })
            if (Directory.Exists(Path.Combine(context, folder)))
                foreach (var test in Directory.GetFiles(Path.Combine(context, folder), "*.csproj", SearchOption.AllDirectories))
                    Execute("dotnet", Path.GetFileName(test) is "Dxgi.Tests.csproj" or "ParticlePresentation.Tests.csproj" or "RenderImage.Tests.csproj"
                        ? ["test", test, "-c", configuration, "--arch", "x86", "--verbosity", "minimal"]
                        : ["test", test, "-c", configuration, "--verbosity", "minimal"], context, environment);
    if (command == "mod-pack")
    {
        var archive = ModPackage.Create(payload, id, version, output, tag);
        var decoderSource = Path.Combine(output, "ffmpeg-8.0.1-source.tar.xz");
        var decoderSourceInput = Path.Combine(root, ".workspace", "av1", "ffmpeg-8.0.1.tar.xz");
        var decoderSourceHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(decoderSourceInput)));
        if (decoderSourceHash != "05EE0B03119B45C0BDB4DF654B96802E909E0A752F72E4FE3794F487229E5A41")
            throw new InvalidDataException("FFmpeg distribution source checksum mismatch.");
        File.Copy(decoderSourceInput, decoderSource, true);
        File.WriteAllText(decoderSource + ".sha256", decoderSourceHash.ToLowerInvariant() + "  " + Path.GetFileName(decoderSource) + "\n");
        var provenance = new
        {
            mod_id = id, version, tag, configuration, sdk, fahrenheit_version = sdkVersion, sdk_origin = mode, fahrenheit_ref = mode is "local" or "source" ? pin : null,
            public_distribution = mode == "official" || legacy, built_at_utc = DateTimeOffset.UtcNow,
            source_commit = Execute("git", ["rev-parse", "HEAD"], root, capture: true).Trim(),
            source_dirty = Execute("git", ["status", "--porcelain"], root, capture: true).Length != 0,
            input_sha256 = inputs,
            sdk_package_sha256 = sdkPackages,
            decoder_source_sha256 = decoderSourceHash,
            archive_sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archive))),
            payload_sha256 = Directory.GetFiles(payload, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToDictionary(f => Path.GetRelativePath(payload, f).Replace('\\', '/'), f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))))
        };
        File.WriteAllText(archive + ".provenance.json", JsonSerializer.Serialize(provenance, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        File.WriteAllText(Path.Combine(output, "release-notes.md"), $"# {Property("AssemblyTitle")} {tag}\n\n" +
            (mode == "local" ? $"Development build: requires Fahrenheit built from `{pin}`. This package is not an official-SDK release.\n\n" : $"Requires official Fahrenheit `{sdkVersion}`.\n\n") +
            $"Extract `{Path.GetFileName(archive)}` into `fahrenheit/mods/`, then add `{id}` to `fahrenheit/mods/loadorder` if absent. Keep the entire `{id}/` folder together. Settings are available in F7.\n\n" +
            "Experimental FFX-only mod. Compilation and package validation do not establish compatibility with every game build or scene. See the repository README for features and known limitations.\n");
        Console.WriteLine($"PACKAGE: {archive}\nSDK origin: {mode}\nPayload validation and tests passed.");
    }
    else Console.WriteLine($"Validated mod payload: {payload}");
    return 0;
}

static string Execute(string executable, IEnumerable<string> arguments, string directory, Dictionary<string, string>? environment = null, bool capture = false)
{
    var start = new ProcessStartInfo(executable) { WorkingDirectory = directory, UseShellExecute = false, RedirectStandardOutput = capture };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    if (environment is not null) foreach (var pair in environment) start.Environment[pair.Key] = pair.Value;
    using var process = Process.Start(start)!;
    var result = capture ? process.StandardOutput.ReadToEnd() : "";
    process.WaitForExit();
    if (process.ExitCode != 0) throw new InvalidOperationException($"{executable} failed with exit code {process.ExitCode}.");
    return result;
}

static void Recreate(string path, string boundary)
{
    if (!Path.GetFullPath(path).StartsWith(Path.GetFullPath(boundary) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Build directory must remain within its workspace boundary.");
    if (Directory.Exists(path)) Directory.Delete(path, true);
    Directory.CreateDirectory(path);
}

static void CopyTree(string source, string destination)
{
    if (!Directory.Exists(source)) return;
    Directory.CreateDirectory(destination);
    foreach (var file in Directory.GetFiles(source))
        File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
    foreach (var child in Directory.GetDirectories(source))
        if (Path.GetFileName(child) is not ("bin" or "obj" or "artifacts" or ".workspace"))
            CopyTree(child, Path.Combine(destination, Path.GetFileName(child)));
}
