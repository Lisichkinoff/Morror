using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Morror
{
    public sealed class TransferDialog
    {
        readonly Window window;
        readonly FileTransfer transfer;
        readonly string receiveFolder;
        readonly Func<IDataObject> readClipboard;
        readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        bool busy, closed, loaded;
        TransferReceipt lastSent;
        List<RemoteEntry> entries = new List<RemoteEntry>();
        CancellationTokenSource previews;
        int page;
        const int PageSize = 12;
        string directory = "/sdcard/DCIM/Camera";
        T Get<T>(string name) where T : class { return window.FindName(name) as T; }
        RemoteEntry Selected { get { return Get<ListBox>("RemoteFiles").SelectedItem as RemoteEntry; } }

        public TransferDialog(Window owner, FileTransfer transfer, string model, string receiveFolder = null, Func<IDataObject> readClipboard = null)
        {
            this.transfer = transfer; this.receiveFolder = receiveFolder ?? FileTransfer.ReceiveFolder();
            this.readClipboard = readClipboard ?? (() => Clipboard.GetDataObject());
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("TransferWindow.xaml")) window = (Window)System.Windows.Markup.XamlReader.Load(stream);
            window.Resources.MergedDictionaries.Add(owner.Resources); window.Owner = owner; window.Icon = owner.Icon;
            var area = SystemParameters.WorkArea;
            window.MinWidth = Math.Min(window.MinWidth, area.Width); window.MinHeight = Math.Min(window.MinHeight, area.Height);
            window.Width = Math.Min(window.Width, area.Width); window.Height = Math.Min(window.Height, area.Height);
            Get<TextBlock>("TransferDevice").Text = model;
            Get<Button>("TransferClose").Click += (s, e) => window.Close();
            Get<Button>("RemoteRefresh").Click += async (s, e) => await Load(directory);
            Get<Button>("RemoteUp").Click += async (s, e) => await Load(directory == "/sdcard" ? directory : directory.Substring(0, directory.LastIndexOf('/')));
            Get<ComboBox>("RemoteFolder").SelectionChanged += async (s, e) => { if (loaded && !busy) await Load((string)((ComboBoxItem)Get<ComboBox>("RemoteFolder").SelectedItem).Tag); };
            Get<ListBox>("RemoteFiles").SelectionChanged += (s, e) => { UpdateButtons(); UpdatePreview(); };
            Get<Button>("PreviousPage").Click += (s, e) => { if (page > 0) { page--; ShowPage(); } };
            Get<Button>("NextPage").Click += (s, e) => { if ((page + 1) * PageSize < entries.Count) { page++; ShowPage(); } };
            Get<ListBox>("RemoteFiles").MouseDoubleClick += async (s, e) => { var item = Selected; if (item != null && item.IsDirectory) await Load(item.Path); };
            Get<Button>("ReceiveClipboard").Click += async (s, e) => await Receive(true);
            Get<Button>("ReceiveSave").Click += async (s, e) => await Receive(false);
            Get<Button>("ReceiveScreen").Click += async (s, e) => await Operation(async () =>
            {
                string file = await transfer.Screenshot(this.receiveFolder, lifetime.Token);
                if (closed) return;
                await ClipboardTransfer.CopyFile(file); Status("Снимок экрана скопирован. Нажмите Ctrl+V на ПК. Файл: " + file);
            }, "Получаем снимок экрана телефона…");
            Get<Button>("SendFile").Click += async (s, e) => await SendFile();
            Get<Button>("SendClipboardImage").Click += async (s, e) => await Operation(SendClipboard, "Готовим картинку из буфера Windows…");
            Get<Button>("OpenSentFile").Click += async (s, e) => await Operation(async () => { await transfer.OpenOnPhone(lastSent, lifetime.Token); Status("Файл открыт на телефоне. Если предложен выбор приложения, выберите просмотр фотографий."); }, "Открываем файл на телефоне…");
            Get<Button>("OpenReceivedFolder").Click += (s, e) => { Directory.CreateDirectory(this.receiveFolder); System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(this.receiveFolder) { UseShellExecute = true }); };
            window.PreviewDragOver += (s, e) => { e.Effects = !busy && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
            window.Drop += async (s, e) =>
            {
                e.Handled = true;
                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files == null || busy) return;
                await Operation(async () =>
                {
                    int sent = 0;
                    foreach (string path in files.Where(File.Exists)) { await Send(path); sent++; }
                    if (sent == 0) throw new IOException("Перетащите файл изображения, а не папку или ссылку из браузера.");
                }, "Отправляем перетащенные файлы…");
            };
            window.Loaded += async (s, e) => { loaded = true; await Load(directory); };
            window.Closed += (s, e) => { closed = true; lifetime.Cancel(); if (previews != null) previews.Cancel(); };
            UpdateButtons();
        }
        public void Show() { window.ShowDialog(); }
        void Status(string value) { if (!closed) Get<TextBlock>("TransferStatus").Text = value; }
        void ResultTitle(string value, System.Windows.Media.Brush color)
        {
            if (closed) return;
            Get<TextBlock>("TransferResultTitle").Text = value;
            Get<TextBlock>("TransferResultTitle").Foreground = color;
        }
        void UpdateButtons()
        {
            if (closed) return;
            bool file = Selected != null && !Selected.IsDirectory;
            foreach (string name in new[] { "ReceiveClipboard", "ReceiveSave" }) Get<Button>(name).IsEnabled = !busy && file;
            foreach (string name in new[] { "RemoteFolder", "RemoteFiles", "RemoteRefresh", "SendFile", "SendClipboardImage", "ReceiveScreen" }) Get<Control>(name).IsEnabled = !busy;
            Get<Button>("RemoteUp").IsEnabled = !busy && directory != "/sdcard";
            Get<Button>("OpenSentFile").IsEnabled = !busy && lastSent != null && !string.IsNullOrEmpty(lastSent.MediaUri);
            Get<Button>("PreviousPage").IsEnabled = !busy && page > 0;
            Get<Button>("NextPage").IsEnabled = !busy && (page + 1) * PageSize < entries.Count;
        }
        async Task Operation(Func<Task> action, string message)
        {
            if (busy || closed) return;
            if (previews != null) previews.Cancel();
            busy = true; UpdateButtons(); Status(message);
            ResultTitle("Выполняем…", System.Windows.Media.Brushes.LightGray);
            try { await action(); }
            catch (OperationCanceledException) { Status("Передача отменена."); ResultTitle("Отменено", System.Windows.Media.Brushes.LightGray); }
            catch (Exception error) { Status(error.Message); ResultTitle("Не получилось", System.Windows.Media.Brushes.LightSalmon); }
            finally
            {
                if (!closed && Get<TextBlock>("TransferResultTitle").Text == "Выполняем…") ResultTitle("Готово", System.Windows.Media.Brushes.LightGreen);
                busy = false; UpdateButtons();
            }
        }
        async Task Load(string path)
        {
            if (busy || closed) return;
            if (previews != null) previews.Cancel();
            await Operation(async () =>
            {
                var files = await transfer.List(path, lifetime.Token);
                if (closed) return;
                directory = path; Get<TextBlock>("RemotePath").Text = directory;
                entries = files; page = 0; ShowPage();
                Status(entries.Count == 0 ? "Папка пуста. Выберите другую папку выше." : "Выберите файл для копирования. Папки открываются двойным щелчком.");
            }, "Читаем папку телефона…");
        }

        void UpdatePreview()
        {
            var selected = Selected;
            Get<System.Windows.Controls.Image>("SelectedPreview").Source = selected == null ? null : selected.Preview;
            Get<TextBlock>("SelectedName").Text = selected == null ? "Выберите фото" : selected.Name;
        }

        void ShowPage()
        {
            if (previews != null) previews.Cancel();
            previews = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            var visible = entries.Skip(page * PageSize).Take(PageSize).ToList();
            // Bound decoded memory as well as network traffic; other pages reload on demand.
            foreach (var entry in entries.Except(visible)) { entry.Preview = null; entry.PreviewHint = null; }
            Get<ListBox>("RemoteFiles").ItemsSource = visible;
            Get<TextBlock>("PageLabel").Text = (page + 1) + " / " + Math.Max(1, (entries.Count + PageSize - 1) / PageSize) + " · " + entries.Count + " файлов и папок";
            UpdateButtons(); UpdatePreview();
            LoadPreviews(visible, previews);
        }

        async void LoadPreviews(List<RemoteEntry> visible, CancellationTokenSource source)
        {
            long budget = 24 * 1024 * 1024;
            try
            {
                foreach (var entry in visible.Where(item => !item.IsDirectory && FileTransfer.IsImage(item.Name)))
                {
                    source.Token.ThrowIfCancellationRequested();
                    if (entry.Preview != null) continue;
                    try
                    {
                        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(source.Token))
                        {
                            timeout.CancelAfter(8000);
                            byte[] bytes = await transfer.ReadPreview(entry.Path, timeout.Token, budget);
                            if (bytes == null) { entry.PreviewHint = "Без превью · доступен оригинал"; continue; }
                            budget -= bytes.Length;
                            var image = await Task.Run(() =>
                            {
                                using (var stream = new MemoryStream(bytes))
                                {
                                    var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand).Frames[0];
                                    bool landscape = frame.PixelWidth >= frame.PixelHeight;
                                    stream.Position = 0;
                                    var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
                                    if (landscape) bitmap.DecodePixelWidth = Math.Min(320, frame.PixelWidth);
                                    else bitmap.DecodePixelHeight = Math.Min(320, frame.PixelHeight);
                                    bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); return bitmap;
                                }
                            }, source.Token);
                            source.Token.ThrowIfCancellationRequested();
                            entry.Preview = image; entry.PreviewHint = "";
                            if (Selected == entry) UpdatePreview();
                        }
                    }
                    catch (OperationCanceledException) { if (source.IsCancellationRequested) break; entry.PreviewHint = "Превью не загружено"; }
                    catch (Exception) { entry.PreviewHint = "Без превью · доступен оригинал"; }
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                foreach (var item in visible.Where(item => item.Preview == null && FileTransfer.IsImage(item.Name)))
                    if (item.PreviewHint == "Загрузка превью…") item.PreviewHint = "Обновите для превью";
                if (previews == source) previews = null; source.Dispose();
            }
        }
        async Task Receive(bool clipboard)
        {
            var item = Selected;
            if (busy || item == null || item.IsDirectory) return;
            string destination; bool replace = false;
            if (clipboard) destination = FileTransfer.NewLocalPath(receiveFolder, item.Name);
            else
            {
                var save = new SaveFileDialog { FileName = FileTransfer.SafeName(item.Name), Filter = "Все файлы|*.*", OverwritePrompt = true };
                if (save.ShowDialog(window) != true) return;
                destination = save.FileName; replace = File.Exists(destination);
            }
            await Operation(async () =>
            {
                string path = await transfer.Pull(item.Path, destination, lifetime.Token, replace);
                if (closed) return;
                if (clipboard)
                {
                    bool picture = await ClipboardTransfer.CopyFile(path);
                    Status(picture ? "Изображение и файл скопированы. Нажмите Ctrl+V в приложении на ПК. Копия: " + path : "Файл скопирован. Нажмите Ctrl+V в Проводнике или приложении, принимающем файлы. Копия: " + path);
                }
                else Status("Файл сохранён: " + path);
            }, "Копируем оригинал файла на ПК…");
        }
        async Task SendFile()
        {
            if (busy || closed) return;
            var open = new OpenFileDialog { Title = "Выберите фото или файл для телефона", Filter = "Фото|*.png;*.jpg;*.jpeg;*.webp;*.gif;*.bmp;*.heic;*.avif|Все файлы|*.*", CheckFileExists = true };
            if (open.ShowDialog(window) != true) return;
            await Operation(async () => await Send(open.FileName), "Отправляем файл на телефон…");
        }
        async Task Send(string source)
        {
            lastSent = null;
            lastSent = await transfer.Push(source, lifetime.Token);
            Status("Файл сохранён на телефоне, размер проверен (" + lastSent.Bytes.ToString("N0") + " байт).\n" + lastSent.Path + "\n" +
                (string.IsNullOrEmpty(lastSent.MediaUri) ? "Откройте «Файлы» → Внутренняя память → Pictures/Morror (для фото) или Download/Morror. Галерея пока не подтвердила индексацию." : "Нажмите «Открыть на телефоне». Фото также доступно в папке Morror в приложении «Файлы» и в выборе вложений.") + " Это отправка файла; буфер Android и поле сообщения не изменяются.");
        }
        async Task SendClipboard()
        {
            string temporary = null;
            try
            {
                var data = readClipboard();
                string source = data != null && data.GetDataPresent(DataFormats.FileDrop) ? ((string[])data.GetData(DataFormats.FileDrop)).FirstOrDefault(path => File.Exists(path) && FileTransfer.IsImage(path)) : null;
                if (source == null)
                {
                    var image = ClipboardTransfer.ReadImage(data);
                    if (image == null) throw new IOException("В буфере Windows нет картинки. В браузере выберите «Копировать изображение», а не «Копировать адрес». Либо отправьте сохранённый файл кнопкой «Выбрать файл…».");
                    if ((long)image.PixelWidth * image.PixelHeight > 32L * 1024 * 1024) throw new IOException("Картинка слишком большая для буфера. Сохраните и отправьте её как файл.");
                    temporary = Path.Combine(Path.GetTempPath(), "Morror-" + Guid.NewGuid().ToString("N") + ".png");
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                    using (var file = new FileStream(temporary, FileMode.CreateNew)) encoder.Save(file);
                    source = temporary;
                }
                await Send(source);
            }
            finally { if (temporary != null && File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
