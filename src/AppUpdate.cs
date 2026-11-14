using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using Microsoft.Win32;

namespace Morror
{
    internal sealed class AppUpdate
    {
        const string Repository = "Lisichkinoff/Morror";
        const string InstallerName = "Morror-Setup.exe";
        const string WorkerName = "Morror.Updater.exe";
        const string StagingPrefix = "Morror-update-";
        const string UpdateMutex = "Local\\Morror.Update";
        const string WorkerReadyPrefix = "Local\\Morror.UpdateReady.";
        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{DF58DC88-4346-493F-BCCE-9BCA671D2387}_is1";
        internal const string ApplyArgument = "--apply-update";
        internal const string SkipCheckArgument = "--skip-update-check";

        internal readonly Version Version;
        internal readonly Uri DownloadUrl;
        internal readonly long Size;
        internal readonly string Sha256;

        AppUpdate(Version version, Uri downloadUrl, long size, string sha256)
        {
            Version = version;
            DownloadUrl = downloadUrl;
            Size = size;
            Sha256 = sha256;
        }

        sealed class Release
        {
            public string tag_name { get; set; }
            public bool draft { get; set; }
            public bool prerelease { get; set; }
            public Asset[] assets { get; set; }
        }

        sealed class Asset
        {
            public string name { get; set; }
            public string state { get; set; }
            public long size { get; set; }
            public string digest { get; set; }
            public string browser_download_url { get; set; }
        }

        internal static AppUpdate ParseRelease(string json, Version installedVersion)
        {
            var release = new JavaScriptSerializer().Deserialize<Release>(json);
            Version version;
            if (release == null || release.draft || release.prerelease || release.assets == null ||
                !Regex.IsMatch(release.tag_name ?? "", @"\Av[0-9]+\.[0-9]+\.[0-9]+\z") ||
                !Version.TryParse(release.tag_name.Substring(1), out version) ||
                version <= new Version(installedVersion.Major, installedVersion.Minor, installedVersion.Build))
                return null;

            string name = "Morror-" + version + "-Setup-win64.exe";
            var url = new Uri("https://github.com/" + Repository + "/releases/download/" + release.tag_name + "/" + name);
            foreach (var asset in release.assets)
            {
                if (asset != null && asset.name == name && asset.state == "uploaded" &&
                    asset.size > 0 && asset.size <= 1024L * 1024 * 1024 &&
                    asset.browser_download_url == url.AbsoluteUri &&
                    Regex.IsMatch(asset.digest ?? "", @"\Asha256:[0-9a-fA-F]{64}\z"))
                    return new AppUpdate(version, url, asset.size, asset.digest.Substring(7));
            }
            return null;
        }

        internal static async Task<AppUpdate> CheckAsync(CancellationToken cancellation)
        {
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(12));
                var request = CreateRequest(new Uri("https://api.github.com/repos/" + Repository + "/releases/latest"));
                request.Accept = "application/vnd.github+json";
                using (deadline.Token.Register(request.Abort))
                using (var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
                {
                    if (response.StatusCode != HttpStatusCode.OK) return null;
                    using (var reader = new StreamReader(response.GetResponseStream()))
                    {
                        var json = await reader.ReadToEndAsync().ConfigureAwait(false);
                        return ParseRelease(json, new Version(AppVersion.Number));
                    }
                }
            }
        }

        static HttpWebRequest CreateRequest(Uri uri)
        {
            var request = WebRequest.CreateHttp(uri);
            request.UserAgent = "Morror/" + AppVersion.Number;
            request.AllowAutoRedirect = false;
            return request;
        }

        internal async Task<string> DownloadAsync(IProgress<int> progress, CancellationToken cancellation)
        {
            string folder = Path.Combine(Path.GetTempPath(), StagingPrefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                {
                    deadline.CancelAfter(TimeSpan.FromMinutes(20));
                    await DownloadFileAsync(Path.Combine(folder, InstallerName), progress, deadline.Token).ConfigureAwait(false);
                }
                return folder;
            }
            catch
            {
                DeleteStaging(folder);
                throw;
            }
        }

