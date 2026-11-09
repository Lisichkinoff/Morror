using System;
using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Morror
{
    public static class ClipboardTransfer
    {
        // Browsers may provide only the registered PNG format, without CF_BITMAP.
        public static BitmapSource ReadImage(IDataObject data)
        {
            if (data == null) return null;
            foreach (string format in new[] { "PNG", "image/png" })
            {
                if (!data.GetDataPresent(format)) continue;
                object value = data.GetData(format);
                var bytes = value as byte[];
                var stream = value as Stream;
                if (bytes != null) stream = new MemoryStream(bytes, false);
                if (stream == null) continue;
                long position = stream.CanSeek ? stream.Position : 0;
                try
                {
                    if (stream.CanSeek) stream.Position = 0;
                    var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    var image = decoder.Frames[0]; image.Freeze(); return image;
                }
                catch (NotSupportedException) { }
                catch (FileFormatException) { }
                catch (IOException) { }
                finally { if (stream.CanSeek) stream.Position = position; }
            }
            var bitmap = data.GetData(DataFormats.Bitmap) as BitmapSource;
            if (bitmap != null && bitmap.CanFreeze) bitmap.Freeze();
            return bitmap;
        }

        // Builds clipboard formats without changing the system clipboard (also used by tests).
        public static DataObject FileData(string path, out bool imageIncluded)
        {
            path = Path.GetFullPath(path);
            if (!File.Exists(path)) throw new FileNotFoundException("Файл для копирования не найден.");
            var data = new DataObject();
            data.SetFileDropList(new StringCollection { path });
            data.SetData("Preferred DropEffect", new MemoryStream(new byte[] { 1,0,0,0 }));
            imageIncluded = false;
            if (FileTransfer.IsImage(path) && new FileInfo(path).Length <= 64L * 1024 * 1024)
            {
                try
                {
                    using (var source = File.OpenRead(path))
                    {
                        var decoder = BitmapDecoder.Create(source, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                        var image = decoder.Frames[0];
                        if ((long)image.PixelWidth * image.PixelHeight <= 32L * 1024 * 1024)
                        {
                            image.Freeze(); data.SetImage(image);
                            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                            var png = new MemoryStream(); encoder.Save(png); png.Position = 0; data.SetData("PNG", png);
                            imageIncluded = true;
                        }
                    }
                }
                catch (NotSupportedException) { }
                catch (System.IO.FileFormatException) { }
                catch (IOException) { }
                catch (COMException) { }
            }
            return data;
        }

        public static async Task<bool> CopyFile(string path)
        {
            bool image;
            var data = FileData(path, out image);
            for (int attempt = 0; ; attempt++)
            {
                try { Clipboard.SetDataObject(data, true); return image; }
                catch (ExternalException)
                {
                    if (attempt >= 3) throw new IOException("Буфер Windows занят другим приложением. Повторите копирование.");
                }
                await Task.Delay(100);
            }
        }
    }
}
