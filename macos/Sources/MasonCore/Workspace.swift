import Foundation
#if canImport(Darwin)
import Darwin
#else
import Glibc
#endif

/// The path with every link followed, as the system itself names it. On macOS this keeps the /private prefix of
/// temporary folders, which the sandbox and directory listings use; Foundation's own resolution drops it.
func realPath(_ path: String) -> String {
    guard let resolved = realpath(path, nil) else { return path }
    defer { free(resolved) }
    return String(cString: resolved)
}

public struct MasonError: Error, CustomStringConvertible, LocalizedError {
    public let description: String
    /// True when the model server could not be reached or answered with an HTTP error, so the run ends as model_unavailable.
    public var modelUnavailable = false
    public init(_ description: String) { self.description = description }
    public static func unavailable(_ description: String) -> MasonError { var error = MasonError(description); error.modelUnavailable = true; return error }
    /// The HTTP status the model server answered with, when it answered with an error.
    public var httpStatus: Int?
    public static func http(_ status: Int, _ description: String) -> MasonError { var error = unavailable(description); error.httpStatus = status; return error }
    /// True when a path was rejected: outside the project, absolute, or naming excluded content.
    public var invalidPath = false
    static func rejected(_ description: String) -> MasonError { var error = MasonError(description); error.invalidPath = true; return error }
    public var errorDescription: String? { description }
}

public struct SearchMatch: Equatable {
    public let path: String
    public let line: Int
    public let text: String
}

/// A project folder. Every file tool goes through `resolve`, which keeps paths inside the folder,
/// refuses links that lead out of it, and never addresses credentials or dependency folders.
public struct Workspace {
    public let root: URL

    static let excludedFolders: Set<String> = [
        ".git", ".svn", ".hg", ".ssh", ".aws", ".azure", ".gnupg", "node_modules", ".build", "build", "DerivedData", "Pods",
        "vendor", ".venv", "venv", "dist", "target", ".cache", "__pycache__", ".mason-sandbox",
    ]
    static let secretSuffixes = [".pem", ".key", ".pfx", ".p12", ".kdbx", ".keychain", ".keychain-db", ".mobileprovision"]
    public static let maxFileBytes = 2 * 1024 * 1024

    public init(path: String) throws {
        let url = URL(fileURLWithPath: realPath(URL(fileURLWithPath: path).standardizedFileURL.path))
        var isDirectory: ObjCBool = false
        guard FileManager.default.fileExists(atPath: url.path, isDirectory: &isDirectory), isDirectory.boolValue else {
            throw MasonError("Open an existing project folder.")
        }
        root = url
    }

    static func forbidden(_ name: String) -> Bool {
        let lower = name.lowercased()
        return excludedFolders.contains(name) || lower == ".env" || lower.hasPrefix(".env.") || lower.hasPrefix("id_rsa") || lower.hasPrefix("id_ed25519")
            || secretSuffixes.contains { lower.hasSuffix($0) }
    }

