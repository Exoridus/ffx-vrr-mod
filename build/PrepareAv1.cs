using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

if (args.Length != 2) throw new ArgumentException("Expected mod root and payload directory.");
string root = Path.GetFullPath(args[0]), payload = Path.GetFullPath(args[1]);
string tools = Path.Combine(root, ".workspace", "av1");
Directory.CreateDirectory(tools);
string source = Path.Combine(root, "native", "av1", "fhav1.c");
string recipe = Path.Combine(root, "build", "PrepareAv1.cs");
string sourceHash = Hash(source), recipeHash = Hash(recipe);
string testHash = Hash(Path.Combine(root, "native", "av1", "tests", "fhav1_test.c"));
const string ffmpegVersion = "8.0.1";
const string ffmpegHash = "05EE0B03119B45C0BDB4DF654B96802E909E0A752F72E4FE3794F487229E5A41";
const string configure = "--toolchain=msvc --arch=x86 --target-os=win32 --disable-everything --disable-autodetect --disable-programs --disable-doc --disable-network --disable-avformat --disable-avdevice --disable-avfilter --disable-swresample --disable-swscale --disable-x86asm --enable-shared --disable-static --enable-decoder=av1 --enable-hwaccel=av1_d3d11va,av1_d3d11va2 --enable-d3d11va --disable-debug --disable-symver";
using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
string dav1dArchive = await Download("dav1d-1.5.3.tar.xz", "https://downloads.videolan.org/pub/videolan/dav1d/1.5.3/dav1d-1.5.3.tar.xz",
    "732010AA5EF461FA93355ED2C6C5FEDB48DDC4B74E697EAABE8907EAEB943011");
string ffmpegArchive = await Download("ffmpeg-8.0.1.tar.xz", "https://ffmpeg.org/releases/ffmpeg-8.0.1.tar.xz", ffmpegHash);
string makeArchive = await Download("make-4.4.1-3-x86_64.pkg.tar.zst", "https://repo.msys2.org/msys/x86_64/make-4.4.1-3-x86_64.pkg.tar.zst",
    "AF0BDBA17F06FE037F0194069ADAA31A8FE45F1A11381501896AEA1FAE37BD5D");
string nasmArchive = await Download("nasm.zip", "https://www.nasm.us/pub/nasm/releasebuilds/2.16.03/win64/nasm-2.16.03-win64.zip",
    "3EE4782247BCB874378D02F7EAB4E294A84D3D15F3F6EE2DE2F47A46AA7226E6");
