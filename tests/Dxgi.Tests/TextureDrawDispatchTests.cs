using Fahrenheit.Mods.Vrr;
using Xunit;

public sealed class TextureDrawDispatchTests
{
    [Fact]
    public void UnqualifiedDrawsInspectNoComAndResolveContinuationOnce()
    {
        int resolves = 0, inspections = 0, restores = 0, draws = 0;
        var dispatch = new TextureDrawDispatch(true,
            () => { resolves++; return (context, count, start, vertex) => {
                Assert.Equal((nint)123, context); Assert.Equal(132u, count);
                Assert.Equal(7u, start); Assert.Equal(-3, vertex); draws++;
            }; }, () => false, _ => { inspections++; return 1; },
            (_, _) => restores++, (_, _, _) => { });
        for (int i = 0; i < 1000; i++) dispatch.Draw(123, 132, 7, -3);
        Assert.Equal(1000, draws); Assert.Equal(1, resolves);
        Assert.Equal(0, inspections); Assert.Equal(0, restores);
    }

    [Fact]
    public void QualifiedDrawRestoresEvenWhenNativeContinuationThrows()
    {
        int restores = 0;
        var dispatch = new TextureDrawDispatch(false, () => (_, _, _, _) => throw new InvalidOperationException(),
            () => true, _ => 42, (_, native) => { Assert.Equal((nint)42, native); restores++; }, (_, _, _) => { });
        Assert.Throws<InvalidOperationException>(() => dispatch.Draw(1, 2, 3, 0));
        Assert.Equal(1, restores);
    }

    [Fact]
    public void BothConsumersShareOneInstalledHookPerNativeEntry()
    {
        var hooks = new TextureDrawHookRegistry<object>();
        int creates = 0, installs = 0;
        for (int feature = 0; feature < 2; feature++)
            foreach (bool indexed in new[] { true, false })
                Assert.True(hooks.Install(indexed, indexed ? 12 : 13,
                    () => { creates++; return new object(); }, _ => { installs++; return true; }));
        Assert.Equal(2, creates); Assert.Equal(2, installs);
    }
    [Fact]
    public void ConcurrentFirstDrawsResolveOnlyOnce()
    {
        int resolves = 0, draws = 0;
        var dispatch = new TextureDrawDispatch(false,
            () => { Interlocked.Increment(ref resolves); return (_, _, _, _) => Interlocked.Increment(ref draws); },
            () => false, _ => throw new InvalidOperationException(), (_, _) => { }, (_, _, _) => { });
        Parallel.For(0, 1000, _ => dispatch.Draw(1, 2, 3, 0));
        Assert.Equal(1, resolves); Assert.Equal(1000, draws);
    }
}
