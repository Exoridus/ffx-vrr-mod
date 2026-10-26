using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Fahrenheit.Mods.Vrr;

internal static unsafe class Program
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Entry();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int WrongResult();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void WrongArguments(int value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void WrongConvention();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Bind(nint callback);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Invoke(nint entry);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nuint ExpectedReturn();
    private static int _calls;
    private static bool _captureOk;
    private static ExpectedReturn? _expectedReturn;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint ReadControl();
    private static ReadControl? _readControl;
    private static Entry? _original;
    private static uint _observedControl;

    private sealed class CallbackOwner
    {
        internal void Invoke(nuint address, nuint slot) => Dispatch(address, slot);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Entry CreateRootedEntry(out WeakReference callbackReference)
    {
        var dispatch = new NativeSnapshotBridge.Dispatch(new CallbackOwner().Invoke);
        callbackReference = new WeakReference(dispatch);
        Check(NativeSnapshotBridge.TryCreate<Entry>(dispatch, out var entry), "create native entry delegate");
        Check(NativeSnapshotBridge.TryCreate<Entry>(dispatch, out var duplicate) && ReferenceEquals(entry, duplicate), "same callback returns rooted entry");
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
            Check(!NativeSnapshotBridge.TryCreate<WrongResult>(Dispatch, out _), "reject non-void delegate");
            Check(!NativeSnapshotBridge.TryCreate<WrongArguments>(Dispatch, out _), "reject delegate arguments");
            Check(!NativeSnapshotBridge.TryCreate<WrongConvention>(Dispatch, out _), "reject non-cdecl delegate");
            Check(!NativeSnapshotBridge.TryCreate<Entry>(null!, out _), "reject null callback");
            if (args.Contains("--unavailable"))
            {
                Check(!NativeSnapshotBridge.TryCreate<Entry>(Dispatch, out _), "missing or invalid helper rejected");
                Console.WriteLine("fhpresent managed factory: unavailable helper safely rejected.");
                return 0;
            }
            var entry = CreateRootedEntry(out var callbackReference);
            var library = NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "fhpresent.dll"));
            var pointer = NativeLibrary.GetExport(library, "fhpresent_entry");
            Check(Marshal.GetFunctionPointerForDelegate(entry!) == pointer, "delegate round trip keeps native export address");
            Check(!NativeSnapshotBridge.TryCreate<Entry>(OtherDispatch, out _), "different callback rejected");
            var bind = Marshal.GetDelegateForFunctionPointer<Bind>(NativeLibrary.GetExport(library, "fhpresent_bind"));
            Check(bind(0) == 0, "native null binding rejected");
            Check(bind(Marshal.GetFunctionPointerForDelegate(new NativeSnapshotBridge.Dispatch(Dispatch))) == 0, "distinct delegate thunk rejected");

            var probe = NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "fhpresent-probe.dll"));
            var invoke = Marshal.GetDelegateForFunctionPointer<Invoke>(NativeLibrary.GetExport(probe, "fhpresent_test_invoke"));
            _expectedReturn = Marshal.GetDelegateForFunctionPointer<ExpectedReturn>(NativeLibrary.GetExport(probe, "fhpresent_test_expected_return"));
            _readControl = Marshal.GetDelegateForFunctionPointer<ReadControl>(NativeLibrary.GetExport(probe, "fhpresent_test_original_control"));
            _original = Marshal.GetDelegateForFunctionPointer<Entry>(NativeLibrary.GetExport(probe, "fhpresent_test_original"));
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Check(callbackReference.IsAlive, "factory roots instance callback across full GC");
            Check(invoke(pointer) == 1, "native caller state preserved around actual managed callback");
            Check(_calls == 1 && _captureOk, "capture reaches managed callback and return slot holds actual native return address");
            Console.WriteLine($"Native FP controls inside chained dummy void original: 0x{_observedControl:x8}; native caller: 0x067f3f80.");
            Check((_observedControl & 0xffffffc0) == 0x067f3f80, "native work inside managed dispatch inherits caller FP controls");
            GC.KeepAlive(entry);
            Console.WriteLine("fhpresent .NET 10 x86 probe: export identity, factory validation, GC lifetime, caller capture and native state preservation passed.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Dispatch(nuint address, nuint slot)
    {
        _original!();
        _observedControl = _readControl!();
        ++_calls;
        _captureOk = slot != 0 && *(nuint*)slot == address && address == _expectedReturn!();
        _ = Math.Sqrt(123.5);
        GC.Collect();
    }

    private static void OtherDispatch(nuint address, nuint slot) { }
}
