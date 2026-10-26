using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

public sealed class NativePresentationPackageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "VrrPresentationPackage-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, object> _record;

    public NativePresentationPackageTests()
    {
        Directory.CreateDirectory(_root);
        foreach (var suffix in new[] { ".dll", ".pdb", ".deps.json", ".runtimeconfig.json" })
            File.WriteAllText(Path.Combine(_root, "fhvrr" + suffix), "fixture");
        File.WriteAllText(Path.Combine(_root, "fhvrr.manifest.json"), "{\"Id\":\"fhvrr\",\"Version\":\"1.2.3\"}");
        File.WriteAllText(Path.Combine(_root, "fhpresent.dll"), "reviewed helper fixture");
        _record = new()
        {
            ["workflow"] = "prepare-presentation", ["architecture"] = "x86", ["abi"] = 1,
            ["native_caller_test"] = "passed", ["managed_x86_probe"] = "passed", ["native_hook_runtime"] = "not_tested",
            ["input_sha256"] = new Dictionary<string, string>
            {
                ["src/native/presentation/fhpresent.c"] = "4D4A32B9BDB9513F839A7ABFEFA8374D880E414ADA1CFAD25C021D49DBA09ED3",
                ["src/native/presentation/fhpresent.def"] = "4847EAF52A2D0ED6B930DA8D30031E39689E1AA1323BB20BCDD6D86D9832B599",
                ["src/NativeSnapshotBridge.cs"] = "8B6DA7A180920CDA2E2588551051BAFE809E20E8EA28E162F9BC8E9EA57E2083"
            },
            ["output_sha256"] = new Dictionary<string, string>
            {
                ["fhpresent.dll"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(_root, "fhpresent.dll"))))
            }
        };
        Save();
    }

    [Fact]
    public void AcceptsMatchingReviewedHelperWithoutQualifyingNativeHooks()
        => ModPackage.Validate(_root, "fhvrr", "1.2.3");

    [Theory]
    [InlineData("fhpresent.dll")]
    [InlineData("fhpresent.build.json")]
    public void RejectsIncompleteHelper(string missing)
    {
        File.Delete(Path.Combine(_root, missing));
        Assert.Throws<InvalidDataException>(() => ModPackage.Validate(_root, "fhvrr", "1.2.3"));
    }

    [Theory]
    [InlineData("workflow", "other")]
    [InlineData("architecture", "x64")]
    [InlineData("abi", 2)]
    [InlineData("native_caller_test", "not_run")]
    [InlineData("managed_x86_probe", "not_run")]
    public void RejectsDifferentRecipeOrUntestedAbi(string field, object value)
    {
        _record[field] = value;
        Save();
        Assert.Throws<InvalidDataException>(() => ModPackage.Validate(_root, "fhvrr", "1.2.3"));
    }

    [Fact]
    public void RejectsChangedDllAfterBuildRecord()
    {
        File.AppendAllText(Path.Combine(_root, "fhpresent.dll"), "changed");
        Assert.Throws<InvalidDataException>(() => ModPackage.Validate(_root, "fhvrr", "1.2.3"));
    }

    [Fact]
    public void RejectsUnreviewedNativeSource()
    {
        ((Dictionary<string, string>)_record["input_sha256"])["src/native/presentation/fhpresent.c"] = new string('0', 64);
        Save();
        Assert.Throws<InvalidDataException>(() => ModPackage.Validate(_root, "fhvrr", "1.2.3"));
    }

    private void Save() => File.WriteAllText(Path.Combine(_root, "fhpresent.build.json"), JsonSerializer.Serialize(_record));
    public void Dispose() => Directory.Delete(_root, true);
}
