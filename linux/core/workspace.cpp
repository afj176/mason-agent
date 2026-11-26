#include "workspace.hpp"

#include <algorithm>
#include <set>

namespace mason {

namespace {
const std::set<std::string> kExcluded = {
    ".git", ".svn", ".hg", ".ssh", ".aws", ".azure", ".gnupg", "node_modules", ".build", "build", "builddir", "_build", "vendor",
    ".venv", "venv", "dist", "target", ".cache", "__pycache__", ".mason-sandbox", ".flatpak-builder"};
const char* const kSecretSuffixes[] = {".pem", ".key", ".pfx", ".p12", ".kdbx", ".keyring"};

std::string shown(const std::string& relative) { return relative.size() > 160 ? relative.substr(0, 160) + "…" : relative; }
}  // namespace

Workspace::Workspace(const std::string& path) {
    std::error_code failure;
    fs::path real = fs::canonical(path, failure);
    if (failure || !fs::is_directory(real)) throw Error("Open an existing project folder.");
    root_ = real.string();
    while (root_.size() > 1 && root_.back() == '/') root_.pop_back();
}

bool Workspace::forbidden(const std::string& name) {
    const std::string lower = to_lower(name);
    if (kExcluded.count(name) || lower == ".env" || starts_with(lower, ".env.") || starts_with(lower, "id_rsa") || starts_with(lower, "id_ed25519")) return true;
    for (const char* suffix : kSecretSuffixes) {
        if (ends_with(lower, suffix)) return true;
    }
    return false;
}

fs::path Workspace::resolve(const std::string& relative) const {
    if (relative.empty() || relative.size() > 1000 || relative[0] == '/' || relative[0] == '~' ||
        std::any_of(relative.begin(), relative.end(), [](unsigned char c) { return c < 0x20 || c == 0x7f; })) {
        throw Error("Rejected path \"" + shown(relative) + "\". Use a path relative to the project folder.");
    }
    fs::path full = root_;
    for (const std::string& part : split(relative, '/', false)) {
        if (part == ".") continue;
        if (part == ".." || forbidden(part)) throw Error("Rejected path \"" + shown(relative) + "\". It is outside the project or addresses excluded content.");
        full /= part;
    }
    // A link inside the project must not lead out of it. For a file that does not exist yet, check its nearest existing parent.
    fs::path existing = full;
    std::error_code failure;
    while (!fs::exists(fs::symlink_status(existing, failure)) && existing.string().size() > root_.size()) existing = existing.parent_path();
    const std::string real = fs::weakly_canonical(existing, failure).string();
    if (failure || !(real == root_ || starts_with(real, root_ + "/"))) {
        throw Error("Rejected path \"" + shown(relative) + "\". It follows a link out of the project.");
    }
    return full;
}

std::string Workspace::read(const std::string& relative) const {
    const fs::path path = resolve(relative);
    std::error_code failure;
    if (!fs::exists(path, failure)) throw Error(relative + " does not exist.");
    if (fs::is_directory(path, failure)) throw Error(relative + " is a folder. Use list_files.");
    if (fs::file_size(path, failure) > kMaxFileBytes) throw Error(relative + " is larger than 2 MiB.");
    std::string content = read_file(path);
    if (!is_valid_utf8(content) || content.find('\0') != std::string::npos) throw Error(relative + " is not UTF-8 text.");
    return content;
}

bool Workspace::exists(const std::string& relative) const {
    try {
        std::error_code failure;
        return fs::exists(resolve(relative), failure);
    } catch (const Error&) {
        return false;
    }
}

void Workspace::write(const std::string& relative, const std::string& content) const {
    const fs::path path = resolve(relative);
    if (content.size() > kMaxFileBytes) throw Error("Refusing to write more than 2 MiB to " + relative + ".");
    // Keep the file's permissions (an executable script stays executable) across the atomic replace.
    std::error_code failure;
    const bool existed = fs::exists(path, failure);
    const fs::perms previous = existed ? fs::status(path, failure).permissions() : fs::perms::none;
    write_file_atomic(path, content);
    if (existed) fs::permissions(path, previous, failure);
}

std::optional<std::string> Workspace::data(const std::string& relative) const {
    const fs::path path = resolve(relative);
    std::error_code failure;
    if (!fs::exists(path, failure)) return std::nullopt;
    return read_file(path);
}

void Workspace::write_data(const std::string& relative, const std::string& bytes) const {
    const fs::path path = resolve(relative);
    std::error_code failure;
    const bool existed = fs::exists(path, failure);
    const fs::perms previous = existed ? fs::status(path, failure).permissions() : fs::perms::none;
    fs::create_directories(path.parent_path(), failure);
    write_file_atomic(path, bytes);
    if (existed) fs::permissions(path, previous, failure);
}

void Workspace::remove(const std::string& relative) const {
    std::error_code failure;
    fs::remove(resolve(relative), failure);
}

FileListing Workspace::files(const std::string& under, size_t limit) const {
    FileListing listing;
    std::vector<fs::path> pending = {under.empty() || under == "." ? fs::path(root_) : resolve(under)};
    while (!pending.empty() && !listing.truncated) {
        const fs::path folder = pending.back();
        pending.pop_back();
        std::vector<fs::directory_entry> entries;
        std::error_code failure;
        for (fs::directory_iterator it(folder, fs::directory_options::skip_permission_denied, failure), end; !failure && it != end; it.increment(failure)) entries.push_back(*it);
        std::sort(entries.begin(), entries.end(), [](const auto& a, const auto& b) { return a.path().filename() < b.path().filename(); });
        for (const auto& entry : entries) {
            const std::string name = entry.path().filename().string();
            if (forbidden(name) || entry.is_symlink(failure)) continue;
            if (entry.is_directory(failure)) {
                pending.push_back(entry.path());
                continue;
            }
            if (!entry.is_regular_file(failure)) continue;
            if (listing.paths.size() >= limit) {
                listing.truncated = true;
                break;
            }
            listing.paths.push_back(entry.path().string().substr(root_.size() + 1));
        }
    }
    std::sort(listing.paths.begin(), listing.paths.end());
    return listing;
}

std::vector<SearchMatch> Workspace::search(const std::string& query, size_t limit, size_t skip) const {
    if (query.empty() || query.size() > 400) throw Error("Search for 1–400 characters.");
    const std::string needle = to_lower(query);
    std::vector<SearchMatch> matches;
    size_t seen = 0;
    for (const std::string& path : files(".", 5000).paths) {
        std::string content;
        try {
            content = read(path);
        } catch (const Error&) {
            continue;
        }
        int number = 0;
        for (const std::string& line : split(content, '\n')) {
            number++;
            if (to_lower(line).find(needle) == std::string::npos) continue;
            if (++seen <= skip) continue;
            matches.push_back({path, number, utf8_prefix(line, 300)});
            if (matches.size() >= limit) return matches;
        }
    }
    return matches;
}

// ---- diff ------------------------------------------------------------------------------------------------------

namespace {
std::vector<std::string> lines_of(const std::optional<std::string>& text) {
    if (!text || text->empty()) return {};
    std::string normalized = replace_all(*text, "\r\n", "\n");
    if (!normalized.empty() && normalized.back() == '\n') normalized.pop_back();
    return split(normalized, '\n');
}
}  // namespace

std::vector<DiffLine> diff_lines(const std::optional<std::string>& before, const std::string& after) {
    const std::vector<std::string> old_lines = lines_of(before), new_lines = lines_of(after);
    const size_t shortest = std::min(old_lines.size(), new_lines.size());
    size_t prefix = 0, suffix = 0;
    while (prefix < shortest && old_lines[prefix] == new_lines[prefix]) prefix++;
    while (suffix < shortest - prefix && old_lines[old_lines.size() - 1 - suffix] == new_lines[new_lines.size() - 1 - suffix]) suffix++;

    std::vector<DiffLine> rows;
    for (size_t i = 0; i < prefix; i++) rows.push_back({' ', new_lines[i], static_cast<int>(i + 1)});
    const size_t n = old_lines.size() - prefix - suffix, m = new_lines.size() - prefix - suffix;
    if (n > 0 && m > 0 && (n + 1) * (m + 1) <= 4'000'000) {
        // table[x][y] = length of the longest common subsequence of old[x...] and new[y...] within the middle region.
        const size_t width = m + 1;
        std::vector<int> table((n + 1) * width, 0);
        for (size_t x = n; x-- > 0;) {
            for (size_t y = m; y-- > 0;) {
                table[x * width + y] = old_lines[prefix + x] == new_lines[prefix + y] ? table[(x + 1) * width + y + 1] + 1
                                                                                      : std::max(table[(x + 1) * width + y], table[x * width + y + 1]);
            }
        }
        size_t x = 0, y = 0;
        while (x < n || y < m) {
            if (x < n && y < m && old_lines[prefix + x] == new_lines[prefix + y]) {
                rows.push_back({' ', new_lines[prefix + y], static_cast<int>(prefix + y + 1)});
                x++;
                y++;
            } else if (x < n && (y == m || table[(x + 1) * width + y] >= table[x * width + y + 1])) {
                rows.push_back({'-', old_lines[prefix + x], static_cast<int>(prefix + x + 1)});
                x++;
            } else {
                rows.push_back({'+', new_lines[prefix + y], static_cast<int>(prefix + y + 1)});
                y++;
            }
        }
    } else {
        for (size_t x = 0; x < n; x++) rows.push_back({'-', old_lines[prefix + x], static_cast<int>(prefix + x + 1)});
        for (size_t y = 0; y < m; y++) rows.push_back({'+', new_lines[prefix + y], static_cast<int>(prefix + y + 1)});
    }
    for (size_t i = 0; i < suffix; i++) {
        const size_t index = new_lines.size() - suffix + i;
        rows.push_back({' ', new_lines[index], static_cast<int>(index + 1)});
    }
    return rows;
}

DiffCounts diff_counts(const std::optional<std::string>& before, const std::string& after) {
    DiffCounts counts;
    for (const DiffLine& row : diff_lines(before, after)) {
        if (row.kind == '+') counts.added++;
        if (row.kind == '-') counts.removed++;
    }
    return counts;
}

}  // namespace mason
