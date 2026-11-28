import Foundation

/// In Auto, commands have no network. With "ask when needed" on, the sandbox may reach one thing: a relay Mason runs
/// on this computer. The relay only tunnels HTTPS (CONNECT to port 443) to a public, named host, and only after the
/// user has allowed that host for the request. A command cannot use it to reach this computer or the local network.
public enum NetworkPolicy {
    /// The destination as it will be asked about: a DNS name, lowercased. Anything but HTTPS to a public name is refused.
    public static func host(_ value: String, port: Int) throws -> String {
        guard port == 443, (3...253).contains(value.utf8.count), value == value.trimmingCharacters(in: .whitespaces), !value.hasSuffix("."), !value.contains(":") else {
            throw MasonError("Only HTTPS (port 443) to a named host is relayed.")
        }
        let name = value.lowercased()
        let labels = name.split(separator: ".", omittingEmptySubsequences: false)
        let numeric = labels.allSatisfy { !$0.isEmpty && $0.allSatisfy { $0.isASCII && $0.isNumber } }
        let wellFormed = labels.allSatisfy { label in
            (1...63).contains(label.count) && label.first != "-" && label.last != "-" && label.allSatisfy { $0.isASCII && ($0.isLetter || $0.isNumber || $0 == "-") }
        }
        guard labels.count >= 2, !numeric, wellFormed, name != "localhost", !name.hasSuffix(".localhost"), !name.hasSuffix(".local"), !name.hasSuffix(".internal") else {
            throw MasonError("A local or malformed destination is not relayed.")
        }
        return name
    }

    /// True for an address on the public internet: not this computer, a private network, a link-local, shared,
    /// documentation, benchmark or multicast range, nor an IPv6 form that carries one of those inside it.
    public static func isPublic(_ address: [UInt8]) -> Bool {
        var b = address
        if b.count == 16, b[0..<10].allSatisfy({ $0 == 0 }), b[10] == 0xff, b[11] == 0xff { b = Array(b[12...]) }
        if b.count == 4 {
            return b[0] != 0 && b[0] != 10 && b[0] != 127 && b[0] < 224 && !(b[0] == 169 && b[1] == 254) && !(b[0] == 172 && (16...31).contains(b[1]))
                && !(b[0] == 192 && (b[1] == 168 || b[1] == 0 || b[1] == 2)) && !(b[0] == 100 && (64...127).contains(b[1]))
                && !(b[0] == 198 && (b[1] == 18 || b[1] == 19 || (b[1] == 51 && b[2] == 100))) && !(b[0] == 203 && b[1] == 0 && b[2] == 113)
        }
        // Global unicast only; documentation, Teredo and 6to4 are refused.
        return b.count == 16 && (b[0] & 0xe0) == 0x20 && !(b[0] == 0x20 && b[1] == 0x01 && (b[2] < 2 || (b[2] == 0x0d && b[3] == 0xb8))) && !(b[0] == 0x20 && b[1] == 0x02)
    }
}

