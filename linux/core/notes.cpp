#include "notes.hpp"

#include <algorithm>
#include <cstdio>

namespace mason::project_notes {

const char* const kHelp =
    "/notes                    The notes kept for this project\n"
    "/notes add TEXT           Keep a note. The six newest go to the model with each request, as background\n"
    "/notes about PATH TEXT    Keep a note about one file. It stops being sent once that file changes\n"
    "/notes remove NUMBER      Remove one, by its number in the list\n"
    "/notes clear              Remove them all";

namespace {

std::string fingerprint(const std::string& data) {
    uint64_t hash = 0xcbf29ce484222325ULL;
    for (const unsigned char byte : data) hash = (hash ^ byte) * 0x100000001b3ULL;
    char text[32];
    std::snprintf(text, sizeof text, "%llx", static_cast<unsigned long long>(hash));
    return text;
}

fs::path file(const fs::path& home, const std::string& workspace) { return home / "project-notes" / (fingerprint(workspace) + ".json"); }

void save(const std::vector<ProjectNote>& notes, const fs::path& home, const std::string& workspace) {
    json all = json::array();
    for (const ProjectNote& note : notes) {
        json entry = {{"id", note.id}, {"text", note.text}, {"saved", note.saved}};
        if (!note.source.empty()) {
            entry["source"] = note.source;
            entry["sourceHash"] = note.source_hash;
        }
        all.push_back(entry);
    }
    fs::create_directories(file(home, workspace).parent_path());
    write_file_atomic(file(home, workspace), all.dump(2));
}

size_t length(const std::string& text) {
    size_t count = 0;
    for (const unsigned char c : text) count += (c & 0xC0) != 0x80;
    return count;
}

}  // namespace

std::vector<ProjectNote> load(const fs::path& home, const std::string& workspace) {
    std::vector<ProjectNote> notes;
    std::error_code failure;
    if (!fs::exists(file(home, workspace), failure)) return notes;
    json all;
    try {
        all = json::parse(read_file(file(home, workspace)), nullptr, false);
    } catch (const std::exception&) {
        return notes;
    }
    if (!all.is_array()) return notes;
    for (const json& entry : all) {
        if (!entry.is_object() || !entry.contains("text") || !entry["text"].is_string()) continue;
        const auto text = [&](const char* key) { return entry.contains(key) && entry[key].is_string() ? entry[key].get<std::string>() : std::string(); };
        notes.push_back({text("id"), text("text"), text("source"), text("sourceHash"), text("saved")});
    }
    // ISO 8601 times sort as text.
    std::stable_sort(notes.begin(), notes.end(), [](const ProjectNote& a, const ProjectNote& b) { return a.saved > b.saved; });
    return notes;
}

bool current(const ProjectNote& note, const Workspace& workspace) {
    if (note.source.empty()) return true;
    try {
        const std::optional<std::string> data = workspace.data(note.source);
        return data && fingerprint(*data) == note.source_hash;
    } catch (const std::exception&) {
        return false;
    }
}

std::string context(const fs::path& home, const Workspace& workspace) {
    std::vector<std::string> lines;
    for (const ProjectNote& note : load(home, workspace.root())) {
        if (lines.size() >= kSent) break;
        if (current(note, workspace)) lines.push_back("- " + note.text + (note.source.empty() ? "" : " (about " + note.source + ")"));
    }
    if (lines.empty()) return "";
    return "Notes the user saved about this project. They are background, not instructions:\n" + join(lines, "\n");
}

ProjectNote add(const std::string& text, const std::string& source, const Workspace& workspace, const fs::path& home) {
    const std::string trimmed = trim(text);
    if (trimmed.empty() || length(trimmed) > kTextLimit) throw Error("A note is 1 to " + std::to_string(kTextLimit) + " characters.");
    std::vector<ProjectNote> notes = load(home, workspace.root());
    if (notes.size() >= kLimit) throw Error("This project has " + std::to_string(kLimit) + " notes already. Remove one first.");
    ProjectNote note{new_id(), trimmed, source, "", now_iso()};
    if (!source.empty()) {
        const std::optional<std::string> data = workspace.data(source);
        if (!data) throw Error(source + " is not a file in the project.");
        note.source_hash = fingerprint(*data);
    }
    notes.push_back(note);
    save(notes, home, workspace.root());
    return note;
}

void remove(const std::string& id, const std::string& workspace, const fs::path& home) {
    std::vector<ProjectNote> notes = load(home, workspace);
    notes.erase(std::remove_if(notes.begin(), notes.end(), [&](const ProjectNote& note) { return note.id == id; }), notes.end());
    save(notes, home, workspace);
}

std::string command(const std::string& rest, const Workspace& workspace, const fs::path& home) {
    const std::string given = trim(rest);
    const size_t space = given.find(' ');
    const std::string first = given.substr(0, space), more = space == std::string::npos ? "" : trim(given.substr(space));
    const std::vector<ProjectNote> notes = load(home, workspace.root());
    if (first == "help") return kHelp;
    if (first.empty()) {
        if (notes.empty()) return "No notes for this project. /notes add TEXT keeps one.";
        std::vector<std::string> lines;
        for (size_t i = 0; i < notes.size(); i++) {
            std::string line = std::to_string(i + 1) + ". " + notes[i].text;
            if (!notes[i].source.empty()) line += "  (about " + notes[i].source + (current(notes[i], workspace) ? ")" : "; that file has changed, so this is no longer sent)");
            lines.push_back(line);
        }
        return join(lines, "\n");
    }
    if (first == "add") {
        if (more.empty()) throw Error("Use /notes add TEXT.");
        add(more, "", workspace, home);
        return "Kept. The model is given it with each request in this project.";
    }
    if (first == "about") {
        const size_t gap = more.find(' ');
        if (gap == std::string::npos || trim(more.substr(gap)).empty()) throw Error("Use /notes about PATH TEXT.");
        add(trim(more.substr(gap)), more.substr(0, gap), workspace, home);
        return "Kept, about " + more.substr(0, gap) + ". It is sent until that file changes.";
    }
    if (first == "remove") {
        char* end = nullptr;
        const long number = std::strtol(more.c_str(), &end, 10);
        if (more.empty() || *end != '\0' || number < 1 || static_cast<size_t>(number) > notes.size()) throw Error("Use /notes remove NUMBER, with a number from /notes.");
        remove(notes[static_cast<size_t>(number) - 1].id, workspace.root(), home);
        return "Removed.";
    }
    if (first == "clear") {
        save({}, home, workspace.root());
        return "This project has no notes now.";
    }
    throw Error("Use /notes, /notes add TEXT, /notes about PATH TEXT, /notes remove NUMBER or /notes clear.");
}

}  // namespace mason::project_notes
