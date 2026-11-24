using System.Runtime.InteropServices;
using System.Windows;

namespace AutomatiskSportTV.App;

public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\AutomatiskSportTV_SingleInstance_Mutex";
    private Mutex? _singleInstanceMutex;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindow(string? lpClassName, string lpWindowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            // Bring existing TV window to front so two instances never lock the same WebView2 profiles
            var existingHwnd = FindWindow(null, "MatchFlow TV");
            if (existingHwnd != nint.Zero)
            {
                ShowWindow(existingHwnd, 9); // SW_RESTORE
                SetForegroundWindow(existingHwnd);
            }
            Shutdown();
            return;
        }

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_singleInstanceMutex is not null)
        {
            try { _singleInstanceMutex.ReleaseMutex(); } catch { }
            _singleInstanceMutex.Dispose();
        }
        base.OnExit(e);
    }
}
