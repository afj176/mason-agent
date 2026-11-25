// Shared small pieces for the Mason engine on Linux.
#pragma once
#include <atomic>
#include <cstdint>
#include <filesystem>
#include <functional>
#include <memory>
#include <optional>
#include <stdexcept>
#include <string>
#include <vector>
#include <nlohmann/json.hpp>

namespace mason {

namespace fs = std::filesystem;
using json = nlohmann::json;

/// An error whose message is written for the person using Mason.
struct Error : std::runtime_error {
    using std::runtime_error::runtime_error;
};
/// Thrown when the user stops a run.
struct Cancelled : std::exception {
    const char* what() const noexcept override { return "Stopped."; }
};

/// Shared between the thread doing work and the one that can stop it.
using CancelToken = std::shared_ptr<std::atomic<bool>>;
inline CancelToken make_cancel() { return std::make_shared<std::atomic<bool>>(false); }
inline bool is_cancelled(const CancelToken& token) { return token && token->load(); }
inline void check_cancel(const CancelToken& token) { if (is_cancelled(token)) throw Cancelled(); }

std::string read_file(const fs::path& path);
/// Writes through a temporary file and a rename, so a reader never sees half a file.
void write_file_atomic(const fs::path& path, const std::string& content);
std::string new_id();
std::string now_iso();
std::string home_dir();
/// Where Mason keeps sessions and settings: $MASON_DATA_HOME, else $XDG_DATA_HOME/MasonAgent, else ~/.local/share/MasonAgent.
fs::path data_home();
std::string byte_text(int64_t bytes);
std::string to_lower(std::string text);
bool contains_ci(const std::string& text, const std::string& part);
bool starts_with(const std::string& text, const std::string& prefix);
bool ends_with(const std::string& text, const std::string& suffix);
std::vector<std::string> split(const std::string& text, char separator, bool keep_empty = true);
std::string join(const std::vector<std::string>& parts, const std::string& separator);
std::string trim(const std::string& text);
std::string replace_all(std::string text, const std::string& from, const std::string& to);
bool is_valid_utf8(const std::string& text);
/// Number of Unicode code points; used to clip text for display.
size_t utf8_length(const std::string& text);
std::string utf8_prefix(const std::string& text, size_t count);
std::string utf8_suffix(const std::string& text, size_t count);

/// Incremental SHA-256.
class Sha256 {
public:
    Sha256();
    void update(const void* data, size_t size);
    std::string hex() const;
private:
    void compress(const uint8_t* block);
    uint32_t state_[8];
    uint8_t pending_[64];
    size_t pending_size_ = 0;
    uint64_t length_ = 0;
};

}  // namespace mason