/// Whether Mason asks before reaching a host on the internet, or has no internet at all: for sandboxed commands in
/// Auto (through the relay) and for web_fetch and web_search. Saved for this computer; "ask" is the default.
public enum NetworkSettings {
    public static func asks(home: URL = DataHome.url) -> Bool {
        guard let data = try? Data(contentsOf: home.appendingPathComponent("network.json")), let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return true }
        return object["mode"] as? String != "offline" && object["mode"] as? String != "off"
    }

    /// Whether this session asks: its own choice when it has made one, otherwise the computer's.
    public static func asks(_ session: Session, home: URL = DataHome.url) -> Bool { session.network.map { $0 == "ask" } ?? asks(home: home) }

    /// The hosts the user chose to always allow for this project. A rule names one host exactly.
    public static func alwaysAllowed(home: URL, workspace: String) -> Set<String> {
        Set(ApprovalRules(home: home, workspace: workspace).rules.filter { $0.tool == "network" }.map { $0.prefix.lowercased() })
    }

    /// Removes a host from a project's always-allowed list.
    public static func forget(_ host: String, home: URL = DataHome.url, workspace: String) throws {
        var rules = ApprovalRules(home: home, workspace: workspace)
        while let index = rules.rules.firstIndex(where: { $0.tool == "network" && $0.prefix.lowercased() == host.lowercased() }) { try rules.remove(at: index) }
    }

    // MARK: the hosts Chat may read without asking, for the whole computer

    static func chatFile(_ home: URL) -> URL { home.appendingPathComponent("chat-hosts.json") }

    /// The hosts the user chose to always allow in Chat. They are kept apart from every project's list.
    public static func chatHosts(home: URL = DataHome.url) -> [String] {
        guard let data = try? Data(contentsOf: chatFile(home)), let hosts = try? JSONDecoder().decode([String].self, from: data) else { return [] }
        return hosts
    }

    public static func addChatHost(_ host: String, home: URL = DataHome.url) throws {
        var hosts = chatHosts(home: home).filter { $0 != host.lowercased() }
        hosts.append(host.lowercased())
        try saveChatHosts(hosts.sorted(), home: home)
    }

    public static func removeChatHost(_ host: String, home: URL = DataHome.url) throws {
        try saveChatHosts(chatHosts(home: home).filter { $0 != host.lowercased() }, home: home)
    }

    static func saveChatHosts(_ hosts: [String], home: URL) throws {
        try FileManager.default.createDirectory(at: home, withIntermediateDirectories: true)
        try JSONEncoder().encode(Array(hosts.prefix(512))).write(to: chatFile(home), options: .atomic)
    }

    public static let note = "Web pages are read over HTTPS from public hosts only. Every new host asks first."

    /// /network in a session: ask and off choose for this session, default goes back to the computer's setting, and
    /// "computer ask" or "computer off" changes that setting.
    public static func sessionCommand(_ rest: String, session: inout Session, home: URL = DataHome.url) throws -> String {
        let words = rest.lowercased().split(separator: " ").map(String.init)
        switch words.first {
        case "ask", "off": session.network = words[0]
        case "default": session.network = nil
        case "computer": return try command(words.dropFirst().joined(separator: " "), home: home)
        case nil: break
        default: return "/network                 Internet for commands in Auto and for web_fetch/web_search in this session\n/network ask             Ask before each host, over HTTPS only\n/network off             No internet: commands have no network and the web tools are not offered\n/network default         Follow the computer's setting\n/network computer ask    Change the computer's setting (or: computer off)"
        }
        let here = asks(session, home: home)
        let source = session.network == nil ? "the computer's setting" : "this session's own choice"
        let allowed = alwaysAllowed(home: home, workspace: session.workspace).sorted()
        var said = "This session has no internet (\(source)): sandboxed commands in Auto have no network, and web_fetch and web_search are not offered."
        if here { said = "This session asks before each host (\(source)): a sandboxed command in Auto, web_fetch and web_search each ask before reaching a new host over HTTPS. Always keeps a host for this project." }
        if !allowed.isEmpty { said += "\nAlways allowed in this project: " + allowed.joined(separator: ", ") }
        return said
    }

    /// The /network command, shared by the command line and the app.
    public static func command(_ rest: String, home: URL = DataHome.url) throws -> String {
        let word = rest.trimmingCharacters(in: .whitespaces).lowercased()
        if word == "ask" || word == "off" {
            try FileManager.default.createDirectory(at: home, withIntermediateDirectories: true)
            try JSONSerialization.data(withJSONObject: ["mode": word == "ask" ? "ask" : "offline"]).write(to: home.appendingPathComponent("network.json"), options: .atomic)
        } else if !word.isEmpty {
            return "/network          Internet for commands in Auto and for web_fetch/web_search: ask before each host, or off\n/network ask      Ask before each host, over HTTPS only\n/network off      No internet: commands have no network and the web tools are not offered"
        }
        return asks(home: home) ? "Mason asks before each host: a sandboxed command in Auto, web_fetch and web_search each ask before reaching a new host over HTTPS. Nothing else is reachable: not this computer, not your local network, not plain HTTP."
                                : "Mason has no internet: sandboxed commands have no network and the web tools are not offered. /network ask lets them ask for a host over HTTPS."
    }
}

public final class NetworkRelay: @unchecked Sendable {
    public typealias Address = (bytes: [UInt8], port: Int)
    /// The relay listens on 127.0.0.1 at this port.
    public let port: Int
    private let listener: Int32
    private let approve: (String) -> Bool
    private let resolve: (String) -> [Address]
    private let publicOnly: Bool
    private let lock = NSLock()
    private let asking = NSLock()
    private var decided: [String: Bool] = [:]
    private var log: [String] = []
    private var stopped = false

    /// `approve` is asked once for each host. `resolve` and `publicOnly` exist for tests; a real relay looks names up
    /// with the system and connects only to public addresses.
    public init(approve: @escaping (String) -> Bool, resolve: ((String) -> [Address])? = nil, publicOnly: Bool = true) throws {
        self.approve = approve
        self.resolve = resolve ?? NetworkRelay.lookup
        self.publicOnly = publicOnly
        let descriptor = NetworkRelay.stream(AF_INET)
        guard descriptor >= 0 else { throw MasonError("The network relay could not start.") }
        var address = sockaddr_in()
        address.sin_family = sa_family_t(AF_INET)
        address.sin_addr.s_addr = inet_addr("127.0.0.1")
        let bound = withUnsafePointer(to: &address) { $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { bind(descriptor, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) } }
        var length = socklen_t(MemoryLayout<sockaddr_in>.size)
        let named = withUnsafeMutablePointer(to: &address) { $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { getsockname(descriptor, $0, &length) } }
        guard bound == 0, named == 0, listen(descriptor, 32) == 0 else { close(descriptor); throw MasonError("The network relay could not start.") }
        listener = descriptor
        port = Int(UInt16(bigEndian: address.sin_port))
        let thread = Thread { [self] in serve() }
        thread.start()
    }

