using Xunit;

namespace Fahrenheit.Mods.Vrr;

public sealed class StartupFailureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Abort_cleans_up_before_terminating_even_when_cleanup_throws(bool cleanupThrows)
    {
        var calls = new List<string>();
        var terminal = new InvalidOperationException("terminal sentinel");

        var error = Assert.Throws<InvalidOperationException>(() => StartupFailure.Abort(
            () =>
            {
                calls.Add("cleanup");
                if (cleanupThrows) throw new IOException("cleanup failed");
            },
            message =>
            {
                calls.Add("terminate");
                Assert.Contains("queued hooks", message);
                throw terminal;
            }));

        Assert.Same(terminal, error);
        Assert.Equal(new[] { "cleanup", "terminate" }, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Abort_rejects_a_terminal_action_that_returns(bool cleanupThrows)
    {
        var calls = new List<string>();

        var error = Assert.Throws<InvalidOperationException>(() => StartupFailure.Abort(
            () =>
            {
                calls.Add("cleanup");
                if (cleanupThrows) throw new IOException("cleanup failed");
            },
            _ => calls.Add("terminate")));

        Assert.Equal("Startup termination returned unexpectedly.", error.Message);
        Assert.Equal(new[] { "cleanup", "terminate" }, calls);
    }
}