        async Task DownloadFileAsync(string file, IProgress<int> progress, CancellationToken cancellation)
        {
            var uri = DownloadUrl;
            for (int redirects = 0; redirects <= 5; redirects++)
            {
                var request = CreateRequest(uri);
                using (cancellation.Register(request.Abort))
                using (var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
                {
                    int status = (int)response.StatusCode;
                    if (status == 301 || status == 302 || status == 303 || status == 307 || status == 308)
                    {
                        Uri next;
                        if (!Uri.TryCreate(uri, response.Headers["Location"], out next) || !IsDownloadHost(next))
                            throw new IOException("GitHub вернул недопустимый адрес установщика.");
                        uri = next;
                        continue;
                    }
                    if (status != 200 || (response.ContentLength >= 0 && response.ContentLength != Size))
                        throw new IOException("Размер установщика не совпадает с данными релиза.");

                    using (var stream = response.GetResponseStream())
                    using (var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                    {
                        long received = 0;
                        int percent = 0;
                        int count;
                        var buffer = new byte[65536];
                        while ((count = await stream.ReadAsync(buffer, 0, buffer.Length, cancellation).ConfigureAwait(false)) != 0)
                        {
                            received += count;
                            if (received > Size) throw new IOException("Размер установщика превышает размер в релизе.");
                            await output.WriteAsync(buffer, 0, count, cancellation).ConfigureAwait(false);
                            int nextPercent = (int)(received * 100 / Size);
                            if (nextPercent != percent) progress.Report(percent = nextPercent);
                        }
                        if (received != Size) throw new IOException("Загрузка установщика прервалась.");
                    }
                    await Task.Run(() => VerifyInstaller(file, Sha256), cancellation).ConfigureAwait(false);
                    return;
                }
            }
            throw new IOException("Слишком много перенаправлений при скачивании.");
        }

        static bool IsDownloadHost(Uri uri)
        {
            return uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) &&
                (uri.Host == "github.com" || uri.Host == "release-assets.githubusercontent.com" ||
                 uri.Host == "objects.githubusercontent.com" || uri.Host == "github-releases.githubusercontent.com");
        }

        static void VerifyInstaller(string file, string expected)
        {
            using (var stream = File.OpenRead(file))
            using (var sha256 = SHA256.Create())
            {
                string actual = BitConverter.ToString(sha256.ComputeHash(stream)).Replace("-", "");
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Контрольная сумма установщика не совпадает с данными GitHub.");
            }
        }

        internal async Task StartInstallerAsync(string folder, CancellationToken cancellation)
        {
            string executable = typeof(AppUpdate).Assembly.Location;
            string worker = Path.Combine(folder, WorkerName);
            File.Copy(executable, worker, true);
            int processId;
            using (var process = Process.GetCurrentProcess()) processId = process.Id;
            using (var ready = new EventWaitHandle(false, EventResetMode.ManualReset, WorkerReadyPrefix + Path.GetFileName(folder)))
            using (var process = Process.Start(new ProcessStartInfo(worker, Protocol.Arguments(new[]
                {
                    ApplyArgument, processId.ToString(), executable, Version.ToString(), Sha256
                })) { UseShellExecute = false, WorkingDirectory = folder }))
            {
                int result = await Task.Run(() => WaitHandle.WaitAny(new WaitHandle[] { ready, cancellation.WaitHandle }, 10000)).ConfigureAwait(false);
                if (result == 0) return;
                try { if (!process.HasExited) process.Kill(); }
                catch (InvalidOperationException) { }
                cancellation.ThrowIfCancellationRequested();
                throw new IOException("Процесс обновления не запустился.");
            }
        }

        internal static bool IsInstalling()
        {
            Mutex mutex;
            if (!Mutex.TryOpenExisting(UpdateMutex, out mutex)) return false;
            mutex.Dispose();
            return true;
        }

        internal static void Apply(string[] args)
        {
            string worker = typeof(AppUpdate).Assembly.Location;
            string folder = Path.GetDirectoryName(worker);
            int processId;
            Version version;
            if (args.Length != 5 || !int.TryParse(args[1], out processId) || processId <= 0 ||
                !Version.TryParse(args[3], out version) || version.Build < 0 || version.Revision >= 0 ||
                !Regex.IsMatch(args[4], @"\A[0-9a-fA-F]{64}\z") ||
                Path.GetFileName(worker) != WorkerName || !Path.GetFileName(folder).StartsWith(StagingPrefix, StringComparison.Ordinal))
                return;

            string executable = args[2];
            string destination = Path.GetDirectoryName(executable);
            if (!Path.IsPathRooted(executable) || string.IsNullOrEmpty(destination) ||
                !string.Equals(Path.GetFileName(executable), "Morror.exe", StringComparison.OrdinalIgnoreCase))
                return;

            bool restart = false;
            string failure = null;
            bool created;
            using (var mutex = new Mutex(true, UpdateMutex, out created))
            {
                if (!created) return;
                try
                {
                    using (var ready = EventWaitHandle.OpenExisting(WorkerReadyPrefix + Path.GetFileName(folder))) ready.Set();
                    WaitForApplication(processId);
                    restart = true;
                    string installer = Path.Combine(folder, InstallerName);
                    VerifyInstaller(installer, args[4]);
                    string arguments = "/SP- /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /RESTARTEXITCODE=3010 /CLOSEAPPLICATIONS /NORESTARTAPPLICATIONS /DIR=" + Protocol.Quote(destination);
                    if (!IsInstalledIn(destination)) arguments += " /MORRORPORTABLEUPDATE";
                    using (var setup = Process.Start(new ProcessStartInfo(installer, arguments)
                    {
                        UseShellExecute = false,
                        WorkingDirectory = folder
                    }))
                    {
                        setup.WaitForExit();
                        if (setup.ExitCode == 3010) throw new IOException("Для завершения обновления требуется перезагрузка Windows.");
                        if (setup.ExitCode != 0) throw new IOException("Код завершения установщика: " + setup.ExitCode + ".");
                    }
                    string expectedVersion = version + ".0";
                    if (FileVersionInfo.GetVersionInfo(executable).FileVersion != expectedVersion)
                        throw new IOException("Версия приложения после установки не совпадает с версией релиза.");
                }
                catch (Exception error)
                {
                    failure = error.Message;
                }
                finally
                {
                    DeleteStaging(folder);
                    mutex.ReleaseMutex();
                }
            }

            if (failure != null)
                MessageBox.Show("Не удалось завершить обновление Morror.\n\n" + failure, "Обновление Morror", MessageBoxButton.OK, MessageBoxImage.Warning);
            if (!restart) return;
            try
            {
                using (Process.Start(new ProcessStartInfo(executable, SkipCheckArgument)
                {
                    UseShellExecute = false,
                    WorkingDirectory = destination
                })) { }
            }
            catch (Win32Exception error)
            {
                MessageBox.Show("Не удалось снова открыть Morror.\n\n" + error.Message, "Обновление Morror", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        static void WaitForApplication(int processId)
        {
            Process process;
            try { process = Process.GetProcessById(processId); }
            catch (ArgumentException) { return; }
            using (process)
            {
                if (!process.WaitForExit(30000)) throw new IOException("Morror не завершил работу. Закройте приложение и повторите попытку.");
            }
        }

        static bool IsInstalledIn(string destination)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(UninstallKey))
                {
                    var installed = key == null ? null : key.GetValue("InstallLocation") as string;
                    return !string.IsNullOrEmpty(installed) &&
                        string.Equals(installed.TrimEnd('\\'), destination.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (SecurityException) { return HasUninstaller(destination); }
            catch (UnauthorizedAccessException) { return HasUninstaller(destination); }
        }

        static bool HasUninstaller(string destination)
        {
            return File.Exists(Path.Combine(destination, "unins000.exe")) &&
                File.Exists(Path.Combine(destination, "unins000.dat"));
        }

        internal static void DeleteStaging(string folder)
        {
            if (folder == null) return;
            try { File.Delete(Path.Combine(folder, InstallerName)); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            try
            {
                File.Delete(Path.Combine(folder, WorkerName));
                Directory.Delete(folder);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        internal static void CleanOldDownloads()
        {
            try
            {
                foreach (string folder in Directory.EnumerateDirectories(Path.GetTempPath(), StagingPrefix + "*"))
                {
                    Guid folderId;
                    if (Guid.TryParseExact(Path.GetFileName(folder).Substring(StagingPrefix.Length), "N", out folderId) &&
                        Directory.GetCreationTimeUtc(folder) < DateTime.UtcNow.AddDays(-1) &&
                        (File.GetAttributes(folder) & FileAttributes.ReparsePoint) == 0)
                        DeleteStaging(folder);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
