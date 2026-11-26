#include "palette.hpp"

#include <algorithm>

namespace mason::palette {

const std::vector<std::pair<std::string, std::string>>& commands() {
    static const std::vector<std::pair<std::string, std::string>> all = {
        {"/notes", "Notes kept for this project and given to the model"},
        {"/languages", "The language server that checks each language's edits"},
        {"/generation", "Temperature, top P and the like, for the model in use"},
        {"/experiment", "Try something in a copy of the project"},
        {"/deliver", "Review changes and draft a pull request"},
        {"/task", "Requests that keep running in the background"},
        {"/roles", "A reviewer model, a visual model, and a trace of model calls"},
        {"/policy", "This project's limits on tools, workflows and hooks"},
        {"/network", "Whether sandboxed commands may ask for a host"},
        {"/bundle", "Skills and documentation installed for every project"},
        {"/schedule", "Requests that run on a schedule"},
        {"/agent", "Use one of your agents for this session"},
    };
    return all;
}

namespace {

/// How well an item matches, lower being better, or -1 when it does not. Every word typed must appear.
int score(const PaletteItem& item, const std::vector<std::string>& words) {
    const std::string title = to_lower(item.title), detail = to_lower(item.detail);
    int worst = 0;
    for (const std::string& word : words) {
        int each = 3;
        const auto starts_a_word = [&] {
            for (const char* before : {" ", "/", "-", "_", "."}) {
                if (title.find(before + word) != std::string::npos) return true;
            }
            return false;
        };
        if (starts_with(title, word)) each = 0;
        else if (starts_a_word()) each = 1;
        else if (title.find(word) != std::string::npos) each = 2;
        else if (detail.find(word) == std::string::npos) return -1;
        worst = std::max(worst, each);
    }
    return worst;
}

}  // namespace

std::vector<PaletteItem> matches(const std::vector<PaletteItem>& items, const std::string& query, size_t limit) {
    const std::vector<std::string> words = split(to_lower(trim(query)), ' ', false);
    std::vector<PaletteItem> found;
    if (words.empty()) {
        for (size_t i = 0; i < items.size() && i < limit; i++) found.push_back(items[i]);
        return found;
    }
    std::vector<std::pair<int, size_t>> scored;
    for (size_t i = 0; i < items.size(); i++) {
        const int each = score(items[i], words);
        if (each >= 0) scored.emplace_back(each, i);
    }
    std::sort(scored.begin(), scored.end());
    for (size_t i = 0; i < scored.size() && i < limit; i++) found.push_back(items[scored[i].second]);
    return found;
}

}  // namespace mason::palette
