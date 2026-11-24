using System.Runtime.InteropServices;

namespace Rayxel.Native;

public static partial class Kernel
{
    [LibraryImport("kernel32.dll")]
    public static partial uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    /// <summary>
    /// Raises timer resolution so frame-loop waits are not rounded up to the default
    /// 15.6 ms tick, which would swamp everything we are trying to measure.
    /// </summary>
    [LibraryImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    public static partial uint TimeBeginPeriod(uint ms);

    [LibraryImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    public static partial uint TimeEndPeriod(uint ms);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", SetLastError = true)]
    private static partial IntPtr CreateWaitableTimerEx(IntPtr attributes, IntPtr name, uint flags, uint access);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWaitableTimer(IntPtr timer, in long dueTime, int period, IntPtr completion,
                                                 IntPtr argument, [MarshalAs(UnmanagedType.Bool)] bool resume);

    [LibraryImport("kernel32.dll")]
    private static unsafe partial uint WaitForMultipleObjects(uint count, IntPtr* handles,
                                                              [MarshalAs(UnmanagedType.Bool)] bool waitAll, uint milliseconds);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    private const uint HighResolution = 0x2;        // CREATE_WAITABLE_TIMER_HIGH_RESOLUTION
    private const uint TimerAllAccess = 0x1F0003;   // TIMER_ALL_ACCESS

    /// <summary>
    /// A timer good to a fraction of a millisecond, for waiting until a frame's moment.
    /// A sleep, even with the timer resolution raised, is good to a millisecond either way,
    /// which is a tenth of the time between two frames at four times sixty; spinning out the
    /// rest costs a core. Windows has had a high-resolution kind since 1803; where it is
    /// refused an ordinary one is made, and the caller spins a little longer.
    /// </summary>
    public sealed class PreciseTimer : IDisposable
    {
        private IntPtr _handle;

        /// <summary>True when the timer is the high-resolution kind.</summary>
        public bool HighResolutionTimer { get; }

        public PreciseTimer()
        {
            _handle = CreateWaitableTimerEx(IntPtr.Zero, IntPtr.Zero, HighResolution, TimerAllAccess);
            HighResolutionTimer = _handle != IntPtr.Zero;
            if (_handle == IntPtr.Zero) _handle = CreateWaitableTimerEx(IntPtr.Zero, IntPtr.Zero, 0, TimerAllAccess);
        }

        /// <summary>
        /// Waits until <paramref name="dueQpc"/> on the performance counter, or until
        /// <paramref name="other"/> is signalled, whichever is first. True when it was the other.
        /// </summary>
        public unsafe bool WaitUntil(long dueQpc, WaitHandle other)
        {
            long ticks = dueQpc - System.Diagnostics.Stopwatch.GetTimestamp();
            if (ticks <= 0) return other.WaitOne(0);

            // Relative, in 100 ns units: negative by the timer's own convention.
            long due = -(long)(ticks * (10_000_000.0 / System.Diagnostics.Stopwatch.Frequency));
            if (_handle == IntPtr.Zero || !SetWaitableTimer(_handle, in due, 0, IntPtr.Zero, IntPtr.Zero, false))
                return other.WaitOne((int)Math.Max(0, ticks * 1000 / System.Diagnostics.Stopwatch.Frequency));

            bool added = false;
            other.SafeWaitHandle.DangerousAddRef(ref added);
            try
            {
                IntPtr* handles = stackalloc IntPtr[2];
                handles[0] = other.SafeWaitHandle.DangerousGetHandle();
                handles[1] = _handle;
                // WAIT_OBJECT_0 is the other handle; anything else is the timer or a timeout.
                return WaitForMultipleObjects(2, handles, false, 1000) == 0;
            }
            finally
            {
                if (added) other.SafeWaitHandle.DangerousRelease();
            }
        }

        public void Dispose()
        {
            if (_handle != IntPtr.Zero) { CloseHandle(_handle); _handle = IntPtr.Zero; }
        }
    }
}
