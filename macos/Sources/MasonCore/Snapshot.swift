import Foundation

/// The project's files as they were at one moment, held in memory while a command runs, so that what the command
/// created, changed or deleted can be recorded and undone. Excluded folders (dependencies, build output, .git) are
/// not followed, the same as for the file tools.
public enum Snapshot {
    public struct Entry { let size: Int; let modified: Date; let data: Data }
    public struct Change { public let path: String; public let before: Data?; public let after: Data? }

    public static let maxFileBytes = 8 * 1024 * 1024
    public static let maxTotalBytes = 256 * 1024 * 1024
    public static let maxFiles = 5000

    /// Reads the project. A file whose size and modification time match `previous` is taken from there instead of
    /// being read again. `complete` is false when a limit left some files out; those are not tracked.
    public static func capture(_ workspace: Workspace, previous: [String: Entry] = [:]) -> (entries: [String: Entry], complete: Bool) {
        var entries: [String: Entry] = [:]
        var total = 0
        guard let listing = try? workspace.files(limit: maxFiles) else { return ([:], false) }
        var complete = !listing.truncated
        for path in listing.paths {
            let url = workspace.root.appendingPathComponent(path)
            guard let attributes = try? FileManager.default.attributesOfItem(atPath: url.path),
                  let size = (attributes[.size] as? NSNumber)?.intValue, let modified = attributes[.modificationDate] as? Date else { continue }
            if size > maxFileBytes || total + size > maxTotalBytes { complete = false; continue }
            if let known = previous[path], known.size == size, known.modified == modified { entries[path] = known; total += size; continue }
            guard let data = try? Data(contentsOf: url) else { continue }
            entries[path] = Entry(size: data.count, modified: modified, data: data)
            total += data.count
        }
        return (entries, complete)
    }

    /// What differs between two captures, in path order.
    public static func changes(from before: [String: Entry], to after: [String: Entry]) -> [Change] {
        var found: [Change] = []
        for (path, old) in before {
            if let new = after[path] { if new.data != old.data { found.append(Change(path: path, before: old.data, after: new.data)) } }
            else { found.append(Change(path: path, before: old.data, after: nil)) }
        }
        for (path, new) in after where before[path] == nil { found.append(Change(path: path, before: nil, after: new.data)) }
        return found.sorted { $0.path < $1.path }
    }
}

/// File contents that cannot live in the session as text (binary, or larger than the text limit), kept by SHA-256.
public struct BlobStore {
    let folder: URL
    public init(home: URL) { folder = home.appendingPathComponent("blobs") }

    public func put(_ data: Data) throws -> String {
        var hasher = Sha256(); hasher.update(data)
        let name = hasher.hex()
        let file = folder.appendingPathComponent(name)
        if !FileManager.default.fileExists(atPath: file.path) {
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            try data.write(to: file, options: .atomic)
        }
        return name
    }
    public func get(_ name: String) -> Data? {
        guard name.count == 64, name.allSatisfy({ $0.isHexDigit }) else { return nil }
        return try? Data(contentsOf: folder.appendingPathComponent(name))
    }
}

extension FileChange {
    /// True when either side is kept as a blob, so there is no text to show a diff for.
    public var isOpaque: Bool { beforeBlob != nil || afterBlob != nil }
    public var created: Bool { before == nil && beforeBlob == nil }
    public var deleted: Bool { afterMissing == true }

    /// The bytes on each side; nil means the file did not exist. Throws when a blob has gone missing.
    func beforeBytes(_ blobs: BlobStore) throws -> Data? {
        if let beforeBlob { guard let data = blobs.get(beforeBlob) else { throw MasonError("The saved copy of \(path) is missing.") }; return data }
        return before.map { Data($0.utf8) }
    }
    func afterBytes(_ blobs: BlobStore) throws -> Data? {
        if afterMissing == true { return nil }
        if let afterBlob { guard let data = blobs.get(afterBlob) else { throw MasonError("The saved copy of \(path) is missing.") }; return data }
        return Data(after.utf8)
    }
}
