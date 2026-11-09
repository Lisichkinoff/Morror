// Integration tests exercise the actual WPF controller with the FakeDevice fixture.
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Morror;

class DesktopTests
{
    static int count;
    static int result = 1;
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    static extern int GetWindowLong(IntPtr window, int index);
    static readonly BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    static T Field<T>(object target, string name) { return (T)target.GetType().GetField(name, Flags).GetValue(target); }
    static void Set(object target, string name, object value) { target.GetType().GetField(name, Flags).SetValue(target, value); }
    static object Invoke(object target, string method) { return target.GetType().GetMethod(method, Flags).Invoke(target, null); }
    static void Snapshot(Window window, string filename)
    {
        window.UpdateLayout();
        var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, filename))) encoder.Save(stream);
    }
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); count++; Console.WriteLine("PASS " + message); }
    static async Task Until(Func<bool> predicate)
    {
        var timeout = Stopwatch.StartNew();
        while (!predicate()) { if (timeout.ElapsedMilliseconds > 10000) throw new TimeoutException("Desktop test timed out."); await Task.Delay(50); }
    }

    [STAThread]
    static int Main()
    {
        var application = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.Startup += async (s, e) =>
        {
            var controller = new Morror.App("Local\\Morror.Tests." + Guid.NewGuid().ToString("N"));
            Window window = null;
            try
            {
                Set(controller, "runtime", Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FakeRuntime"));
                Invoke(controller, "Initialize");
                Set(controller, "trayHintShown", true); // No notification balloons during automated checks.
                window = Field<Window>(controller, "window");
                Check(((CheckBox)window.FindName("AutoTray")).IsChecked == true, "Auto-hide is enabled in the shipped XAML");
                Check(((CheckBox)window.FindName("ClipboardSync")).IsChecked == true, "Shared text clipboard is enabled in the shipped UI");
                window.Show();
                await Until(() => ((TextBlock)window.FindName("DeviceAndroid")).Text == "Android 15");
                Check(((TextBlock)window.FindName("DeviceResolution")).Text == "1440 × 3120" && ((TextBlock)window.FindName("DeviceModel")).Text == "Galaxy S24", "Selected phone supplies the live device card instead of a fixed brand profile");
                Check(((ComboBox)window.FindName("Quality")).SelectedIndex == 1 && ((ComboBox)window.FindName("FrameRate")).SelectedIndex == 2, "Shipped balanced mode limits capture to HD and 60 FPS");
                Snapshot(window, "main-balanced.png");
                var presets = (ComboBox)window.FindName("StreamPreset"); presets.SelectedIndex = 1;
                Check(((ComboBox)window.FindName("Quality")).SelectedIndex == 0 && ((ComboBox)window.FindName("FrameRate")).SelectedIndex == 1, "Low heat preset actually selects 720 px and 30 FPS");
                presets.SelectedIndex = 0;
                ((System.Windows.Controls.Button)window.FindName("WifiButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                await Until(() => !Field<bool>(controller, "refreshing"));
                Check(((ComboBox)window.FindName("Devices")).SelectedItem is Device, "Wi-Fi setup retains the authorized USB phone for migration");
                ((System.Windows.Controls.Button)window.FindName("UsbWifiButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                await Until(() => !Field<bool>(controller, "networkBusy") && !Field<bool>(controller, "refreshing"));
                Check(((Device)((ComboBox)window.FindName("Devices")).SelectedItem).Serial == "192.168.1.20:5555" && ((TextBlock)window.FindName("Notice")).Text.Contains("USB можно отключить"), "USB to Wi-Fi button enables TCP, verifies readiness and selects the wireless transport");
                File.Delete(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FakeRuntime", "Storage", "wifi-enabled"));
                ((System.Windows.Controls.Button)window.FindName("UsbButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                await Until(() => !Field<bool>(controller, "refreshing"));
                var devicePreset = (System.Windows.Controls.Button)window.FindName("DevicePresetButton");
                devicePreset.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                Check(((System.Windows.Controls.ComboBox)window.FindName("Quality")).SelectedIndex == Protocol.NativeQuality && ((System.Windows.Controls.ComboBox)window.FindName("FrameRate")).SelectedIndex == 0, "Device preset selects native resolution and unbounded device FPS");
                var fps = (System.Windows.Controls.ComboBox)window.FindName("FrameRate");
                fps.SelectedIndex = 8;
                Check(((TextBlock)window.FindName("ProfileFps")).Text == "240 FPS", "240 FPS is reachable through the shipped UI");
                fps.SelectedIndex = 0;
                window.Width = 1380; window.Height = 960;
                Snapshot(window, "main-large-text.png");
                window.Width = 800; window.Height = 800;
                Snapshot(window, "main-compact-text.png");
                window.Width = 1380; window.Height = 960;
                var clipboardPng = new System.Windows.DataObject();
                var pixels = new byte[160 * 100 * 4];
                for (int p = 0; p < pixels.Length; p += 4) { pixels[p] = (byte)(70 + p / 4 % 160); pixels[p + 1] = (byte)(100 + p / 640); pixels[p + 2] = 60; pixels[p + 3] = 255; }
                var testImage = BitmapSource.Create(160,100,96,96,PixelFormats.Bgra32,null,pixels,640);
                var testEncoder = new PngBitmapEncoder(); testEncoder.Frames.Add(BitmapFrame.Create(testImage));
                var testPng = new MemoryStream(); testEncoder.Save(testPng); testPng.Position = 0; clipboardPng.SetData("PNG",testPng);
                string gallery = "Gallery-" + Guid.NewGuid().ToString("N");
                string galleryPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FakeRuntime", "Storage", "DCIM", "Camera", gallery);
                Directory.CreateDirectory(Path.Combine(galleryPath, "Альбом"));
                for (int photo = 1; photo <= 13; photo++) File.WriteAllBytes(Path.Combine(galleryPath, "Фото " + photo.ToString("00") + ".png"), testPng.ToArray());
                var fileDialog = new TransferDialog(window, new FileTransfer(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"FakeRuntime","adb.exe"),"TEST-PHONE"), "Тестовое устройство", Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"Received-Test"), () => clipboardPng);
                var fileWindow = Field<Window>(fileDialog,"window");
                bool fileUiReady = false;
                fileWindow.Loaded += async (fs, fe) =>
                {
                    await Until(() => !Field<bool>(fileDialog,"busy"));
                    await (Task)fileDialog.GetType().GetMethod("Load", Flags).Invoke(fileDialog, new object[] { "/sdcard/DCIM/Camera/" + gallery });
                    var galleryList = (System.Windows.Controls.ListBox)fileWindow.FindName("RemoteFiles");
                    try { await Until(() => galleryList.Items.Count == 12 && Field<CancellationTokenSource>(fileDialog, "previews") == null && ((RemoteEntry)galleryList.Items[1]).Preview != null); }
                    catch
                    {
                        Console.Error.WriteLine("Gallery status: " + ((TextBlock)fileWindow.FindName("TransferStatus")).Text);
                        foreach (RemoteEntry entry in galleryList.Items) Console.Error.WriteLine(entry.Name + " | " + entry.PreviewHint);
                        Snapshot(fileWindow, "gallery-failed.png"); throw;
                    }
                    galleryList.SelectedIndex = 1;
                    Check(((RemoteEntry)galleryList.Items[1]).Preview.IsFrozen && ((System.Windows.Controls.Image)fileWindow.FindName("SelectedPreview")).Source != null && ((TextBlock)fileWindow.FindName("SelectedName")).Text == "Фото 01.png", "Gallery decodes thumbnails and shows the selected photo and Unicode filename");
                    fileUiReady = ((System.Windows.Controls.ListBox)fileWindow.FindName("RemoteFiles")).ItemsSource != null;
                    Snapshot(fileWindow,"transfer-window.png");
                    ((System.Windows.Controls.Button)fileWindow.FindName("NextPage")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    Check(galleryList.Items.Count == 2 && ((System.Windows.Controls.Image)fileWindow.FindName("SelectedPreview")).Source == null, "Gallery pagination limits visible items and clears the previous selection preview");
                    ((System.Windows.Controls.Button)fileWindow.FindName("SendClipboardImage")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    await Until(() => !Field<bool>(fileDialog,"busy"));
                    Check(((TextBlock)fileWindow.FindName("TransferStatus")).Text.Contains("размер проверен") && ((System.Windows.Controls.Button)fileWindow.FindName("OpenSentFile")).IsEnabled, "PNG-only clipboard upload through the actual button confirms delivery and enables opening on the phone without touching the user's clipboard");
                    Snapshot(fileWindow,"transfer-success.png"); fileWindow.Close();
                };
                fileDialog.Show();
                Check(fileUiReady, "Owned transfer dialog loads its themed layout and lists only fixture phone files");
                var waiting = new WirelessPairing((args, token) => Task.FromResult(new CommandResult { ExitCode = 0, Output = "", Error = "" }), token => Task.Delay(25, token));
                var qrDialog = new QrPairingDialog(window, waiting);
                var qrWindow = Field<Window>(qrDialog, "window");
                bool qrShown = false;
                qrWindow.Loaded += async (qs, qe) =>
                {
                    await Task.Delay(80);
                    qrShown = ((System.Windows.Controls.Image)qrWindow.FindName("QrImage")).Source != null;
                    Snapshot(qrWindow, "qr-large-text.png");
                    qrWindow.Close();
                };
                Check(qrDialog.Show() == null && qrShown, "Owned QR dialog loads the shipped layout and renders a scannable image");
                await Until(() => Field<CancellationTokenSource>(qrDialog, "cancellation") == null);
                Check(((System.Windows.Controls.Image)qrWindow.FindName("QrImage")).Source == null, "Closing QR dialog cancels discovery and clears the displayed credentials");
                await Until(() => ((System.Windows.Controls.ComboBox)window.FindName("Devices")).SelectedItem is Device);
                Check(((Device)((System.Windows.Controls.ComboBox)window.FindName("Devices")).SelectedItem).Serial == "TEST-PHONE", "Fixture discovery uses the test runtime without touching a phone");
                ((CheckBox)window.FindName("AlwaysOnTop")).IsChecked = true;
                await (Task)Invoke(controller, "Start");
                var mirror = Field<Process>(controller, "mirror");
                Check(mirror != null && !mirror.HasExited && !window.IsVisible, "Settings hide only after the mirror window opens; the stream stays alive");
                Check((GetWindowLong(mirror.MainWindowHandle, -20) & 8) != 0, "Mirror window is actually pinned at startup");
                ((CheckBox)window.FindName("AlwaysOnTop")).IsChecked = false;
                Check((GetWindowLong(mirror.MainWindowHandle, -20) & 8) == 0 && !mirror.HasExited, "Unpinning takes effect without restarting the stream");
                using (var signal = EventWaitHandle.OpenExisting(Field<string>(controller, "restoreSignalName"))) signal.Set();
                await Until(() => window.IsVisible);
                Check(!mirror.HasExited, "Reopen signal restores hidden settings without interrupting the stream");
                window.Close();
                Check(!window.IsVisible && !mirror.HasExited, "Closing settings during a stream hides them instead of stopping playback");
                var tray = Field<TrayController>(controller, "tray");
                var menu = Field<ContextMenuStrip>(tray, "menu");
                menu.Items["pin"].PerformClick();
                Check(((CheckBox)window.FindName("AlwaysOnTop")).IsChecked == true && (GetWindowLong(mirror.MainWindowHandle, -20) & 8) != 0, "Tray pin action updates the live mirror and settings together");
                menu.Items[1].PerformClick();
                await Until(() => Field<Process>(controller, "mirror") == null && window.IsVisible);
                Check(true, "Tray Stop ends only the owned fixture and restores settings");
                ((CheckBox)window.FindName("AutoTray")).IsChecked = false;
                await (Task)Invoke(controller, "Start");
                mirror = Field<Process>(controller, "mirror");
                Check(mirror != null && window.IsVisible, "Disabling auto-hide leaves settings visible after startup");
                mirror.CloseMainWindow();
                await Until(() => Field<Process>(controller, "mirror") == null);
                ((CheckBox)window.FindName("AutoTray")).IsChecked = true;
                await (Task)Invoke(controller, "Start");
                mirror = Field<Process>(controller, "mirror");
                mirror.Kill();
                await Until(() => Field<Process>(controller, "mirror") == null && window.IsVisible);
                Check(((TextBlock)window.FindName("Notice")).Text.Contains("прервана"), "Unexpected stream loss restores settings and explains the error");
                await (Task)Invoke(controller, "Start");
                mirror = Field<Process>(controller, "mirror");
                var notification = Field<NotifyIcon>(tray, "notification");
                menu.Items["exit"].PerformClick();
                Check(Field<bool>(controller, "closing") && mirror.HasExited && !notification.Visible, "Tray Exit terminates the owned stream and removes the icon");
                Console.WriteLine(count + " desktop lifecycle checks passed.");
                result = 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); }
            finally
            {
                if (window != null && !Field<bool>(controller, "closing")) { Set(controller, "exitRequested", true); window.Close(); }
                application.Shutdown();
            }
        };
        application.Run();
        return result;
    }
}

