using System.ComponentModel;
using System.Runtime.InteropServices;

namespace TimecodeSyncPlayer.Output;

internal enum VblankWaitResult { Reached, Cancelled }

/// <summary>
/// worker 所有の waitable timer。高分解能（CREATE_WAITABLE_TIMER_HIGH_RESOLUTION）を要求し、
/// 取れなければ通常タイマーへフォールバックする。worker 自身がループ前に作成し、終了後に閉じる。
/// 試作 scripts/GpuOutputProbe の VblankWaitTimer を移植。
/// </summary>
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

    // 停止 handle が index 0 で優先。フォールバック budget（整数ms + 2）の満了も到達として扱う。
    public unsafe VblankWaitResult WaitUntilOrStop(WaitHandle stop, long nowQpc, long dueQpc, long frequency)
    {
        long remaining = Math.Max(1, (dueQpc - nowQpc) * 10_000_000 / frequency);
        long due = -remaining;
        if (!Native.SetWaitableTimer(handle, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetWaitableTimer failed.");
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

internal static partial class Native
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint desiredAccess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period, IntPtr completionRoutine, IntPtr argument, [MarshalAs(UnmanagedType.Bool)] bool resume);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern unsafe uint WaitForMultipleObjects(uint count, IntPtr* handles, [MarshalAs(UnmanagedType.Bool)] bool waitAll, uint milliseconds);
}
