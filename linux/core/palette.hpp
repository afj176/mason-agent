// The search box that reaches everything: sessions, projects, the project's files, commands and chats. The app
// gathers the items; choosing what matches what the user typed, and in what order, is done here.
#pragma once

#include "util.hpp"

namespace mason {

struct PaletteItem {
    std::string kind;    ///< session, project, file, command or chat
    std::string title, detail;
    std::string value;   ///< What the app acts on: a session or chat id, a folder, a file path, or a command's name.
};

namespace palette {
/// The commands the request box understands, for the palette to offer.
const std::vector<std::pair<std::string, std::string>>& commands();
/// The items that match what was typed, best first; items that match equally keep the order they were given in.
std::vector<PaletteItem> matches(const std::vector<PaletteItem>& items, const std::string& query, size_t limit = 40);
}  // namespace palette

}  // namespace mason
