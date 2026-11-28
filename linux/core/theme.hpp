// The five approved theme families, shared by the app and the command line tool. Mirrors
// windows/src/Mason.Code.Core/ThemeFamilies.cs: seven tokens per mode, everything else is derived from them.
#pragma once
#include <cstdint>
#include <string>
#include <vector>

#include "util.hpp"

namespace mason::theme {

/// One mode of a family: the seven approved tokens, each 0xRRGGBB.
struct Tokens {
    uint32_t background, pane, card, line, text, accent, on_accent;
};

/// An approved family with its light and dark modes.
struct Family {
    std::string name;
    Tokens light, dark;
    const Tokens& tokens(bool dark_mode) const { return dark_mode ? dark : light; }
    /// The lower-case, hyphenated name used by the CLI ("retro-neon").
    std::string slug() const;
};

/// Blueprint, Evergreen, Graphite, Kiln and Retro Neon, in that order.
const std::vector<Family>& families();
/// Blueprint.
const Family& default_family();
/// Finds a family by display name or slug, ignoring case; nullptr when there is none of that name.
const Family* try_find(const std::string& name);
/// Like try_find, but an unknown name gives the default.
const Family& find(const std::string& name);

/// Mixes two 0xRRGGBB colours: amount 0 gives `a`, 1 gives `b`.
uint32_t mix(uint32_t a, uint32_t b, double amount);
/// "#RRGGBB".
std::string hex(uint32_t rgb);

/// The family saved in appearance.json ("Family") in the data folder, or the default.
const Family& saved(const fs::path& data_home);
/// Saves the family into appearance.json, keeping every other value already in the file.
void save(const fs::path& data_home, const Family& family);

/// Light, dark or following the system: "light", "dark" or "system", kept in the file "appearance" in the data
/// folder, which the app reads at start.
std::string saved_mode(const fs::path& data_home);
/// Saves "light", "dark" or "system"; anything else is refused.
void save_mode(const fs::path& data_home, const std::string& mode);

}  // namespace mason::theme
