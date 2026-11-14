using System;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Threading.Tasks;

namespace Morror
{
    public static class AndroidMonitorClient
    {
        internal static async Task<int> OpenDownloadTunnel(string adb, string serial, int hostPort)
        {
            var result = await Commands.Run(adb, 8000, "-s", serial, "reverse", "--no-rebind", "tcp:0", "tcp:" + hostPort);
            int phonePort;
            if (result.ExitCode != 0 || !int.TryParse(result.Output.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out phonePort) ||
                phonePort < 1 || phonePort > 65535)
                throw new IOException("Не удалось открыть канал скачивания. Переподключите телефон и разрешите отладку.");
            return phonePort;
        }

        internal static async Task CloseDownloadTunnel(string adb, string serial, int phonePort)
        {
            try
            {
                var result = await Commands.Run(adb, 5000, "-s", serial, "reverse", "--remove", "tcp:" + phonePort);
                if (result.ExitCode != 0) System.Diagnostics.Trace.WriteLine(result.Combined);
            }
            catch (Exception error) { System.Diagnostics.Trace.WriteLine(error); }
        }

        public static async Task Install(string adb,string serial,string apk)
        {
            CommandResult result;
            try{result=await Commands.Run(adb,120000,"-s",serial,"install","-r",apk);}
            catch(TimeoutException){throw new IOException("Время установки Morror Монитор истекло. Разблокируйте Android, подтвердите запрос установки на устройстве и повторите запуск.");}
            if(result.ExitCode!=0||!result.Output.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries).Any(line=>line.Trim()=="Success"))
                throw new IOException(InstallationError(result.Combined));
        }

        public static string InstallationError(string output)
        {
            string hint;
            if(output.Contains("INSTALL_FAILED_UPDATE_INCOMPATIBLE")||output.Contains("INSTALL_PARSE_FAILED_INCONSISTENT_CERTIFICATES"))
                hint="На Android уже есть Morror Монитор с другой подписью. Удалите старое приложение Morror Монитор на устройстве и повторите запуск.";
            else if(output.Contains("INSTALL_FAILED_VERSION_DOWNGRADE"))
                hint="На Android установлена более новая версия Morror Монитор. Обновите Morror на ПК до той же или более новой версии.";
            else if(output.Contains("INSTALL_FAILED_USER_RESTRICTED")||output.Contains("INSTALL_CANCELED_BY_USER")||output.Contains("INSTALL_FAILED_ABORTED"))
                hint="Android запретил установку. Разблокируйте устройство и подтвердите запрос. На Xiaomi проверьте «Для разработчиков → Установка через USB», если этот пункт доступен. Политика администратора устройства также может запрещать установку.";
            else if(output.Contains("INSTALL_FAILED_INSUFFICIENT_STORAGE"))
                hint="На Android недостаточно свободного места. Освободите место и повторите запуск.";
            else if(output.Contains("INSTALL_FAILED_OLDER_SDK"))
                hint="Morror Монитор требует Android 8.0 или новее.";
            else
                hint="Проверьте подключение Android, разблокируйте устройство и подтвердите запрос установки, если он появился.";
            return "Не удалось установить Morror Монитор. "+hint+"\nОтвет Android: "+output.Trim();
        }
    }
}
