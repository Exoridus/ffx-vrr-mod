using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;

namespace Fahrenheit.Mods.Vrr;

[Collection("Render scheduler")]
public sealed class MenuInputCacheTests : IDisposable
{
    private readonly VrrModule _module = new();
    public MenuInputCacheTests()
    {
        _module.EnableAdapter();
        _module.SetHookScope(true, true, true, true);
        _module.ResetHistoryStartup();
        FhUtil.MainMenu = FhUtil.MenuPause = FhUtil.Overdrive = 0;
        FhUtil.MenuReservation = -1;
        FhUtil.Dummy = 0;
        NativeClock.MenuUpdate = null;
        NativeClock.MenuRead = null;
        NativeClock.MenuCalls = NativeClock.MenuUpdates = NativeClock.PadCalls = 0;
        NativeClock.HistoryHookAction = null;
        NativeClock.HistoryRead = null;
        NativeClock.HistoryHookRegistrations.Clear();
        NativeClock.Hooks.Clear();
    }
    public void Dispose()
    {
        FhUtil.MainMenu = FhUtil.MenuPause = FhUtil.Overdrive = 0;
        FhUtil.MenuReservation = -1;
        FhUtil.Dummy = 0;
        NativeClock.MenuUpdate = null;
        NativeClock.MenuRead = null;
        NativeClock.HistoryHookAction = null;
        RenderPassContext.IsActive = RenderPassContext.IsPresentation = false;
    }
    [Fact]
    public void Presentation_preserves_previous_held_and_timer_until_one_overdue_native_pulse()
    {
        Assert.True(_module.InitHistory());
        ushort held = 0x1000, previous = 0x1000, press = 0, repeat = 0;
        float priorTime = 1, elapsed = .45f, now = 1.02f;
        byte state = 9;
        NativeClock.MenuUpdate = () =>
        {
            press = (ushort)((held ^ previous) & held);
            previous = held;
            elapsed += now - priorTime;
            priorTime = now;
            repeat = 0;
            if (elapsed > .46666664f) { repeat = held; state = 9; elapsed = BitConverter.Int32BitsToSingle(0x3e999999); }
        };
        var before = (previous, press, repeat, priorTime, elapsed, state);
        _module.UpdateMenu();
        _module.UpdateMenu();
        Assert.Equal(0, NativeClock.MenuUpdates);
        Assert.Equal(before, (previous, press, repeat, priorTime, elapsed, state));
        now = 5;
        _module.SetHookScope(true, true, true, false);
        _module.UpdateMenu();
        Assert.Equal(1, NativeClock.MenuUpdates);
        Assert.Equal((ushort)0x1000, repeat);
        Assert.Equal((ushort)0, press);
        Assert.Equal(BitConverter.Int32BitsToSingle(0x3e999999), elapsed);
        _module.UpdateMenu();
        Assert.Equal(2, NativeClock.MenuUpdates);
        Assert.Equal((ushort)0, repeat);
    }
    public static IEnumerable<object[]> Owners()
        => from owner in new[] { "ordinary", "outside", "disarmed", "handover", "unready", "foreign", "sync", "movie", "menu", "main_menu", "reservation", "overdrive", "dummy", "unknown_dummy" } select new object[] { owner };
    private void SetOwner(string owner)
    {
        if (owner == "ordinary") _module.SetHookScope(true, true, true, false);
        if (owner == "outside") _module.SetHookScope(true, true, false, true);
        if (owner == "disarmed") _module.SetHookScope(false, true, true, true);
        if (owner == "handover") _module.SetHookScope(true, false, true, true);
        if (owner == "unready") _module.ResetHistoryStartup();
        if (owner == "sync") _module.NativeSync = true;
        if (owner == "movie") _module.NativeMovie = true;
        if (owner == "menu") FhUtil.MenuPause = 1;
        if (owner == "main_menu") FhUtil.MainMenu = 1;
        if (owner == "reservation") FhUtil.MenuReservation = 0;
        if (owner == "overdrive") FhUtil.Overdrive = 1;
        if (owner == "dummy") FhUtil.Dummy = 1;
        if (owner == "unknown_dummy") FhUtil.Dummy = 255;
    }
    [Theory]
    [MemberData(nameof(Owners))]
    public void Native_and_unowned_scopes_invoke_producer_and_preserve_exact_getters(string owner)
    {
        Assert.True(_module.InitHistory());
        SetOwner(owner);
        NativeClock.MenuRead = _ => 0x91ef;
        void Run()
        {
            _module.UpdateMenu();
            Assert.Equal((ushort)0x91ef, _module.ReadMenu(0));
            Assert.Equal((ushort)0x91ef, _module.ReadMenu(1));
        }
        if (owner == "foreign")
        {
            Exception? failure = null;
            var thread = new Thread(() => { try { Run(); } catch (Exception e) { failure = e; } });
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
            Assert.Null(failure);
        }
        else Run();
        Assert.Equal(1, NativeClock.MenuUpdates);
        Assert.Equal(2, NativeClock.MenuCalls);
    }
    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 0xffff)]
    [InlineData(1, 0x8000)]
    [InlineData(1, 0x4001)]
    public void Original_getter_runs_once_before_owned_result_is_hidden(int kind, int value)
    {
        Assert.True(_module.InitHistory());
        NativeClock.MenuRead = actual => { Assert.Equal(kind, actual); return (ushort)value; };
        Assert.Equal((ushort)0, _module.ReadMenu(kind));
        Assert.Equal(1, NativeClock.MenuCalls);
        _module.SetHookScope(true, true, true, false);
        Assert.Equal((ushort)value, _module.ReadMenu(kind));
        Assert.Equal(2, NativeClock.MenuCalls);
    }
    public static IEnumerable<object[]> ScopeTransitions()
        => from scope in new[] { "ordinary", "outside", "disarmed", "handover", "unready" }
           from kind in Enumerable.Range(0, 2) from enter in new[] { false, true }
           select new object[] { scope, kind, enter };
    [Theory]
    [MemberData(nameof(ScopeTransitions))]
    public void Ownership_must_allow_suppression_at_both_getter_boundaries(string scope, int kind, bool enter)
    {
        Assert.True(_module.InitHistory());
        if (enter) SetOwner(scope);
        NativeClock.MenuRead = _ =>
        {
            if (enter)
            {
                _module.SetHookScope(true, true, true, true);
                _module.InputReadiness(true, true);
            }
            else SetOwner(scope);
            return 0xffff;
        };
        Assert.Equal((ushort)0xffff, _module.ReadMenu(kind));
        Assert.Equal(1, NativeClock.MenuCalls);
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Both_existing_readiness_flags_are_required_for_menu_suppression(bool latch, bool readers)
    {
        _module.InputReadiness(latch, readers);
        NativeClock.MenuRead = _ => 0x8000;
        _module.UpdateMenu();
        Assert.Equal(1, NativeClock.MenuUpdates);
        Assert.Equal((ushort)0x8000, _module.ReadMenu(0));
        Assert.Equal((ushort)0x8000, _module.ReadMenu(1));
    }
    [Fact]
    public void Successful_native_getter_clear_survives_presentation_cleanup()
    {
        Assert.True(_module.InitHistory());
        ushort[] cache = [0xffff, 0x8000];
        NativeClock.MenuRead = kind =>
        {
            ushort value = cache[kind];
            Array.Clear(cache);
            return value;
        };
        Assert.Equal((ushort)0, _module.ReadMenu(0));
        _module.EndPad();
        Assert.Equal(new ushort[2], cache);
        Assert.Equal(1, NativeClock.MenuCalls);
    }
    public static IEnumerable<object[]> Transitions()
        => from owner in new[] { "sync", "movie", "menu", "main_menu", "reservation", "overdrive", "dummy", "unknown_dummy" }
           from kind in Enumerable.Range(0, 2) from release in new[] { false, true } select new object[] { owner, kind, release };
    [Theory]
    [MemberData(nameof(Transitions))]
    public void Owner_transition_inside_original_preserves_native_value_in_either_direction(string owner, int kind, bool release)
    {
        Assert.True(_module.InitHistory());
        if (release) SetOwner(owner);
        NativeClock.MenuRead = _ =>
        {
            if (release)
            {
                _module.NativeSync = _module.NativeMovie = false;
                FhUtil.MainMenu = FhUtil.MenuPause = FhUtil.Overdrive = 0;
                FhUtil.MenuReservation = -1;
                FhUtil.Dummy = 0;
            }
            else SetOwner(owner);
            return 0xcafe;
        };
        Assert.Equal((ushort)0xcafe, _module.ReadMenu(kind));
        Assert.Equal(1, NativeClock.MenuCalls);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Native_exceptions_and_legitimate_cache_clears_survive(int kind)
    {
        Assert.True(_module.InitHistory());
        ushort cached = 0xffff;
        var failure = new InvalidOperationException("native menu fixture");
        NativeClock.MenuRead = _ => { cached = 0; throw failure; };
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => _module.ReadMenu(kind)));
        _module.EndPad();
        Assert.Equal((ushort)0, cached);
        Assert.Equal(1, NativeClock.MenuCalls);
        NativeClock.MenuUpdate = () => throw failure;
        _module.SetHookScope(true, true, true, false);
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(_module.UpdateMenu));
        Assert.Equal(1, NativeClock.MenuUpdates);
    }
    public static IEnumerable<object[]> GuardBytes()
        => from rva in new[] { 0x4be500, 0x4be4b0, 0x4be470, 0x4ca510 }
           from position in Enumerable.Range(0, rva == 0x4be500 ? 64 : rva == 0x4ca510 ? 8 : 44) select new object[] { rva, position };
    [Theory]
    [MemberData(nameof(GuardBytes))]
    public void Every_guard_byte_including_operands_and_calls_is_checked_before_registration(int rva, int position)
    {
        _module.MenuInputEntries[rva][position] ^= 0xff;
        Assert.True(_module.InitHistory());
        Assert.False(_module.HistoryReady);
        Assert.Empty(NativeClock.HistoryHookRegistrations);
        _module.UpdateMenu();
        Assert.Equal(1, NativeClock.MenuUpdates);
        NativeClock.MenuRead = _ => 0x8000;
        Assert.Equal((ushort)0x8000, _module.ReadMenu(0));
    }
    [Theory]
    [InlineData(0x4be500)]
    [InlineData(0x4be4b0)]
    [InlineData(0x4be470)]
    [InlineData(0x4ca510)]
    public void Truncated_guard_leaves_entire_group_native(int rva)
    {
        _module.MenuInputEntries[rva] = _module.MenuInputEntries[rva][..^1];
        Assert.True(_module.InitHistory());
        Assert.False(_module.HistoryReady);
        Assert.Empty(NativeClock.HistoryHookRegistrations);
    }
    [Theory]
    [InlineData("pad latch")]
    [InlineData("pad pressed history")]
    [InlineData("pad released history")]
    [InlineData("pad repeat history")]
    [InlineData("pad repeat2 history")]
    [InlineData("pad menu cache update")]
    [InlineData("pad menu pressed")]
    [InlineData("pad menu repeat")]
    public void Partial_installation_keeps_all_eight_handlers_native(string rejected)
    {
        _module.RejectedHook = rejected;
        NativeClock.MenuRead = _ => 0x91ef;
        NativeClock.HistoryValue = 0x91ef;
        NativeClock.HistoryHookAction = () =>
        {
            Assert.False(_module.HistoryReady);
            Assert.Equal(15, _module.CodeChecks.Count);
            Assert.Equal(19, _module.OperandChecks.Count);
            Assert.Equal(9, _module.CallChecks.Count);
            _module.UpdateMenu();
            _module.CallPad();
            for (int kind = 0; kind < 4; kind++) Assert.Equal((ushort)0x91ef, _module.ReadHistory(kind, 0, 0, -1));
            Assert.Equal((ushort)0x91ef, _module.ReadMenu(0));
            Assert.Equal((ushort)0x91ef, _module.ReadMenu(1));
        };
        Assert.False(_module.InitHistory());
        Assert.False(_module.HistoryReady);
        Assert.Equal(8, NativeClock.MenuUpdates);
        Assert.Equal(8, NativeClock.PadCalls);
    }
    [Fact]
    public void Hook_ids_and_zero_argument_abis_are_stable()
    {
        Assert.True(_module.InitHistory());
        Assert.Equal(new[] { 144, 145, 146 }, new[] { (int)HookSiteId.PadLatchMenuCacheUpdate, (int)HookSiteId.PadLatchMenuPressed, (int)HookSiteId.PadLatchMenuRepeat });
        var added = NativeClock.HistoryHookRegistrations.Skip(5).Take(3).ToArray();
        Assert.Equal(new nint[] { 0x4be500, 0x4be4b0, 0x4be470 }, added.Select(h => h.Rva));
        Assert.Equal(new[] { "d_render_void", "d_menu_edge", "d_menu_edge" }, added.Select(h => h.Callback.GetType().Name));
        foreach (var hook in added)
        {
            Assert.Empty(hook.Callback.GetType().GetMethod("Invoke")!.GetParameters());
            Assert.Equal(CallingConvention.Cdecl, hook.Callback.GetType().GetCustomAttribute<UnmanagedFunctionPointerAttribute>()!.CallingConvention);
            Assert.Contains(typeof(CallConvCdecl), hook.Callback.Method.GetCustomAttribute<UnmanagedCallConvAttribute>()!.CallConvs!);
        }
    }
}
public unsafe sealed partial class VrrModule
{
    internal readonly Dictionary<nint, byte[]> MenuInputEntries = new()
    {
        [0x4be500] = Convert.FromHexString("558BEC5156668B35E2095D02E84F27D7FFD95DFC33C0A3D2095D0266A3D6095D02A3E2095D0266A3E6095D02E8DFBF000083F8017505E855BF0000E8D0BF0000"),
        [0x4be4b0] = Convert.FromHexString("E85BC0000083F801750766A1D4095D02C3B800F00000668505D2095D0266A1D4095D02750666A1E4095D02C3"),
        [0x4be470] = Convert.FromHexString("E89BC0000083F801750766A1D6095D02C3B800F00000668505D2095D0266A1D6095D02750666A1E6095D02C3"),
        [0x4ca510] = Convert.FromHexString("0FB60530A48601C3"),
    };
    internal void UpdateMenu() => h_pad_menu_cache_update();
    internal ushort ReadMenu(int kind) => kind == 0 ? h_pad_menu_pressed() : h_pad_menu_repeat();
}
internal static partial class NativeClock
{
    internal static Action? MenuUpdate;
    internal static Func<int, ushort>? MenuRead;
    internal static int MenuUpdates, MenuCalls;
    public static void MenuCacheUpdate() { MenuUpdates++; MenuUpdate?.Invoke(); }
    public static ushort MenuPressed() { MenuCalls++; return MenuRead?.Invoke(0) ?? 0; }
    public static ushort MenuRepeat() { MenuCalls++; return MenuRead?.Invoke(1) ?? 0; }
}