    /// The file URL for a project-relative path, or an error when the path leaves the project or names excluded content.
    public func resolve(_ relative: String) throws -> URL {
        let shown = relative.count > 160 ? String(relative.prefix(160)) + "…" : relative
        guard !relative.isEmpty, relative.count <= 1000, !relative.hasPrefix("/"), !relative.hasPrefix("~"),
              !relative.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) }) else {
            throw MasonError.rejected("Rejected path \"\(shown)\". Use a path relative to the project folder.")
        }
        let parts = relative.split(separator: "/", omittingEmptySubsequences: true).map(String.init).filter { $0 != "." }
        guard !parts.contains(".."), !parts.contains(where: Workspace.forbidden) else {
            throw MasonError.rejected("Rejected path \"\(shown)\". It is outside the project or addresses excluded content.")
        }
        let url = parts.reduce(root) { $0.appendingPathComponent($1) }
        // A link inside the project must not lead out of it. For a file that does not exist yet, check its nearest existing parent.
        var existing = url
        while !FileManager.default.fileExists(atPath: existing.path), existing.path.count > root.path.count { existing.deleteLastPathComponent() }
        let real = realPath(existing.path)
        guard real == root.path || real.hasPrefix(root.path + "/") else {
            throw MasonError.rejected("Rejected path \"\(shown)\". It follows a link out of the project.")
        }
        return url
    }

    public func read(_ relative: String) throws -> String {
        let url = try resolve(relative)
        var isDirectory: ObjCBool = false
        guard FileManager.default.fileExists(atPath: url.path, isDirectory: &isDirectory) else { throw MasonError("\(relative) does not exist.") }
        guard !isDirectory.boolValue else { throw MasonError("\(relative) is a folder. Use list_files.") }
        let data = try Data(contentsOf: url)
        guard data.count <= Workspace.maxFileBytes else { throw MasonError("\(relative) is larger than 2 MiB.") }
        guard let text = String(data: data, encoding: .utf8) else { throw MasonError("\(relative) is not UTF-8 text.") }
        return text
    }

    public func exists(_ relative: String) -> Bool {
        guard let url = try? resolve(relative) else { return false }
        return FileManager.default.fileExists(atPath: url.path)
    }

    public func write(_ relative: String, _ content: String) throws {
        let url = try resolve(relative)
        guard content.utf8.count <= Workspace.maxFileBytes else { throw MasonError("Refusing to write more than 2 MiB to \(relative).") }
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data(content.utf8).write(to: url, options: .atomic)
    }

    /// A file's bytes, or nil when it does not exist. Used to compare and restore files of any kind.
    public func data(_ relative: String) throws -> Data? {
        let url = try resolve(relative)
        guard FileManager.default.fileExists(atPath: url.path) else { return nil }
        return try Data(contentsOf: url)
    }

    public func write(_ relative: String, data: Data) throws {
        let url = try resolve(relative)
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try data.write(to: url, options: .atomic)
    }

    public func delete(_ relative: String) throws {
        let url = try resolve(relative)
        if FileManager.default.fileExists(atPath: url.path) { try FileManager.default.removeItem(at: url) }
    }

    /// Project-relative file paths in a stable order, skipping excluded folders. `truncated` is true when the limit cut the listing.
    public func files(under relative: String = ".", limit: Int = 2000) throws -> (paths: [String], truncated: Bool) {
        let start = relative == "." || relative.isEmpty ? root : try resolve(relative)
        var found: [String] = []
        // Each folder travels with its project-relative prefix, so the result never depends on how the system spells the root.
        let startPrefix = start.path == root.path ? "" : String(start.path.dropFirst(root.path.count + 1)) + "/"
        var pending = [(start, startPrefix)]
        var truncated = false
        while let (folder, prefix) = pending.popLast(), !truncated {
            let entries = (try? FileManager.default.contentsOfDirectory(at: folder, includingPropertiesForKeys: [.isDirectoryKey, .isSymbolicLinkKey])) ?? []
            for entry in entries.sorted(by: { $0.lastPathComponent < $1.lastPathComponent }) {
                let name = entry.lastPathComponent
                if Workspace.forbidden(name) || name == ".DS_Store" { continue }
                let values = try? entry.resourceValues(forKeys: [.isDirectoryKey, .isSymbolicLinkKey])
                if values?.isSymbolicLink == true { continue }
                if values?.isDirectory == true { pending.append((entry, prefix + name + "/")); continue }
                if found.count >= limit { truncated = true; break }
                found.append(prefix + name)
            }
        }
        return (found.sorted(), truncated)
    }

    /// Case-insensitive text search over the project's UTF-8 files.
    public func search(_ query: String, limit: Int = 100, skip: Int = 0) throws -> [SearchMatch] {
        guard !query.isEmpty, query.count <= 400 else { throw MasonError("Search for 1–400 characters.") }
        var matches: [SearchMatch] = []
        var seen = 0
        for path in try files(limit: 5000).paths {
            guard let text = try? read(path) else { continue }
            var number = 0
            for line in text.split(separator: "\n", omittingEmptySubsequences: false) {
                number += 1
                if line.range(of: query, options: .caseInsensitive) != nil {
                    seen += 1
                    if seen <= skip { continue }
                    matches.append(SearchMatch(path: path, line: number, text: String(line.prefix(300))))
                    if matches.count >= limit { return matches }
                }
            }
        }
        return matches
    }
}
