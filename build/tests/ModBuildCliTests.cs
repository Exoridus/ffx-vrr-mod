using System.Diagnostics;
using Xunit;

public sealed class ModBuildCliTests
{
    [Theory]
    [InlineData("mod-build")]
    [InlineData("mod-verify")]
    [InlineData("mod-pack")]
    public async Task RejectsFullDistributionBeforeReadingProjectOrStartingBuild(string command)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "build", "distribution", "ModBuild.csproj")))
            root = root.Parent;
        Assert.NotNull(root);
        var missingRoot = Path.Combine(Path.GetTempPath(), "VrrMissingRoot-" + Guid.NewGuid().ToString("N"));
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = root.FullName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { "run", "--project", Path.Combine(root.FullName, "build", "distribution", "ModBuild.csproj"), "--", missingRoot, command, "--full", "true" })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await stdout + await stderr;
        Assert.NotEqual(0, process.ExitCode);
        Assert.Contains("mod-only", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("install Fahrenheit separately", output, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(missingRoot));
    }
}
