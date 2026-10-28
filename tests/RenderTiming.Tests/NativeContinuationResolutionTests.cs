using Xunit;

namespace Fahrenheit.Mods.Vrr;

public sealed class NativeContinuationResolutionTests
{
    [Fact]
    public void Resolution_returns_the_exact_delegate_without_invoking_it()
    {
        int resolutions = 0;
        int nativeCalls = 0;
        int failures = 0;
        Action native = () => nativeCalls++;

        var continuation = HookSites.ResolveContinuation<Action>(
            () => { resolutions++; return native; }, _ => failures++);

        Assert.Same(native, continuation);
        Assert.Equal(1, resolutions);
        Assert.Equal(0, nativeCalls);
        Assert.Equal(0, failures);
    }

    [Fact]
    public void Lookup_exception_is_delivered_once_and_failure_handler_exception_is_preserved()
    {
        var lookupError = new InvalidOperationException("lookup failed");
        var terminalError = new InvalidOperationException("sentinel terminal handler");
        int resolutions = 0;
        int failures = 0;
        Exception? reported = null;

        var actual = Assert.Throws<InvalidOperationException>(() => HookSites.ResolveContinuation<Action>(
            () => { resolutions++; throw lookupError; },
            error => { failures++; reported = error; throw terminalError; }));

        Assert.Same(terminalError, actual);
        Assert.Same(lookupError, reported);
        Assert.Equal(1, resolutions);
        Assert.Equal(1, failures);
    }

    [Fact]
    public void Missing_delegate_is_reported_once_without_returning_a_continuation()
    {
        var terminalError = new InvalidOperationException("sentinel terminal handler");
        int resolutions = 0;
        int failures = 0;
        Exception? reported = null;

        var actual = Assert.Throws<InvalidOperationException>(() => HookSites.ResolveContinuation<Action>(
            () => { resolutions++; return null; },
            error => { failures++; reported = error; throw terminalError; }));

        Assert.Same(terminalError, actual);
        Assert.IsType<InvalidOperationException>(reported);
        Assert.Equal(1, resolutions);
        Assert.Equal(1, failures);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Returning_failure_handler_still_throws_and_preserves_the_resolution_error(bool lookupThrows)
    {
        var lookupError = new InvalidOperationException("lookup failed");
        int resolutions = 0;
        int failures = 0;
        Exception? reported = null;

        var actual = Assert.Throws<InvalidOperationException>(() => HookSites.ResolveContinuation<Action>(
            () => { resolutions++; if (lookupThrows) throw lookupError; return null; },
            error => { failures++; reported = error; }));

        Assert.NotNull(reported);
        Assert.Same(reported, actual.InnerException);
        if (lookupThrows) Assert.Same(lookupError, reported);
        Assert.Equal(1, resolutions);
        Assert.Equal(1, failures);
    }

    [Fact]
    public void Invoking_the_returned_native_delegate_does_not_reenter_resolution_failure_handling()
    {
        var nativeError = new InvalidOperationException("native invocation failed");
        int resolutions = 0;
        int nativeCalls = 0;
        int failures = 0;
        Action native = () => { nativeCalls++; throw nativeError; };
        var continuation = HookSites.ResolveContinuation<Action>(
            () => { resolutions++; return native; }, _ => failures++);

        Assert.Same(nativeError, Assert.Throws<InvalidOperationException>(() => continuation()));
        Assert.Equal(1, resolutions);
        Assert.Equal(1, nativeCalls);
        Assert.Equal(0, failures);
    }
}
