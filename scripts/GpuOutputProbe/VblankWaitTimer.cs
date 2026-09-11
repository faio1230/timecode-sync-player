using System.ComponentModel;
using System.Runtime.InteropServices;

namespace GpuOutputProbe;

// Worker-owned waitable timer: the GPU worker's vblank target wait and each Loop's idle wait (one per worker). High-resolution
// when the OS grants it; otherwise a normal timer (recorded in the manifest). Created by its worker before the run loop and
// closed after it ends; never waited on by another thread.
internal sealed class VblankWaitTimer : IDisposable
{
    private const uint CreateWaitableTimerHighResolution = 0x2, TimerAllAccess = 0x1F0003;
    private readonly IntPtr handle;
    public bool HighResolution { get; }
    public VblankWaitTimer()
    {
        handle = Native.CreateWaitableTimerExW(IntPtr.Zero, null, CreateWaitableTimerHighResolution, TimerAllAccess);
        HighResolution = handle != IntPtr.Zero;
        if (!HighResolution) handle = Native.CreateWaitableTimerExW(IntPtr.Zero, null, 0, TimerAllAccess);
        if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateWaitableTimerExW failed.");
    }
    // Stop handle at index 0 wins. The fallback WaitForMultipleObjects budget (whole ms + 2) also counts as reached.
    public unsafe VblankWaitResult WaitUntilOrStop(WaitHandle stop, long nowQpc, long dueQpc, long frequency)
    {
        long remaining = Math.Max(1, (dueQpc - nowQpc) * 10_000_000 / frequency); // 100 ns units, relative.
        long due = -remaining;
        if (!Native.SetWaitableTimer(handle, ref due, 0, IntPtr.Zero, IntPtr.Zero, false)) throw new Win32Exception(Marshal.GetLastWin32Error(), "SetWaitableTimer failed.");
        uint fallbackMs = (uint)Math.Min(int.MaxValue, remaining / 10_000 + 2);
        var stopHandle = stop.SafeWaitHandle;
        bool referenced = false;
        try
        {
            stopHandle.DangerousAddRef(ref referenced);
            IntPtr* handles = stackalloc IntPtr[2] { stopHandle.DangerousGetHandle(), handle };
            uint result = Native.WaitForMultipleObjects(2, handles, false, fallbackMs);
            return result switch
            {
                0 => VblankWaitResult.Cancelled,
                1 or 258 => VblankWaitResult.Reached,
                _ => throw new Win32Exception(Marshal.GetLastWin32Error(), $"WaitForMultipleObjects returned 0x{result:X8}.")
            };
        }
        finally { if (referenced) stopHandle.DangerousRelease(); }
    }
    public void Dispose() => Native.CloseHandle(handle);
}

// Loop idle wait policy: the timer is used whenever more than YieldMicroseconds remain; at or below that, Thread.Yield().
// No "-1 ms" truncation: the timer is set to the due time itself (100 ns units).
internal static class LoopIdleWait
{
    public const long YieldMicroseconds = 50;
    public static bool UseTimer(long nowQpc, long dueQpc, long frequency) => (dueQpc - nowQpc) * 1_000_000 > YieldMicroseconds * frequency;
}

internal static partial class Native
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint desiredAccess);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period, IntPtr completionRoutine, IntPtr argument, [MarshalAs(UnmanagedType.Bool)] bool resume);
}
