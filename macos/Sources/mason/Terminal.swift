import Foundation
import MasonCore
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
    static let green = "\u{1B}[38;2;120;200;140m", red = "\u{1B}[38;2;240;120;120m"
    static let yellow = "\u{1B}[38;2;230;190;110m", cyan = "\u{1B}[38;2;130;190;220m"

    /// The theme family chosen in Mason Agent's Settings or with `mason theme` (appearance.json), used for the accent colour.
    private(set) static var theme = ThemeFamilies.saved()
    /// Mason's accent, from the theme family.
    private(set) static var accent = accentStyle(theme)
    /// The highlighted row or cell: the family's accent as the background.
    private(set) static var selected = selectedStyle(theme)

    /// A light terminal background gets the family's light-mode accent. COLORFGBG ("15;0") is the only widely set hint;
    /// without it the terminal is taken to be dark, as most are.
    static var lightBackground: Bool {
        guard let colors = ProcessInfo.processInfo.environment["COLORFGBG"], let last = colors.split(separator: ";").last, let background = Int(last) else { return false }
        return background == 7 || background == 15
    }
    static func rgb(_ color: UInt32) -> String {
        let c = ThemeFamilies.channels(color)
        return "\(c.red);\(c.green);\(c.blue)"
    }
    private static func accentStyle(_ family: ThemeFamily) -> String { "\u{1B}[38;2;\(rgb(family.tokens(dark: !lightBackground).accent))m" }
    private static func selectedStyle(_ family: ThemeFamily) -> String {
        let tokens = family.tokens(dark: !lightBackground)
        return "\u{1B}[48;2;\(rgb(tokens.accent))m\u{1B}[38;2;\(rgb(tokens.onAccent))m\u{1B}[1m"
    }
    static func useTheme(_ family: ThemeFamily) {
        theme = family
        accent = accentStyle(family)
        selected = selectedStyle(family)
    }

    /// The app icon drawn with upper half blocks (two pixel rows per line) in 24-bit colour, one string per line.
    /// Empty when colour is off or the terminal is narrower than 80 columns.
    static func picture() -> [String] {
        guard styled, width >= 80 else { return [] }
        let rows = PixelBear.pixels.map { Array($0) }
        func cell(_ row: [Character], _ x: Int) -> String { String(row[(x * 6)..<(x * 6 + 6)]) }
        func color(_ hex: String) -> String {
            let value = UInt32(hex, radix: 16) ?? 0
            return rgb(value)
        }
        var lines: [String] = []
        for row in 0..<(PixelBear.size / 2) {
            let top = rows[row * 2], bottom = rows[row * 2 + 1]
            var line = ""
            for x in 0..<PixelBear.size {
                let upper = cell(top, x), lower = cell(bottom, x)
                if upper == "......" && lower == "......" { line += reset + " " }
                else if lower == "......" { line += reset + "\u{1B}[38;2;\(color(upper))m▀" }
                else if upper == "......" { line += reset + "\u{1B}[38;2;\(color(lower))m▄" }
                else { line += "\u{1B}[38;2;\(color(upper))m\u{1B}[48;2;\(color(lower))m▀" }
            }
            lines.append(line + reset)
        }
        return lines
    }

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

    /// True when output goes to a terminal, so lines can be redrawn in place (with or without colour).
    static let tty = isatty(1) != 0
    /// Terminal rows; 24 when it does not say.
    static var height: Int {
        var size = winsize()
        return ioctl(1, UInt(TIOCGWINSZ), &size) == 0 && size.ws_row > 0 ? Int(size.ws_row) : 24
    }

    enum Key: Equatable { case up, down, left, right, enter, escape, interrupt, character(Character), other }
    /// Waits for one key in raw mode and names it; arrows arrive as escape sequences.
    static func readKey() -> Key {
        while true {
            guard let byte = key() else { continue }
            switch byte {
            case 27:
                guard let next = key() else { return .escape }
                guard next == 91 || next == 79, let final = key() else { drainKeys(); return .escape }
                switch final {
                case 65: return .up
                case 66: return .down
                case 67: return .right
                case 68: return .left
                default: drainKeys(); return .other
                }
            case 13, 10: return .enter
            case 3: return .interrupt
            case 32..<127: return .character(Character(UnicodeScalar(byte)))
            default: drainKeys(); return .other
            }
        }
    }

    /// Writes raw text, for drawing that moves the cursor itself.
    static func emit(_ text: String) {
        lock.lock(); defer { lock.unlock() }
        put(text)
    }
    /// Replaces the line the cursor is on, without ending it.
    static func redrawLine(_ text: String) { emit("\r\u{1B}[2K" + text) }
    /// Replaces the line the cursor is on and ends it.
    static func finishLine(_ text: String) { emit("\r\u{1B}[2K" + text + newline) }
    /// Replaces the `previous` lines just written with `lines`, cursor ending below them.
    static func redraw(_ lines: [String], previous: Int) {
        var text = previous > 0 ? "\u{1B}[\(previous)A\r\u{1B}[J" : ""
        for line in lines { text += line + newline }
        emit(text)
    }
    /// Rewrites one line `up` lines above the cursor and returns to where the cursor was.
    static func rewrite(up: Int, _ text: String) {
        guard up > 0 else { return }
        emit("\u{1B}[\(up)A\r\u{1B}[2K" + text + "\r\u{1B}[\(up)B")
    }
    /// Words wrapped to a width, for text printed under a picker that is redrawn by line count.
    static func wrap(_ text: String, _ width: Int) -> [String] {
        var lines: [String] = [], current = ""
        for word in text.split(separator: " ") {
            if !current.isEmpty, current.count + 1 + word.count > width { lines.append(current); current = "" }
            current += (current.isEmpty ? "" : " ") + word
        }
        if !current.isEmpty { lines.append(current) }
        return lines
    }

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
