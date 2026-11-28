// In Auto, commands have no network. With "ask when needed" on, the sandbox may reach one thing: a relay Mason runs
// on this computer. The relay only tunnels HTTPS (CONNECT to port 443) to a public, named host, and only after the
// user has allowed that host for the request. A command cannot use it to reach this computer or the local network.
#pragma once
#include "session.hpp"
#include "workspace.hpp"

#include <set>

#include <cstdint>
#include <map>
#include <mutex>
#include <thread>

namespace mason {

namespace network_policy {
/// The destination as it will be asked about: a DNS name, lowercased. Anything but HTTPS to a public name is refused.
std::string host(const std::string& value, int port);
/// True for an address on the public internet: not this computer, a private network, a link-local, shared,
/// documentation, benchmark or multicast range, nor an IPv6 form that carries one of those inside it.
bool is_public(std::vector<uint8_t> address);
}  // namespace network_policy

/// Internet for sandboxed commands in Auto and for the web tools: ask before each host (the default), or off.
/// Saved for this computer; a session can choose its own.
namespace network_settings {
bool asks(const fs::path& home);
/// Whether this session asks: its own choice when it has made one, otherwise the computer's. When it is off, the
/// web tools are not offered at all.
bool asks(const Session& session, const fs::path& home);
/// The hosts the user chose to always allow for this project. A rule names one host exactly. The relay and the web
/// tools share this one list.
std::set<std::string> always_allowed(const fs::path& home, const std::string& workspace);
void forget(const fs::path& home, const std::string& workspace, const std::string& host);
/// The hosts always allowed in Chat, for the whole computer, kept apart from the projects' lists.
std::set<std::string> chat_hosts(const fs::path& home);
void allow_in_chat(const fs::path& home, const std::string& host);
void forget_in_chat(const fs::path& home, const std::string& host);
/// /network in a session: ask and off choose for this session, default goes back to the computer's setting, and
/// "computer ask" or "computer off" changes that setting.
std::string session_command(const std::string& rest, Session& session, const fs::path& home);
/// The /network command for the computer's setting.
std::string command(const std::string& rest, const fs::path& home);
}  // namespace network_settings

struct RelayAddress {
    std::vector<uint8_t> bytes;
    int port = 443;
};

class NetworkRelay {
public:
    using Approve = std::function<bool(const std::string& host)>;
    using Resolve = std::function<std::vector<RelayAddress>(const std::string& host)>;
    /// The port commands inside the sandbox use; a forwarder there carries it to the relay's socket.
    static constexpr int kInsidePort = 3128;

    /// `approve` is asked once for each host. `resolve` and `public_only` exist for tests; a real relay looks names up
    /// with the system and connects only to public addresses.
    explicit NetworkRelay(Approve approve, Resolve resolve = nullptr, bool public_only = true);
    ~NetworkRelay();
    NetworkRelay(const NetworkRelay&) = delete;
    NetworkRelay& operator=(const NetworkRelay&) = delete;

    /// The folder holding the relay's socket, which the sandbox is given, and the socket in it.
    const fs::path& folder() const { return folder_; }
    fs::path socket_path() const { return folder_ / "relay.sock"; }
    /// What the relay let through and what it refused, in order.
    std::vector<std::string> receipts();
    /// `command` as it runs in the sandbox with the relay within reach.
    std::string wrap(const std::string& command) const;

private:
    void serve();
    void handle(int client);
    bool allowed(const std::string& host);
    void record(const std::string& line);

    Approve approve_;
    Resolve resolve_;
    bool public_only_;
    fs::path folder_;
    int listener_ = -1;
    std::thread thread_;
    std::mutex lock_, asking_;
    std::map<std::string, bool> decided_;
    std::vector<std::string> log_;
    bool stopped_ = false;
};

namespace network {
/// The addresses a name resolves to, with the system's resolver, for port 443.
std::vector<RelayAddress> resolve(const std::string& host);
/// When a program is started as `PROGRAM net-forward SOCKET PORT`, it listens on 127.0.0.1:PORT, carries each
/// connection to the Unix socket, and this returns its exit code. Otherwise this returns nothing and the program goes
/// on as usual. Every Mason program calls it first, because the sandbox starts whichever one is running.
std::optional<int> maybe_forward(int argc, char** argv);
}  // namespace network

}  // namespace mason
