import Foundation
#if canImport(IOKit)
import IOKit
#endif

// A live reading of the graphics processor: how busy it is and how much memory it has in use. On a Mac the
// processor publishes this in the I/O registry, and on Apple silicon its memory is the computer's own. Nothing is
// installed or changed to take a reading.

public struct GpuAdapter: Equatable {
    public var name: String
    /// nil when the driver does not say.
    public var busyPercent: Int?
    /// 0 when the processor shares the computer's memory and reports no total of its own.
    public var totalBytes: Int64
    public var usedBytes: Int64
}

public enum Gpu {
    /// One adapter from what a graphics accelerator publishes about itself in the I/O registry.
    static func adapter(from properties: [String: Any], physicalMemory: Int64) -> GpuAdapter? {
        guard let statistics = properties["PerformanceStatistics"] as? [String: Any] else { return nil }
        func whole(_ key: String) -> Int64? { (statistics[key] as? NSNumber)?.int64Value }
        var name = (properties["model"] as? String) ?? ""
        if name.isEmpty, let data = properties["model"] as? Data { name = String(decoding: data, as: UTF8.self).trimmingCharacters(in: CharacterSet(charactersIn: "\0")) }
        if name.isEmpty { name = (properties["IOClass"] as? String) ?? "Graphics processor" }
        let busy = whole("Device Utilization %") ?? whole("GPU Activity(%)")
        let used = whole("In use system memory") ?? whole("vramUsedBytes") ?? 0
        // A card with its own memory says how much is free; a processor that shares the computer's memory does not.
        var total: Int64 = 0
        if let free = whole("vramFreeBytes"), let taken = whole("vramUsedBytes") { total = free + taken }
        if total == 0, whole("In use system memory") != nil { total = physicalMemory }
        return GpuAdapter(name: name, busyPercent: busy.map { Int(min(100, max(0, $0))) }, totalBytes: total, usedBytes: used)
    }

    /// The graphics processors on this computer, read now.
    public static func read() -> (adapters: [GpuAdapter], note: String?) {
        var adapters: [GpuAdapter] = []
        #if canImport(IOKit)
        var iterator: io_iterator_t = 0
        if IOServiceGetMatchingServices(kIOMainPortDefault, IOServiceMatching("IOAccelerator"), &iterator) == KERN_SUCCESS {
            var service = IOIteratorNext(iterator)
            while service != 0 {
                var unmanaged: Unmanaged<CFMutableDictionary>?
                if IORegistryEntryCreateCFProperties(service, &unmanaged, kCFAllocatorDefault, 0) == KERN_SUCCESS, let dictionary = unmanaged?.takeRetainedValue() {
                    let properties = (dictionary as NSDictionary) as? [String: Any] ?? [:]
                    if let found = adapter(from: properties, physicalMemory: Int64(ProcessInfo.processInfo.physicalMemory)) { adapters.append(found) }
                }
                IOObjectRelease(service)
                service = IOIteratorNext(iterator)
            }
            IOObjectRelease(iterator)
        }
        #endif
        return (adapters, adapters.isEmpty ? "The graphics processor reports nothing about its use on this computer." : nil)
    }

    /// One adapter's reading as a line for a person: how busy, and its memory.
    public static func describe(_ adapter: GpuAdapter) -> String {
        var text = adapter.busyPercent.map { "\($0)% busy" } ?? "use not reported"
        if adapter.totalBytes > 0 { text += " · \(ByteText.size(adapter.usedBytes)) of \(ByteText.size(adapter.totalBytes)) in use" }
        else { text += " · shares the computer's memory" }
        return text
    }
}
