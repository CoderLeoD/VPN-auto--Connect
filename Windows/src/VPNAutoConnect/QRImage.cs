using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using VPNAutoConnect.Core;

namespace VPNAutoConnect;

/// 把 Windows 图片交给 Core 里的 ZXing 识别二维码
static class QRImage
{
    public static List<string> Decode(Image image)
    {
        var results = DecodeScaled(image, 1);
        // 手机截图/拍照可能二维码太小或图片太大，缩放后再试
        if (results.Count == 0 && Math.Max(image.Width, image.Height) < 1200) results = DecodeScaled(image, 2);
        if (results.Count == 0 && Math.Max(image.Width, image.Height) > 1600) results = DecodeScaled(image, 0.5);
        return results;
    }

    public static List<string> Decode(string file)
    {
        try
        {
            using var img = Image.FromFile(file);
            return Decode(img);
        }
        catch
        {
            return [];
        }
    }

    public static List<string> DecodeFromClipboard()
    {
        if (Clipboard.ContainsFileDropList())
        {
            foreach (var f in Clipboard.GetFileDropList())
                if (f != null) return Decode(f);
        }
        if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } img)
        {
            using (img) return Decode(img);
        }
        return [];
    }

    static List<string> DecodeScaled(Image image, double scale)
    {
        int w = Math.Max(1, (int)(image.Width * scale)), h = Math.Max(1, (int)(image.Height * scale));
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);   // 透明背景的二维码图按白底处理
            g.InterpolationMode = scale >= 1 ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
            g.DrawImage(image, 0, 0, w, h);
        }
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, bytes, y * w * 4, w * 4);
            return QRDecoder.Decode(bytes, w, h);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }
}
