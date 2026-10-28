using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SnapTool;

/// <summary>Clipboard, file naming and saving.</summary>
public static class OutputService
{
    static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp" };

    /// <summary>Next free path in the save folder built from FileNameTemplate.</summary>
    public static string NextCapturePath(AppSettings s)
    {
        string folder = s.ResolvedSaveFolder;
        Directory.CreateDirectory(folder);
        string ext = s.ImageFormat.Trim().ToLowerInvariant() is "jpg" or "jpeg" ? ".jpg" : ".png";
        string name = ExpandTemplate(s.FileNameTemplate, folder);
        string path = Path.Combine(folder, name + ext);
        for (int i = 2; File.Exists(path); i++) path = Path.Combine(folder, $"{name}_{i}{ext}");
        return path;
    }

    /// <summary>What the next capture's file name would look like (doesn't touch the disk).</summary>
    public static string PreviewFileName(string template, string format, string folder)
    {
        string ext = format.Trim().ToLowerInvariant() is "jpg" or "jpeg" ? ".jpg" : ".png";
        return ExpandTemplate(template, folder) + ext;
    }

    static string ExpandTemplate(string template, string folder)
    {
        if (string.IsNullOrWhiteSpace(template)) template = "Snap_{yyyy-MM-dd_HH-mm-ss}";
        var now = DateTime.Now;
        string result = Regex.Replace(template, @"\{([^}]+)\}", m =>
        {
            string f = m.Groups[1].Value;
            if (f == "n" || f.StartsWith("n:"))
            {
                int count = Directory.Exists(folder)
                    ? Directory.EnumerateFiles(folder).Count(p => ImageExtensions.Contains(Path.GetExtension(p).ToLowerInvariant()))
                    : 0;
                return (count + 1).ToString(f.Length > 2 ? f[2..] : "0");
            }
            try { return now.ToString(f); } catch (FormatException) { return m.Value; }
        });
        foreach (var c in Path.GetInvalidFileNameChars()) result = result.Replace(c, '-');
        return result;
    }

    /// <summary>The image as PNG file bytes (encode once, then use for both the file and the clipboard).</summary>
    public static byte[] EncodePng(BitmapSource img)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(img));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    public static void SaveImage(BitmapSource img, string path, int jpegQuality, byte[]? png = null)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (png != null && ext == ".png")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllBytes(path + ".tmp", png);
            File.Move(path + ".tmp", path, overwrite: true);
            return;
        }
        BitmapEncoder encoder = ext switch
        {
            ".jpg" or ".jpeg" => new JpegBitmapEncoder { QualityLevel = Math.Clamp(jpegQuality, 10, 100) },
            ".bmp" => new BmpBitmapEncoder(),
            _ => new PngBitmapEncoder(),
        };
        BitmapSource frame = encoder is PngBitmapEncoder ? img : new FormatConvertedBitmap(img, PixelFormats.Bgr24, null, 0);
        encoder.Frames.Add(BitmapFrame.Create(frame));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string tmp = path + ".tmp";
        using (var fs = File.Create(tmp)) encoder.Save(fs);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// Puts the image on the clipboard as a plain bitmap (CF_DIB; Windows derives the other bitmap formats from it on
    /// request) and as PNG (apps like Slack/Teams prefer PNG). One bitmap copy, not several, keeps big captures light.
    /// </summary>
    public static void CopyToClipboard(BitmapSource img, byte[]? png = null)
    {
        var data = new DataObject();
        data.SetData(DataFormats.Dib, Dib(img), false);
        data.SetData("PNG", new MemoryStream(png ?? EncodePng(img)), false);

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, true);
                return;
            }
            catch (COMException) when (attempt < 10)
            {
                Thread.Sleep(60); // another app has the clipboard open
            }
        }
    }

    /// <summary>A packed, bottom-up, 32-bit opaque DIB (BITMAPINFOHEADER + pixels) of the image.</summary>
    static MemoryStream Dib(BitmapSource img)
    {
        if (img.Format != PixelFormats.Bgra32 && img.Format != PixelFormats.Bgr32)
            img = new FormatConvertedBitmap(img, PixelFormats.Bgra32, null, 0);
        int w = img.PixelWidth, h = img.PixelHeight, stride = w * 4;
        var buf = new byte[40 + stride * h];
        BitConverter.TryWriteBytes(buf.AsSpan(0), 40);          // biSize
        BitConverter.TryWriteBytes(buf.AsSpan(4), w);           // biWidth
        BitConverter.TryWriteBytes(buf.AsSpan(8), h);           // biHeight > 0: bottom-up rows
        BitConverter.TryWriteBytes(buf.AsSpan(12), (short)1);   // biPlanes
        BitConverter.TryWriteBytes(buf.AsSpan(14), (short)32);  // biBitCount; biCompression 0 = BI_RGB
        BitConverter.TryWriteBytes(buf.AsSpan(20), stride * h); // biSizeImage
        for (int y = 0; y < h; y++)
            img.CopyPixels(new Int32Rect(0, y, w, 1), buf, stride, 40 + (h - 1 - y) * stride);
        var words = MemoryMarshal.Cast<byte, uint>(buf.AsSpan(40));
        for (int i = 0; i < words.Length; i++) words[i] |= 0xFF000000; // opaque
        return new MemoryStream(buf, false);
    }
}
