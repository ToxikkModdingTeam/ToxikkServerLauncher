using System;
using System.Runtime.InteropServices;

namespace ToxikkServerLauncher
{
  public static class Win32
  {
    public const int SW_SHOWNORMAL = 1;

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
  }
}
