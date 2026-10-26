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
    private static Dispatch? _dispatch;
    private static Delegate? _entry;
    private static nint _library;

    internal static bool TryCreate<T>(Dispatch dispatch, out T? entry) where T : Delegate
    {
        entry = null;
        var invoke = typeof(T).GetMethod("Invoke");
        if (!OperatingSystem.IsWindows() || IntPtr.Size != 4 || dispatch is null
            || invoke?.ReturnType != typeof(void) || invoke.GetParameters().Length != 0
            || typeof(T).GetCustomAttribute<UnmanagedFunctionPointerAttribute>()?.CallingConvention != CallingConvention.Cdecl)
            return false;

        lock (Gate)
        {
            if (_dispatch is not null)
            {
                if (_dispatch != dispatch || _entry is not T existing) return false;
                entry = existing;
                return true;
            }

            nint library = 0;
            try
            {
                string? directory = Path.GetDirectoryName(typeof(NativeSnapshotBridge).Assembly.Location);
                if (string.IsNullOrEmpty(directory)
                    || !NativeLibrary.TryLoad(Path.Combine(directory, "fhpresent.dll"), out library)
                    || !NativeLibrary.TryGetExport(library, "fhpresent_abi", out var abiPointer)
                    || !NativeLibrary.TryGetExport(library, "fhpresent_bind", out var bindPointer)
                    || !NativeLibrary.TryGetExport(library, "fhpresent_entry", out var entryPointer)
                    || Marshal.GetDelegateForFunctionPointer<Abi>(abiPointer)() != 1)
                    return false;

                T candidate = Marshal.GetDelegateForFunctionPointer<T>(entryPointer);
                var bind = Marshal.GetDelegateForFunctionPointer<Bind>(bindPointer);
                if (bind(Marshal.GetFunctionPointerForDelegate(dispatch)) != 1) return false;

                // The native callback can outlive a module instance or a failed hook installation.
                _library = library;
                _dispatch = dispatch;
                _entry = candidate;
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
