// Hosted CI-only timing probe. Does not capture pixels or log window metadata.
import Foundation
import CoreGraphics
import AppKit
import Darwin

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
    static func blockingSleepSamples(_ label: String, useThread: Bool) -> [String: Any] {
        var samples: [Double] = []
        for _ in 0..<8 {
            let start = now()
            if useThread { Thread.sleep(forTimeInterval: 0.08) }
            else { usleep(80_000) }
            samples.append(now() - start)
        }
        return ["label": label, "requestedMs": 80, "actualMs": samples]
    }
    static func measuredDeadlineSamples() -> [String: Any] {
        var samples: [Double] = []
        for _ in 0..<8 {
            let start = now()
            while now() - start < 80 { usleep(1_000) }
            samples.append(now() - start)
        }
        return ["label": "measured-clock-short-sleep-deadline", "requestedMs": 80, "actualMs": samples]
    }
    static func machSleepSamples() -> [String: Any] {
        var info = mach_timebase_info_data_t()
        guard mach_timebase_info(&info) == KERN_SUCCESS else { fatalError("No Mach timebase") }
        var samples: [Double] = []
        for _ in 0..<8 {
            let start = now()
            let ticks = UInt64(80_000_000) * UInt64(info.denom) / UInt64(info.numer)
            guard mach_wait_until(mach_absolute_time() + ticks) == KERN_SUCCESS else { fatalError("Mach wait refused") }
            samples.append(now() - start)
        }
        return ["label": "mach-absolute-deadline", "requestedMs": 80, "actualMs": samples]
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
    @MainActor static func main() async throws {
        let app = NSApplication.shared
        app.setActivationPolicy(.regular)
        let owned = NSWindow(contentRect: NSRect(x: 100, y: 100, width: 320, height: 200), styleMask: [.titled], backing: .buffered, defer: false)
        owned.title = "Furchive timing probe (no media)"
        owned.makeKeyAndOrderFront(nil)
        app.activate(ignoringOtherApps: true)
        try await Task.sleep(for: .milliseconds(300), tolerance: .zero)
        defer { owned.orderOut(nil) }
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
        results.append(blockingSleepSamples("thread-relative-sleep", useThread: true))
        results.append(blockingSleepSamples("posix-relative-sleep", useThread: false))
        results.append(measuredDeadlineSamples())
        results.append(machSleepSamples())
        results.append(try await sleepSamples("active-zero-tolerance-sleep", precise: true))
        let data = try JSONSerialization.data(withJSONObject: ["schema": 1, "results": results], options: [.prettyPrinted, .sortedKeys])
        print(String(decoding: data, as: UTF8.self))
    }
}
