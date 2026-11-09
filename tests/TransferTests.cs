using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Morror;

class TransferTests
{
    static int count;
    static void Check(bool condition, string label) { if (!condition) throw new Exception(label); count++; Console.WriteLine("PASS " + label); }
    static bool Reject(Action action) { try { action(); return false; } catch (ArgumentException) { return true; } }
    static void MakePng(string path)
    {
        var pixels = new byte[16*12*4];
        for (int i=0; i<pixels.Length; i+=4) { pixels[i]=61; pixels[i+1]=180; pixels[i+2]=120; pixels[i+3]=255; }
        var image = BitmapSource.Create(16,12,96,96,PixelFormats.Bgra32,null,pixels,64);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
        using (var stream = File.Create(path)) png.Save(stream);
    }
    [STAThread]
    static int Main()
    {
        try { Run().GetAwaiter().GetResult(); Console.WriteLine(count + " transfer checks passed."); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    static async Task Run()
    {
        string test = Guid.NewGuid().ToString("N");
        string basePath = AppDomain.CurrentDomain.BaseDirectory;
        string runtime = Path.Combine(basePath,"FakeRuntime"), storage = Path.Combine(runtime,"Storage");
        string remoteFolder = "/sdcard/DCIM/Camera/" + test;
        string sourceFolder = Path.Combine(storage,"DCIM","Camera",test), targetFolder = Path.Combine(basePath,"transfers-" + test);
        Directory.CreateDirectory(sourceFolder); Directory.CreateDirectory(targetFolder);
        string original = Path.Combine(sourceFolder,"Фото ' лето.png"); MakePng(original);
        File.Copy(original,Path.Combine(storage,"screenshot.png"),true);
        Directory.CreateDirectory(Path.Combine(sourceFolder,"Album"));
        var transfer = new FileTransfer(Path.Combine(runtime,"adb.exe"),"TEST-PHONE");
        Check(Reject(() => FileTransfer.SharedPath("/sdcard/../data")) && Reject(() => FileTransfer.SharedPath("/sdcardevil/file")) && Reject(() => FileTransfer.SharedPath("/sdcard/line\nfile")), "Remote paths stay in shared storage and reject traversal and control characters");
        Check(FileTransfer.ShellQuote("a'b") == "'a'\\''b'", "Android shell paths escape embedded quotes");
        var entries = await transfer.List(remoteFolder,CancellationToken.None);
        Check(entries.Count == 2 && entries[0].IsDirectory && entries[1].Name == "Фото ' лето.png", "File listing preserves Unicode and spaces and sorts directories first");
        var preview = await transfer.ReadPreview(entries[1].Path, CancellationToken.None);
        Check(preview != null && preview.SequenceEqual(File.ReadAllBytes(original)), "Photo preview reads the exact image without keeping a local original");
        Check(await transfer.ReadPreview(entries[1].Path, CancellationToken.None, 1) == null && await transfer.ReadPreview(remoteFolder + "/notes.txt", CancellationToken.None) == null, "Preview transfer respects its byte budget and skips non-images");
        string destination = FileTransfer.NewLocalPath(targetFolder,entries[1].Name);
        await transfer.Pull(entries[1].Path,destination,CancellationToken.None);
        Check(File.ReadAllBytes(original).SequenceEqual(File.ReadAllBytes(destination)) && File.Exists(original), "Photo download preserves original bytes and leaves the phone source in place");
        string next = FileTransfer.NewLocalPath(targetFolder,entries[1].Name);
        Check(next != destination && Path.GetFileName(next).Contains("(1)"), "Repeated clipboard copies receive a new local filename");
        bool existing = false;
        try { await transfer.Pull(entries[1].Path,destination,CancellationToken.None); } catch (IOException) { existing = true; }
        Check(existing && File.ReadAllBytes(original).SequenceEqual(File.ReadAllBytes(destination)), "A local file is not overwritten without explicit replacement");
        bool partialFailed = false; string failed = Path.Combine(targetFolder,"failed.png");
        try { await transfer.Pull(remoteFolder + "/fail.png",failed,CancellationToken.None); } catch (IOException) { partialFailed = true; }
        Check(partialFailed && !File.Exists(failed) && !Directory.GetFiles(targetFolder,"*.part").Any(), "Failed downloads remove their own partial file");
        var receipt = await transfer.Push(destination,CancellationToken.None);
        string pushed = receipt.Path;
        string pushedLocal = Path.Combine(storage,pushed.Substring(8).Replace('/',Path.DirectorySeparatorChar));
        Check(pushed.StartsWith("/sdcard/Pictures/Morror/") && File.ReadAllBytes(pushedLocal).SequenceEqual(File.ReadAllBytes(original)), "Photo upload lands in Pictures/Morror with exact bytes");
        string pushedAgain = (await transfer.Push(destination,CancellationToken.None)).Path;
        Check(pushedAgain != pushed && File.Exists(pushedLocal), "Repeated uploads do not replace an existing phone file");
        string rejectedPhoto = Path.Combine(targetFolder,"fail-send-" + test + ".png"); File.Copy(original,rejectedPhoto);
        int before = Directory.GetFiles(Path.Combine(storage,"Pictures","Morror")).Length;
        bool uploadFailed = false;
        try { await transfer.Push(rejectedPhoto,CancellationToken.None); } catch (IOException) { uploadFailed = true; }
        Check(uploadFailed && Directory.GetFiles(Path.Combine(storage,"Pictures","Morror")).Length == before && File.Exists(pushedLocal), "Failed upload cleans only its newly created partial destination");
        string textFile = Path.Combine(targetFolder,"notes.txt"); File.WriteAllText(textFile,"test");
        Check((await transfer.Push(textFile,CancellationToken.None)).Path.StartsWith("/sdcard/Download/Morror/"), "Other file types land in Download/Morror");
        Check(receipt.Bytes == new FileInfo(original).Length && receipt.MediaUri == "content://media/external/file/42", "Verified upload reports actual size and an indexed Android media URI");
        await transfer.OpenOnPhone(receipt,CancellationToken.None);
        string shortPhoto = Path.Combine(targetFolder,"short-send-" + test + ".png"); File.Copy(original,shortPhoto);
        before = Directory.GetFiles(Path.Combine(storage,"Pictures","Morror")).Length;
        bool shortFailed = false;
        try { await transfer.Push(shortPhoto,CancellationToken.None); } catch (IOException) { shortFailed = true; }
        Check(shortFailed && Directory.GetFiles(Path.Combine(storage,"Pictures","Morror")).Length == before, "An ADB success with an incomplete remote file is rejected and cleaned up");
        string noIndex = Path.Combine(targetFolder,"no-index-" + test + ".png"); File.Copy(original,noIndex);
        var noIndexReceipt = await transfer.Push(noIndex,CancellationToken.None);
        Check(noIndexReceipt.MediaUri == null && File.Exists(Path.Combine(storage,noIndexReceipt.Path.Substring(8).Replace('/',Path.DirectorySeparatorChar))), "Unavailable gallery indexing preserves the delivered file without claiming it can be opened automatically");
        string screenshot = await transfer.Screenshot(targetFolder,CancellationToken.None);
        Check(File.ReadAllBytes(screenshot).SequenceEqual(File.ReadAllBytes(original)), "Screenshot capture carries raw PNG bytes without text encoding corruption");
        bool picture;
        var data = ClipboardTransfer.FileData(destination,out picture);
        Check(picture && data.GetDataPresent(DataFormats.Bitmap) && data.GetDataPresent("PNG") && data.GetDataPresent(DataFormats.FileDrop), "Image payload contains bitmap, PNG and file-paste formats without writing the system clipboard");
        var bitmap = (BitmapSource)data.GetData(DataFormats.Bitmap);
        Check(bitmap.PixelWidth == 16 && bitmap.PixelHeight == 12, "Clipboard image preserves pixel dimensions");
        var pngOnly = new DataObject();
        var pngStream = new MemoryStream(File.ReadAllBytes(original)); pngStream.Position = pngStream.Length;
        pngOnly.SetData("PNG",pngStream);
        var browserImage = ClipboardTransfer.ReadImage(pngOnly);
        Check(browserImage != null && browserImage.PixelWidth == 16 && pngStream.Position == pngStream.Length, "Browser PNG-only clipboard images decode even when the stream position is at its end");
        var mimePng = new DataObject(); mimePng.SetData("image/png",File.ReadAllBytes(original));
        Check(ClipboardTransfer.ReadImage(mimePng).PixelHeight == 12, "PNG byte-array clipboard data is accepted without CF_BITMAP");
        Check(ClipboardTransfer.ReadImage(new DataObject(DataFormats.Text,"https://example.com/photo.png")) == null, "An image URL is not mistaken for image pixels or downloaded silently");
        var fileOnly = ClipboardTransfer.FileData(textFile,out picture);
        Check(!picture && fileOnly.GetDataPresent(DataFormats.FileDrop), "Non-image files support paste into file-aware Windows applications");
        Check(FileTransfer.SafeName("CON.png") == "_CON.png" && !FileTransfer.SafeName("../a:?.png").Contains("/"), "Android filenames are made valid for Windows without path traversal");
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel(); bool stopped = false;
            try { await transfer.Pull(entries[1].Path,Path.Combine(targetFolder,"canceled.png"),canceled.Token); } catch (OperationCanceledException) { stopped = true; }
            Check(stopped && !File.Exists(Path.Combine(targetFolder,"canceled.png")), "Cancellation prevents a new transfer from creating a destination file");
        }
    }
}
