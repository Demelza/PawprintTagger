using System.Runtime.InteropServices;

namespace PawprintTagger.Gtk;

/// <summary>Own native idle callbacks explicitly, including their managed delegate lifetimes.</summary>
internal static class GtkDispatcher
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SourceCallback(IntPtr data);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DestroyCallback(IntPtr data);
    private static readonly SourceCallback Invoke = Run;
    private static readonly DestroyCallback Release = data => GCHandle.FromIntPtr(data).Free();

    [DllImport("libglib-2.0.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern uint g_idle_add_full(int priority, SourceCallback callback, IntPtr data, DestroyCallback notify);

    public static void Post(Action action)
    {
        var handle = GCHandle.Alloc(action);
        g_idle_add_full(200, Invoke, GCHandle.ToIntPtr(handle), Release);
    }

    private static int Run(IntPtr data)
    {
        try { ((Action)GCHandle.FromIntPtr(data).Target!)(); }
        catch (Exception ex) { Console.Error.WriteLine(ex); }
        return 0;
    }
}
