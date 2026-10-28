using System.Formats.Tar;
using System.IO.Compression;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

if (args.Length != 2) throw new ArgumentException("Expected mod root and payload directory.");
string root = Path.GetFullPath(args[0]), payload = Path.GetFullPath(args[1]);
string tools = Path.Combine(root, ".workspace", "presentation");
Directory.CreateDirectory(tools);
const string minhookSha512 = "8A33233598B56AD9DA44D22D470C2432F68364DAC31BC719FCD6B085E681FA10DDD41865FBDE056EE7F4E7A075CC135344B6BF444EADBD7E7314EE1BEDFD89B5";
const string minhookUrl = "https://github.com/TsudaKageyu/minhook/archive/refs/tags/v1.3.4.tar.gz";
string minhookArchive = Path.Combine(tools, "minhook-v1.3.4.tar.gz");
if (!File.Exists(minhookArchive))
{
    string cached = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "vcpkg", "downloads", "TsudaKageyu-minhook-v1.3.4.tar.gz");
    if (File.Exists(cached)) File.Copy(cached, minhookArchive);
    else
    {
        using var client = new System.Net.Http.HttpClient();
        File.WriteAllBytes(minhookArchive, await client.GetByteArrayAsync(minhookUrl));
    }
}
if (Convert.ToHexString(SHA512.HashData(File.ReadAllBytes(minhookArchive))) != minhookSha512)
    throw new InvalidDataException("MinHook 1.3.4 archive does not match the official vcpkg baseline pin.");
using (var archive = File.OpenRead(minhookArchive))
using (var gzip = new GZipStream(archive, CompressionMode.Decompress))
    TarFile.ExtractToDirectory(gzip, tools, overwriteFiles: true);
string minhook = Path.Combine(tools, "minhook-1.3.4");
string native = Path.Combine(root, "src", "native", "presentation");
string test = Path.Combine(native, "tests", "fhpresent_test.c");
string probeProject = Path.Combine(root, "build", "presentation-probe", "ManagedProbe.csproj");
string probe = Path.Combine(tools, "probe");
string output = Path.Combine(tools, "fhpresent.dll"), stamp = Path.Combine(tools, "build.json");
string[] inputNames = ["src/NativeSnapshotBridge.cs", "src/native/presentation/fhpresent.c", "src/native/presentation/fhpresent.def",
    "src/native/presentation/tests/fhpresent_test.c", "src/native/presentation/tests/fhpresent_bad_abi.c",
    "build/presentation-probe/ManagedProbe.csproj", "build/presentation-probe/Program.cs",
    "build/PreparePresentation.cs", "src/native/presentation/tests/startup_trampoline.c",
    "build/presentation-probe/StartupTrampolineProbe.cs", "src/StartupActivation.cs", "src/StartupCaller.cs"];
var inputs = inputNames.ToDictionary(name => name, name => Hash(Path.Combine(root, name)));
foreach (string file in Directory.EnumerateFiles(Path.Combine(minhook, "src"), "*", SearchOption.AllDirectories)
    .Concat(Directory.EnumerateFiles(Path.Combine(minhook, "include"))).Append(Path.Combine(minhook, "LICENSE.txt"))
    .Order(StringComparer.Ordinal))
    inputs.Add("minhook/1.3.4/" + Path.GetRelativePath(minhook, file).Replace('\\', '/'), Hash(file));
string[] outputNames = ["fhstartup-probe.dll", "fhpresent.dll", "fhpresent-test.exe", "fhpresent-probe.dll", "bad-abi/fhpresent.dll",
    "probe/ManagedProbe.dll", "probe/ManagedProbe.deps.json", "probe/ManagedProbe.runtimeconfig.json"];
