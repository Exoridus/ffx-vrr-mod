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
            ["workflow"] = "prepare-presentation", ["architecture"] = "x86", ["abi"] = 2, ["symbol_count"] = 5,
            ["native_caller_test"] = "passed", ["managed_x86_probe"] = "passed", ["native_hook_runtime"] = "not_tested",
            ["input_sha256"] = new Dictionary<string, string>
            {
                ["src/native/presentation/fhpresent.c"] = "EC01EC9EB988E0B970A268E3716907052517880E8926F3CABBCF5A989B59E4E9",
                ["src/native/presentation/fhpresent.def"] = "D2299E0309C51B5B5B8745DF1CC343F20AB1B9C056CC12705FED3634CA093E8B",
                ["src/NativeSnapshotBridge.cs"] = "DB8E0777766DD654BAEE0042A83B671A36B446BFEE6F7DBA3B7A2B329D642A8C"
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
    [InlineData("abi", 1)]
    [InlineData("symbol_count", 3)]
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

    [Theory]
    [InlineData("src/native/presentation/fhpresent.c")]
    [InlineData("src/native/presentation/fhpresent.def")]
    [InlineData("src/NativeSnapshotBridge.cs")]
    public void RejectsUnreviewedNativeSource(string source)
    {
        ((Dictionary<string, string>)_record["input_sha256"])[source] = new string('0', 64);
        Save();
        Assert.Throws<InvalidDataException>(() => ModPackage.Validate(_root, "fhvrr", "1.2.3"));
    }

    private void Save() => File.WriteAllText(Path.Combine(_root, "fhpresent.build.json"), JsonSerializer.Serialize(_record));
    public void Dispose() => Directory.Delete(_root, true);
}