    /// What the relay let through and what it refused, in order.
    public var receipts: [String] { lock.lock(); defer { lock.unlock() }; return log }

    /// What a command needs in its environment to use the relay.
    public var environment: [String: String] {
        let proxy = "http://127.0.0.1:\(port)"
        return ["HTTPS_PROXY": proxy, "https_proxy": proxy, "npm_config_https_proxy": proxy, "NO_PROXY": "", "no_proxy": ""]
    }

    public func stop() {
        lock.lock(); let already = stopped; stopped = true; lock.unlock()
        if already { return }
        shutdown(listener, Int32(SHUT_RDWR))
        close(listener)
    }

    deinit { stop() }

    private func record(_ line: String) { lock.lock(); if log.count < 128 { log.append(line) }; lock.unlock() }

    private func serve() {
        while true {
            let client = accept(listener, nil, nil)
            if client < 0 {
                lock.lock(); let done = stopped; lock.unlock()
                if done { return }
                continue
            }
            let thread = Thread { [self] in handle(client) }
            thread.start()
        }
    }

    /// One question at a time, and one answer for each host while this relay lives.
    private func allowed(_ host: String) -> Bool {
        asking.lock(); defer { asking.unlock() }
        lock.lock(); let earlier = decided[host]; lock.unlock()
        if let earlier { return earlier }
        let answer = approve(host)
        lock.lock(); decided[host] = answer; lock.unlock()
        return answer
    }

    private func handle(_ client: Int32) {
        defer { close(client) }
        NetworkRelay.quiet(client)
        var timeout = timeval(tv_sec: 15, tv_usec: 0)
        setsockopt(client, SOL_SOCKET, SO_RCVTIMEO, &timeout, socklen_t(MemoryLayout<timeval>.size))
        var head = [UInt8](), buffer = [UInt8](repeating: 0, count: 2048)
        while head.count < 8192, !NetworkRelay.ends(head) {
            let count = recv(client, &buffer, buffer.count, 0)
            if count <= 0 { return }
            head.append(contentsOf: buffer[0..<count])
        }
        func refuse(_ status: String, _ reason: String) {
            NetworkRelay.send(client, Array("HTTP/1.1 \(status)\r\nContent-Type: text/plain\r\nConnection: close\r\nContent-Length: \(reason.utf8.count + 1)\r\n\r\n\(reason)\n".utf8))
        }
        // Anything a client sent after the request's head already belongs to the tunnel.
        let end = NetworkRelay.headEnd(head) ?? head.count
        let early = Array(head[end...])
        let words = (String(decoding: head[..<end], as: UTF8.self).components(separatedBy: "\r\n").first ?? "").split(separator: " ")
        guard NetworkRelay.ends(head), words.count == 3, words[0] == "CONNECT", let colon = words[1].lastIndex(of: ":"), let wanted = Int(words[1][words[1].index(after: colon)...]) else {
            record("refused: a request that was not an HTTPS tunnel")
            return refuse("405 Method Not Allowed", "Mason relays only HTTPS, through CONNECT to port 443.")
        }
        let name: String
        do { name = try NetworkPolicy.host(String(words[1][..<colon]), port: wanted) }
        catch {
            record("refused: \(words[1])")
            return refuse("403 Forbidden", (error as? MasonError)?.description ?? "Refused.")
        }
        guard allowed(name) else {
            record("denied by you: \(name)")
            return refuse("403 Forbidden", "The user did not allow \(name).")
        }
        // The name is looked up here, outside the sandbox, and only a public address is connected to.
        guard let target = resolve(name).first(where: { !publicOnly || NetworkPolicy.isPublic($0.bytes) }), let upstream = NetworkRelay.connected(to: target) else {
            record("allowed but unreachable: \(name)")
            return refuse("502 Bad Gateway", "\(name) could not be reached at a public address.")
        }
        defer { close(upstream) }
        NetworkRelay.send(client, Array("HTTP/1.1 200 Connection established\r\n\r\n".utf8))
        var moved = early.count
        if !early.isEmpty, !NetworkRelay.send(upstream, early) { return }
        var fds = [pollfd(fd: client, events: Int16(POLLIN), revents: 0), pollfd(fd: upstream, events: Int16(POLLIN), revents: 0)]
        var chunk = [UInt8](repeating: 0, count: 32 * 1024)
        pumping: while moved < 4 * 1024 * 1024 * 1024 {
            // Two idle minutes end a tunnel.
            guard poll(&fds, 2, 120_000) > 0 else { break }
            for (index, other) in [(0, upstream), (1, client)] where fds[index].revents != 0 {
                let count = recv(fds[index].fd, &chunk, chunk.count, 0)
                if count <= 0 || !NetworkRelay.send(other, Array(chunk[0..<count])) { break pumping }
                moved += count
            }
        }
        record("allowed: \(name), \(moved) bytes")
    }

