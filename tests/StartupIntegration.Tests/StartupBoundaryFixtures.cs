global using System.Diagnostics;
global using System.Runtime.InteropServices;
global using System.Text.Json;

using System.Buffers.Binary;
using System.ComponentModel;
using System.Reflection;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Fahrenheit.Mods.Vrr;

internal enum RequiredReceipt { Available, False, Missing }

internal sealed record StartupState(byte[] General, byte[] Water, bool Initialized, bool RenderReady, bool Armed);

internal sealed unsafe class StartupImage : IDisposable
{
    internal const nint GeneralRva = 0x1000;
    internal const nint WaterRva = 0x2000;
    private const nint CallerRva = 0x3000;
    private const nint SlotRva = 0x4000;
    private readonly nint _image;
    internal VrrModule Module { get; } = new();
    internal nuint ReturnAddress => (nuint)(_image + CallerRva + 5);
    internal nuint ReturnSlot => (nuint)(_image + SlotRva);
    internal readonly List<(string Stage, StartupState State)> Trace = [];
    internal int NativeCalls;
    internal int RequiredProbes;
    internal Action? DuringNative;
    internal Action? DuringRequiredProbe;

    internal StartupImage()
    {
        if (IntPtr.Size != 4) throw new InvalidOperationException("This integration fixture requires an x86 test host.");
        _image = VirtualAlloc(0, 0x600000, 0x3000, 0x04);
        if (_image == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        FhUtil.ImageBase = _image;
        Write(GeneralRva, [0x11, 0x12]);
        Write(WaterRva, [0x21, 0x22]);
        Write(0x54a224, [0x55, 0x8B, 0xEC, 0x83, 0xEC, 0x14]);
        Span<byte> caller = new((void*)(_image + CallerRva), 10);
        caller[0] = 0xE8;
        BinaryPrimitives.WriteUInt32LittleEndian(caller.Slice(1, 4), unchecked((uint)(_image + 0x54a224) - (uint)ReturnAddress));
        caller[5] = 0xE9;
        BinaryPrimitives.WriteUInt32LittleEndian(caller.Slice(6, 4), unchecked((uint)(_image + 0x5493c6) - (uint)ReturnAddress - 5u));
        SetStackReturn(ReturnAddress);
        NativeSnapshotBridge.Reset(ReturnAddress, ReturnSlot);
        SyntheticSdk.Reset();
        SyntheticSdk.Native = RunNative;
        SyntheticSdk.LookupObserved = _ => Record("startup lookup");
        Module.DiagnosticObserved = kind => { if (kind == "startup_armed") Record(kind); };
    }

    internal void Prepare(RequiredReceipt receipt = RequiredReceipt.Available, bool renderReady = true, bool overlap = false)
    {
        if (!Module.SelectPlan() || !Module.PrepareBridge())
            throw new InvalidOperationException("Synthetic startup setup failed.");
        Module.BeginPreparation();
        if (!Module.InstallStartup()) throw new InvalidOperationException("Synthetic startup insertion failed.");
        if (!Module.InstallRequired(receipt, () =>
        {
            RequiredProbes++;
            Record("required probe");
            DuringRequiredProbe?.Invoke();
            return receipt == RequiredReceipt.Available;
        })) throw new InvalidOperationException("Synthetic required registration failed.");
        Module.QueueGeneral(GeneralRva, [0xA1, 0xA2]);
        Module.QueueWater(overlap ? GeneralRva + 1 : WaterRva, [0xB1, 0xB2]);
        Module.CompletePreparation(renderReady);
        Trace.Clear();
    }

    internal StartupState State => new(Read(GeneralRva), Read(WaterRva), Module.Initialized, Module.RenderReady, Module.RuntimeEffectsArmed);
    internal void Record(string stage) => Trace.Add((stage, State));
    internal void RunNative()
    {
        NativeCalls++;
        Record("native");
        DuringNative?.Invoke();
    }
    internal bool InvokeCore() => Module.InvokeCore(ReturnAddress, ReturnSlot, RunNative);
    internal void Write(nint rva, ReadOnlySpan<byte> bytes) => bytes.CopyTo(new Span<byte>((void*)(_image + rva), bytes.Length));
    internal byte[] Read(nint rva) => new ReadOnlySpan<byte>((void*)(_image + rva), 2).ToArray();
    internal void SetStackReturn(nuint value) => BinaryPrimitives.WriteUInt32LittleEndian(new Span<byte>((void*)ReturnSlot, 4), (uint)value);
    internal void BreakCaller() => *(byte*)(_image + CallerRva) = 0x90;
    internal void RestoreCaller() => *(byte*)(_image + CallerRva) = 0xE8;
    internal void BreakCookieEntry() => *(byte*)(_image + 0x54a224) = 0x90;
    internal void SetCallerReadable(bool readable) => SetReadable(CallerRva, readable);
    internal void SetSlotReadable(bool readable) => SetReadable(SlotRva, readable);
    private void SetReadable(nint rva, bool readable)
    {
        if (!VirtualProtect(_image + rva, 0x1000, readable ? 0x04u : 0x01u, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public void Dispose()
    {
        try { Module.Cleanup(); }
        finally
        {
            SyntheticSdk.Reset();
            FhUtil.ImageBase = 0;
            if (!VirtualFree(_image, 0, 0x8000)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint VirtualAlloc(nint address, nuint size, uint allocationType, uint protection);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualProtect(nint address, nuint size, uint protection, out uint previous);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualFree(nint address, nuint size, uint freeType);
}

internal static unsafe class FhUtil
{
    internal static nint ImageBase;
    internal static T* ptr_at<T>(nint rva) where T : unmanaged => (T*)(ImageBase + rva);
}

internal readonly ref struct FhCallLocation
{
    internal nint Rva { get; }
    internal FhCallLocation(nint rva, int argumentCount) => Rva = rva;
}

internal readonly struct FhCallHandle<T> where T : Delegate
{
    private readonly nint _rva;
    internal T? fnptr { get; }
    internal FhCallHandle(FhCallLocation location) { _rva = location.Rva; fnptr = null; }
    private FhCallHandle(T? continuation) { _rva = 0; fnptr = continuation; }
    internal bool hook(VrrModule module, T callback) => SyntheticSdk.Insert(module, callback);
    internal FhCallHandle<T> chain_from(T callback) => new(SyntheticSdk.Lookup(_rva, callback));
}

internal static class SyntheticSdk
{
    private static readonly Dictionary<Delegate, Delegate> Continuations = [];
    internal static Action Native = () => { };
    internal static Action<nint>? LookupObserved;
    internal static bool Available;
    internal static bool ReceiptExistedBeforeInsertion;
    internal static int Insertions;
    internal static int Lookups;
    internal static int DefaultLocationLookups;

    internal static bool Insert<T>(VrrModule module, T callback) where T : Delegate
    {
        ReceiptExistedBeforeInsertion = module.PendingReceiptMatches(callback);
        Action native = () => Native();
        Continuations.Add(callback, Delegate.CreateDelegate(typeof(T), native.Target, native.Method));
        Insertions++;
        return true;
    }

    internal static T? Lookup<T>(nint rva, T callback) where T : Delegate
    {
        Lookups++;
        if (rva == 0) DefaultLocationLookups++;
        LookupObserved?.Invoke(rva);
        return Available && Continuations.TryGetValue(callback, out Delegate? native) ? (T)native : null;
    }

    internal static void Reset()
    {
        Continuations.Clear();
        Native = () => { };
        LookupObserved = null;
        Available = true;
        ReceiptExistedBeforeInsertion = false;
        Insertions = Lookups = DefaultLocationLookups = 0;
    }
}

internal static class NativeSnapshotBridge
{
    internal delegate void Dispatch(nuint returnAddress, nuint returnSlot);
    internal static bool Available;
    internal static nuint ReturnAddress;
    internal static nuint ReturnSlot;
    internal static int Creations;

    internal static bool TryCreateStartup<T>(Dispatch dispatch, out T? callback) where T : Delegate
    {
        Creations++;
        Action entry = () => dispatch(ReturnAddress, ReturnSlot);
        callback = Available ? (T)Delegate.CreateDelegate(typeof(T), entry.Target, entry.Method) : null;
        return callback is not null;
    }

    internal static void Reset(nuint address, nuint slot)
    {
        Available = true;
        ReturnAddress = address;
        ReturnSlot = slot;
        Creations = 0;
    }
}

internal sealed class TestLogger
{
    internal readonly List<string> Errors = [];
    internal void Error(string message) => Errors.Add(message);
    internal void Warning(string message) { }
    internal void Info(string message) { }
}

public unsafe sealed partial class VrrModule
{
    private readonly TestLogger _logger = new();
    private readonly PatchJournal _patches = new();
    private readonly PatchJournal _menu_water_patches = new();
    private bool _initialized;
    private bool _render_mode_ready;
    internal bool Initialized => _initialized;
    internal bool RenderReady => _render_mode_ready;
    internal Action<string>? DiagnosticObserved;
    internal Action? CleanupObserved;
    internal bool CleanupOwnsPublicationGate => Monitor.IsEntered(_startupPublicationGate);
    private readonly Action _requiredCallback = () => { };

    internal bool SelectPlan() => HookRegistrationPlan.TryCreate(
        [new(HookSiteId.ModuleStartupCookieInitialization), new(HookSiteId.ModuleFFXApplicationAnimate)],
        HookRegistrationPlan.AvailableSourceContracts, out _hookRegistration, out _);
    internal bool PrepareBridge() => prepare_startup_arm();
    internal bool InstallStartup() => init_startup_hook();
    internal void BeginPreparation() => begin_startup_preparation();
    internal void QueueGeneral(nint rva, ReadOnlySpan<byte> bytes) => _patches.Write(rva, bytes);
    internal void QueueWater(nint rva, ReadOnlySpan<byte> bytes) => _menu_water_patches.Write(rva, bytes);
    internal void CompletePreparation(bool renderReady) => complete_startup_preparation(renderReady);
    internal bool InvokeCore(nuint address, nuint slot, Action native) => startup_invoke(address, slot, native);
    internal void InvokePublished() => _startupEntry!();
    internal void Stop() => stop_startup_activation();
    internal void Cleanup() => cleanup_runtime();
    internal bool InstallRequired(RequiredReceipt receipt, Func<bool> probe)
        => install_registered_hook(HookSiteId.ModuleFFXApplicationAnimate, 0x5000,
            () => receipt == RequiredReceipt.Missing || _hookRegistration!.RecordContinuation(_requiredCallback, probe));

    internal bool PendingReceiptMatches(Delegate callback)
    {
        object? pending = typeof(HookRegistrationPlan).GetField("_pendingContinuation", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_hookRegistration);
        return pending is not null && ReferenceEquals(callback, pending.GetType().GetProperty("Callback")!.GetValue(pending));
    }

    private bool fx_code_matches(nint rva, string pattern)
    {
        byte[] expected = pattern.Split(' ').Select(value => Convert.ToByte(value, 16)).ToArray();
        Span<byte> code = stackalloc byte[expected.Length];
        return MemoryProbe.TryCopy((nint)FhUtil.ptr_at<byte>(rva), code) && code.SequenceEqual(expected);
    }
    private bool hook_or_log(HookSiteId site, string name, nint rva, Func<bool> install) => register_hook(site, name, rva, install);
    private void diagnostic_event(string kind, object data) => DiagnosticObserved?.Invoke(kind);
    private void cleanup_runtime_owned(bool failedStartup)
    {
        CleanupObserved?.Invoke();
        dispose_hook_registration();
        _initialized = _render_mode_ready = false;
        _menu_water_patches.RestoreAll();
        _patches.RestoreAll();
    }
}
