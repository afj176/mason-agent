import Foundation
#if canImport(Darwin)
import Darwin
#else
import Glibc
#endif

/// Styled terminal output with one transient status line, and single-key input for approvals and stopping a run.
/// Falls back to plain text when output is not a terminal or NO_COLOR is set.
enum Term {
    static let styled = isatty(1) != 0 && (ProcessInfo.processInfo.environment["NO_COLOR"] ?? "").isEmpty
    static let interactive = isatty(0) != 0 && isatty(1) != 0
    private static let lock = NSLock()
    private static var status = ""
    private static var saved: termios?

    static let reset = "\u{1B}[0m", bold = "\u{1B}[1m", dim = "\u{1B}[2m"
    static let accent = "\u{1B}[38;2;195;194;255m", green = "\u{1B}[38;2;120;200;140m", red = "\u{1B}[38;2;240;120;120m"
    static let yellow = "\u{1B}[38;2;230;190;110m", cyan = "\u{1B}[38;2;130;190;220m"

    static func paint(_ style: String, _ text: String) -> String { styled && !text.isEmpty ? style + text + reset : text }

    static var width: Int {
        var size = winsize()
        return ioctl(1, UInt(TIOCGWINSZ), &size) == 0 && size.ws_col > 0 ? min(max(Int(size.ws_col), 40), 200) : 100
    }
    static func clip(_ text: String, _ width: Int) -> String { width < 2 || text.count <= width ? text : String(text.prefix(width - 1)) + "…" }

    private static func put(_ text: String) { FileHandle.standardOutput.write(Data(text.utf8)) }
    // In raw mode the terminal no longer turns "\n" into a new line at column one, so lines end with "\r\n".
    private static var newline: String { saved == nil ? "\n" : "\r\n" }

    /// Writes a complete line above the status line.
    static func line(_ text: String = "") {
        lock.lock(); defer { lock.unlock() }
        if !status.isEmpty { put("\r\u{1B}[2K") }
        put(text.replacingOccurrences(of: "\n", with: newline) + newline)
        if !status.isEmpty { put(dim + status + reset) }
    }
    /// Writes text without a newline and clears the status line; used for prompts.
    static func write(_ text: String) {
        lock.lock(); defer { lock.unlock() }
        if !status.isEmpty { put("\r\u{1B}[2K"); status = "" }
        put(text)
    }
    /// Replaces the transient status line; an empty value removes it. Ignored when output is not a terminal.
    static func setStatus(_ text: String) {
        guard styled else { return }
        lock.lock(); defer { lock.unlock() }
        let next = clip(text.replacingOccurrences(of: "\n", with: " "), width - 1)
        guard next != status else { return }
        if !status.isEmpty { put("\r\u{1B}[2K") }
        status = next
        if !status.isEmpty { put(dim + status + reset) }
    }

    /// Raw mode delivers each key as it is pressed, without echo, and Ctrl+C as a key instead of a signal.
    static func raw(_ on: Bool) {
        guard isatty(0) != 0 else { return }
        if on {
            guard saved == nil else { return }
            var current = termios()
            guard tcgetattr(0, &current) == 0 else { return }
            saved = current
            current.c_lflag &= ~tcflag_t(ECHO | ICANON | ISIG)
            withUnsafeMutablePointer(to: &current.c_cc) { pointer in
                pointer.withMemoryRebound(to: cc_t.self, capacity: Int(NCCS)) { cc in cc[Int(VMIN)] = 0; cc[Int(VTIME)] = 1 }
            }
            tcsetattr(0, TCSANOW, &current)
        } else if var previous = saved {
            tcsetattr(0, TCSANOW, &previous)
            saved = nil
        }
    }
    /// One key in raw mode, or nil after a tenth of a second with none.
    static func key() -> UInt8? {
        var byte: UInt8 = 0
        return read(0, &byte, 1) == 1 ? byte : nil
    }
    /// True when the key just read (27) was the Esc key itself and not the start of an arrow or function key sequence.
    static func wasEscapeAlone() -> Bool {
        guard key() != nil else { return true }
        while key() != nil {}
        return false
    }
    static func drainKeys() { while key() != nil {} }

    /// Markdown for a terminal: fenced code, headings, bullets, bold and inline code. Other syntax is left as written.
    static func markdown(_ text: String) -> [String] {
        var code = false
        return text.replacingOccurrences(of: "\r\n", with: "\n").split(separator: "\n", omittingEmptySubsequences: false).map { raw in
            let line = String(raw)
            let trimmed = line.trimmingCharacters(in: .whitespaces)
            if trimmed.hasPrefix("```") {
                code.toggle()
                return paint(dim, code ? "  ┌─ " + trimmed.dropFirst(3) : "  └─")
            }
            if code { return paint(dim, "  │ ") + paint(cyan, line) }
            if trimmed.hasPrefix("#") { return paint(bold + accent, trimmed.drop { $0 == "#" }.trimmingCharacters(in: .whitespaces)) }
            let indent = String(line.prefix { $0 == " " })
            if trimmed.hasPrefix("- ") || trimmed.hasPrefix("* ") { return indent + paint(accent, "•") + " " + inline(String(trimmed.dropFirst(2))) }
            return indent + inline(trimmed)
        }
    }
    static func inline(_ text: String) -> String {
        guard styled else { return text }
        var result = "", rest = Substring(text)
        while let start = rest.firstIndex(where: { $0 == "`" || $0 == "*" }) {
            result += rest[..<start]
            let after = rest[rest.index(after: start)...]
            if rest[start] == "`", let end = after.firstIndex(of: "`") {
                result += cyan + after[..<end] + reset; rest = after[after.index(after: end)...]
            } else if rest[start...].hasPrefix("**"), let end = after.dropFirst().range(of: "**") {
                result += bold + after.dropFirst()[..<end.lowerBound] + reset; rest = after[end.upperBound...]
            } else {
                result.append(rest[start]); rest = after
            }
        }
        return result + rest
    }
}
