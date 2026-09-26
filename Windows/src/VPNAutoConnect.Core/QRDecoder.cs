using ZXing;
using ZXing.Common;

namespace VPNAutoConnect.Core;

public static class QRDecoder
{
    /// 识别 BGRA32 像素数据中的所有二维码，返回其文本内容
    public static List<string> Decode(byte[] bgra, int width, int height)
    {
        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = true,
            Options = new DecodingOptions
            {
                TryHarder = true,
                TryInverted = true,
                PossibleFormats = [BarcodeFormat.QR_CODE],
            },
        };
        var source = new RGBLuminanceSource(bgra, width, height, RGBLuminanceSource.BitmapFormat.BGRA32);
        var results = reader.DecodeMultiple(source);
        return results?.Select(r => r.Text).Where(t => !string.IsNullOrEmpty(t)).Distinct().ToList() ?? [];
    }
}
