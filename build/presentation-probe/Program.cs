using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Fahrenheit.Mods.Vrr;

internal static unsafe class Program
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Entry();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void StartupEntry();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int WrongResult();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void WrongArguments(int value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void WrongConvention();
    private delegate void MissingConvention();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Bind(nint callback);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Invoke(nint entry);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nuint ExpectedReturn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint ReadControl();
    private static readonly int[] Calls = new int[2];
    private static readonly bool[] CaptureOk = new bool[2];
    private static readonly uint[] ObservedControl = new uint[2];
    private static ExpectedReturn? _expectedReturn;
    private static ReadControl? _readControl;
    private static Entry? _original;

    private sealed class CallbackOwner(int slot)
    {
        internal void Invoke(nuint address, nuint returnSlot) => Dispatch(slot, address, returnSlot);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static T CreateRootedEntry<T>(bool startup, out WeakReference callbackReference) where T : Delegate
    {
        var dispatch = new NativeSnapshotBridge.Dispatch(new CallbackOwner(startup ? 1 : 0).Invoke);
        callbackReference = new WeakReference(dispatch);
        bool created = startup ? NativeSnapshotBridge.TryCreateStartup<T>(dispatch, out var entry)
            : NativeSnapshotBridge.TryCreate<T>(dispatch, out entry);
        Check(created, "create native entry delegate for slot " + startup);
        bool repeated = startup ? NativeSnapshotBridge.TryCreateStartup<T>(dispatch, out var duplicate)
            : NativeSnapshotBridge.TryCreate<T>(dispatch, out duplicate);
        Check(repeated && ReferenceEquals(entry, duplicate), "same callback and type returns rooted entry for slot " + startup);
        return entry!;
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException("CHECK failed: " + message);
    }

    private static int Main(string[] args)
    {
        try
        {
            Check(IntPtr.Size == 4, "actual x86 process");
            if (args.Length == 2 && args[0] == "--startup-trampoline") return StartupTrampolineProbe.Run(args[1]);
            Check(!NativeSnapshotBridge.TryCreate<WrongResult>(OtherDispatch, out _), "snapshot rejects non-void delegate");
            Check(!NativeSnapshotBridge.TryCreate<WrongArguments>(OtherDispatch, out _), "snapshot rejects delegate arguments");
            Check(!NativeSnapshotBridge.TryCreate<WrongConvention>(OtherDispatch, out _), "snapshot rejects non-cdecl delegate");
            Check(!NativeSnapshotBridge.TryCreate<MissingConvention>(OtherDispatch, out _), "snapshot rejects unspecified convention");
            Check(!NativeSnapshotBridge.TryCreate<Entry>(null!, out _), "snapshot rejects null callback");
            Check(!NativeSnapshotBridge.TryCreateStartup<WrongResult>(OtherDispatch, out _), "startup rejects non-void delegate");
            Check(!NativeSnapshotBridge.TryCreateStartup<WrongArguments>(OtherDispatch, out _), "startup rejects delegate arguments");
            Check(!NativeSnapshotBridge.TryCreateStartup<WrongConvention>(OtherDispatch, out _), "startup rejects non-cdecl delegate");
            Check(!NativeSnapshotBridge.TryCreateStartup<MissingConvention>(OtherDispatch, out _), "startup rejects unspecified convention");
            Check(!NativeSnapshotBridge.TryCreateStartup<StartupEntry>(null!, out _), "startup rejects null callback");
            if (args.Contains("--unavailable"))
            {
                Check(!NativeSnapshotBridge.TryCreate<Entry>(OtherDispatch, out _), "snapshot rejects missing or invalid helper");
                Check(!NativeSnapshotBridge.TryCreateStartup<StartupEntry>(OtherDispatch, out _), "startup rejects missing or invalid helper");
                Console.WriteLine("fhpresent managed factory: both slots reject unavailable helper.");
                return 0;
            }
            bool startupFirst = args.Contains("--startup-first");
            Entry? entry = null;
            StartupEntry? startupEntry = null;
            WeakReference callbackReference = null!, startupReference = null!;
            if (startupFirst) startupEntry = CreateRootedEntry<StartupEntry>(true, out startupReference);
            else entry = CreateRootedEntry<Entry>(false, out callbackReference);
            var library = NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "fhpresent.dll"));
            var pointer = NativeLibrary.GetExport(library, "fhpresent_entry");
            var startupPointer = NativeLibrary.GetExport(library, "fhpresent_startup_entry");
            Check(pointer != startupPointer, "distinct native slot addresses");

            var probe = NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "fhpresent-probe.dll"));
            var invoke = Marshal.GetDelegateForFunctionPointer<Invoke>(NativeLibrary.GetExport(probe, "fhpresent_test_invoke"));
            _expectedReturn = Marshal.GetDelegateForFunctionPointer<ExpectedReturn>(NativeLibrary.GetExport(probe, "fhpresent_test_expected_return"));
            _readControl = Marshal.GetDelegateForFunctionPointer<ReadControl>(NativeLibrary.GetExport(probe, "fhpresent_test_original_control"));
            _original = Marshal.GetDelegateForFunctionPointer<Entry>(NativeLibrary.GetExport(probe, "fhpresent_test_original"));
            Check(invoke(startupFirst ? pointer : startupPointer) == 1 && Calls[0] == 0 && Calls[1] == 0, "binding first slot leaves other slot unbound");

            if (startupFirst) entry = CreateRootedEntry<Entry>(false, out callbackReference);
            else startupEntry = CreateRootedEntry<StartupEntry>(true, out startupReference);
            Check(Marshal.GetFunctionPointerForDelegate(entry!) == pointer, "snapshot delegate round trip keeps native export address");
            Check(Marshal.GetFunctionPointerForDelegate(startupEntry!) == startupPointer, "startup delegate round trip keeps native export address");
            Check(!NativeSnapshotBridge.TryCreate<Entry>(OtherDispatch, out _), "snapshot rejects different callback");
            Check(!NativeSnapshotBridge.TryCreateStartup<StartupEntry>(OtherDispatch, out _), "startup rejects different callback");
            Check(!NativeSnapshotBridge.TryCreate<StartupEntry>((NativeSnapshotBridge.Dispatch)callbackReference.Target!, out _), "snapshot rejects changed entry type");
            Check(!NativeSnapshotBridge.TryCreateStartup<Entry>((NativeSnapshotBridge.Dispatch)startupReference.Target!, out _), "startup rejects changed entry type");
            foreach (var slot in new[] { ("fhpresent_bind", callbackReference), ("fhpresent_startup_bind", startupReference) })
            {
                var bind = Marshal.GetDelegateForFunctionPointer<Bind>(NativeLibrary.GetExport(library, slot.Item1));
                Check(bind(0) == 0, "native null binding rejected for " + slot.Item1);
                Check(bind(Marshal.GetFunctionPointerForDelegate((NativeSnapshotBridge.Dispatch)slot.Item2.Target!)) == 1, "native identical thunk accepted for " + slot.Item1);
                Check(bind(Marshal.GetFunctionPointerForDelegate(new NativeSnapshotBridge.Dispatch(OtherDispatch))) == 0, "native replacement thunk rejected for " + slot.Item1);
            }
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Check(callbackReference.IsAlive && startupReference.IsAlive, "factory roots both instance callbacks across full GC");
            Check(invoke(pointer) == 1, "snapshot preserves native caller state around managed callback");
            Check(Calls[0] == 1 && Calls[1] == 0 && CaptureOk[0], "snapshot captures its caller without invoking startup");
            Check(invoke(startupPointer) == 1, "startup preserves native caller state around managed callback");
            Check(Calls[0] == 1 && Calls[1] == 1 && CaptureOk[1], "startup captures its caller without invoking snapshot");
            for (int slot = 0; slot < 2; ++slot)
            {
                Console.WriteLine($"Slot {slot} FP controls inside chained dummy void original: 0x{ObservedControl[slot]:x8}; native caller: 0x067f3f80.");
                Check((ObservedControl[slot] & 0xffffffc0) == 0x067f3f80, "native work inherits caller FP controls for slot " + slot);
            }
            GC.KeepAlive(entry);
            GC.KeepAlive(startupEntry);
            Console.WriteLine("fhpresent .NET 10 x86 probe: two isolated slots, export identity, factory validation, GC lifetime, caller capture and native state preservation passed.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Dispatch(int slot, nuint address, nuint returnSlot)
    {
        _original!();
        ObservedControl[slot] = _readControl!();
        ++Calls[slot];
        CaptureOk[slot] = returnSlot != 0 && *(nuint*)returnSlot == address && address == _expectedReturn!();
        _ = Math.Sqrt(123.5);
        GC.Collect();
    }

    private static void OtherDispatch(nuint address, nuint slot) { }
}
