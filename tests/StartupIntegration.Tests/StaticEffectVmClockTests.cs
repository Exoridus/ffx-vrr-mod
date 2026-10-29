using Xunit;
using static Fahrenheit.Mods.Vrr.BattleEffectPauseCode;

namespace Fahrenheit.Mods.Vrr;

public unsafe sealed class StaticEffectVmClockTests
{
    [Fact]
    public void Permanent_backend_prepares_without_live_writes_then_commits_and_restores_exactly_seven_sites()
    {
        using var image = new VmImage();
        var before = image.Bytes();
        image.Module.BeginPreparation();
        Assert.True(image.Module.PrepareStaticVm());
        Assert.True(image.Module.StaticVmInstalled);
        Assert.False(image.Module.FullBattleInstalled);
        Assert.Equal(before, image.Bytes());
        image.Module.CommitStaticVm();
        var after = image.Bytes();
        var sites = image.Module.StaticVmPatchSites;
        Assert.Equal(7, sites.Length);
        for (int i = 0; i < before.Length; i++)
            if (before[i] != after[i]) Assert.Contains(sites, site => i >= site.Site && i < site.Site + site.Length);
        image.Module.RestoreStaticVm();
        Assert.Equal(before, image.Bytes());
        Assert.False(image.Module.StaticVmInstalled);
        Assert.True(image.Module.StaticVmStorageRetained);
    }

    [Fact]
    public void Release_revokes_capability_without_clearing_repeat_history_or_an_inflight_observation()
    {
        using var image = new VmImage();
        image.Module.BeginPreparation();
        Assert.True(image.Module.PrepareStaticVm());
        image.Module.CommitStaticVm();
        Assert.False(image.Module.StaticVmRepeatEntry(0, 0x100, 0x500));
        image.Module.StaticVmRepeatNext(0x100, 0x500);
        Assert.False(image.Module.StaticVmRepeatEntry(3, 0x100, 0x500));
        Assert.False(image.Module.StaticVmRepeatEntry(0, 0x200, 0x600));

        image.Module.RestoreStaticVm();

        Assert.False(image.Module.StaticVmInstalled);
        Assert.True(image.Module.StaticVmStorageRetained);
        image.Module.StaticVmRepeatNext(0x200, 0x600);
        Assert.False(image.Module.StaticVmRepeatEntry(3, 0x100, 0x500));
        Assert.False(image.Module.StaticVmRepeatEntry(3, 0x200, 0x600));
    }
    [Theory]
    [InlineData("bytes")]
    [InlineData("table")]
    [InlineData("pilot")]
    [InlineData("before_entry")]
    [InlineData("before_table")]
    [InlineData("not_preparing")]
    public void Missing_contract_refuses_without_image_writes_or_ownership(string failure)
    {
        using var image = new VmImage();
        image.Module.BreakStaticVm(failure);
        var before = image.Bytes();
        if (failure != "not_preparing") image.Module.BeginPreparation();
        Assert.False(image.Module.PrepareStaticVm());
        Assert.False(image.Module.StaticVmInstalled);
        Assert.False(image.Module.FullBattleInstalled);
        Assert.Equal(before, image.Bytes());
    }

    [Fact]
    public void Changed_prepared_bytes_fail_before_any_commit_and_cleanup_revokes_capability()
    {
        using var image = new VmImage();
        image.Module.BeginPreparation();
        Assert.True(image.Module.PrepareStaticVm());
        image.Module.BreakStaticVm("bytes");
        var before = image.Bytes();
        Assert.Throws<InvalidOperationException>(() => image.Module.CommitStaticVm());
        Assert.Equal(before, image.Bytes());
        image.Module.RestoreStaticVm();
        Assert.False(image.Module.StaticVmInstalled);
    }