bool current = false;
if (File.Exists(stamp))
{
    using var previous = JsonDocument.Parse(File.ReadAllText(stamp));
    current = previous.RootElement.TryGetProperty("input_sha256", out var savedInputs)
        && inputs.All(input => savedInputs.TryGetProperty(input.Key, out var hash) && hash.GetString() == input.Value)
        && previous.RootElement.TryGetProperty("output_sha256", out var savedOutputs)
        && outputNames.All(name => File.Exists(Path.Combine(tools, name)) && savedOutputs.TryGetProperty(name, out var hash)
            && hash.GetString() == Hash(Path.Combine(tools, name)));
}
if (!current)
{
    string vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer", "vswhere.exe");
    string vs = Run(vswhere, ["-latest", "-products", "*", "-requires", "Microsoft.VisualStudio.Component.VC.Tools.x86.x64", "-property", "installationPath"], true).Trim();
    if (vs.Length == 0) throw new InvalidOperationException("Presentation bridge requires MSVC x86 build tools.");
    string vcvars = Path.Combine(vs, "VC", "Auxiliary", "Build", "vcvarsall.bat");
    Directory.CreateDirectory(Path.Combine(tools, "bad-abi"));
    string batch = Path.Combine(tools, "build.cmd");
    string[] commands = [
        $"cl /nologo /O2 /MT /arch:SSE2 /LD /I\"{Path.Combine(minhook, "include")}\" \"{Path.Combine(native, "tests", "startup_trampoline.c")}\" \"{Path.Combine(minhook, "src", "buffer.c")}\" \"{Path.Combine(minhook, "src", "hook.c")}\" \"{Path.Combine(minhook, "src", "trampoline.c")}\" \"{Path.Combine(minhook, "src", "hde", "hde32.c")}\" /link /Brepro /OUT:fhstartup-probe.dll",
        $"cl /nologo /O2 /MT /arch:SSE2 /LD \"{Path.Combine(native, "fhpresent.c")}\" /Fo:fhpresent.obj /link /Brepro /DEF:\"{Path.Combine(native, "fhpresent.def")}\" /OUT:fhpresent.dll",
        $"cl /nologo /O2 /MT /arch:SSE2 \"{test}\" /Fo:fhpresent-test.obj /Fe:fhpresent-test.exe /link /Brepro",
        $"cl /nologo /O2 /MT /arch:SSE2 /LD /DFHPRESENT_PROBE \"{test}\" /Fo:fhpresent-probe.obj /link /Brepro /OUT:fhpresent-probe.dll",
        $"cl /nologo /O2 /MT /LD \"{Path.Combine(native, "tests", "fhpresent_bad_abi.c")}\" /Fo:bad-abi.obj /link /Brepro /OUT:\"{Path.Combine(tools, "bad-abi", "fhpresent.dll")}\""
    ];
    // An inherited VSINSTALLDIR can make vcvarsall select another installation.
    File.WriteAllText(batch, "@echo off\r\nset \"VSINSTALLDIR=\"\r\ncall \"" + vcvars + "\" x86\r\nif errorlevel 1 exit /b %errorlevel%\r\n"
        + string.Join("\r\n", commands.Select(command => command + "\r\nif errorlevel 1 exit /b %errorlevel%")) + "\r\n");
    Run("cmd", ["/d", "/c", batch]);
    Run("dotnet", ["build", probeProject, "-c", "Release", "-o", probe, "--verbosity", "quiet", "--ignore-failed-sources"]);
}
ValidateX86(output);
Run(Path.Combine(tools, "fhpresent-test.exe"), [output]);
File.Copy(output, Path.Combine(probe, "fhpresent.dll"), true);
File.Copy(Path.Combine(tools, "fhpresent-probe.dll"), Path.Combine(probe, "fhpresent-probe.dll"), true);
File.Copy(Path.Combine(tools, "fhstartup-probe.dll"), Path.Combine(probe, "fhstartup-probe.dll"), true);
string x86Host = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "dotnet", "dotnet.exe");
if (!File.Exists(x86Host)) throw new FileNotFoundException("Presentation probe requires the .NET 10 x86 runtime.", x86Host);
Run(x86Host, [Path.Combine(probe, "ManagedProbe.dll")]);
Run(x86Host, [Path.Combine(probe, "ManagedProbe.dll"), "--startup-first"]);
foreach (string scenario in new[] { "good", "false", "missing" })
    Run(x86Host, [Path.Combine(probe, "ManagedProbe.dll"), "--startup-trampoline", scenario]);
