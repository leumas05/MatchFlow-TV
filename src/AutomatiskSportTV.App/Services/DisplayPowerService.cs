using System.Runtime.InteropServices;

namespace AutomatiskSportTV.App.Services;

/// <summary>
/// Provides Win32 display sleep prevention and cursor position polling for fullscreen TV playback.
/// </summary>
public static class DisplayPowerService
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [Flags]
    private enum ExecutionState : uint
    {
        EsContinuous = 0x80000000,
        EsDisplayRequired = 0x00000002,
        EsSystemRequired = 0x00000001
    }

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern uint SetThreadExecutionState(uint esFlags);

    [DllImport("user32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint lpPoint);

    public static void SetPowerMode(bool keepDisplayOn, bool keepSystemAwake)
    {
        var flags = ExecutionState.EsContinuous;
        if (keepDisplayOn)
        {
            flags |= ExecutionState.EsDisplayRequired;
        }
        if (keepSystemAwake)
        {
            flags |= ExecutionState.EsSystemRequired;
        }

        SetThreadExecutionState((uint)flags);
    }

    public static (int X, int Y) GetCursorPosition()
    {
        return GetCursorPos(out var pt) ? (pt.X, pt.Y) : (0, 0);
    }
}
