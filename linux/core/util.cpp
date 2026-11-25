#include "util.hpp"

#include <algorithm>
#include <cstdlib>
#include <cstring>
#include <ctime>
#include <fstream>
#include <random>
#include <sstream>
#include <pwd.h>
#include <unistd.h>

namespace mason {

std::string read_file(const fs::path& path) {
    std::ifstream in(path, std::ios::binary);
    if (!in) throw Error("Cannot read " + path.string() + ".");
    std::ostringstream out;
    out << in.rdbuf();
    return out.str();
}

void write_file_atomic(const fs::path& path, const std::string& content) {
    if (path.has_parent_path()) fs::create_directories(path.parent_path());
    fs::path temporary = path;
    temporary += "." + new_id().substr(0, 8) + ".tmp";
    {
        std::ofstream out(temporary, std::ios::binary | std::ios::trunc);
        if (!out) throw Error("Cannot write " + path.string() + ".");
        out.write(content.data(), static_cast<std::streamsize>(content.size()));
        out.flush();
        if (!out) {
            std::error_code ignored;
            fs::remove(temporary, ignored);
            throw Error("Cannot write " + path.string() + ".");
        }
    }
    std::error_code failure;
    fs::rename(temporary, path, failure);
    if (failure) {
        std::error_code ignored;
        fs::remove(temporary, ignored);
        throw Error("Cannot write " + path.string() + ": " + failure.message());
    }
}

std::string new_id() {
    static thread_local std::mt19937_64 generator{std::random_device{}()};
    static const char* digits = "0123456789abcdef";
    std::string id(32, '0');
    for (size_t i = 0; i < 32; i += 16) {
        uint64_t value = generator();
        for (size_t j = 0; j < 16; j++) {
            id[i + j] = digits[value & 15];
            value >>= 4;
        }
    }
    return id;
}

std::string now_iso() {
    std::time_t now = std::time(nullptr);
    std::tm parts{};
    gmtime_r(&now, &parts);
    char buffer[32];
    std::strftime(buffer, sizeof buffer, "%Y-%m-%dT%H:%M:%SZ", &parts);
    return buffer;
}

std::string home_dir() {
    if (const char* home = std::getenv("HOME"); home && *home) return home;
    if (passwd* entry = getpwuid(getuid())) return entry->pw_dir;
    return "/";
}

fs::path data_home() {
    if (const char* custom = std::getenv("MASON_DATA_HOME"); custom && *custom) return custom;
    if (const char* xdg = std::getenv("XDG_DATA_HOME"); xdg && *xdg) return fs::path(xdg) / "MasonAgent";
    return fs::path(home_dir()) / ".local" / "share" / "MasonAgent";
}

std::string byte_text(int64_t bytes) {
    char buffer[48];
    if (bytes >= (1LL << 30)) std::snprintf(buffer, sizeof buffer, "%.1f GiB", bytes / 1073741824.0);
    else if (bytes >= (1LL << 20)) std::snprintf(buffer, sizeof buffer, "%.1f MiB", bytes / 1048576.0);
    else std::snprintf(buffer, sizeof buffer, "%lld B", static_cast<long long>(bytes));
    return buffer;
}

std::string to_lower(std::string text) {
    std::transform(text.begin(), text.end(), text.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
    return text;
}

bool contains_ci(const std::string& text, const std::string& part) { return to_lower(text).find(to_lower(part)) != std::string::npos; }
bool starts_with(const std::string& text, const std::string& prefix) { return text.compare(0, prefix.size(), prefix) == 0; }
bool ends_with(const std::string& text, const std::string& suffix) {
    return text.size() >= suffix.size() && text.compare(text.size() - suffix.size(), suffix.size(), suffix) == 0;
}

std::vector<std::string> split(const std::string& text, char separator, bool keep_empty) {
    std::vector<std::string> parts;
    size_t start = 0;
    while (true) {
        size_t end = text.find(separator, start);
        std::string part = text.substr(start, end == std::string::npos ? std::string::npos : end - start);
        if (keep_empty || !part.empty()) parts.push_back(std::move(part));
        if (end == std::string::npos) break;
        start = end + 1;
    }
    return parts;
}

std::string join(const std::vector<std::string>& parts, const std::string& separator) {
    std::string out;
    for (size_t i = 0; i < parts.size(); i++) {
        if (i > 0) out += separator;
        out += parts[i];
    }
    return out;
}

std::string trim(const std::string& text) {
    size_t start = text.find_first_not_of(" \t\r\n");
    if (start == std::string::npos) return "";
    size_t end = text.find_last_not_of(" \t\r\n");
    return text.substr(start, end - start + 1);
}

std::string replace_all(std::string text, const std::string& from, const std::string& to) {
    if (from.empty()) return text;
    for (size_t at = 0; (at = text.find(from, at)) != std::string::npos; at += to.size()) text.replace(at, from.size(), to);
    return text;
}

bool is_valid_utf8(const std::string& text) {
    size_t i = 0;
    const size_t n = text.size();
    while (i < n) {
        const unsigned char c = static_cast<unsigned char>(text[i]);
        size_t extra;
        if (c < 0x80) extra = 0;
        else if ((c >> 5) == 0x6) extra = 1;
        else if ((c >> 4) == 0xE) extra = 2;
        else if ((c >> 3) == 0x1E) extra = 3;
        else return false;
        if (i + extra >= n && extra > 0) return false;
        for (size_t j = 1; j <= extra; j++) {
            if ((static_cast<unsigned char>(text[i + j]) >> 6) != 0x2) return false;
        }
        i += extra + 1;
    }
    return true;
}

size_t utf8_length(const std::string& text) {
    size_t count = 0;
    for (unsigned char c : text) {
        if ((c >> 6) != 0x2) count++;
    }
    return count;
}

std::string utf8_prefix(const std::string& text, size_t count) {
    size_t seen = 0, i = 0;
    for (; i < text.size(); i++) {
        if ((static_cast<unsigned char>(text[i]) >> 6) != 0x2) {
            if (seen == count) break;
            seen++;
        }
    }
    return text.substr(0, i);
}

std::string utf8_suffix(const std::string& text, size_t count) {
    size_t total = utf8_length(text);
    if (total <= count) return text;
    return text.substr(utf8_prefix(text, total - count).size());
}

// ---- SHA-256 ---------------------------------------------------------------------------------------------------

namespace {
const uint32_t kRound[64] = {
    0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5, 0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3,
    0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174, 0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
    0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967, 0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13,
    0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85, 0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
    0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3, 0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208,
    0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2};
inline uint32_t rotr(uint32_t x, uint32_t n) { return (x >> n) | (x << (32 - n)); }
}  // namespace

Sha256::Sha256() : state_{0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19} {}

void Sha256::compress(const uint8_t* block) {
    uint32_t w[64];
    for (int i = 0; i < 16; i++) {
        w[i] = uint32_t(block[i * 4]) << 24 | uint32_t(block[i * 4 + 1]) << 16 | uint32_t(block[i * 4 + 2]) << 8 | uint32_t(block[i * 4 + 3]);
    }
    for (int i = 16; i < 64; i++) {
        uint32_t s0 = rotr(w[i - 15], 7) ^ rotr(w[i - 15], 18) ^ (w[i - 15] >> 3);
        uint32_t s1 = rotr(w[i - 2], 17) ^ rotr(w[i - 2], 19) ^ (w[i - 2] >> 10);
        w[i] = w[i - 16] + s0 + w[i - 7] + s1;
    }
    uint32_t a = state_[0], b = state_[1], c = state_[2], d = state_[3], e = state_[4], f = state_[5], g = state_[6], h = state_[7];
    for (int i = 0; i < 64; i++) {
        uint32_t t1 = h + (rotr(e, 6) ^ rotr(e, 11) ^ rotr(e, 25)) + ((e & f) ^ (~e & g)) + kRound[i] + w[i];
        uint32_t t2 = (rotr(a, 2) ^ rotr(a, 13) ^ rotr(a, 22)) + ((a & b) ^ (a & c) ^ (b & c));
        h = g; g = f; f = e; e = d + t1;
        d = c; c = b; b = a; a = t1 + t2;
    }
    state_[0] += a; state_[1] += b; state_[2] += c; state_[3] += d;
    state_[4] += e; state_[5] += f; state_[6] += g; state_[7] += h;
}

void Sha256::update(const void* data, size_t size) {
    const uint8_t* bytes = static_cast<const uint8_t*>(data);
    length_ += size;
    while (size > 0) {
        size_t take = std::min(size, sizeof pending_ - pending_size_);
        std::memcpy(pending_ + pending_size_, bytes, take);
        pending_size_ += take;
        bytes += take;
        size -= take;
        if (pending_size_ == 64) {
            compress(pending_);
            pending_size_ = 0;
        }
    }
}

std::string Sha256::hex() const {
    Sha256 copy = *this;
    const uint64_t bits = length_ * 8;
    uint8_t pad[72] = {0x80};
    const size_t pad_size = (pending_size_ < 56 ? 56 : 120) - pending_size_;
    copy.update(pad, pad_size);
    uint8_t size_bytes[8];
    for (int i = 0; i < 8; i++) size_bytes[i] = static_cast<uint8_t>(bits >> (56 - 8 * i));
    copy.update(size_bytes, 8);
    char out[65];
    for (int i = 0; i < 8; i++) std::snprintf(out + i * 8, 9, "%08x", copy.state_[i]);
    return out;
}

}  // namespace mason
