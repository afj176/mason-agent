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

#include "util.hpp"

namespace term {

inline const bool styled = isatty(1) && (!std::getenv("NO_COLOR") || !*std::getenv("NO_COLOR"));
inline const bool interactive = isatty(0) && isatty(1);

inline const char* const reset = "\x1b[0m";
inline const char* const bold = "\x1b[1m";
inline const char* const dim = "\x1b[2m";
inline const char* const accent = "\x1b[38;2;195;194;255m";
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
inline std::string pad(const std::string& text, size_t columns) {
    const size_t length = mason::utf8_length(text);
    return length >= columns ? text : text + std::string(columns - length, ' ');
}
inline std::string left_pad(const std::string& text, size_t columns) { return text.size() >= columns ? text : std::string(columns - text.size(), ' ') + text; }

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