    // MARK: sockets

    static func stream(_ family: Int32) -> Int32 {
        #if canImport(Darwin)
        return socket(family, SOCK_STREAM, 0)
        #else
        return socket(family, Int32(SOCK_STREAM.rawValue), 0)
        #endif
    }

    /// A socket whose other end has gone gives an error here, not a signal that would end Mason.
    static func quiet(_ descriptor: Int32) {
        #if canImport(Darwin)
        var on: Int32 = 1
        setsockopt(descriptor, SOL_SOCKET, SO_NOSIGPIPE, &on, socklen_t(MemoryLayout<Int32>.size))
        #endif
    }

    @discardableResult
    static func send(_ descriptor: Int32, _ bytes: [UInt8]) -> Bool {
        var sent = 0
        while sent < bytes.count {
            #if canImport(Darwin)
            let flags: Int32 = 0
            #else
            let flags = Int32(MSG_NOSIGNAL)
            #endif
            let count = bytes[sent...].withUnsafeBytes { Foundation.send(descriptor, $0.baseAddress, $0.count, flags) }
            if count <= 0 { return false }
            sent += count
        }
        return true
    }

    /// Where a request's head ends (after its blank line), if it has arrived.
    static func headEnd(_ bytes: [UInt8]) -> Int? {
        guard bytes.count >= 4 else { return nil }
        for index in 0...(bytes.count - 4) where bytes[index] == 13 && bytes[index + 1] == 10 && bytes[index + 2] == 13 && bytes[index + 3] == 10 { return index + 4 }
        return nil
    }

    static func ends(_ head: [UInt8]) -> Bool { headEnd(head) != nil }

    static func lookup(_ host: String) -> [Address] {
        var hints = addrinfo()
        #if canImport(Darwin)
        hints.ai_socktype = SOCK_STREAM
        #else
        hints.ai_socktype = Int32(SOCK_STREAM.rawValue)
        #endif
        var found: UnsafeMutablePointer<addrinfo>?
        guard getaddrinfo(host, "443", &hints, &found) == 0 else { return [] }
        defer { freeaddrinfo(found) }
        var addresses: [Address] = []
        var next = found
        while let entry = next {
            if entry.pointee.ai_family == AF_INET, let raw = entry.pointee.ai_addr {
                addresses.append((raw.withMemoryRebound(to: sockaddr_in.self, capacity: 1) { withUnsafeBytes(of: $0.pointee.sin_addr) { Array($0) } }, 443))
            } else if entry.pointee.ai_family == AF_INET6, let raw = entry.pointee.ai_addr {
                addresses.append((raw.withMemoryRebound(to: sockaddr_in6.self, capacity: 1) { withUnsafeBytes(of: $0.pointee.sin6_addr) { Array($0) } }, 443))
            }
            next = entry.pointee.ai_next
        }
        return addresses
    }

    static func connected(to target: Address) -> Int32? {
        let six = target.bytes.count == 16
        let descriptor = stream(six ? AF_INET6 : AF_INET)
        guard descriptor >= 0 else { return nil }
        quiet(descriptor)
        var timeout = timeval(tv_sec: 15, tv_usec: 0)
        setsockopt(descriptor, SOL_SOCKET, SO_SNDTIMEO, &timeout, socklen_t(MemoryLayout<timeval>.size))
        let result: Int32
        if six {
            var address = sockaddr_in6()
            address.sin6_family = sa_family_t(AF_INET6)
            address.sin6_port = in_port_t(UInt16(target.port).bigEndian)
            withUnsafeMutableBytes(of: &address.sin6_addr) { $0.copyBytes(from: target.bytes) }
            result = withUnsafePointer(to: &address) { $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { connect(descriptor, $0, socklen_t(MemoryLayout<sockaddr_in6>.size)) } }
        } else {
            var address = sockaddr_in()
            address.sin_family = sa_family_t(AF_INET)
            address.sin_port = in_port_t(UInt16(target.port).bigEndian)
            withUnsafeMutableBytes(of: &address.sin_addr) { $0.copyBytes(from: target.bytes) }
            result = withUnsafePointer(to: &address) { $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { connect(descriptor, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) } }
        }
        if result != 0 { close(descriptor); return nil }
        return descriptor
    }
}
