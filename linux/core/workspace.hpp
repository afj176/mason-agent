// A project folder and line diffs of its files.
#pragma once
#include "util.hpp"

namespace mason {

struct SearchMatch {
    std::string path;
    int line;
    std::string text;
};

struct FileListing {
    std::vector<std::string> paths;
    bool truncated = false;
};

/// A project folder. Every file tool goes through `resolve`, which keeps paths inside the folder,
/// refuses links that lead out of it, and never addresses credentials or dependency folders.
class Workspace {
public:
    static constexpr size_t kMaxFileBytes = 2 * 1024 * 1024;

    explicit Workspace(const std::string& path);
    const std::string& root() const { return root_; }

    static bool forbidden(const std::string& name);
    fs::path resolve(const std::string& relative) const;
    std::string read(const std::string& relative) const;
    bool exists(const std::string& relative) const;
    void write(const std::string& relative, const std::string& content) const;
    void remove(const std::string& relative) const;
    /// A file's bytes, or nothing when it does not exist. Used to compare and restore files of any kind.
    std::optional<std::string> data(const std::string& relative) const;
    void write_data(const std::string& relative, const std::string& bytes) const;
    /// Project-relative file paths in a stable order, skipping excluded folders and links.
    FileListing files(const std::string& under = ".", size_t limit = 2000) const;
    /// Case-insensitive text search over the project's UTF-8 files.
    std::vector<SearchMatch> search(const std::string& query, size_t limit = 100, size_t skip = 0) const;

private:
    std::string root_;
};

struct DiffLine {
    char kind;  // ' ' same, '+' added, '-' removed
    std::string text;
    int number;  // line number in the new text for same/added lines, in the old text for removed lines
};

/// Line diff of two texts. `before` is null for a file that did not exist.
std::vector<DiffLine> diff_lines(const std::optional<std::string>& before, const std::string& after);
struct DiffCounts { int added = 0, removed = 0; };
DiffCounts diff_counts(const std::optional<std::string>& before, const std::string& after);

}  // namespace mason
