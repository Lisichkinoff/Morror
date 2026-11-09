using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Morror
{
    public static class MirrorWindow
    {
        public static bool SetPinned(Process process, bool pinned)
        {
            if (process == null || process.HasExited) return false;
            process.Refresh();
            IntPtr handle = process.MainWindowHandle;
            if (handle == IntPtr.Zero) return false;
            if (!SetWindowPos(handle, new IntPtr(pinned ? -1 : -2), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0200))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return true;
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    }
}
