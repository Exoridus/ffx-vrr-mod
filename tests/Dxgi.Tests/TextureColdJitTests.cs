using System.Diagnostics;
using System.Runtime.InteropServices;
using Xunit;

public sealed class TextureColdJitTests
{
    [Fact]
    public async Task ColdGpuConstructorCompilesOnNative64KiBThread()
    {
        string host = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet.exe"));
        var start = new ProcessStartInfo(host) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(typeof(TextureColdJitTests).Assembly.Location);
        start.ArgumentList.Add("--texture-cold-jit");
        using var child = Process.Start(start)!;
        Task<string> output = child.StandardOutput.ReadToEndAsync(), errors = child.StandardError.ReadToEndAsync();
        bool exited = child.WaitForExit(20000);
        if (!exited) child.Kill(entireProcessTree: true);
        Assert.True(exited, "Cold JIT child exceeded its time limit.");
        string captured = await output + await errors;
        Assert.True(child.ExitCode == 0, captured);
        Assert.Contains($"pointer_size={IntPtr.Size}", captured);
        Assert.Contains("native_stack_reserve=65536", captured);
        Assert.Contains("PREPARE-PASS", captured);
    }
}
