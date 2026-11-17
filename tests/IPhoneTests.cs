using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Morror;

class IPhoneTests
{
    static int count, result = 1;
    static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
        count++;
        Console.WriteLine("PASS " + message);
    }
    static async Task Until(Func<bool> predicate)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate())
        {
            if (watch.ElapsedMilliseconds > 10000) throw new TimeoutException("iPhone UI test timed out");
            await Task.Delay(20);
        }
    }
    static T Get<T>(Window window, string name) where T : FrameworkElement { return (T)window.FindName(name); }
    static void Click(Window window, string name) { Get<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }

    [StructLayout(LayoutKind.Sequential)]
    struct ClientRect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    static extern bool GetClientRect(IntPtr handle, out ClientRect rect);

    static void Snapshot(Window window, string name)
    {
        window.UpdateLayout();
        var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, name))) encoder.Save(file);
    }

    [STAThread]
    static int Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (sender, args) =>
        {
            try
            {
                Check(Marshal.SizeOf(typeof(IPhoneMirrorEngine.Device)) == 1184 &&
                    Marshal.SizeOf(typeof(IPhoneMirrorEngine.CaptureStatus)) == 464, "Native API 18 layouts match the x64 header");
                var engine = new FakeEngine();
                var dialog = new IPhoneMirrorDialog(engine);
                var window = dialog.Window;
                Check(app.Windows.Count == 1, "Loading the theme does not leave an invisible window that keeps the process alive");
                window.Show();
                await Task.Delay(100);
                var frame = Get<Border>(window, "WindowFrame");
                Check(frame.CornerRadius.TopLeft == 26 && frame.Clip is RectangleGeometry &&
                    Get<Button>(window, "Start").ActualHeight == 44 && Get<Button>(window, "Stop").ActualHeight == 44,
                    "iPhone uses the rounded Morror frame and matched action heights");
                Snapshot(window, "iphone-airplay.png");
                Click(window, "MaximizeButton");
                await Until(() => window.WindowState == WindowState.Maximized);
                Check(frame.Clip == null && frame.CornerRadius.TopLeft == 0, "The iPhone caption button maximizes to the work area");
                Click(window, "MaximizeButton");
                await Until(() => window.WindowState == WindowState.Normal && frame.Clip != null);
                Check(engine.InitializeCalls == 0 && engine.UsbCalls == 0, "Opening iPhone does not wait for native initialization or USB");
                Check(Get<ComboBox>(window, "Transport").SelectedIndex == 1 && Get<Button>(window, "Start").IsEnabled,
                    "AirPlay is immediately selectable on first open");
                Check(Get<TextBlock>(window, "Instructions").Text.Contains("Пункт управления"), "Instructions identify the screen mirroring control");

                Click(window, "Start");
                await Until(() => Get<TextBlock>(window, "Status").Text.Contains("ожидаем iPhone"));
                Check(!Get<TextBlock>(window, "Status").Text.Contains("AirPlay готов"), "Receiver startup is not reported as verified connectivity");
                Check(engine.InitializeCalls == 1 && engine.UsbCalls == 0, "AirPlay starts without probing any USB driver");
                await Until(() => Get<Button>(window, "Network").IsEnabled);
                Check(Get<Button>(window, "Network").IsEnabled, "Network setup remains accessible while waiting for a phone");

                engine.WirelessDevice = true;
                await Until(() => engine.AttachStarted);
                Check(!engine.AttachOnUiThread, "Native renderer attachment runs off the UI thread");
                // A dispatcher continuation must run while the native renderer is held.
                Check(window.IsVisible && !engine.Attached, "Window stays responsive during slow renderer creation");
                engine.AttachGate.Set();
                await Until(() => Get<TextBlock>(window, "Status").Text.Contains("iPhone подключён"));
                var preview = engine.AttachedWindow;
                ClientRect initial, resized;
                Check(GetClientRect(preview, out initial), "iPhone attaches the preview to a valid child window");
                int polls = engine.StatusCalls;
                window.Left += 22;
                window.Width = Math.Max(window.MinWidth, window.Width - 66);
                window.UpdateLayout();
                await Until(() => engine.StatusCalls > polls);
                Check(engine.HasSession && engine.AttachedWindow == preview && engine.AttachCalls == 1 &&
                    GetClientRect(preview, out resized) && resized.Right < initial.Right,
                    "Moving and resizing iPhone keeps the same live preview window");
                engine.Failed = true;
                await Until(() => Get<TextBlock>(window, "Status").Text.Contains("test decoder failure") && Get<Button>(window, "Start").IsEnabled);
                Check(!engine.HasSession && !engine.Listening && Get<Button>(window, "Start").IsEnabled,
                    "Session failure tears down resources and allows retry with a useful error");

                engine.WirelessDevice = false;
                engine.Failed = false;
                Get<ComboBox>(window, "Transport").SelectedIndex = 0;
                await Until(() => Get<Button>(window, "Start").IsEnabled);
                Check(engine.UsbCalls == 1 && !engine.MetadataRefresh, "USB selection uses lightweight discovery");
                Click(window, "Start");
                await Until(() => Get<TextBlock>(window, "Status").Text.Contains("iPhone подключён"));
                Check(!engine.LastStartWireless, "Selected USB device is passed to the USB session");
                bool closed = false;
                window.Closed += (s, e) => closed = true;
                Click(window, "CloseButton");
                await Until(() => closed);
                Check(engine.Disposed && !engine.HasSession, "Closing waits for native cleanup before destroying preview");

                var slowEngine = new FakeEngine { SlowInitialize = true };
                var slowWindow = new IPhoneMirrorDialog(slowEngine).Window;
                slowWindow.Show();
                Click(slowWindow, "Start");
                await Until(() => slowEngine.InitializeCalls != 0);
                bool slowClosed = false;
                slowWindow.Closed += (s, e) => slowClosed = true;
                slowWindow.Close();
                Check(!slowClosed && !slowEngine.Disposed, "Close during startup does not race native disposal");
                slowEngine.InitializeGate.Set();
                await Until(() => slowClosed);
                Check(slowEngine.Disposed && !slowEngine.Listening, "Close requested during startup completes without a second click");
                Console.WriteLine(count + " iPhone checks passed.");
                result = 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); }
            finally { app.Shutdown(); }
        };
        app.Run();
        return result;
    }

    sealed class FakeEngine : IIPhoneMirrorEngine
    {
        readonly int uiThread = Thread.CurrentThread.ManagedThreadId;
        public int InitializeCalls, UsbCalls, AttachCalls, StatusCalls;
        public IntPtr AttachedWindow;
        public volatile bool Listening, WirelessDevice, AttachStarted, Attached, Failed, Disposed;
        public bool AttachOnUiThread, MetadataRefresh, LastStartWireless, SlowInitialize;
        public readonly ManualResetEventSlim AttachGate = new ManualResetEventSlim();
        public readonly ManualResetEventSlim InitializeGate = new ManualResetEventSlim();
        public bool HasSession { get; private set; }
        public string DriverTool { get { return "unused.exe"; } }
        public void Initialize()
        {
            Interlocked.Increment(ref InitializeCalls);
            if (SlowInitialize && !InitializeGate.Wait(8000)) throw new TimeoutException();
        }
        public IPhoneMirrorEngine.Device[] Devices(bool wifi, bool refresh)
        {
            if (!wifi) { UsbCalls++; MetadataRefresh = refresh; }
            return wifi && !WirelessDevice ? new IPhoneMirrorEngine.Device[0] :
                new[] { new IPhoneMirrorEngine.Device { Id = "test", Name = "Test iPhone", UsbConnected = wifi ? 0 : 1 } };
        }
        public void Listen() { Listening = true; }
        public bool ReceiverReady() { return Listening; }
        public void Start(IPhoneMirrorEngine.Device device, bool wifi) { LastStartWireless = wifi; HasSession = true; }
        public void Attach(IntPtr handle)
        {
            if (handle == IntPtr.Zero) throw new IOException("Missing preview HWND");
            AttachedWindow = handle;
            Interlocked.Increment(ref AttachCalls);
            AttachOnUiThread = Thread.CurrentThread.ManagedThreadId == uiThread;
            AttachStarted = true;
            if (!AttachGate.Wait(8000)) throw new TimeoutException();
            Attached = true;
        }
        public IPhoneMirrorEngine.CaptureStatus Status()
        {
            Interlocked.Increment(ref StatusCalls);
            return new IPhoneMirrorEngine.CaptureStatus { State = Failed ? 7 : 4, VideoFrames = 1,
                Width = 1170, Height = 2532, Fps = 60, ErrorCode = Failed ? -4 : 0,
                FailureStage = Failed ? 7 : 0, Message = Failed ? "test decoder failure" : "" };
        }
        public void Stop() { HasSession = false; }
        public void StopListening() { Listening = false; }
        public void Dispose() { Stop(); StopListening(); Disposed = true; }
    }
}
