using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
// Run against the packaged Morror.iPhone.exe with no other iPhone window open.
// Tests real process exit, which an OnExplicitShutdown WPF fixture cannot catch.
class IPhoneProcessTests
{
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
    static void Until(Func<bool> done) { for(int i=0;i<150;i++){if(done())return; Thread.Sleep(50);} throw new Exception("Window activation timed out"); }
    static int Main(string[] args)
    {
        try
        {
            for(int run=0;run<2;run++)
            using(var first=Process.Start(new ProcessStartInfo(args[0]) { UseShellExecute=false }))
            {
                try
                {
                    Until(()=> {first.Refresh(); return first.MainWindowHandle!=IntPtr.Zero;});
                    var hwnd=first.MainWindowHandle;
                    ShowWindow(hwnd,6);
                    Until(()=>IsIconic(hwnd));
                    using(var second=Process.Start(new ProcessStartInfo(args[0]) { UseShellExecute=false }))
                    {
                        if(!second.WaitForExit(5000)||second.ExitCode!=0)throw new Exception("Second instance did not hand off");
                    }
                    Until(()=>!IsIconic(hwnd));
                    Console.WriteLine("PASS open/restore existing iPhone window, cycle "+run);
                }
                finally
                {
                    if(!first.HasExited){first.CloseMainWindow(); if(!first.WaitForExit(8000)){first.Kill(); throw new Exception("Window failed to close");}}
                }
            }
            return 0;
        }
        catch(Exception e){Console.WriteLine(e);return 1;}
    }
}
