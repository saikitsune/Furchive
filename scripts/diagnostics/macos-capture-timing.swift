// Hosted CI-only timing probe. Does not capture pixels or log window metadata.
import Foundation
import CoreGraphics

@main struct Probe {
    static func now() -> Double { ProcessInfo.processInfo.systemUptime * 1000 }
    static func sleepSamples(_ label: String, precise: Bool) async throws -> [String: Any] {
        var samples: [Double] = []
        for _ in 0..<8 {
            let start = now()
            if precise { try await Task.sleep(for: .milliseconds(80), tolerance: .zero) }
            else { try await Task.sleep(nanoseconds: 80_000_000) }
            samples.append(now() - start)
        }
        return ["label": label, "requestedMs": 80, "actualMs": samples]
    }
    static func windowSamples(_ label: String, narrow: Bool, window: CGWindowID) -> [String: Any] {
        var samples: [Double] = []
        for _ in 0..<8 {
            let start = now()
            let option: CGWindowListOption = narrow ? [.optionOnScreenAboveWindow, .optionIncludingWindow] : .optionOnScreenOnly
            guard CGWindowListCopyWindowInfo(option, narrow ? window : kCGNullWindowID) != nil else { fatalError("WindowServer unavailable") }
            samples.append(now() - start)
        }
        return ["label": label, "actualMs": samples]
    }
    static func main() async throws {
        guard let rows = CGWindowListCopyWindowInfo(.optionOnScreenOnly, kCGNullWindowID) as? [[String: Any]],
              let first = rows.first(where: { ($0[kCGWindowLayer as String] as? Int) == 0 && ($0[kCGWindowAlpha as String] as? Double ?? 0) > 0 }),
              let window = first[kCGWindowNumber as String] as? UInt32 else { fatalError("No foreground normal window") }
        var results: [[String: Any]] = []
        results.append(windowSamples("full-on-screen-window-info", narrow: false, window: window))
        results.append(windowSamples("above-and-including-front-window-info", narrow: true, window: window))
        results.append(try await sleepSamples("default-sleep", precise: false))
        results.append(try await sleepSamples("zero-tolerance-sleep", precise: true))
        let activity = ProcessInfo.processInfo.beginActivity(options: [.userInitiated, .latencyCritical], reason: "Bounded hosted capture timing diagnostic")
        defer { ProcessInfo.processInfo.endActivity(activity) }
        results.append(try await sleepSamples("active-default-sleep", precise: false))
        results.append(try await sleepSamples("active-zero-tolerance-sleep", precise: true))
        let data = try JSONSerialization.data(withJSONObject: ["schema": 1, "results": results], options: [.prettyPrinted, .sortedKeys])
        print(String(decoding: data, as: UTF8.self))
    }
}
