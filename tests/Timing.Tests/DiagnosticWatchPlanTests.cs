namespace Fahrenheit.Mods.Vrr;

using System.Text.Json;
using Xunit;

public sealed class DiagnosticWatchPlanTests
{
    [Theory]
    [InlineData("wrong", "0x100", 16)]
    [InlineData("fixture", "0x1000", 16)]
    [InlineData("fixture", "0xffc", 16)]
    [InlineData("fixture", "0x100", 3)]
    [InlineData("fixture", "0x100", 500)]
    public void Invalid_identity_or_range_is_refused(string hash, string rva, int bytes)
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new { image_sha256 = hash, watches = new[] { new { name = "speed", rva, bytes } } }));
            Assert.Throws<InvalidDataException>(() => DiagnosticWatchPlan.Load(path, "fixture", 0x1000));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Pointer_walk_remains_explicit_and_bound_to_the_image()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            File.WriteAllText(path, "{\"image_sha256\":\"fixture\",\"watches\":[{\"name\":\"actor\",\"rva\":\"0xffc\",\"bytes\":496,\"offsets\":[16,32]}]}");
            var plan = DiagnosticWatchPlan.Load(path, "fixture", 0x1000);
            Assert.Equal(new[] { 16, 32 }, plan.Watches[0].Offsets);
        }
        finally { File.Delete(path); }
    }
}
