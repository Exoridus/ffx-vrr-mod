using System.Runtime.InteropServices;
using Fahrenheit.Mods.Vrr;

internal static unsafe class StartupTrampolineProbe
{
    private enum Value { Entry, EntryTrampoline, Cookie, CookieOriginal, Continuations, Cookies, Boots, Events, Patch, ContinuationFlags }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Entry();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Notify(int phase);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Setup(nint callback, Notify notify);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nuint Get(int field);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Mark(int value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Stop();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Invoke(nint entry);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    private static readonly StartupActivation Activation = new();
    private static Get _get = null!;
    private static Mark _mark = null!;
    private static Entry _original = null!;
    private static string _scenario = "";
    private static string? _error;
    private static int _captures;
    private static int _receipts;
    private static bool _matched;
    private static bool _terminalSentinel;
    private static nuint _returnAddress;

    internal static int Run(string scenario)
    {
        _scenario = scenario;
        Check(scenario is "good" or "false" or "missing", "known startup scenario");
        Check(NativeSnapshotBridge.TryCreateStartup<Entry>(Dispatch, out var bridge), "create real startup bridge");
        Check(NativeLibrary.TryLoad(Path.Combine(AppContext.BaseDirectory, "fhstartup-probe.dll"), out var library), "load actual MinHook startup probe");
        _get = Export<Get>(library, "startup_probe_get");
        _mark = Export<Mark>(library, "startup_probe_mark");
        var setup = Export<Setup>(library, "startup_probe_setup");
        var stop = Export<Stop>(library, "startup_probe_stop");
        Notify notify = Observe;
        Check(setup(Marshal.GetFunctionPointerForDelegate(bridge!), notify) == 1, "create and enable real entry and callee MinHook detours");
        try
        {
            _original = Marshal.GetDelegateForFunctionPointer<Entry>((nint)GetValue(Value.CookieOriginal));
            var stateProbe = NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "fhpresent-probe.dll"));
            var invoke = Export<Invoke>(stateProbe, "fhpresent_test_invoke");
            Check(invoke((nint)GetValue(Value.Entry)) == 1, "entry detour/trampoline/callee bridge preserves caller ABI and full tested FP state");
            Check(_error is null, "managed boundary: " + _error);
            Check(_captures == 1 && _matched, "exact relocated caller/slot accepted once");
            Check(_returnAddress == GetValue(Value.EntryTrampoline) + 5 && _returnAddress != GetValue(Value.Entry) + 5,
                "return lies inside relocated entry trampoline rather than original entry");
            Console.WriteLine($"Relocated entry: original=0x{GetValue(Value.Entry):x8}, trampoline=0x{GetValue(Value.EntryTrampoline):x8}, return=0x{_returnAddress:x8}, cookie=0x{GetValue(Value.Cookie):x8}, window={Convert.ToHexString(new ReadOnlySpan<byte>((void*)(_returnAddress - 5), 10))}.");
            Check(GetValue(Value.Cookies) == 1 && GetValue(Value.Continuations) == 1 && GetValue(Value.Boots) == 1,
                "boot, real original cookie, and native continuation each run exactly once");
            Check(_receipts == (scenario == "missing" ? 0 : 1), "only exact mock owned receipt is consulted when present");
            bool expectedArm = scenario == "good";
            Check(Activation.IsArmed == expectedArm && (GetValue(Value.Patch) != 0) == expectedArm, "apply and arm require positive exact own receipt");
            Check(GetValue(Value.ContinuationFlags) == (expectedArm ? 3u : 0u), "native continuation observes expected arm and patch state");
            Check(_terminalSentinel != expectedArm, "refused activation reaches sentinel terminal policy without process termination");
            int[] expected = expectedArm ? [1, 2, 3, 4, 5, 6] : [1, 2, 3, 7, 6];
            Check(GetValue(Value.Events) == (nuint)expected.Length, "exact startup event count");
            for (int i = 0; i < expected.Length; ++i)
                Check(_get(100 + i) == (nuint)expected[i], "boot/original/verify/apply/arm/continuation event order at " + i);
            Activation.Stop();
            Check(!Activation.IsArmed, "stop withdraws startup arm");
            Console.WriteLine($"Actual MinHook 1.3.4 startup trampoline ({scenario}): relocated caller, original-once, own receipt, apply/arm order, continuation and native ABI passed.");
            return 0;
        }
        finally
        {
            Check(stop() == 1, "remove both owned test hooks and free owned entry stub");
            GC.KeepAlive(notify);
            GC.KeepAlive(bridge);
        }
    }

    private static int Observe(int phase)
    {
        try
        {
            if (phase == 1)
            {
                Check(GetValue(Value.Cookies) == 0 && GetValue(Value.Continuations) == 0, "boot precedes original entry trampoline");
                Activation.Prepare(GetCurrentThreadId());
                return 0;
            }
            Check(phase == 2, "known continuation observation");
            return (Activation.IsArmed ? 1 : 0) | (GetValue(Value.Patch) != 0 ? 2 : 0);
        }
        catch (Exception error) { _error = error.Message; return 0; }
    }

    private static void Dispatch(nuint returnAddress, nuint returnSlot)
    {
        try
        {
            ++_captures;
            _returnAddress = returnAddress;
            var window = new ReadOnlySpan<byte>((void*)(returnAddress - 5), 10);
            _matched = StartupCaller.Matches(window, returnAddress, *(nuint*)returnSlot,
                GetValue(Value.Cookie), GetValue(Value.Entry) + 5);
            Func<bool>? receipt = _scenario == "missing" ? null : () =>
            {
                ++_receipts;
                Check(GetValue(Value.Cookies) == 1, "receipt follows original cookie");
                return _scenario == "good";
            };
            bool armed = Activation.Invoke(GetCurrentThreadId(), _matched, _original.Invoke,
                () => { _mark(3); return receipt?.Invoke() == true; }, () =>
                {
                    Check(GetValue(Value.Cookies) == 1 && !Activation.IsArmed, "apply follows original and precedes arm");
                    _mark(4);
                });
            if (armed) _mark(5);
            else { _terminalSentinel = true; _mark(7); }
        }
        catch (Exception error) { _error = error.Message; _terminalSentinel = true; }
    }

    private static T Export<T>(nint library, string name) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
    private static nuint GetValue(Value field) => _get((int)field);
    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException("CHECK failed: " + message);
    }
}
