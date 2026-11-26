#include "network.hpp"

#include "agent.hpp"
#include "shell.hpp"

#include <algorithm>
#include <cctype>
#include <cstring>

#include <arpa/inet.h>
#include <fcntl.h>
#include <netdb.h>
#include <netinet/in.h>
#include <poll.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/un.h>
#include <unistd.h>

namespace mason {

namespace {

bool send_all(int descriptor, const char* data, size_t size) {
    size_t sent = 0;
    while (sent < size) {
        const ssize_t count = ::send(descriptor, data + sent, size - sent, MSG_NOSIGNAL);
        if (count <= 0) return false;
        sent += static_cast<size_t>(count);
    }
    return true;
}

bool send_all(int descriptor, const std::string& text) { return send_all(descriptor, text.data(), text.size()); }

/// Carries bytes both ways until one side closes, two minutes pass in silence, or the limit is reached.
long long pump(int a, int b, long long moved, long long limit) {
    pollfd waiting[2] = {{a, POLLIN, 0}, {b, POLLIN, 0}};
    char chunk[32 * 1024];
    while (moved < limit) {
        if (poll(waiting, 2, 120000) <= 0) break;
        bool ended = false;
        for (int side = 0; side < 2 && !ended; side++) {
            if (waiting[side].revents == 0) continue;
            const ssize_t count = recv(waiting[side].fd, chunk, sizeof chunk, 0);
            if (count <= 0 || !send_all(side == 0 ? b : a, chunk, static_cast<size_t>(count))) ended = true;
            else moved += count;
        }
        if (ended) break;
    }
    return moved;
}

std::vector<RelayAddress> lookup(const std::string& host) {
    std::vector<RelayAddress> found;
    addrinfo hints{};
    hints.ai_socktype = SOCK_STREAM;
    addrinfo* list = nullptr;
    if (getaddrinfo(host.c_str(), "443", &hints, &list) != 0) return found;
    for (addrinfo* entry = list; entry; entry = entry->ai_next) {
        if (entry->ai_family == AF_INET) {
            const auto* address = reinterpret_cast<const sockaddr_in*>(entry->ai_addr);
            const auto* bytes = reinterpret_cast<const uint8_t*>(&address->sin_addr);
            found.push_back({std::vector<uint8_t>(bytes, bytes + 4), 443});
        } else if (entry->ai_family == AF_INET6) {
            const auto* address = reinterpret_cast<const sockaddr_in6*>(entry->ai_addr);
            const auto* bytes = reinterpret_cast<const uint8_t*>(&address->sin6_addr);
            found.push_back({std::vector<uint8_t>(bytes, bytes + 16), 443});
        }
    }
    freeaddrinfo(list);
    return found;
}

int connected(const RelayAddress& target) {
    const bool six = target.bytes.size() == 16;
    const int descriptor = socket(six ? AF_INET6 : AF_INET, SOCK_STREAM | SOCK_CLOEXEC, 0);
    if (descriptor < 0) return -1;
    timeval timeout{15, 0};
    setsockopt(descriptor, SOL_SOCKET, SO_SNDTIMEO, &timeout, sizeof timeout);
    int result = -1;
    if (six) {
        sockaddr_in6 address{};
        address.sin6_family = AF_INET6;
        address.sin6_port = htons(static_cast<uint16_t>(target.port));
        std::memcpy(&address.sin6_addr, target.bytes.data(), 16);
        result = connect(descriptor, reinterpret_cast<sockaddr*>(&address), sizeof address);
    } else if (target.bytes.size() == 4) {
        sockaddr_in address{};
        address.sin_family = AF_INET;
        address.sin_port = htons(static_cast<uint16_t>(target.port));
        std::memcpy(&address.sin_addr, target.bytes.data(), 4);
        result = connect(descriptor, reinterpret_cast<sockaddr*>(&address), sizeof address);
    }
    if (result != 0) { close(descriptor); return -1; }
    return descriptor;
}

int unix_socket(const std::string& path, bool listening) {
    sockaddr_un address{};
    address.sun_family = AF_UNIX;
    if (path.size() >= sizeof address.sun_path) return -1;
    std::strncpy(address.sun_path, path.c_str(), sizeof address.sun_path - 1);
    const int descriptor = socket(AF_UNIX, SOCK_STREAM | SOCK_CLOEXEC, 0);
    if (descriptor < 0) return -1;
    const int result = listening ? bind(descriptor, reinterpret_cast<sockaddr*>(&address), sizeof address) : connect(descriptor, reinterpret_cast<sockaddr*>(&address), sizeof address);
    if (result != 0 || (listening && listen(descriptor, 32) != 0)) { close(descriptor); return -1; }
    return descriptor;
}

}  // namespace

namespace network_policy {

std::string host(const std::string& value, int port) {
    if (port != 443 || value.size() < 3 || value.size() > 253 || value != trim(value) || value.back() == '.' || value.find(':') != std::string::npos) {
        throw Error("Only HTTPS (port 443) to a named host is relayed.");
    }
    const std::string name = to_lower(value);
    const std::vector<std::string> labels = split(name, '.');
    bool numeric = true, well_formed = true;
    for (const std::string& label : labels) {
        if (label.empty() || label.size() > 63 || label.front() == '-' || label.back() == '-') well_formed = false;
        for (unsigned char c : label) {
            if (!std::isdigit(c)) numeric = false;
            if (c >= 0x80 || (!std::isalnum(c) && c != '-')) well_formed = false;
        }
    }
    if (labels.size() < 2 || numeric || !well_formed || name == "localhost" || ends_with(name, ".localhost") || ends_with(name, ".local") || ends_with(name, ".internal")) {
        throw Error("A local or malformed destination is not relayed.");
    }
    return name;
}

bool is_public(std::vector<uint8_t> b) {
    if (b.size() == 16 && std::all_of(b.begin(), b.begin() + 10, [](uint8_t byte) { return byte == 0; }) && b[10] == 0xff && b[11] == 0xff) b.erase(b.begin(), b.begin() + 12);
    if (b.size() == 4) {
        return b[0] != 0 && b[0] != 10 && b[0] != 127 && b[0] < 224 && !(b[0] == 169 && b[1] == 254) && !(b[0] == 172 && b[1] >= 16 && b[1] <= 31) && !(b[0] == 192 && (b[1] == 168 || b[1] == 0 || b[1] == 2)) &&
               !(b[0] == 100 && b[1] >= 64 && b[1] <= 127) && !(b[0] == 198 && (b[1] == 18 || b[1] == 19 || (b[1] == 51 && b[2] == 100))) && !(b[0] == 203 && b[1] == 0 && b[2] == 113);
    }
    // Global unicast only; documentation, Teredo and 6to4 are refused.
    return b.size() == 16 && (b[0] & 0xe0) == 0x20 && !(b[0] == 0x20 && b[1] == 0x01 && (b[2] < 2 || (b[2] == 0x0d && b[3] == 0xb8))) && !(b[0] == 0x20 && b[1] == 0x02);
}

}  // namespace network_policy

namespace network_settings {

bool asks(const fs::path& home) {
    if (!fs::exists(home / "network.json")) return false;
    const json value = json::parse(read_file(home / "network.json"), nullptr, false);
    return value.is_object() && value.value("mode", "") == "ask";
}

bool asks(const Session& session, const fs::path& home) { return session.network.empty() ? asks(home) : session.network == "ask"; }

std::set<std::string> always_allowed(const fs::path& home, const std::string& workspace) {
    std::set<std::string> hosts;
    const ApprovalRules saved(home, workspace);
    for (const ApprovalRules::Rule& rule : saved.rules()) {
        if (rule.tool == "network") hosts.insert(to_lower(rule.prefix));
    }
    return hosts;
}

std::string session_command(const std::string& rest, Session& session, const fs::path& home) {
    const std::vector<std::string> words = split(to_lower(trim(rest)), ' ', false);
    if (!words.empty()) {
        if (words[0] == "ask" || words[0] == "off") session.network = words[0];
        else if (words[0] == "default") session.network.clear();
        else if (words[0] == "computer") return command(words.size() > 1 ? words[1] : "", home);
        else return "/network                 What Auto does about the network in this session\n/network ask             Ask before a sandboxed command reaches a host over HTTPS\n/network off             Commands in Auto have no network\n/network default         Follow the computer's setting\n/network computer ask    Change the computer's setting (or: computer off)";
    }
    const std::string source = session.network.empty() ? "the computer's setting" : "this session's own choice";
    std::string said = asks(session, home) ? "In Auto, a sandboxed command in this session that needs a host over HTTPS asks first (" + source + "). An answer lasts for that request; Always Allow keeps a host for this project."
                                           : "In Auto, sandboxed commands in this session have no network (" + source + ").";
    const std::set<std::string> allowed = always_allowed(home, session.workspace);
    if (!allowed.empty()) said += "\nAlways allowed in this project: " + join(std::vector<std::string>(allowed.begin(), allowed.end()), ", ");
    return said;
}

std::string command(const std::string& rest, const fs::path& home) {
    const std::string word = to_lower(trim(rest));
    if (word == "ask" || word == "off") {
        fs::create_directories(home);
        write_file_atomic(home / "network.json", json{{"mode", word == "ask" ? "ask" : "offline"}}.dump());
    } else if (!word.empty()) {
        return "/network          What Auto does about the network\n/network ask      Ask before a sandboxed command reaches a host over HTTPS; allowed for that request only\n/network off      Commands in Auto have no network";
    }
    return asks(home) ? "In Auto, a sandboxed command that needs a host over HTTPS asks first. An answer lasts for that request. Nothing else is reachable: not this computer, not your local network, not plain HTTP."
                      : "In Auto, sandboxed commands have no network. /network ask lets them ask for a host over HTTPS.";
}

}  // namespace network_settings

NetworkRelay::NetworkRelay(Approve approve, Resolve resolve, bool public_only) : approve_(std::move(approve)), resolve_(resolve ? std::move(resolve) : Resolve(lookup)), public_only_(public_only) {
    folder_ = fs::temp_directory_path() / ("mason-relay-" + new_id().substr(0, 12));
    fs::create_directories(folder_);
    ::chmod(folder_.c_str(), 0700);
    listener_ = unix_socket(socket_path().string(), true);
    if (listener_ < 0) {
        std::error_code ignored;
        fs::remove_all(folder_, ignored);
        throw Error("The network relay could not start.");
    }
    thread_ = std::thread([this] { serve(); });
}

NetworkRelay::~NetworkRelay() {
    {
        std::lock_guard<std::mutex> guard(lock_);
        stopped_ = true;
    }
    shutdown(listener_, SHUT_RDWR);
    close(listener_);
    if (thread_.joinable()) thread_.join();
    std::error_code ignored;
    fs::remove_all(folder_, ignored);
}

std::vector<std::string> NetworkRelay::receipts() {
    std::lock_guard<std::mutex> guard(lock_);
    return log_;
}

void NetworkRelay::record(const std::string& line) {
    std::lock_guard<std::mutex> guard(lock_);
    if (log_.size() < 128) log_.push_back(line);
}

std::string NetworkRelay::wrap(const std::string& command) const {
    std::error_code ignored;
    const std::string program = fs::read_symlink("/proc/self/exe", ignored).string();
    const std::string proxy = "http://127.0.0.1:" + std::to_string(kInsidePort);
    // The forwarder returns once it is listening, so the command never races it.
    return shell::quote(program) + " net-forward " + shell::quote(socket_path().string()) + " " + std::to_string(kInsidePort) +
           " || echo 'Mason: the network relay could not be reached from the sandbox.' >&2\nexport HTTPS_PROXY=" + proxy + " https_proxy=" + proxy + " npm_config_https_proxy=" + proxy + " NO_PROXY= no_proxy=\n" + command;
}

void NetworkRelay::serve() {
    while (true) {
        const int client = accept4(listener_, nullptr, nullptr, SOCK_CLOEXEC);
        if (client < 0) {
            std::lock_guard<std::mutex> guard(lock_);
            if (stopped_) return;
            continue;
        }
        // Each tunnel has its own thread; it ends with its connection, and holds nothing of the relay's once that is gone.
        std::thread([this, client] { handle(client); }).detach();
    }
}

bool NetworkRelay::allowed(const std::string& host) {
    // One question at a time, and one answer for each host while this relay lives.
    std::lock_guard<std::mutex> one(asking_);
    {
        std::lock_guard<std::mutex> guard(lock_);
        const auto earlier = decided_.find(host);
        if (earlier != decided_.end()) return earlier->second;
    }
    const bool answer = approve_ && approve_(host);
    std::lock_guard<std::mutex> guard(lock_);
    decided_[host] = answer;
    return answer;
}

void NetworkRelay::handle(int client) {
    timeval timeout{15, 0};
    setsockopt(client, SOL_SOCKET, SO_RCVTIMEO, &timeout, sizeof timeout);
    std::string head;
    char buffer[2048];
    size_t end = std::string::npos;
    while (head.size() < 8192 && (end = head.find("\r\n\r\n")) == std::string::npos) {
        const ssize_t count = recv(client, buffer, sizeof buffer, 0);
        if (count <= 0) { close(client); return; }
        head.append(buffer, static_cast<size_t>(count));
    }
    auto refuse = [&](const std::string& status, const std::string& reason) {
        send_all(client, "HTTP/1.1 " + status + "\r\nContent-Type: text/plain\r\nConnection: close\r\nContent-Length: " + std::to_string(reason.size() + 1) + "\r\n\r\n" + reason + "\n");
        close(client);
    };
    const std::vector<std::string> words = split(head.substr(0, head.find("\r\n")), ' ', false);
    const size_t colon = words.size() == 3 ? words[1].rfind(':') : std::string::npos;
    const std::string digits = colon == std::string::npos ? "" : words[1].substr(colon + 1);
    if (end == std::string::npos || words.size() != 3 || words[0] != "CONNECT" || digits.empty() || digits.size() > 5 || digits.find_first_not_of("0123456789") != std::string::npos) {
        record("refused: a request that was not an HTTPS tunnel");
        return refuse("405 Method Not Allowed", "Mason relays only HTTPS, through CONNECT to port 443.");
    }
    std::string name;
    try {
        name = network_policy::host(words[1].substr(0, colon), std::stoi(digits));
    } catch (const Error& error) {
        record("refused: " + words[1]);
        return refuse("403 Forbidden", error.what());
    }
    if (!allowed(name)) {
        record("denied by you: " + name);
        return refuse("403 Forbidden", "The user did not allow " + name + ".");
    }
    // The name is looked up here, outside the sandbox, and only a public address is connected to.
    int upstream = -1;
    for (const RelayAddress& target : resolve_(name)) {
        if (public_only_ && !network_policy::is_public(target.bytes)) continue;
        upstream = connected(target);
        break;
    }
    if (upstream < 0) {
        record("allowed but unreachable: " + name);
        return refuse("502 Bad Gateway", name + " could not be reached at a public address.");
    }
    send_all(client, "HTTP/1.1 200 Connection established\r\n\r\n");
    // Anything the client sent after the request's head already belongs to the tunnel.
    const std::string early = head.substr(end + 4);
    long long moved = static_cast<long long>(early.size());
    if (early.empty() || send_all(upstream, early)) moved = pump(client, upstream, moved, 4LL * 1024 * 1024 * 1024);
    close(upstream);
    close(client);
    record("allowed: " + name + ", " + std::to_string(moved) + " bytes");
}

namespace network {

std::optional<int> maybe_forward(int argc, char** argv) {
    if (argc != 4 || std::string(argv[1]) != "net-forward") return std::nullopt;
    const std::string path = argv[2], digits = argv[3];
    if (digits.empty() || digits.size() > 5 || digits.find_first_not_of("0123456789") != std::string::npos) return 2;
    const int listener = socket(AF_INET, SOCK_STREAM | SOCK_CLOEXEC, 0);
    sockaddr_in address{};
    address.sin_family = AF_INET;
    address.sin_port = htons(static_cast<uint16_t>(std::stoi(digits)));
    address.sin_addr.s_addr = inet_addr("127.0.0.1");
    const int reuse = 1;
    setsockopt(listener, SOL_SOCKET, SO_REUSEADDR, &reuse, sizeof reuse);
    if (listener < 0 || bind(listener, reinterpret_cast<sockaddr*>(&address), sizeof address) != 0 || listen(listener, 32) != 0) return 1;
    // It is listening: the starter may go on, and a copy stays behind to do the carrying until the sandbox ends.
    const pid_t child = fork();
    if (child < 0) return 1;
    if (child > 0) return 0;
    const int null = open("/dev/null", O_RDWR);
    dup2(null, 0); dup2(null, 1); dup2(null, 2);
    while (true) {
        const int client = accept4(listener, nullptr, nullptr, SOCK_CLOEXEC);
        if (client < 0) continue;
        std::thread([client, path] {
            const int relay = unix_socket(path, false);
            if (relay >= 0) {
                pump(client, relay, 0, 8LL * 1024 * 1024 * 1024);
                close(relay);
            }
            close(client);
        }).detach();
    }
}

}  // namespace network

}  // namespace mason