foreach (string variant in new[] { "missing", "bad-image", "bad-abi" })
{
    string directory = Path.Combine(tools, "probe-" + variant);
    Directory.CreateDirectory(directory);
    foreach (string name in new[] { "ManagedProbe.dll", "ManagedProbe.deps.json", "ManagedProbe.runtimeconfig.json" })
        File.Copy(Path.Combine(probe, name), Path.Combine(directory, name), true);
    if (variant == "bad-image") File.WriteAllText(Path.Combine(directory, "fhpresent.dll"), "invalid PE image\n");
    if (variant == "bad-abi") File.Copy(Path.Combine(tools, "bad-abi", "fhpresent.dll"), Path.Combine(directory, "fhpresent.dll"), true);
    Run(x86Host, [Path.Combine(directory, "ManagedProbe.dll"), "--unavailable"]);
}
using (var stream = File.Create(stamp))
using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
{
    writer.WriteStartObject();
    writer.WriteString("workflow", "prepare-presentation");
    writer.WriteString("architecture", "x86");
    writer.WriteNumber("abi", 2);
    writer.WriteNumber("symbol_count", 5);
    writer.WriteString("minhook_version", "1.3.4");
    writer.WriteString("minhook_vcpkg_baseline", "4bca8fd8654e5ba76f92661db7bfe954768ad8ef");
    writer.WriteString("minhook_archive_url", minhookUrl);
    writer.WriteString("minhook_archive_sha512", minhookSha512);
    writer.WriteStartObject("input_sha256");
    foreach (var input in inputs) writer.WriteString(input.Key, input.Value);
    writer.WriteEndObject();
    writer.WriteStartObject("output_sha256");
    foreach (string name in outputNames) writer.WriteString(name, Hash(Path.Combine(tools, name)));
    writer.WriteEndObject();
    writer.WriteString("native_caller_test", "passed");
    writer.WriteString("managed_x86_probe", "passed");
    writer.WriteString("offline_startup_trampoline", "passed");
    writer.WriteString("native_hook_runtime", "not_tested");
    writer.WriteEndObject();
}
Directory.CreateDirectory(payload);
File.Copy(output, Path.Combine(payload, "fhpresent.dll"), true);
File.Copy(stamp, Path.Combine(payload, "fhpresent.build.json"), true);

string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
void ValidateX86(string file)
{
    using var stream = File.OpenRead(file);
    using var reader = new BinaryReader(stream);
    if (stream.Length < 64 || reader.ReadUInt16() != 0x5a4d) throw new InvalidDataException("Presentation bridge is not a PE image.");
    stream.Position = 0x3c;
    int peOffset = reader.ReadInt32();
    if (peOffset < 64 || peOffset > stream.Length - 24) throw new InvalidDataException("Presentation bridge has an invalid PE header.");
    stream.Position = peOffset;
    if (reader.ReadUInt32() != 0x4550 || reader.ReadUInt16() != 0x14c) throw new InvalidDataException("Presentation bridge must be x86.");
}
string Run(string executable, string[] arguments, bool capture = false)
{
    var start = new ProcessStartInfo(executable) { WorkingDirectory = tools, UseShellExecute = false, RedirectStandardOutput = capture };
    foreach (string argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start)!;
    string result = capture ? process.StandardOutput.ReadToEnd() : "";
    process.WaitForExit();
    if (process.ExitCode != 0) throw new InvalidOperationException(executable + " failed: " + process.ExitCode);
    return result;
}
