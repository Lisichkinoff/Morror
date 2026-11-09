using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Morror
{
    public sealed class TransferReceipt
    {
        public string Path { get; set; }
        public long Bytes { get; set; }
        public string MediaUri { get; set; }
        public string MimeType { get; set; }
    }

    public sealed class RemoteEntry : INotifyPropertyChanged
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public bool IsDirectory { get; set; }
        System.Windows.Media.Imaging.BitmapSource preview;
        string previewHint;
        public System.Windows.Media.Imaging.BitmapSource Preview { get { return preview; } set { preview = value; Changed("Preview"); } }
        public string PreviewHint { get { return previewHint ?? (IsDirectory ? "Папка" : FileTransfer.IsImage(Name) ? "Загрузка превью…" : "Файл"); } set { previewHint = value; Changed("PreviewHint"); } }
        public string Icon { get { return IsDirectory ? "▣" : FileTransfer.IsImage(Name) ? "▧" : "≡"; } }
        public event PropertyChangedEventHandler PropertyChanged;
        void Changed(string property) { var handler = PropertyChanged; if (handler != null) handler(this, new PropertyChangedEventArgs(property)); }
        public string Label { get { return (IsDirectory ? "▣  " : "    ") + Name + (IsDirectory ? "/" : ""); } }
    }

    public sealed class FileTransfer
    {
        readonly string adb;
        readonly string serial;
        public FileTransfer(string adb, string serial) { this.adb = adb; this.serial = serial; }

        public static string SharedPath(string path)
        {
            if (string.IsNullOrEmpty(path) || path.Any(char.IsControl) || path.Contains("\\")) throw new ArgumentException("Недопустимый путь на телефоне.");
            path = path.TrimEnd('/');
            if (path != "/sdcard" && !path.StartsWith("/sdcard/", StringComparison.Ordinal)) throw new ArgumentException("Доступна общая память телефона /sdcard.");
            if (path.Split('/').Any(part => part == "." || part == "..")) throw new ArgumentException("Недопустимый путь на телефоне.");
            return path;
        }
        public static string ShellQuote(string value)
        {
            if (value.Any(char.IsControl)) throw new ArgumentException("Недопустимое имя файла.");
            return "'" + value.Replace("'", "'\\''") + "'";
        }
        public static List<RemoteEntry> ParseListing(string directory, string output)
        {
            directory = SharedPath(directory); var entries = new List<RemoteEntry>();
            foreach (string line in output.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string name = line.TrimEnd('\r'); bool folder = name.EndsWith("/", StringComparison.Ordinal);
                if (folder) name = name.Substring(0, name.Length - 1);
                if (name.Length == 0 || name == "." || name == ".." || name.Any(char.IsControl) || name.Contains("/") || name.Contains("\\")) continue;
                entries.Add(new RemoteEntry { Name = name, Path = SharedPath(directory + "/" + name), IsDirectory = folder });
            }
            return entries.OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        public static string SafeName(string name)
        {
            var invalid = System.IO.Path.GetInvalidFileNameChars();
            name = new string(name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
            if (string.IsNullOrEmpty(name)) name = "file";
            if (Regex.IsMatch(name, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\..*)?$", RegexOptions.IgnoreCase)) name = "_" + name;
            if (name.Length > 160) name = name.Substring(0, 140) + System.IO.Path.GetExtension(name).Substring(0, Math.Min(15, System.IO.Path.GetExtension(name).Length));
            return name;
        }
        public static string NewLocalPath(string folder, string name)
        {
            Directory.CreateDirectory(folder); name = SafeName(name);
            string stem = System.IO.Path.GetFileNameWithoutExtension(name), extension = System.IO.Path.GetExtension(name);
            for (int index = 0; index < 10000; index++)
            {
                string target = System.IO.Path.Combine(folder, stem + (index == 0 ? "" : " (" + index + ")") + extension);
                if (!File.Exists(target) && !Directory.Exists(target)) return target;
            }
            return System.IO.Path.Combine(folder, Guid.NewGuid().ToString("N") + extension);
        }
        public static bool IsImage(string path)
        {
            return new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".heic", ".heif", ".avif" }.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant());
        }
        public static string ReceiveFolder()
        {
            IntPtr pointer;
            Guid downloads = new Guid("374DE290-123F-4565-9164-39C4925E467B");
            if (SHGetKnownFolderPath(ref downloads, 0, IntPtr.Zero, out pointer) == 0)
            { try { return System.IO.Path.Combine(Marshal.PtrToStringUni(pointer), "Morror"); } finally { Marshal.FreeCoTaskMem(pointer); } }
            return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Morror");
        }
        [DllImport("shell32.dll")]
        static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);

        async Task<CommandResult> Run(CancellationToken token, params string[] args)
        {
            return await Commands.RunCancellable(adb, 120000, token, new[] { "-s", serial }.Concat(args).ToArray());
        }
        public async Task<List<RemoteEntry>> List(string directory, CancellationToken token)
        {
            directory = SharedPath(directory);
            var result = await Run(token, "shell", "ls -1pa -- " + ShellQuote(directory));
            if (result.ExitCode != 0) throw new IOException("Не удалось открыть папку. Проверьте подключение и доступ к общей памяти телефона.");
            return ParseListing(directory, result.Output);
        }
        public async Task<string> Pull(string remote, string destination, CancellationToken token, bool replaceExisting = false)
        {
            remote = SharedPath(remote); destination = System.IO.Path.GetFullPath(destination);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination));
            if (!replaceExisting && File.Exists(destination)) throw new IOException("Файл на ПК уже существует. Выберите другое имя.");
            string partial = destination + ".morror-" + Guid.NewGuid().ToString("N") + ".part";
            try
            {
                var result = await Run(token, "pull", remote, partial);
                token.ThrowIfCancellationRequested();
                if (result.ExitCode != 0 || !File.Exists(partial)) throw new IOException("Не удалось скопировать файл с телефона. Оригинал не изменён.");
                if (replaceExisting && File.Exists(destination)) File.Replace(partial, destination, null);
                else File.Move(partial, destination);
                return destination;
            }
            finally { if (File.Exists(partial)) File.Delete(partial); }
        }

        public async Task<byte[]> ReadPreview(string remote, CancellationToken token, long budget = 8 * 1024 * 1024)
        {
            remote = SharedPath(remote);
            if (!IsImage(remote)) return null;
            var size = await Run(token, "shell", "stat -c %s -- " + ShellQuote(remote));
            long bytes;
            if (size.ExitCode != 0 || !long.TryParse(size.Output.Trim(), out bytes) || bytes < 1 || bytes > Math.Min(budget, 8 * 1024 * 1024)) return null;
            string temporary = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Morror-preview-" + Guid.NewGuid().ToString("N"));
            try
            {
                var result = await Run(token, "pull", remote, temporary);
                token.ThrowIfCancellationRequested();
                if (result.ExitCode != 0 || !File.Exists(temporary)) return null;
                // Recheck in case the file changed after stat.
                if (new FileInfo(temporary).Length > Math.Min(budget, 8 * 1024 * 1024)) return null;
                return await Task.Run(() => File.ReadAllBytes(temporary), token);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        public async Task<TransferReceipt> Push(string source, CancellationToken token)
        {
            source = System.IO.Path.GetFullPath(source);
            if (!File.Exists(source)) throw new FileNotFoundException("Файл на ПК не найден.");
            bool image = IsImage(source);
            string directory = image ? "/sdcard/Pictures/Morror" : "/sdcard/Download/Morror";
            string name = SafeName(System.IO.Path.GetFileName(source));
            if (name.Length > 70) name = Guid.NewGuid().ToString("N") + System.IO.Path.GetExtension(name);
            string remote = directory + "/" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + "-" + name;
            var folder = await Run(token, "shell", "mkdir -p -- " + ShellQuote(directory));
            if (folder.ExitCode != 0) throw new IOException("Не удалось создать папку передачи на телефоне.");
            var available = await Run(token, "shell", "test ! -e " + ShellQuote(remote));
            if (available.ExitCode != 0) throw new IOException("Имя файла на телефоне уже занято. Повторите передачу.");
            Exception transferError = null;
            try
            {
                var pushed = await Run(token, "push", source, remote);
                token.ThrowIfCancellationRequested();
                if (pushed.ExitCode != 0) throw new IOException("Не удалось отправить файл. Проверьте подключение и свободное место на телефоне.");
                var size = await Run(token, "shell", "stat -c %s -- " + ShellQuote(remote));
                long actual;
                if (size.ExitCode != 0 || !long.TryParse(size.Output.Trim(), out actual) || actual != new FileInfo(source).Length)
                    throw new IOException("Телефон не подтвердил полный размер файла. Передача не завершена; повторите попытку.");
            }
            catch (Exception error) { transferError = error; }
            if (transferError != null)
            {
                // This is the newly generated destination of this operation, never the source.
                try { await Commands.Run(adb, 3000, "-s", serial, "shell", "rm -f -- " + ShellQuote(remote)); } catch (Exception) { }
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(transferError).Throw();
            }
            var receipt = new TransferReceipt { Path = remote, Bytes = new FileInfo(source).Length };
            try
            {
                var scan = await Commands.RunCancellable(adb, 10000, token, "-s", serial, "shell", "content call --uri content://media --method scan_file --arg " + ShellQuote(remote));
                if (scan.ExitCode != 0 || scan.Combined.Contains("Exception") || scan.Combined.Contains("Error"))
                {
                    string uri = "file://" + string.Join("/", remote.Split('/').Select(Uri.EscapeDataString));
                    await Commands.RunCancellable(adb, 4000, token, "-s", serial, "shell", "am broadcast -a android.intent.action.MEDIA_SCANNER_SCAN_FILE -d " + ShellQuote(uri));
                }
                string where = "_display_name='" + System.IO.Path.GetFileName(remote).Replace("'", "''") + "'";
                var query = await Commands.RunCancellable(adb, 5000, token, "-s", serial, "shell", "content query --uri content://media/external/file --projection _id:mime_type --where " + ShellQuote(where));
                var match = Regex.Match(query.Output, @"\b_id=(\d+),\s*mime_type=([a-zA-Z0-9.+/-]+)");
                if (query.ExitCode == 0 && match.Success)
                {
                    receipt.MediaUri = "content://media/external/file/" + match.Groups[1].Value;
                    receipt.MimeType = match.Groups[2].Value;
                }
            }
            catch (Exception) { } // The file is already delivered; gallery indexing is best effort.
            return receipt;
        }
        public async Task OpenOnPhone(TransferReceipt receipt, CancellationToken token)
        {
            if (receipt == null || string.IsNullOrEmpty(receipt.MediaUri) || !Regex.IsMatch(receipt.MediaUri, @"^content://media/external/file/\d+$"))
                throw new IOException("Откройте приложение «Файлы» на телефоне и папку Pictures/Morror или Download/Morror.");
            var result = await Run(token, "shell", "am start -a android.intent.action.VIEW -d " + ShellQuote(receipt.MediaUri) + " -t " + ShellQuote(receipt.MimeType) + " -f 1");
            if (result.ExitCode != 0 || result.Combined.Contains("Error:") || result.Combined.Contains("Exception"))
                throw new IOException("Не удалось открыть файл автоматически. Откройте папку Morror в приложении «Файлы» на телефоне.");
        }
        public async Task<string> Screenshot(string folder, CancellationToken token)
        {
            string target = NewLocalPath(folder, "Screen-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".png");
            string partial = target + ".morror-" + Guid.NewGuid().ToString("N") + ".part";
            try
            {
                await CapturePng(partial, token);
                using (var file = File.OpenRead(partial))
                {
                    byte[] header = new byte[8]; int read = file.Read(header, 0, header.Length);
                    if (read != 8 || !header.SequenceEqual(new byte[] { 137,80,78,71,13,10,26,10 })) throw new IOException("Телефон не вернул изображение экрана.");
                }
                File.Move(partial, target); return target;
            }
            finally { if (File.Exists(partial)) File.Delete(partial); }
        }
        async Task CapturePng(string destination, CancellationToken token)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            using (var process = new Process { StartInfo = Commands.StartInfo(adb, new[] { "-s", serial, "exec-out", "screencap", "-p" }) })
            {
                timeout.CancelAfter(20000); timeout.Token.ThrowIfCancellationRequested(); process.Start();
                using (timeout.Token.Register(() => { try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { } }))
                {
                    var errors = process.StandardError.ReadToEndAsync();
                    try
                    {
                        using (var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                        {
                            byte[] buffer = new byte[65536]; int read; long total = 0;
                            while ((read = await process.StandardOutput.BaseStream.ReadAsync(buffer, 0, buffer.Length, timeout.Token)) > 0)
                            {
                                total += read;
                                if (total > 64L * 1024 * 1024) throw new IOException("Снимок слишком большой для передачи в буфер.");
                                await file.WriteAsync(buffer, 0, read, timeout.Token);
                            }
                        }
                        timeout.Token.ThrowIfCancellationRequested();
                        await Task.Run(() => process.WaitForExit()); await errors;
                        if (process.ExitCode != 0) throw new IOException("Не удалось получить снимок экрана телефона.");
                    }
                    catch
                    {
                        try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { }
                        token.ThrowIfCancellationRequested();
                        if (timeout.IsCancellationRequested) throw new TimeoutException("Телефон не ответил на запрос снимка экрана.");
                        throw;
                    }
                }
            }
        }
    }
}
