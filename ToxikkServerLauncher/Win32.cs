using System;
using System.Runtime.InteropServices;

namespace ToxikkServerLauncher
{
  public static class Win32
  {
    public const int SW_SHOWNORMAL = 1;
    public const int WM_CHAR = 0x102;

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr handle, int flags);

    [DllImport("user32.dll")]
    public static extern bool SetActiveWindow(IntPtr handle);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr handle);

    [DllImport("user32.dll")]
    public static extern bool SetCapture(IntPtr handle);

    [DllImport("user32.dll")]
    public static extern bool SetFocus(IntPtr handle);

    [DllImport("user32.dll")]
    public static extern bool SendMessage(IntPtr handle, int msg, int wparam, int lparam);

    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr handle, int msg, int wparam, int lparam);



    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    public static extern bool FreeConsole();

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GenerateConsoleCtrlEvent(CtrlTypes dwCtrlEvent, uint dwProcessGroupId);

    [DllImport("kernel32.dll")]
    public static extern bool SetConsoleCtrlHandler(ConsoleCtrlDelegate handlerRoutine, bool add);

    public delegate Boolean ConsoleCtrlDelegate(CtrlTypes CtrlType);


    // Enumerated type for the control messages sent to the handler routine
    public enum CtrlTypes : uint
    {
      CTRL_C_EVENT = 0,
      CTRL_BREAK_EVENT,
      CTRL_CLOSE_EVENT,
      CTRL_LOGOFF_EVENT = 5,
      CTRL_SHUTDOWN_EVENT
    }

  }
}
