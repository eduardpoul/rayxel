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
}
