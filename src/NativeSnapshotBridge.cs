using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Fahrenheit.Mods.Vrr;

internal static class NativeSnapshotBridge
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void Dispatch(nuint returnAddress, nuint returnSlot);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Abi();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Bind(nint callback);

    private static readonly object Gate = new();
    private sealed class Slot
    {
        internal Dispatch? Callback;
        internal Delegate? Entry;
    }

    private static readonly Slot Snapshot = new();
    private static readonly Slot Startup = new();
    private static nint _library;

    internal static bool TryCreate<T>(Dispatch dispatch, out T? entry) where T : Delegate
        => TryCreate(dispatch, Snapshot, false, out entry);

    internal static bool TryCreateStartup<T>(Dispatch dispatch, out T? entry) where T : Delegate
        => TryCreate(dispatch, Startup, true, out entry);

    private static bool TryCreate<T>(Dispatch dispatch, Slot slot, bool startup, out T? entry) where T : Delegate
    {
        entry = null;
        var invoke = typeof(T).GetMethod("Invoke");
        if (!OperatingSystem.IsWindows() || IntPtr.Size != 4 || dispatch is null
            || invoke?.ReturnType != typeof(void) || invoke.GetParameters().Length != 0
            || typeof(T).GetCustomAttribute<UnmanagedFunctionPointerAttribute>()?.CallingConvention != CallingConvention.Cdecl)
            return false;

        lock (Gate)
        {
            if (slot.Callback is not null)
            {
                if (slot.Callback != dispatch || slot.Entry is not T existing) return false;
                entry = existing;
                return true;
            }

            nint library = _library;
            try
            {
                string? directory = Path.GetDirectoryName(typeof(NativeSnapshotBridge).Assembly.Location);
                if (string.IsNullOrEmpty(directory)
                    || library == 0 && !NativeLibrary.TryLoad(Path.Combine(directory, "fhpresent.dll"), out library)
                    || !NativeLibrary.TryGetExport(library, "fhpresent_abi", out var abiPointer)
                    || !NativeLibrary.TryGetExport(library, "fhpresent_bind", out var bindPointer)
                    || !NativeLibrary.TryGetExport(library, "fhpresent_entry", out var entryPointer)
                    || !NativeLibrary.TryGetExport(library, "fhpresent_startup_bind", out var startupBindPointer)
                    || !NativeLibrary.TryGetExport(library, "fhpresent_startup_entry", out var startupEntryPointer)
                    || Marshal.GetDelegateForFunctionPointer<Abi>(abiPointer)() != 2)
                    return false;

                T candidate = Marshal.GetDelegateForFunctionPointer<T>(startup ? startupEntryPointer : entryPointer);
                var bind = Marshal.GetDelegateForFunctionPointer<Bind>(startup ? startupBindPointer : bindPointer);
                if (bind(Marshal.GetFunctionPointerForDelegate(dispatch)) != 1) return false;

                // The native callback can outlive a module instance or a failed hook installation.
                _library = library;
                slot.Callback = dispatch;
                slot.Entry = candidate;
                entry = candidate;
                return true;
            }
            catch (Exception error) when (error is DllNotFoundException or BadImageFormatException
                or EntryPointNotFoundException or ArgumentException or MarshalDirectiveException or IOException)
            {
                return false;
            }
            finally
            {
                if (library != 0 && _library != library) NativeLibrary.Free(library);
            }
        }
    }
}