    private sealed class VmImage : IDisposable
    {
        private readonly nint _memory;
        internal VrrModule Module { get; } = new();
        internal VmImage()
        {
            VrrModule.ClearStaticOwner();
            Assert.Equal(4, IntPtr.Size);
            _memory = VmAlloc(0, 0x900000, 0x3000, 4);
            Assert.NotEqual(0, _memory);
            FhUtil.ImageBase = _memory;
            Module.SeedStaticVm();
        }
        internal byte[] Bytes() => new ReadOnlySpan<byte>((void*)_memory, 0x900000).ToArray();
        public void Dispose() { Module.RestoreStaticVm(); VmFree(_memory, 0, 0x8000); }
        [DllImport("kernel32.dll", EntryPoint = "VirtualAlloc")] private static extern nint VmAlloc(nint address, nuint size, uint type, uint protect);
        [DllImport("kernel32.dll", EntryPoint = "VirtualFree")] private static extern bool VmFree(nint address, nuint size, uint type);
    }
}

public unsafe sealed partial class VrrModule
{
    private bool _staticEffectVmClockInstalled, _battleEffectPauseInstalled;
    private bool _particle_pilot_installed = true;
    private int _particle_pilot_thread;
    private const nint PilotWorker = 0x40cd60;
    internal static void ClearStaticOwner() => s_battle_effects = null;
    internal bool StaticVmInstalled => _staticEffectVmClockInstalled;
    internal bool FullBattleInstalled => _battleEffectPauseInstalled;
    internal bool StaticVmStorageRetained => _bx != null;
    internal bool StaticVmRepeatEntry(byte hold, nint record, uint pc) => _bxRepeat.Entry(hold, record, 0, pc, 0, 1);
    internal void StaticVmRepeatNext(nint record, uint pc) => _bxRepeat.Next(record, 0, pc, 1);
    internal bool PrepareStaticVm() => init_static_effect_vm_clock();
    internal void CommitStaticVm() => _patches.CommitPrepared();
    internal void RestoreStaticVm() { _patches.RestoreAll(); release_static_effect_vm_clock(); }
    internal (nint Site, int Length)[] StaticVmPatchSites => [(BxSentry,7),(BxHandler,7),(BxDec,6),(BxPcCommit,5),(BxFreshWait,6),(BxNext,7),(BxGate,7)];
    internal void SeedStaticVm()
    {
        foreach(var pair in new (nint Site,string Pattern)[] { (BxStreamHead,BxHeadPattern),(BxSentry,BxSentryPattern),(BxHandler,BxHandlerPattern),(BxDec,BxDecPattern),(BxPcCommit,BxPcCommitPattern),(BxFreshWait,BxFreshWaitPattern),(BxNext,BxNextPattern),(BxGate,BxGatePattern) })
        {
            byte[] bytes=pair.Pattern.Split(' ').Select(s=>Convert.ToByte(s,16)).ToArray();
            bytes.CopyTo(new Span<byte>(FhUtil.ptr_at<byte>(pair.Site),bytes.Length));
        }
        static_vm_before_bytes().CopyTo(new Span<byte>(FhUtil.ptr_at<byte>(0x400610),87));
        *(uint*)FhUtil.ptr_at<byte>(0x848d88) = (uint)FhUtil.ptr_at<byte>(0x848d90);
        *(uint*)FhUtil.ptr_at<byte>(0x848d9c) = (uint)FhUtil.ptr_at<byte>(0x400610);
        foreach(var entry in BxOpuHandlers) *(uint*)FhUtil.ptr_at<byte>(BxOpuTable+entry.Opcode*4)=(uint)FhUtil.ptr_at<byte>(entry.TargetRva);
    }
    internal void BreakStaticVm(string failure)
    {
        if(failure=="bytes") *FhUtil.ptr_at<byte>(BxHandler)^=1;
        if(failure=="table") *(uint*)FhUtil.ptr_at<byte>(BxOpuTable+9*4)=0;
        if(failure=="before_entry") *FhUtil.ptr_at<byte>(0x400610+56)^=1;
        if(failure=="before_table") *(uint*)FhUtil.ptr_at<byte>(0x848d9c)=0;
        if(failure=="pilot") _particle_pilot_installed=false;
    }
}
internal static class EngineAddresses { internal const nint EternalEffectRunBefore = 0x400610; }
