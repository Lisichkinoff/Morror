using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;

namespace Morror
{
    // Used ONLY in the explicitly selected Windows touch mode. No mouse emulation fallback.
    public sealed class ScreenTouchInjection : IDisposable
    {
        readonly Rectangle bounds;
        readonly Dictionary<int, Point> previous = new Dictionary<int, Point>();
        bool initialized;
        IntPtr mouseHook;
        HookCallback callback;
        long touchDeadline;
        internal int SuppressedMoves;
        internal Point? LastOtherMouseMove;
        public ScreenTouchInjection(Rectangle bounds) { this.bounds = bounds; }
        public void Apply(ScreenTouchFrame frame)
        {
            if (!initialized)
            {
                callback=FilterTouchMouseMove;
                mouseHook=SetWindowsHookEx(14,callback,GetModuleHandle(null),0);
                if(mouseHook==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());
                if (!InitializeTouchInjection(10, 3)) {int error=Marshal.GetLastWin32Error();Dispose();throw new Win32Exception(error);} initialized = true;
            }
            if (frame.type == "cancel") { Cancel(); return; }
            var current = frame.points.ToDictionary(p => p.id, p => SecondScreenProtocol.MapToScreen(p, bounds));
            var input = new List<TouchInfo>();
            foreach (var point in current) input.Add(Make(point.Key, point.Value, previous.ContainsKey(point.Key) ? 0x20006u : 0x10006u));
            foreach (var point in previous.Where(p => !current.ContainsKey(p.Key))) input.Add(Make(point.Key, point.Value, 0x40000u));
            System.Threading.Interlocked.Exchange(ref touchDeadline,DateTime.UtcNow.AddMilliseconds(500).Ticks);
            if (input.Count > 0 && !InjectTouchInput((uint)input.Count, input.ToArray())) { int error = Marshal.GetLastWin32Error(); Cancel(); throw new Win32Exception(error); }
            previous.Clear(); foreach (var point in current) previous.Add(point.Key, point.Value);
        }
        public void Cancel()
        {
            if(previous.Count>0)System.Threading.Interlocked.Exchange(ref touchDeadline,DateTime.UtcNow.AddMilliseconds(500).Ticks);
            if (previous.Count > 0) InjectTouchInput((uint)previous.Count, previous.Select(p => Make(p.Key, p.Value, 0x48000u)).ToArray());
            previous.Clear();
        }
        // Windows labels mouse messages promoted from touch with the MI_WP signature.
        // Suppress ONLY their movement, ONLY inside our monitor, ONLY during our recent injection.
        // Hardware mouse events and promoted button events continue through the normal chain.
        IntPtr FilterTouchMouseMove(int code,IntPtr message,IntPtr data)
        {
            if(code>=0 && message.ToInt64()==0x200 && DateTime.UtcNow.Ticks<System.Threading.Interlocked.Read(ref touchDeadline))
            {
                var mouse=(MouseInfo)Marshal.PtrToStructure(data,typeof(MouseInfo));
                uint extra=unchecked((uint)mouse.extra.ToUInt64());
                if((extra&0xffffff80u)==0xff515780u && bounds.Contains(mouse.point.x,mouse.point.y)) {SuppressedMoves++;return new IntPtr(1);}
                if((extra&0xffffff80u)!=0xff515780u)LastOtherMouseMove=new Point(mouse.point.x,mouse.point.y);
            }
            return CallNextHookEx(mouseHook,code,message,data);
        }
        public void Dispose(){Cancel();if(mouseHook!=IntPtr.Zero){UnhookWindowsHookEx(mouseHook);mouseHook=IntPtr.Zero;}}
        delegate IntPtr HookCallback(int code,IntPtr message,IntPtr data);
        [StructLayout(LayoutKind.Sequential)] struct MouseInfo{public NativePoint point;public uint mouseData,flags,time;public UIntPtr extra;}
        static TouchInfo Make(int id, Point p, uint flags)
        {
            return new TouchInfo { pointer = new PointerInfo { pointerType = 2, pointerId = (uint)id, pointerFlags = flags, location = new NativePoint { x = p.X, y = p.Y } }, touchMask = 1, contact = new NativeRect { left = p.X - 2, top = p.Y - 2, right = p.X + 2, bottom = p.Y + 2 } };
        }
        [StructLayout(LayoutKind.Sequential)] struct NativePoint { public int x, y; }
        [StructLayout(LayoutKind.Sequential)] struct NativeRect { public int left, top, right, bottom; }
        [StructLayout(LayoutKind.Sequential)] struct PointerInfo
        {
            public uint pointerType, pointerId, frameId, pointerFlags;
            public IntPtr sourceDevice, hwndTarget;
            public NativePoint location, himetricLocation, locationRaw, himetricLocationRaw;
            public uint time, historyCount;
            public int inputData;
            public uint keyStates;
            public ulong performanceCount;
            public int buttonChangeType;
        }
        [StructLayout(LayoutKind.Sequential)] struct TouchInfo
        {
            public PointerInfo pointer;
            public uint touchFlags, touchMask;
            public NativeRect contact, contactRaw;
            public uint orientation, pressure;
        }
        [DllImport("user32.dll", SetLastError = true)] static extern bool InitializeTouchInjection(uint maxCount, uint feedbackMode);
        [DllImport("user32.dll", SetLastError = true)] static extern bool InjectTouchInput(uint count, [In] TouchInfo[] contacts);
        [DllImport("user32.dll",SetLastError=true)]static extern IntPtr SetWindowsHookEx(int id,HookCallback callback,IntPtr module,uint thread);
        [DllImport("user32.dll")]static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")]static extern IntPtr CallNextHookEx(IntPtr hook,int code,IntPtr message,IntPtr data);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]static extern IntPtr GetModuleHandle(string name);
    }
}
