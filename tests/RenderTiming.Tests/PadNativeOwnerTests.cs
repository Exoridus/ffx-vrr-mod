using Xunit;

namespace Fahrenheit.Mods.Vrr;

[Collection("Render scheduler")]
public sealed class PadNativeOwnerTests : IDisposable
{
    private readonly VrrModule _module = new();
    public PadNativeOwnerTests()
    {
        FhUtil.MainMenu = FhUtil.MenuPause = FhUtil.Overdrive = 0;
        FhUtil.Dummy = 0;
        NativeClock.PadCalls = 0;
        NativeClock.HistoryValue = 0x91ef;
        NativeClock.HistoryRead = null;
        NativeClock.HistoryHookAction = null;
        NativeClock.Advance = null;
        NativeClock.LoopAction = null;
        NativeClock.HistoryCalls.Clear();
        NativeClock.HistoryHookRegistrations.Clear();
        _module.SetHookScope(true, true, true, true);
    }
    public void Dispose()
    {
        FhUtil.MainMenu = FhUtil.MenuPause = FhUtil.Overdrive = 0;
        FhUtil.Dummy = 0;
        NativeClock.HistoryRead = null;
        NativeClock.Advance = null;
        NativeClock.LoopAction = null;
        RenderPassContext.IsPresentation = RenderPassContext.IsActive = false;
    }
    private void Activate(string owner)
    {
        if (owner == "sync") _module.NativeSync = true;
        if (owner == "movie") _module.NativeMovie = true;
        if (owner == "menu") FhUtil.MenuPause = 1;
        if (owner == "main_menu") FhUtil.MainMenu = 1;
        if (owner == "dummy") FhUtil.Dummy = 1;
        if (owner == "unknown_dummy") FhUtil.Dummy = 255;
        if (owner == "overdrive") FhUtil.Overdrive = 1;
    }
    public static IEnumerable<object[]> Readers()
        => from owner in new[] { "sync", "movie", "menu", "main_menu", "overdrive", "dummy", "unknown_dummy" }
           from reader in Enumerable.Range(0, 4)
           from history in new[] { -1, 0, 3 }
           select new object[] { owner, reader, history };
    [Theory]
    [MemberData(nameof(Readers))]
    public void Native_owner_preserves_all_history_return_values(string owner, int reader, int history)
    {
        Activate(owner);
        Assert.Equal(0x91ef, _module.ReadHistory(reader, 0, 0, history));
        Assert.Single(NativeClock.HistoryCalls);
    }
    public static IEnumerable<object[]> LateReaders()
        => from owner in new[] { "sync", "movie", "menu", "main_menu", "overdrive", "dummy", "unknown_dummy" }
           from reader in Enumerable.Range(0, 4)
           select new object[] { owner, reader };
    [Theory]
    [MemberData(nameof(LateReaders))]
    public void Native_owner_selected_inside_original_getter_is_rechecked(string owner, int reader)
    {
        NativeClock.HistoryRead = (_, _, _, _) => { Activate(owner); return 0x91ef; };
        Assert.Equal(0x91ef, _module.ReadHistory(reader, 0, 0, -1));
        Assert.Single(NativeClock.HistoryCalls);
    }
    [Theory]
    [MemberData(nameof(LateReaders))]
    public void Native_owner_released_inside_original_getter_preserves_native_value(string owner, int reader)
    {
        Activate(owner);
        NativeClock.HistoryRead = (_, _, _, _) =>
        {
            _module.NativeSync = _module.NativeMovie = false;
            FhUtil.MainMenu = FhUtil.MenuPause = FhUtil.Overdrive = 0;
            FhUtil.Dummy = 0;
            return 0x91ef;
        };
        Assert.Equal(0x91ef, _module.ReadHistory(reader, 0, 0, -1));
        Assert.Single(NativeClock.HistoryCalls);
    }
    [Theory]
    [InlineData("sync", false)]
    [InlineData("movie", false)]
    [InlineData("menu", false)]
    [InlineData("main_menu", false)]
    [InlineData("overdrive", false)]
    [InlineData("dummy", false)]
    [InlineData("unknown_dummy", false)]
    [InlineData("sync", true)]
    [InlineData("movie", true)]
    [InlineData("menu", true)]
    [InlineData("main_menu", true)]
    [InlineData("overdrive", true)]
    [InlineData("dummy", true)]
    [InlineData("unknown_dummy", true)]
    public void Native_owner_latch_is_immediate_before_and_after_clock(string owner, bool seen)
    {
        _module.ConfigurePad(seen, true);
        Activate(owner);
        _module.CallPad();
        Assert.Equal(1, NativeClock.PadCalls);
        Assert.False(_module.PadState.Pending);
        Assert.Equal(0, _module.PadState.Skipped);
        Assert.Equal(0, _module.PadState.Deferred);
    }
    [Theory]
    [InlineData("sync")]
    [InlineData("movie")]
    [InlineData("menu")]
    [InlineData("main_menu")]
    [InlineData("overdrive")]
    [InlineData("dummy")]
    [InlineData("unknown_dummy")]
    public void Pending_latch_survives_a_late_native_owner_before_presentation_decision(string owner)
    {
        _module.ConfigurePad(false, false);
        _module.CallPad();
        Assert.True(_module.PadState.Pending);
        Activate(owner);
        _module.DecidePad(true);
        Assert.Equal(1, NativeClock.PadCalls);
        Assert.False(_module.PadState.Pending);
        Assert.Equal(0, _module.PadState.Skipped);
    }
    [Theory]
    [InlineData("sync")]
    [InlineData("movie")]
    [InlineData("menu")]
    [InlineData("main_menu")]
    [InlineData("overdrive")]
    [InlineData("dummy")]
    [InlineData("unknown_dummy")]
    public void Native_input_owner_prevents_image_admission(string owner)
    {
        Assert.True(_module.EnableAdapter().InitScheduler());
        _module.ArmScheduler();
        Activate(owner);
        _module.BeginFrame();
        Assert.False(_module.InputFrameEnabled);
        Assert.Equal(0, _module.AdmittedImages);
        Assert.Equal(60, _module.ReportedRate);
    }
    [Theory]
    [InlineData("sync")]
    [InlineData("movie")]
    [InlineData("menu")]
    [InlineData("main_menu")]
    [InlineData("overdrive")]
    [InlineData("dummy")]
    [InlineData("unknown_dummy")]
    public void Native_input_owner_selected_in_original_clock_keeps_native_publication_position(string owner)
    {
        _module.PreparePass();
        NativeClock.Advance = () =>
        {
            Activate(owner);
            _module.CallPad();
            Assert.Equal(1, NativeClock.PadCalls);
            Assert.False(_module.PadState.Pending);
        };
        _module.RunNativePass();
        Assert.False(_module.InputFrameEnabled);
        Assert.Equal(1, NativeClock.PadCalls);
    }
    [Fact]
    public void No_clock_menu_consumers_observe_publication_before_main_loop_returns()
    {
        _module.PreparePass();
        FhUtil.MenuPause = 1;
        NativeClock.LoopAction = _ =>
        {
            _module.CallPad();
            Assert.Equal(1, NativeClock.PadCalls);
            Assert.False(_module.PadState.Pending);
            Assert.Equal(0x91ef, _module.ReadHistory(0, 0, 0, 0));
        };
        _module.CallDiagnosticLoop(1f / 60);
        Assert.Equal(1, NativeClock.PadCalls);
        Assert.Equal(0, _module.PadState.Late);
    }
    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Main_menu_flag_preserves_native_input_for_every_nonzero_value(int flag)
    {
        FhUtil.MainMenu = flag;
        for (int reader = 0; reader < 4; reader++)
            Assert.Equal((ushort)0x91ef, _module.ReadHistory(reader, 0, 0, -1));
    }
    [Fact]
    public void Main_menu_guard_and_operand_preflight_before_any_input_hook()
    {
        _module.EnableAdapter();
        _module.ResetHistoryStartup();
        Assert.True(_module.InitHistory());
        Assert.Contains(_module.CodeChecks, x => x.Rva == 0x4ab034 && x.Pattern == "83 3D ?? ?? ?? ?? 00 74 4C");
        Assert.Contains(_module.OperandChecks, x => x.Rva == 0x4ab036 && x.Expected == 0xf40824);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    public void Main_menu_guard_changes_leave_the_complete_input_group_native(int offset)
    {
        _module.EnableAdapter();
        _module.ResetHistoryStartup();
        if (offset == 9) _module.MainMenuEntry = _module.MainMenuEntry[..8];
        else _module.MainMenuEntry[offset] ^= 0xff;
        Assert.True(_module.InitHistory());
        Assert.False(_module.HistoryReady);
        Assert.Empty(NativeClock.HistoryHookRegistrations);
        Assert.Equal((ushort)0x91ef, _module.ReadHistory(0, 0, 0, -1));
    }
    [Fact]
    public void Main_menu_operand_rejection_leaves_the_complete_input_group_native()
    {
        _module.EnableAdapter();
        _module.ResetHistoryStartup();
        _module.RejectedPadOperandSite = 0x4ab036;
        Assert.True(_module.InitHistory());
        Assert.False(_module.HistoryReady);
        Assert.Empty(NativeClock.HistoryHookRegistrations);
    }
    [Fact]
    public void Menu_guard_and_relocated_operand_preflight_before_hooks()
    {
        _module.EnableAdapter();
        _module.ResetHistoryStartup();
        Assert.True(_module.InitHistory());
        Assert.True(_module.HistoryReady);
        Assert.Contains(_module.CodeChecks, x => x.Rva == 0x420b89 && x.Pattern == "83 3D ?? ?? ?? ?? 00 75 11");
        Assert.Contains(_module.OperandChecks, x => x.Rva == 0x420b8b && x.Expected == 0xf40828);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    public void Changed_or_truncated_menu_guard_keeps_entire_input_group_native(int offset)
    {
        _module.EnableAdapter();
        _module.ResetHistoryStartup();
        if (offset == 9) _module.MenuPauseEntry = _module.MenuPauseEntry[..8];
        else _module.MenuPauseEntry[offset] ^= 0xff;
        Assert.True(_module.InitHistory());
        Assert.False(_module.HistoryReady);
        Assert.Empty(NativeClock.HistoryHookRegistrations);
        Assert.Equal(0x91ef, _module.ReadHistory(0, 0, 0, 0));
    }
}
public unsafe sealed partial class VrrModule
{
    internal byte[] MainMenuEntry = Convert.FromHexString("833D2408340100744C");
    internal byte[] MenuPauseEntry = Convert.FromHexString("833D28083401007511");
}