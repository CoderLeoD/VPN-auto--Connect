import AppKit
import CoreImage

enum QRDecoder {
    /// 识别图片中的所有二维码，返回其文本内容
    static func decode(image: NSImage) -> [String] {
        guard let tiff = image.tiffRepresentation,
              let ciImage = CIImage(data: tiff) else { return [] }
        return decode(ciImage: ciImage)
    }

    static func decode(url: URL) -> [String] {
        guard let ciImage = CIImage(contentsOf: url) else {
            if let img = NSImage(contentsOf: url) { return decode(image: img) }
            return []
        }
        return decode(ciImage: ciImage)
    }

    static func decode(ciImage: CIImage) -> [String] {
        let detector = CIDetector(
            ofType: CIDetectorTypeQRCode,
            context: nil,
            options: [CIDetectorAccuracy: CIDetectorAccuracyHigh]
        )
        var results = (detector?.features(in: ciImage) ?? [])
            .compactMap { ($0 as? CIQRCodeFeature)?.messageString }
        if results.isEmpty {
            // 手机截图/拍照可能二维码太小，放大后再试一次
            let scaled = ciImage.transformed(by: CGAffineTransform(scaleX: 2, y: 2))
            results = (detector?.features(in: scaled) ?? [])
                .compactMap { ($0 as? CIQRCodeFeature)?.messageString }
        }
        return results
    }

    static func decodeFromPasteboard() -> [String] {
        let pb = NSPasteboard.general
        if let urls = pb.readObjects(forClasses: [NSURL.self]) as? [URL],
           let fileURL = urls.first(where: { $0.isFileURL }) {
            return decode(url: fileURL)
        }
        if let image = NSImage(pasteboard: pb) {
            return decode(image: image)
        }
        return []
    }
}
