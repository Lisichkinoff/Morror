using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

class IconBuilder
{
    static GraphicsPath Rounded(float x, float y, float width, float height, float radius)
    {
        var path = new GraphicsPath(); float diameter = radius * 2;
        path.AddArc(x, y, diameter, diameter, 180, 90); path.AddArc(x + width - diameter, y, diameter, diameter, 270, 90);
        path.AddArc(x + width - diameter, y + height - diameter, diameter, diameter, 0, 90); path.AddArc(x, y + height - diameter, diameter, diameter, 90, 90); path.CloseFigure(); return path;
    }
    static Bitmap Render(int size)
    {
        using (var large = new Bitmap(1024, 1024, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(large))
            using (var frame = Rounded(8, 8, 240, 240, 52))
            using (var bg = new LinearGradientBrush(new Rectangle(8, 8, 240, 240), Color.FromArgb(35,54,46), Color.FromArgb(16,25,31), 65))
            using (var mint = new LinearGradientBrush(new Rectangle(40,40,176,176), Color.FromArgb(220,255,230), Color.FromArgb(137,215,172), 65))
            using (var edge = new Pen(Color.FromArgb(62,88,75), 2))
            using (var diamond = new Pen(mint, 12) { LineJoin = LineJoin.Round })
            using (var letter = new Pen(Color.FromArgb(186,243,207), 12) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.Clear(Color.Transparent); g.SmoothingMode = SmoothingMode.AntiAlias; g.ScaleTransform(4, 4);
                g.FillPath(bg, frame); g.DrawPath(edge, frame);
                g.DrawPolygon(diamond, new[] { new PointF(128,40), new PointF(216,128), new PointF(128,216), new PointF(40,128) });
                g.DrawLines(letter, new[] { new PointF(94,151), new PointF(94,104), new PointF(128,135), new PointF(162,104), new PointF(162,151) });
            }
            var output = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(output)) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.CompositingQuality = CompositingQuality.HighQuality; g.DrawImage(large, new Rectangle(0,0,size,size)); }
            return output;
        }
    }
    static void Main(string[] args)
    {
        string directory = args[0]; Directory.CreateDirectory(directory);
        var sizes = new[] { 16,20,24,32,40,48,64,96,128,256 }; var images = new List<byte[]>();
        foreach (int size in sizes)
        {
            using (var bitmap = Render(size)) using (var stream = new MemoryStream()) { bitmap.Save(stream, ImageFormat.Png); images.Add(stream.ToArray()); if (size == 256) bitmap.Save(Path.Combine(directory,"morror.png"),ImageFormat.Png); }
        }
        using (var writer = new BinaryWriter(File.Create(Path.Combine(directory,"morror.ico"))))
        {
            writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)sizes.Length); int offset = 6 + 16 * sizes.Length;
            for (int i=0; i<sizes.Length; i++) { writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i])); writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i])); writer.Write((byte)0); writer.Write((byte)0); writer.Write((ushort)1); writer.Write((ushort)32); writer.Write(images[i].Length); writer.Write(offset); offset += images[i].Length; }
            foreach (var bytes in images) writer.Write(bytes);
        }
        using (var small = new Bitmap(56,56)) using (var g = Graphics.FromImage(small)) using (var icon = Render(52)) { g.Clear(Color.White); g.DrawImage(icon,2,2); small.Save(Path.Combine(directory,"wizard-small.bmp"),ImageFormat.Bmp); }
        using (var banner = new Bitmap(164,314)) using (var g = Graphics.FromImage(banner))
        using (var bg = new LinearGradientBrush(new Rectangle(0,0,164,314),Color.FromArgb(28,49,39),Color.FromArgb(13,19,24),90))
        using (var icon = Render(96)) using (var title = new Font("Segoe UI",23,FontStyle.Bold)) using (var text = new Font("Segoe UI",12))
        using (var muted = new SolidBrush(Color.FromArgb(169,195,184))) using (var format = new StringFormat { Alignment = StringAlignment.Center })
        {
            g.FillRectangle(bg,0,0,164,314); g.SmoothingMode = SmoothingMode.AntiAlias; g.DrawImage(icon,34,49);
            g.DrawString("morror",title,Brushes.White,new RectangleF(0,157,164,40),format);
            g.DrawString("Телефон.\nНа вашем ПК.",text,muted,new RectangleF(8,208,148,70),format);
            banner.Save(Path.Combine(directory,"wizard-large.bmp"),ImageFormat.Bmp);
        }
    }
}
