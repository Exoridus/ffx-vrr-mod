using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Fahrenheit.Mods.Vrr;

internal static class Program
{
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint NativeEntry(nint argument);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateThread(nint attributes, nuint stackSize, NativeEntry entry, nint argument, uint flags, out uint threadId);
    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(nint handle, uint milliseconds);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    private static int Main(string[] arguments)
    {
        if (arguments is not ["--texture-cold-jit"]) return 2;
        Console.WriteLine($"pointer_size={IntPtr.Size}; native_stack_reserve=65536");
        MethodBase constructor = typeof(TextureFrameGpuBlender).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
        ExceptionDispatchInfo? failure = null;
        NativeEntry entry = _ =>
        {
            try
            {
                Console.WriteLine("PREPARE-BEGIN");
                RuntimeHelpers.PrepareMethod(constructor.MethodHandle);
                Console.WriteLine("PREPARE-PASS");
            }
            catch (Exception error) { failure = ExceptionDispatchInfo.Capture(error); }
            return 0;
        };
        nint thread = CreateThread(0, 65536, entry, 0, 0x10000, out _);
        if (thread == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if (WaitForSingleObject(thread, 15000) != 0) return 3;
            failure?.Throw();
        }
        finally { CloseHandle(thread); GC.KeepAlive(entry); }
        return 0;
    }
}
