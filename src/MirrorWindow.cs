using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Morror
{
    public static class MirrorWindow
    {
        static readonly uint FullscreenMessage = RegisterWindowMessageW("Morror.Scrcpy.ToggleFullscreen");
        static readonly uint TopmostMessage = RegisterWindowMessageW("Morror.Scrcpy.SetAlwaysOnTop");

        internal static bool ToggleFullscreen(IntPtr window)
        {
            return window != IntPtr.Zero && FullscreenMessage != 0
                && PostMessageW(window, FullscreenMessage, IntPtr.Zero, IntPtr.Zero);
        }

        public static bool SetPinned(Process process, bool pinned)
        {
            if (process == null || process.HasExited) return false;
            process.Refresh();
            IntPtr handle = process.MainWindowHandle;
            if (handle == IntPtr.Zero) return false;
            if (!SetWindowPos(handle, new IntPtr(pinned ? -1 : -2), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0200))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            // Keep SDL's window flag in sync so mode changes do not restore the launch state.
            if (TopmostMessage != 0 && !PostMessageW(handle, TopmostMessage, new IntPtr(pinned ? 1 : 0), IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return true;
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        static extern uint RegisterWindowMessageW(string name);
        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        static extern bool PostMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    }
}
