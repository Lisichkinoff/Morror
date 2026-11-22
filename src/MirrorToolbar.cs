using System;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Threading;

namespace Morror
{
    internal sealed class MirrorToolbar : IDisposable
    {
        const int MouseActivate = 0x0021;
        const int PointerActivate = 0x024B;
        const int DoNotActivate = 3;
        const int ExtendedStyle = -20;
        const int NoActivate = 0x08000000;
        const uint EventForeground = 0x0003;
        const uint EventWindowDestroy = 0x8001;
        const uint EventWindowLocationChange = 0x800B;
        const uint WinEventOutOfContext = 0;
        const int WindowObject = 0;
        const int ChildSelf = 0;
        const uint KeepSizeOrderAndFocus = 0x0001 | 0x0004 | 0x0010;
        const int FullWidth = 198;
        const int CompactWidth = 146;
        readonly Window window;
        readonly IntPtr mirror;
        readonly IntPtr handle;
        readonly DispatcherTimer feedbackTimer;
        readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        readonly Func<CancellationToken, Task<string>> capture;
        readonly Action<string> logError;
        IntPtr mirrorEvents, foregroundEvents;
        GCHandle eventCallback;
        DispatcherOperation pendingPlacement;
        string feedback;
        bool pinned, capturing, disposed;

        Button Button(string name) { return (Button)window.FindName(name); }
        TextBlock Text(string name) { return (TextBlock)window.FindName(name); }

        internal MirrorToolbar(Window settings, IntPtr mirror, Action<bool> pin, Action stop,
            Func<CancellationToken, Task<string>> capture, Action<string> logError)
        {
            this.mirror = mirror;
            this.capture = capture;
            this.logError = logError;
            uint processId;
            uint threadId = GetWindowThreadProcessId(mirror, out processId);
            if (threadId == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            using (var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("MirrorToolbar.xaml"))
                window = (Window)XamlReader.Load(resource);
            window.Resources.MergedDictionaries.Add(settings.Resources);
            var interop = new WindowInteropHelper(window) { Owner = mirror };
            handle = interop.EnsureHandle();
            SetWindowLongW(handle, ExtendedStyle, GetWindowLongW(handle, ExtendedStyle) | NoActivate);
            HwndSource.FromHwnd(handle).AddHook(KeepMirrorFocused);
            Button("MirrorPin").Click += (sender, args) => pin(!pinned);
            Button("MirrorFullscreen").Click += (sender, args) =>
            {
                if (!MirrorWindow.ToggleFullscreen(mirror)) ShowFeedback("Не удалось переключить полный экран.");
            };
            Button("MirrorScreenshot").Click += async (sender, args) => await TakeScreenshot();
            Button("MirrorStop").Click += (sender, args) => stop();
            feedbackTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
            feedbackTimer.Tick += (sender, args) =>
            {
                feedbackTimer.Stop();
                if (capturing) return;
                feedback = null;
                RenderFeedback();
            };
            WinEventCallback callback = OnWindowEvent;
            eventCallback = GCHandle.Alloc(callback);
            mirrorEvents = SetWinEventHook(EventWindowDestroy, EventWindowLocationChange,
                IntPtr.Zero, callback, processId, threadId, WinEventOutOfContext);
            foregroundEvents = SetWinEventHook(EventForeground, EventForeground,
                IntPtr.Zero, callback, 0, 0, WinEventOutOfContext);
            if (mirrorEvents == IntPtr.Zero || foregroundEvents == IntPtr.Zero)
            {
                Dispose();
                throw new InvalidOperationException("Не удалось подписаться на события окна трансляции.");
            }
            QueuePlacement();
        }

        internal void SetPinned(bool value)
        {
            pinned = value;
            var button = Button("MirrorPin");
            string label = pinned ? "Открепить окно" : "Закрепить поверх остальных окон";
            button.Content = pinned ? "\uE77A" : "\uE718";
            button.ToolTip = label;
            AutomationProperties.SetName(button, label);
        }

        internal void SetFps(int? framesPerSecond)
        {
            Text("MirrorFps").Text = framesPerSecond.HasValue ? framesPerSecond.Value + " FPS" : "— FPS";
        }

        async Task TakeScreenshot()
        {
            if (capturing || disposed) return;
            capturing = true;
            var button = Button("MirrorScreenshot");
            button.IsEnabled = false;
            ShowFeedback("Получаем снимок…");
            try
            {
                string result = await capture(lifetime.Token);
                if (!disposed) ShowFeedback(result);
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                if (!disposed)
                {
                    logError(error.Message);
                    ShowFeedback("Не удалось получить снимок. Подробности в журнале.");
                }
            }
            finally
            {
                capturing = false;
                if (disposed) lifetime.Dispose();
                else button.IsEnabled = true;
            }
        }

        void ShowFeedback(string message)
        {
            feedback = message;
            feedbackTimer.Stop();
            feedbackTimer.Start();
            RenderFeedback();
        }

        void RenderFeedback()
        {
            var text = Text("MirrorFeedback");
            if (text.Text != feedback) text.Text = feedback;
            text.Visibility = string.IsNullOrEmpty(feedback) ? Visibility.Collapsed : Visibility.Visible;
        }

        void OnWindowEvent(IntPtr hook, uint windowEvent, IntPtr source, int objectId, int childId,
            uint threadId, uint eventTime)
        {
            if (disposed) return;
            bool foregroundChanged = windowEvent == EventForeground && (source == mirror || source == handle || window.IsVisible);
            bool mirrorChanged = hook == mirrorEvents && source == mirror && objectId == WindowObject && childId == ChildSelf;
            if (foregroundChanged || mirrorChanged) QueuePlacement();
        }

        void QueuePlacement()
        {
            if (pendingPlacement != null) return;
            pendingPlacement = window.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
            {
                pendingPlacement = null;
                if (!disposed) PlaceOnMirror();
            }));
        }

