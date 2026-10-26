using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

public sealed class IndependentAdmissionBuildValidationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "VrrAdmission-" + Guid.NewGuid().ToString("N"));
    public IndependentAdmissionBuildValidationTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "Owner.cs"), "reviewed source");
    }

    private void WriteResource(string[]? paths = null, string? hash = null)
    {
        hash ??= Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(_root, "src", "Owner.cs"))));
        var snapshot = new
        {
            schema_version = "1.0", policy = "independent_presentation_veto_only", supported_build = "Steam 25501027",
            canonical_contract_snapshot_sha256 = new string('a', 64),
            required_contracts = Enumerable.Range(0, 14).Select(n => $"contract-{n}").ToArray(), blockers = new[] { "unqualified" },
            source_inputs = (paths ?? ["src/Owner.cs"]).Select(path => new { path, sha256 = hash }).ToArray(),
            generator_inputs = new[] { new { path = "src/Generator.cs", sha256 = new string('c', 64) } }
        };
        File.WriteAllText(Path.Combine(_root, "src", "IndependentPresentationAdmission.json"), JsonSerializer.Serialize(snapshot));
    }

    [Fact]
    public void Exact_reviewed_copied_source_is_accepted()
    {
        WriteResource();
        IndependentAdmissionBuildValidation.Validate(_root);
    }

    [Fact]
    public void Changed_source_requires_regeneration()
    {
        WriteResource();
        File.AppendAllText(Path.Combine(_root, "src", "Owner.cs"), " changed");
        Assert.Contains("Stale", Assert.Throws<InvalidDataException>(() => IndependentAdmissionBuildValidation.Validate(_root)).Message);
    }

    [Fact]
    public void Stale_hash_is_rejected()
    {
        WriteResource(hash: new string('0', 64));
        Assert.Throws<InvalidDataException>(() => IndependentAdmissionBuildValidation.Validate(_root));
    }

    [Theory]
    [InlineData("../Owner.cs")]
    [InlineData("src/../Owner.cs")]
    [InlineData("C:/Owner.cs")]
    [InlineData("src\\Owner.cs")]
    [InlineData("src/Missing.cs")]
    [InlineData("src/IndependentPresentationAdmission.json")]
    public void Unsafe_or_unknown_paths_are_rejected(string path)
    {
        WriteResource([path]);
        Assert.Throws<InvalidDataException>(() => IndependentAdmissionBuildValidation.Validate(_root));
    }

    [Fact]
    public void Duplicate_paths_are_rejected()
    {
        WriteResource(["src/Owner.cs", "src/Owner.cs"]);
        Assert.Throws<InvalidDataException>(() => IndependentAdmissionBuildValidation.Validate(_root));
    }

    [Fact]
    public void Missing_resource_is_rejected()
        => Assert.Throws<InvalidDataException>(() => IndependentAdmissionBuildValidation.Validate(_root));

    public void Dispose() => Directory.Delete(_root, true);
}
