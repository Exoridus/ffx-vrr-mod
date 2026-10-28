using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Fahrenheit.Mods.Vrr;

internal static unsafe class Program
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Entry();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void StartupEntry();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void PadEntry();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int WrongResult();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void WrongArguments(int value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void WrongConvention();
    private delegate void MissingConvention();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Bind(nint callback);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Invoke(nint entry);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nuint ExpectedReturn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint ReadControl();
    private static readonly int[] Calls = new int[3];
    private static readonly bool[] CaptureOk = new bool[3];
    private static readonly uint[] ObservedControl = new uint[3];
    private static ExpectedReturn? _expectedReturn;
    private static ReadControl? _readControl;
    private static Entry? _original;

    private sealed class CallbackOwner(int slot)
    {
        internal void Invoke(nuint address, nuint returnSlot) => Dispatch(slot, address, returnSlot);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static T CreateRootedEntry<T>(int slot, out WeakReference callbackReference) where T : Delegate
    {
        var dispatch = new NativeSnapshotBridge.Dispatch(new CallbackOwner(slot).Invoke);
        callbackReference = new WeakReference(dispatch);
        bool created = TryCreateSlot<T>(slot, dispatch, out var entry);
        Check(created, "create native entry delegate for slot " + slot);
        bool repeated = TryCreateSlot<T>(slot, dispatch, out var duplicate);
        Check(repeated && ReferenceEquals(entry, duplicate), "same callback and type returns rooted entry for slot " + slot);
        return entry!;
    }

    private static bool TryCreateSlot<T>(int slot, NativeSnapshotBridge.Dispatch dispatch, out T? entry) where T : Delegate
        => slot switch
        {
            0 => NativeSnapshotBridge.TryCreate<T>(dispatch, out entry),
            1 => NativeSnapshotBridge.TryCreateStartup<T>(dispatch, out entry),
            2 => NativeSnapshotBridge.TryCreatePadLatch<T>(dispatch, out entry),
            _ => throw new ArgumentOutOfRangeException(nameof(slot))
        };

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
            for (int slot = 0; slot < 3; slot++)
            {
                Check(!TryCreateSlot<WrongResult>(slot, OtherDispatch, out _), "rejects non-void delegate for slot " + slot);
                Check(!TryCreateSlot<WrongArguments>(slot, OtherDispatch, out _), "rejects delegate arguments for slot " + slot);
                Check(!TryCreateSlot<WrongConvention>(slot, OtherDispatch, out _), "rejects non-cdecl delegate for slot " + slot);
                Check(!TryCreateSlot<MissingConvention>(slot, OtherDispatch, out _), "rejects unspecified convention for slot " + slot);
                Check(!TryCreateSlot<Entry>(slot, null!, out _), "rejects null callback for slot " + slot);
            }
            if (args.Contains("--unavailable"))
            {
                for (int slot = 0; slot < 3; slot++)
                    Check(!TryCreateSlot<Entry>(slot, OtherDispatch, out _), "rejects missing or invalid helper for slot " + slot);
                Console.WriteLine("fhpresent managed factory: all three slots reject unavailable helper.");
                return 0;
            }
            int first = args.Contains("--pad-first") ? 2 : args.Contains("--startup-first") ? 1 : 0;
            Entry? entry = null;
            StartupEntry? startupEntry = null;
            PadEntry? padEntry = null;
            WeakReference callbackReference = null!, startupReference = null!, padReference = null!;
            void Create(int slot)
            {
                if (slot == 0) entry = CreateRootedEntry<Entry>(0, out callbackReference);
                else if (slot == 1) startupEntry = CreateRootedEntry<StartupEntry>(1, out startupReference);
                else padEntry = CreateRootedEntry<PadEntry>(2, out padReference);
            }
            Create(first);
            var library = NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "fhpresent.dll"));
            var pointer = NativeLibrary.GetExport(library, "fhpresent_entry");
            var startupPointer = NativeLibrary.GetExport(library, "fhpresent_startup_entry");
            var padPointer = NativeLibrary.GetExport(library, "fhpresent_pad_entry");
            nint[] pointers = [pointer, startupPointer, padPointer];
            Check(pointers.Distinct().Count() == 3, "distinct native slot addresses");

            var probe = NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "fhpresent-probe.dll"));
            var invoke = Marshal.GetDelegateForFunctionPointer<Invoke>(NativeLibrary.GetExport(probe, "fhpresent_test_invoke"));
            _expectedReturn = Marshal.GetDelegateForFunctionPointer<ExpectedReturn>(NativeLibrary.GetExport(probe, "fhpresent_test_expected_return"));
            _readControl = Marshal.GetDelegateForFunctionPointer<ReadControl>(NativeLibrary.GetExport(probe, "fhpresent_test_original_control"));
            _original = Marshal.GetDelegateForFunctionPointer<Entry>(NativeLibrary.GetExport(probe, "fhpresent_test_original"));
            for (int slot = 0; slot < 3; slot++)
                if (slot != first) Check(invoke(pointers[slot]) == 1 && Calls.All(count => count == 0), "first binding leaves slot unbound " + slot);
            for (int slot = 0; slot < 3; slot++) if (slot != first) Create(slot);
            Check(Marshal.GetFunctionPointerForDelegate(entry!) == pointer, "snapshot delegate round trip keeps native export address");
            Check(Marshal.GetFunctionPointerForDelegate(startupEntry!) == startupPointer, "startup delegate round trip keeps native export address");
            Check(Marshal.GetFunctionPointerForDelegate(padEntry!) == padPointer, "pad delegate round trip keeps native export address");
            Check(!NativeSnapshotBridge.TryCreatePadLatch<PadEntry>(OtherDispatch, out _), "pad rejects different callback");
            Check(!NativeSnapshotBridge.TryCreatePadLatch<Entry>((NativeSnapshotBridge.Dispatch)padReference.Target!, out _), "pad rejects changed entry type");
            Check(!NativeSnapshotBridge.TryCreate<Entry>(OtherDispatch, out _), "snapshot rejects different callback");
            Check(!NativeSnapshotBridge.TryCreateStartup<StartupEntry>(OtherDispatch, out _), "startup rejects different callback");
            Check(!NativeSnapshotBridge.TryCreate<StartupEntry>((NativeSnapshotBridge.Dispatch)callbackReference.Target!, out _), "snapshot rejects changed entry type");
            Check(!NativeSnapshotBridge.TryCreateStartup<Entry>((NativeSnapshotBridge.Dispatch)startupReference.Target!, out _), "startup rejects changed entry type");
            foreach (var slot in new[] { ("fhpresent_bind", callbackReference), ("fhpresent_startup_bind", startupReference), ("fhpresent_pad_bind", padReference) })
            {
                var bind = Marshal.GetDelegateForFunctionPointer<Bind>(NativeLibrary.GetExport(library, slot.Item1));
                Check(bind(0) == 0, "native null binding rejected for " + slot.Item1);
                Check(bind(Marshal.GetFunctionPointerForDelegate((NativeSnapshotBridge.Dispatch)slot.Item2.Target!)) == 1, "native identical thunk accepted for " + slot.Item1);
                Check(bind(Marshal.GetFunctionPointerForDelegate(new NativeSnapshotBridge.Dispatch(OtherDispatch))) == 0, "native replacement thunk rejected for " + slot.Item1);
            }
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Check(callbackReference.IsAlive && startupReference.IsAlive && padReference.IsAlive, "factory roots all three instance callbacks across full GC");
            Check(invoke(pointer) == 1, "snapshot preserves native caller state around managed callback");
            Check(Calls[0] == 1 && Calls[1] == 0 && CaptureOk[0], "snapshot captures its caller without invoking startup");
            Check(invoke(startupPointer) == 1, "startup preserves native caller state around managed callback");
            Check(Calls[0] == 1 && Calls[1] == 1 && CaptureOk[1], "startup captures its caller without invoking snapshot");
            Check(invoke(padPointer) == 1, "pad preserves native caller state around managed callback");
            Check(Calls[0] == 1 && Calls[1] == 1 && Calls[2] == 1 && CaptureOk[2], "pad captures its caller without invoking other slots");
            for (int slot = 0; slot < 3; ++slot)
            {
                Console.WriteLine($"Slot {slot} FP controls inside chained dummy void original: 0x{ObservedControl[slot]:x8}; native caller: 0x067f3f80.");
                Check((ObservedControl[slot] & 0xffffffc0) == 0x067f3f80, "native work inherits caller FP controls for slot " + slot);
            }
            GC.KeepAlive(entry);
            GC.KeepAlive(startupEntry);
            GC.KeepAlive(padEntry);
            Console.WriteLine("fhpresent .NET 10 x86 probe: three isolated slots, export identity, factory validation, GC lifetime, caller capture and native state preservation passed.");
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
