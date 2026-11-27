// Styled terminal output with one transient status line, and single-key input for approvals and stopping a run.
// Falls back to plain text when output is not a terminal or NO_COLOR is set.
#pragma once
#include <algorithm>
#include <cstdio>
#include <cstdlib>
#include <mutex>
#include <string>
#include <vector>
#include <sys/ioctl.h>
#include <termios.h>
#include <unistd.h>

#include "pixel_icon.hpp"
#include "theme.hpp"
#include "util.hpp"

namespace term {

inline const bool styled = isatty(1) && (!std::getenv("NO_COLOR") || !*std::getenv("NO_COLOR"));
inline const bool interactive = isatty(0) && isatty(1);

inline const char* const reset = "\x1b[0m";
inline const char* const bold = "\x1b[1m";
inline const char* const dim = "\x1b[2m";
/// The theme family's accent; use_theme sets it from the family saved in appearance.json.
inline const char* accent = "\x1b[38;2;180;184;255m";
inline const char* const green = "\x1b[38;2;120;200;140m";
inline const char* const red = "\x1b[38;2;240;120;120m";
inline const char* const yellow = "\x1b[38;2;230;190;110m";
inline const char* const cyan = "\x1b[38;2;130;190;220m";

inline std::string paint(const std::string& style, const std::string& text) { return styled && !text.empty() ? style + text + reset : text; }

inline int width() {
    winsize size{};
    if (ioctl(1, TIOCGWINSZ, &size) == 0 && size.ws_col > 0) return std::min(std::max<int>(size.ws_col, 40), 200);
    return 100;
}
inline std::string clip(const std::string& text, int limit) {
    if (limit < 2 || static_cast<int>(mason::utf8_length(text)) <= limit) return text;
    return mason::utf8_prefix(text, static_cast<size_t>(limit - 1)) + "…";
}
/// Breaks text into lines of at most `columns` characters at spaces; a word longer than a line stays whole.
inline std::vector<std::string> wrap(const std::string& text, int columns) {
    std::vector<std::string> lines;
    std::string current;
    for (const std::string& word : mason::split(text, ' ', false)) {
        if (!current.empty() && static_cast<int>(mason::utf8_length(current) + 1 + mason::utf8_length(word)) > columns) {
            lines.push_back(current);
            current.clear();
        }
        current += (current.empty() ? "" : " ") + word;
    }
    if (!current.empty()) lines.push_back(current);
    return lines;
}
inline std::string pad(const std::string& text, size_t columns) {
    const size_t length = mason::utf8_length(text);
    return length >= columns ? text : text + std::string(columns - length, ' ');
}
inline std::string left_pad(const std::string& text, size_t columns) { return text.size() >= columns ? text : std::string(columns - text.size(), ' ') + text; }
/// Right-aligns text to `columns` characters, counting multi-byte characters such as ● once.
inline std::string right_align(const std::string& text, size_t columns) {
    const size_t length = mason::utf8_length(text);
    return length >= columns ? text : std::string(columns - length, ' ') + text;
}
inline int height() {
    winsize size{};
    if (ioctl(1, TIOCGWINSZ, &size) == 0 && size.ws_row > 0) return size.ws_row;
    return 24;
}
/// The selected cell of a picker: accent background with the family's on-accent text.
inline const char* selected = "\x1b[1;48;2;180;184;255;38;2;21;27;48m";
inline const char* const inverse = "\x1b[7m";

/// The theme family chosen in Mason Agent's Appearance or with /theme (appearance.json), used for the accent colour.
inline const mason::theme::Family* family = &mason::theme::default_family();

/// A light terminal background gets the family's light-mode accent. COLORFGBG ("15;0") is the only widely set
/// hint; without it the terminal is taken to be dark, as most are.
inline bool light_background() {
    const char* colors = std::getenv("COLORFGBG");
    if (!colors || !*colors) return false;
    const std::string text = colors;
    const std::string last = text.substr(text.rfind(';') == std::string::npos ? 0 : text.rfind(';') + 1);
    return last == "7" || last == "15";
}

inline std::string rgb_code(uint32_t c) { return std::to_string((c >> 16) & 0xFF) + ";" + std::to_string((c >> 8) & 0xFF) + ";" + std::to_string(c & 0xFF); }

inline void use_theme(const mason::theme::Family& chosen) {
    static std::string accent_code, selected_code;
    family = &chosen;
    const mason::theme::Tokens& tokens = chosen.tokens(!light_background());
    accent_code = "\x1b[38;2;" + rgb_code(tokens.accent) + "m";
    selected_code = "\x1b[1;48;2;" + rgb_code(tokens.accent) + ";38;2;" + rgb_code(tokens.on_accent) + "m";
    accent = accent_code.c_str();
    selected = selected_code.c_str();
}

/// The app icon drawn with half blocks (two pixel rows per line) in 24-bit colour, one string per line.
/// Empty when colour is off or the terminal is narrower than 80 columns.
inline std::vector<std::string> picture() {
    std::vector<std::string> lines;
    if (!styled || width() < 80) return lines;
    auto rgb = [](const char* hex) { return std::to_string(std::stoi(std::string(hex, 2), nullptr, 16)) + ";" + std::to_string(std::stoi(std::string(hex + 2, 2), nullptr, 16)) + ";" + std::to_string(std::stoi(std::string(hex + 4, 2), nullptr, 16)); };
    for (int row = 0; row < pixel_icon::size / 2; row++) {
        std::string line;
        const char* top = pixel_icon::pixels[row * 2];
        const char* bottom = pixel_icon::pixels[row * 2 + 1];
        for (int x = 0; x < pixel_icon::size; x++) {
            const char* upper = top + x * 6;
            const char* lower = bottom + x * 6;
            const bool upper_clear = upper[0] == '.', lower_clear = lower[0] == '.';
            if (upper_clear && lower_clear) line += std::string(reset) + " ";
            else if (lower_clear) line += std::string(reset) + "\x1b[38;2;" + rgb(upper) + "m▀";
            else if (upper_clear) line += std::string(reset) + "\x1b[38;2;" + rgb(lower) + "m▄";
            else line += "\x1b[38;2;" + rgb(upper) + "m\x1b[48;2;" + rgb(lower) + "m▀";
        }
        lines.push_back(line + reset);
    }
    return lines;
}

namespace detail {
inline std::mutex lock;
inline std::string status;
inline bool raw_on = false;
inline termios saved{};
inline void put(const std::string& text) {
    std::fwrite(text.data(), 1, text.size(), stdout);
    std::fflush(stdout);
}
}  // namespace detail

/// Writes a complete line above the status line.
inline void line(const std::string& text = "") {
    std::lock_guard<std::mutex> guard(detail::lock);
    // In raw mode the terminal no longer turns "\n" into a new line at column one.
    const std::string newline = detail::raw_on ? "\r\n" : "\n";
    std::string out = detail::status.empty() ? "" : "\r\x1b[2K";
    out += mason::replace_all(text, "\n", newline) + newline;
    if (!detail::status.empty()) out += std::string(dim) + detail::status + reset;
    detail::put(out);
}
/// Writes text without a newline and clears the status line; used for prompts.
inline void write(const std::string& text) {
    std::lock_guard<std::mutex> guard(detail::lock);
    std::string out = detail::status.empty() ? "" : "\r\x1b[2K";
    detail::status.clear();
    detail::put(out + text);
}
/// Replaces the transient status line; an empty value removes it. Ignored when output is not a terminal.
inline void set_status(const std::string& text) {
    if (!styled) return;
    std::lock_guard<std::mutex> guard(detail::lock);
    const std::string next = clip(mason::replace_all(text, "\n", " "), width() - 1);
    if (next == detail::status) return;
    std::string out = detail::status.empty() ? "" : "\r\x1b[2K";
    detail::status = next;
    if (!next.empty()) out += std::string(dim) + next + reset;
    detail::put(out);
}

/// Raw mode delivers each key as it is pressed, without echo, and Ctrl+C as a key instead of a signal.
inline void raw(bool on) {
    if (!isatty(0)) return;
    std::lock_guard<std::mutex> guard(detail::lock);
    if (on && !detail::raw_on) {
        if (tcgetattr(0, &detail::saved) != 0) return;
        termios current = detail::saved;
        current.c_lflag &= ~static_cast<tcflag_t>(ECHO | ICANON | ISIG);
        current.c_cc[VMIN] = 0;
        current.c_cc[VTIME] = 1;
        tcsetattr(0, TCSANOW, &current);
        detail::raw_on = true;
    } else if (!on && detail::raw_on) {
        tcsetattr(0, TCSANOW, &detail::saved);
        detail::raw_on = false;
    }
}
/// One key in raw mode, or -1 after a tenth of a second with none.
inline int key() {
    unsigned char byte = 0;
    return read(0, &byte, 1) == 1 ? byte : -1;
}
/// True when the key just read (27) was the Esc key itself and not the start of an arrow or function key sequence.
inline bool was_escape_alone() {
    if (key() < 0) return true;
    while (key() >= 0) {}
    return false;
}
inline void drain_keys() { while (key() >= 0) {} }

/// Keys beyond single bytes, as `read_key` returns them.
enum Key : int { kNoKey = -1, kEnter = 10, kUp = 1001, kDown, kLeft, kRight, kEscape };
/// One key in raw mode, with arrow keys and Esc told apart, or kNoKey after a tenth of a second with none.
inline int read_key() {
    const int first = key();
    if (first == 13 || first == 10) return kEnter;
    if (first != 27) return first;
    const int second = key();
    if (second < 0) return kEscape;
    if (second != '[' && second != 'O') { drain_keys(); return kNoKey; }
    int code = key();
    // Modified arrows send parameters first, such as ESC [ 1 ; 5 C.
    while (code >= '0' && code <= ';') code = key();
    switch (code) {
        case 'A': return kUp;
        case 'B': return kDown;
        case 'C': return kRight;
        case 'D': return kLeft;
        default: drain_keys(); return kNoKey;
    }
}

/// Draws a block of lines in place: the block drawn before (`drawn` lines, ending where the cursor is) is replaced.
inline void draw(const std::vector<std::string>& lines, int& drawn) {
    std::lock_guard<std::mutex> guard(detail::lock);
    std::string out = drawn > 0 ? "\x1b[" + std::to_string(drawn) + "A" : "";
    for (const std::string& text : lines) out += "\r\x1b[2K" + text + "\r\n";
    const int extra = drawn - static_cast<int>(lines.size());
    for (int i = 0; i < extra; i++) out += "\r\x1b[2K\r\n";
    if (extra > 0) out += "\x1b[" + std::to_string(extra) + "A";
    drawn = static_cast<int>(lines.size());
    detail::put(out);
}
/// Replaces the line `up` lines above the cursor and puts the cursor back.
inline void rewrite_above(int up, const std::string& text) {
    std::lock_guard<std::mutex> guard(detail::lock);
    detail::put("\x1b[" + std::to_string(up) + "A\r\x1b[2K" + text + "\x1b[" + std::to_string(up) + "B\r");
}
/// Replaces the line the cursor is on, without a newline.
inline void overwrite(const std::string& text) {
    std::lock_guard<std::mutex> guard(detail::lock);
    detail::put("\r\x1b[2K" + text);
}

inline std::string inline_markdown(const std::string& text) {
    if (!styled) return text;
    std::string out;
    size_t at = 0;
    while (at < text.size()) {
        if (text[at] == '`') {
            const size_t end = text.find('`', at + 1);
            if (end != std::string::npos) {
                out += cyan + text.substr(at + 1, end - at - 1) + reset;
                at = end + 1;
                continue;
            }
        }
        if (text.compare(at, 2, "**") == 0) {
            const size_t end = text.find("**", at + 2);
            if (end != std::string::npos) {
                out += bold + text.substr(at + 2, end - at - 2) + reset;
                at = end + 2;
                continue;
            }
        }
        out += text[at++];
    }
    return out;
}

/// Markdown for a terminal: fenced code, headings, bullets, bold and inline code. Other syntax is left as written.
inline std::vector<std::string> markdown(const std::string& text) {
    std::vector<std::string> lines;
    bool code = false;
    for (const std::string& raw_line : mason::split(mason::replace_all(text, "\r\n", "\n"), '\n')) {
        const std::string trimmed = mason::trim(raw_line);
        if (mason::starts_with(trimmed, "```")) {
            code = !code;
            lines.push_back(paint(dim, code ? "  ┌─ " + trimmed.substr(3) : "  └─"));
            continue;
        }
        if (code) {
            lines.push_back(paint(dim, "  │ ") + paint(cyan, raw_line));
            continue;
        }
        if (mason::starts_with(trimmed, "#")) {
            lines.push_back(paint(std::string(bold) + accent, mason::trim(trimmed.substr(trimmed.find_first_not_of('#') == std::string::npos ? trimmed.size() : trimmed.find_first_not_of('#')))));
            continue;
        }
        const std::string indent = raw_line.substr(0, raw_line.find_first_not_of(' ') == std::string::npos ? 0 : raw_line.find_first_not_of(' '));
        if (mason::starts_with(trimmed, "- ") || mason::starts_with(trimmed, "* ")) lines.push_back(indent + paint(accent, "•") + " " + inline_markdown(trimmed.substr(2)));
        else lines.push_back(indent + inline_markdown(trimmed));
    }
    return lines;
}

}  // namespace term