        void PlaceOnMirror()
        {
            var origin = new NativePoint();
            NativeRect client;
            if (!IsWindowVisible(mirror) || IsIconic(mirror)
                || !GetClientRect(mirror, out client) || !ClientToScreen(mirror, ref origin))
            {
                if (window.IsVisible) window.Hide();
                return;
            }
            IntPtr foreground = GetForegroundWindow();
            bool show = foreground == mirror || foreground == handle;
            if (!show && window.IsVisible) window.Hide();
            double scale = GetDpiForWindow(handle) / 96.0;
            if (scale == 0) scale = 1;
            const int margin = 8;
            bool compact = client.Right < (FullWidth + 2 * margin) * scale;
            int width = compact ? CompactWidth : FullWidth;
            Text("MirrorFps").Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            window.Width = width;
            int x = origin.X + Math.Max(0, client.Right - (int)Math.Ceiling((width + margin) * scale));
            int y = origin.Y + (int)Math.Ceiling(margin * scale);
            if (show && !window.IsVisible)
            {
                SetWindowPos(handle, IntPtr.Zero, x, y, 0, 0, KeepSizeOrderAndFocus);
                window.Show();
            }
            SetWindowPos(handle, IntPtr.Zero, x, y, 0, 0, KeepSizeOrderAndFocus);
        }

        static IntPtr KeepMirrorFocused(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message != MouseActivate && message != PointerActivate) return IntPtr.Zero;
            handled = true;
            return new IntPtr(DoNotActivate);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (pendingPlacement != null) pendingPlacement.Abort();
            if (mirrorEvents != IntPtr.Zero) UnhookWinEvent(mirrorEvents);
            if (foregroundEvents != IntPtr.Zero) UnhookWinEvent(foregroundEvents);
            if (eventCallback.IsAllocated) eventCallback.Free();
            feedbackTimer.Stop();
            lifetime.Cancel();
            if (!capturing) lifetime.Dispose();
            window.Close();
        }

        [StructLayout(LayoutKind.Sequential)]
        struct NativePoint { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)]
        struct NativeRect { public int Left, Top, Right, Bottom; }
        delegate void WinEventCallback(IntPtr hook, uint windowEvent, IntPtr hwnd, int objectId, int childId,
            uint threadId, uint eventTime);
        [DllImport("user32.dll", SetLastError = true)]
        static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
        [DllImport("user32.dll")]
        static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventCallback callback,
            uint processId, uint threadId, uint flags);
        [DllImport("user32.dll")]
        static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")]
        static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")]
        static extern bool GetClientRect(IntPtr hwnd, out NativeRect rect);
        [DllImport("user32.dll")]
        static extern bool ClientToScreen(IntPtr hwnd, ref NativePoint point);
        [DllImport("user32.dll")]
        static extern uint GetDpiForWindow(IntPtr hwnd);
        [DllImport("user32.dll", ExactSpelling = true)]
        static extern int GetWindowLongW(IntPtr hwnd, int index);
        [DllImport("user32.dll", ExactSpelling = true)]
        static extern int SetWindowLongW(IntPtr hwnd, int index, int value);
        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    }
}
