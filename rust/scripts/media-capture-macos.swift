// Test-owned native window capture, not an application screenshot capability.
import Foundation
import ScreenCaptureKit
import CoreGraphics
import ImageIO
import UniformTypeIdentifiers

struct Client: Decodable { let x: Int; let y: Int; let width: Int; let height: Int; let scale: Double }
struct Request: Decodable { let pid: Int32; let checkpoint: String; let client: Client }
enum CaptureError: Error { case invalidInput, wrongWindow, foregroundChanged, captureSize, pngWrite }

@main struct CaptureOwnedWindow {
    static func foreground(_ pid: Int32, _ windowID: CGWindowID) throws {
        guard let rows = CGWindowListCopyWindowInfo(.optionOnScreenOnly, kCGNullWindowID) as? [[String: Any]],
              let first = rows.first(where: { ($0[kCGWindowLayer as String] as? Int) == 0 && ($0[kCGWindowAlpha as String] as? Double ?? 0) > 0 }),
              (first[kCGWindowOwnerPID as String] as? Int32) == pid,
              (first[kCGWindowNumber as String] as? UInt32) == windowID else { throw CaptureError.foregroundChanged }
    }
    static func main() async throws {
        guard CommandLine.arguments.count == 4,
              let wid = UInt32(CommandLine.arguments[2]) else { throw CaptureError.invalidInput }
        let path = URL(fileURLWithPath: CommandLine.arguments[1])
        let output = URL(fileURLWithPath: CommandLine.arguments[3], isDirectory: true)
        let request = try JSONDecoder().decode(Request.self, from: Data(contentsOf: path))
        guard ["gif", "apng", "webp"].contains(request.checkpoint), request.pid > 0,
              request.client.width > 0, request.client.height > 0,
              request.client.width <= 16384, request.client.height <= 16384,
              (0.5...4).contains(request.client.scale) else { throw CaptureError.invalidInput }
        try foreground(request.pid, wid)
        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
        guard let window = content.windows.first(where: { $0.windowID == wid && $0.owningApplication?.processID == request.pid }) else { throw CaptureError.wrongWindow }
        let filter = SCContentFilter(desktopIndependentWindow: window)
        let config = SCStreamConfiguration()
        let scale = request.client.scale
        config.width = Int((window.frame.width * scale).rounded())
        config.height = Int((window.frame.height * scale).rounded())
        config.showsCursor = false
        config.capturesAudio = false
        config.ignoreShadowsSingleWindow = true
        let crop = CGRect(x: Double(request.client.x) - window.frame.origin.x * scale,
                          y: Double(request.client.y) - window.frame.origin.y * scale,
                          width: Double(request.client.width), height: Double(request.client.height))
        guard crop.minX >= 0, crop.minY >= 0, crop.maxX <= Double(config.width), crop.maxY <= Double(config.height) else { throw CaptureError.captureSize }
        var captures: [(Double, Double, CGImage)] = []
        for _ in 0..<12 {
            try foreground(request.pid, wid)
            let started = ProcessInfo.processInfo.systemUptime
            let image = try await SCScreenshotManager.captureImage(contentFilter: filter, configuration: config)
            let captured = ProcessInfo.processInfo.systemUptime
            try foreground(request.pid, wid)
            guard image.width == config.width, image.height == config.height,
                  let client = image.cropping(to: crop), client.width == request.client.width,
                  client.height == request.client.height else { throw CaptureError.captureSize }
            captures.append((started * 1000, captured * 1000, client))
            let remaining = 0.08 - (ProcessInfo.processInfo.systemUptime - started)
            if remaining > 0 { try await Task.sleep(nanoseconds: UInt64(remaining * 1_000_000_000)) }
        }
        var frames: [[String: Any]] = []
        for (index, capture) in captures.enumerated() {
            let leaf = "\(request.checkpoint)-\(index).png"
            guard let dest = CGImageDestinationCreateWithURL(output.appendingPathComponent(leaf) as CFURL, UTType.png.identifier as CFString, 1, nil) else { throw CaptureError.pngWrite }
            CGImageDestinationAddImage(dest, capture.2, nil)
            guard CGImageDestinationFinalize(dest) else { throw CaptureError.pngWrite }
            frames.append(["file": leaf, "monotonicMs": capture.1, "requestStartedMs": capture.0])
        }
        let receipt: [String: Any] = ["pid": request.pid, "windowId": String(wid), "checkpoint": request.checkpoint,
                                    "captureAPI": "ScreenCaptureKit.SCScreenshotManager.desktopIndependentWindow", "frames": frames]
        try JSONSerialization.data(withJSONObject: receipt, options: .prettyPrinted).write(to: output.appendingPathComponent("\(request.checkpoint).native-frames.json"), options: .atomic)
    }
}
