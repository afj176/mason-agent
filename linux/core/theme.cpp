#include "theme.hpp"

#include <cmath>
#include <cstdio>

namespace mason::theme {

std::string Family::slug() const { return replace_all(to_lower(name), " ", "-"); }

const std::vector<Family>& families() {
    // Values were sampled from the approved light and dark reference images (same as the Windows app).
    static const std::vector<Family> all = {
        {"Blueprint", {0xF3F5FC, 0xE6EAF7, 0xE3E8F6, 0xD5DCF0, 0x172240, 0x374FAB, 0xFFFFFF}, {0x151B30, 0x212A44, 0x232C46, 0x2F3852, 0xF4F6FF, 0xB4B8FF, 0x151B30}},
        {"Evergreen", {0xF3F7F0, 0xE5EEE2, 0xE2ECE0, 0xD4E4D6, 0x18362A, 0x22603F, 0xFFFFFF}, {0x111E19, 0x1C2D24, 0x1E3027, 0x2A3F33, 0xF0FBF2, 0x95DDAF, 0x111E19}},
        {"Graphite", {0xF6F7F9, 0xE9EBEF, 0xE6E9ED, 0xD9DDE4, 0x161A22, 0x124FBB, 0xFFFFFF}, {0x141619, 0x202328, 0x23262B, 0x2F333A, 0xF6F7FA, 0x87B0FF, 0x141619}},
        {"Kiln", {0xFBF5EE, 0xF1E6DA, 0xEFE3D7, 0xE7D7C8, 0x35231E, 0x9F4125, 0xFFFFFF}, {0x211915, 0x2F241E, 0x322620, 0x413129, 0xFFF1E6, 0xF2AB7E, 0x211915}},
        {"Retro Neon", {0xF7F0FE, 0xEEE2FA, 0xEDE0F9, 0xE1D3F2, 0x281132, 0x8F267B, 0xFFFFFF}, {0x191126, 0x271835, 0x2A1937, 0x352146, 0xFFF4FF, 0xDD8BC8, 0x191126}},
    };
    return all;
}

const Family& default_family() { return families().front(); }

const Family* try_find(const std::string& name) {
    const std::string key = replace_all(to_lower(trim(name)), "-", " ");
    if (key.empty()) return nullptr;
    for (const Family& family : families()) {
        if (to_lower(family.name) == key) return &family;
    }
    return nullptr;
}

const Family& find(const std::string& name) {
    const Family* found = try_find(name);
    return found ? *found : default_family();
}

uint32_t mix(uint32_t a, uint32_t b, double amount) {
    auto blend = [&](int shift) {
        const double from = (a >> shift) & 0xFF, to = (b >> shift) & 0xFF;
        return static_cast<uint32_t>(std::lround(from + (to - from) * amount)) & 0xFF;
    };
    return (blend(16) << 16) | (blend(8) << 8) | blend(0);
}

std::string hex(uint32_t rgb) {
    char buffer[8];
    std::snprintf(buffer, sizeof buffer, "#%06X", static_cast<unsigned>(rgb & 0xFFFFFF));
    return buffer;
}

namespace {
fs::path file_in(const fs::path& data_home) { return data_home / "appearance.json"; }
}  // namespace

const Family& saved(const fs::path& data_home) {
    try {
        const fs::path path = file_in(data_home);
        if (!fs::exists(path)) return default_family();
        const json document = json::parse(read_file(path));
        if (document.is_object() && document.contains("Family") && document["Family"].is_string()) return find(document["Family"].get<std::string>());
    } catch (const std::exception&) {
        // An unreadable file means the default.
    }
    return default_family();
}

void save(const fs::path& data_home, const Family& family) {
    fs::create_directories(data_home);
    const fs::path path = file_in(data_home);
    json values = json::object();
    try {
        if (fs::exists(path)) {
            json document = json::parse(read_file(path));
            if (document.is_object()) values = std::move(document);
        }
    } catch (const std::exception&) {
        // A damaged file is replaced.
    }
    values["Family"] = family.name;
    write_file_atomic(path, values.dump(2));
}

std::string saved_mode(const fs::path& data_home) {
    try {
        const fs::path path = data_home / "appearance";
        if (!fs::exists(path)) return "system";
        const std::string saved = trim(read_file(path));
        if (saved == "light" || saved == "dark") return saved;
    } catch (const std::exception&) {
        // Nothing chosen yet.
    }
    return "system";
}

void save_mode(const fs::path& data_home, const std::string& mode) {
    if (mode != "light" && mode != "dark" && mode != "system") throw Error("Choose light, dark or system.");
    fs::create_directories(data_home);
    write_file_atomic(data_home / "appearance", mode);
}

}  // namespace mason::theme