string dav1dSource = Path.Combine(tools, "dav1d-1.5.3"), ffmpegSource = Path.Combine(tools, "ffmpeg-8.0.1");
if (!Directory.Exists(dav1dSource)) Run("tar", ["-xf", dav1dArchive, "-C", tools]);
if (!Directory.Exists(ffmpegSource)) Run("tar", ["-xf", ffmpegArchive, "-C", tools]);
string make = Path.Combine(tools, "make");
if (!Directory.Exists(make)) { Directory.CreateDirectory(make); Run("tar", ["-xf", makeArchive, "-C", make]); }
if (!Directory.Exists(Path.Combine(tools, "nasm-2.16.03"))) ZipFile.ExtractToDirectory(nasmArchive, tools);
string vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer", "vswhere.exe");
string vs = Run(vswhere, ["-latest", "-products", "*", "-requires", "Microsoft.VisualStudio.Component.VC.Tools.x86.x64", "-property", "installationPath"], true).Trim();
if (vs.Length == 0) throw new InvalidOperationException("AV1 bridge requires MSVC x86 build tools.");
string vcvars = Path.Combine(vs, "VC", "Auxiliary", "Build", "vcvarsall.bat");
string bash = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe");
if (!File.Exists(bash)) throw new InvalidOperationException("AV1 dependency build requires Git for Windows bash.");
string ffmpegBuild = Path.Combine(tools, "ffmpeg-x86-d3d11");
string ffmpegStamp = Path.Combine(ffmpegBuild, "fh-config.txt");
string output = Path.Combine(tools, "fhav1.dll"), stamp = Path.Combine(tools, "build.json");
bool hasDependencyRecord = false;
if (File.Exists(stamp))
{
    using var previous = JsonDocument.Parse(File.ReadAllText(stamp));
    hasDependencyRecord = previous.RootElement.TryGetProperty("ffmpeg_version", out var previousVersion)
        && previousVersion.GetString() == ffmpegVersion && previous.RootElement.TryGetProperty("outputs", out _);
}
bool rebuiltFfmpeg = false;
if (!hasDependencyRecord || !File.Exists(ffmpegStamp) || File.ReadAllText(ffmpegStamp) != configure
    || !File.Exists(Path.Combine(ffmpegBuild, "libavcodec", "avcodec-62.dll"))
    || !File.Exists(Path.Combine(ffmpegBuild, "libavutil", "avutil-60.dll")))
{
    Directory.CreateDirectory(ffmpegBuild);
    string script = Path.Combine(tools, "ffmpeg-build.sh");
    File.WriteAllText(script, "set -eu\nexport PATH=" + Shell(Msys(Path.Combine(make, "usr", "bin"))) + ":/usr/bin:$PATH\ncd " + Shell(Msys(ffmpegBuild))
        + "\nif [ -f Makefile ]; then make clean; fi\n" + Shell(Msys(Path.Combine(ffmpegSource, "configure"))) + " " + configure + "\nmake -j8\n");
    Batch("ffmpeg-build.cmd", [$"\"{bash}\" \"{script}\""]);
    File.WriteAllText(ffmpegStamp, configure);
    rebuiltFfmpeg = true;
}
bool current = false;
if (File.Exists(stamp))
{
    using var document = JsonDocument.Parse(File.ReadAllText(stamp));
    if (!rebuiltFfmpeg && document.RootElement.TryGetProperty("ffmpeg_version", out var cachedVersion)
        && cachedVersion.GetString() == ffmpegVersion)
    {
        var recordedOutputs = document.RootElement.GetProperty("outputs");
        foreach (var dependency in new[] { ("libavcodec", "avcodec-62.dll"), ("libavutil", "avutil-60.dll") })
            if (recordedOutputs.GetProperty(dependency.Item2).GetString() != Hash(Path.Combine(ffmpegBuild, dependency.Item1, dependency.Item2)))
                throw new InvalidDataException("Cached AV1 dependency changed: " + dependency.Item2);
    }
    current = File.Exists(output) && document.RootElement.TryGetProperty("recipe_sha256", out var savedRecipe)
        && savedRecipe.GetString() == recipeHash
        && document.RootElement.GetProperty("test_sha256").GetString() == testHash
        && document.RootElement.GetProperty("source_sha256").GetString() == sourceHash
        && document.RootElement.GetProperty("outputs").GetProperty("fhav1.dll").GetString() == Hash(output);
}
if (!current)
{
    if (!Directory.Exists(Path.Combine(tools, "python", "mesonbuild")))
        Run("python", ["-m", "pip", "install", "--target", Path.Combine(tools, "python"), "meson==1.7.2"]);
    string dav1dBuild = Path.Combine(tools, "dav1d-x86");
    var lines = new List<string>
    {
        $"set \"PYTHONPATH={Path.Combine(tools, "python")}\"",
        $"set \"PATH={Path.Combine(tools, "nasm-2.16.03")};%PATH%\""
    };
    if (!File.Exists(Path.Combine(dav1dBuild, "build.ninja")))
        lines.Add($"python -m mesonbuild.mesonmain setup \"{dav1dBuild}\" \"{dav1dSource}\" --buildtype=release --default-library=static -Denable_tools=false -Denable_tests=false");
    lines.AddRange(["if errorlevel 1 exit /b %errorlevel%", $"ninja -C \"{dav1dBuild}\"", "if errorlevel 1 exit /b %errorlevel%",
        $"cl /nologo /O2 /MD /LD /I\"{Path.Combine(dav1dSource, "include")}\" /I\"{ffmpegSource}\" /I\"{ffmpegBuild}\" \"{source}\" /Fo:\"{Path.Combine(tools, "fhav1.obj")}\" /link \"{Path.Combine(dav1dBuild, "src", "libdav1d.a")}\" \"{Path.Combine(ffmpegBuild, "libavcodec", "avcodec.lib")}\" \"{Path.Combine(ffmpegBuild, "libavutil", "avutil.lib")}\" /OUT:\"{output}\"",
        "if errorlevel 1 exit /b %errorlevel%",
        $"cl /nologo /O2 /MD \"{Path.Combine(root, "native", "av1", "tests", "fhav1_test.c")}\" /Fo:\"{Path.Combine(tools, "fhav1-test.obj")}\" /Fe:\"{Path.Combine(tools, "fhav1-test.exe")}\""
    ]);
    Batch("build.cmd", lines);
}
File.Copy(Path.Combine(ffmpegBuild, "libavcodec", "avcodec-62.dll"), Path.Combine(tools, "avcodec-62.dll"), true);
File.Copy(Path.Combine(ffmpegBuild, "libavutil", "avutil-60.dll"), Path.Combine(tools, "avutil-60.dll"), true);
Directory.CreateDirectory(payload);
foreach (string name in new[] { "fhav1.dll", "avcodec-62.dll", "avutil-60.dll" }) File.Copy(Path.Combine(tools, name), Path.Combine(payload, name), true);
File.Copy(Path.Combine(dav1dSource, "COPYING"), Path.Combine(payload, "dav1d.LICENSE.txt"), true);
File.Copy(Path.Combine(ffmpegSource, "COPYING.LGPLv2.1"), Path.Combine(payload, "ffmpeg.LICENSE.txt"), true);
File.WriteAllText(Path.Combine(payload, "ffmpeg.SOURCES.txt"), $"FFmpeg {ffmpegVersion}, LGPL-2.1-or-later, dynamically linked.\nSource: https://ffmpeg.org/releases/ffmpeg-{ffmpegVersion}.tar.xz\nSHA-256: {ffmpegHash}\nNo source modifications.\nConfigure: {configure}\nBuild: MSVC x86, MSYS GNU make.\nRebuild: build/PrepareAv1.cs in the mod source repository.\nMatching source archive: ffmpeg-8.0.1-source.tar.xz, distributed alongside the mod ZIP.\n");
using (var stream = File.Create(stamp))
using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
{
    writer.WriteStartObject();
    writer.WriteString("source_sha256", sourceHash); writer.WriteString("recipe_sha256", recipeHash);
    writer.WriteString("test_sha256", testHash);
    writer.WriteString("dav1d_version", "1.5.3"); writer.WriteString("ffmpeg_version", ffmpegVersion);
    writer.WriteString("ffmpeg_source_sha256", ffmpegHash); writer.WriteString("architecture", "x86"); writer.WriteNumber("abi", 2);
    writer.WriteStartObject("outputs");
    foreach (string name in new[] { "fhav1.dll", "avcodec-62.dll", "avutil-60.dll", "dav1d.LICENSE.txt", "ffmpeg.LICENSE.txt", "ffmpeg.SOURCES.txt" })
        writer.WriteString(name, Hash(Path.Combine(payload, name)));
    writer.WriteEndObject(); writer.WriteEndObject();
}
File.Copy(stamp, Path.Combine(payload, "fhav1.build.json"), true);

string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
string Msys(string path) => "/" + char.ToLowerInvariant(path[0]) + path[2..].Replace('\\', '/');
string Shell(string value) => "'" + value.Replace("'", "'\\''") + "'";
async Task<string> Download(string name, string url, string hash)
{
    string file = Path.Combine(tools, name);
    if (!File.Exists(file)) await File.WriteAllBytesAsync(file, await http.GetByteArrayAsync(url));
    if (Hash(file) != hash) throw new InvalidDataException("AV1 dependency checksum mismatch: " + name);
    return file;
}
void Batch(string name, IEnumerable<string> commands)
{
    string file = Path.Combine(tools, name);
    File.WriteAllText(file, "@echo off\r\ncall \"" + vcvars + "\" x86\r\nif errorlevel 1 exit /b %errorlevel%\r\n"
        + string.Join("\r\n", commands.Select(c => c + "\r\nif errorlevel 1 exit /b %errorlevel%")) + "\r\n");
    Run("cmd", ["/d", "/c", file]);
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
