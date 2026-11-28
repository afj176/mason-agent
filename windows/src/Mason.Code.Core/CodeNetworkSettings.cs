using System.Text.Json;

namespace Mason.Code;

/// <summary>
/// The internet setting: whether sandboxed commands in Auto and the web_fetch and web_search tools ask before each host,
/// or have no internet. The computer's setting lives in network.json ({"mode":"ask"|"offline"}, ask when unset) and is
/// what a new session starts with; /network changes one session. "Always" answers are approval rules with the tool
/// "network" and the exact host, one list per project, shared by the command relay and the web tools. Chat, which has
/// no project, keeps its own computer-wide list in chat-hosts.json.
/// </summary>
public static class CodeNetworkSettings
{
    public const string Help = "Internet for commands in Auto and for web_fetch/web_search: ask before each host, or off";
    public const string Notice = "Web pages are read over HTTPS from public hosts only. Every new host asks first.";

    static string File_(string root) => Path.Combine(root, "network.json");
    static string ChatFile(string root) => Path.Combine(root, "chat-hosts.json");

    public static CodeNetworkMode Computer(string root)
    {
        try
        {
            var path = File_(root);
            if (!File.Exists(path) || new FileInfo(path).Length > 4096) return CodeNetworkMode.Ask;
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            return json.RootElement.TryGetProperty("mode", out var mode) && mode.GetString() == "offline" ? CodeNetworkMode.Offline : CodeNetworkMode.Ask;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException) { return CodeNetworkMode.Ask; }
    }

    public static void SetComputer(string root, CodeNetworkMode mode)
    {
        Directory.CreateDirectory(root);
        var path = File_(root); var temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new { mode = mode == CodeNetworkMode.Ask ? "ask" : "offline" }));
        File.Move(temp, path, true);
    }

    /// <summary>The hosts the user chose to always allow for this project. A rule names one host exactly.</summary>
    public static SortedSet<string> AlwaysAllowed(string root, string workspace) =>
        new(CodeApprovalRules.Load(root, workspace).Where(r => r.Tool == "network" && r.Allow).Select(r => r.Subject.Trim().ToLowerInvariant()).Where(h => h.Length > 0), StringComparer.Ordinal);

    public static void Allow(string root, string workspace, string host) =>
        CodeApprovalRules.Add(root, workspace, new("network", CodeNetworkPolicy.Host(host, 443), true, DateTimeOffset.UtcNow));

    public static void Forget(string root, string workspace, string host) =>
        CodeApprovalRules.Save(root, workspace, CodeApprovalRules.Load(root, workspace).Where(r => !(r.Tool == "network" && r.Subject.Equals(host, StringComparison.OrdinalIgnoreCase))));

    /// <summary>Chat's computer-wide always-allowed hosts.</summary>
    public static SortedSet<string> ChatHosts(string root)
    {
        try
        {
            var path = ChatFile(root);
            if (!File.Exists(path) || new FileInfo(path).Length > 65536) return new(StringComparer.Ordinal);
            return new((JsonSerializer.Deserialize<string[]>(File.ReadAllText(path)) ?? []).Select(h => h.ToLowerInvariant()), StringComparer.Ordinal);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(StringComparer.Ordinal); }
    }

    public static void SetChatHost(string root, string host, bool allowed)
    {
        var hosts = ChatHosts(root);
        if (allowed) { if (hosts.Count >= 200) throw new InvalidOperationException("Chat can always allow up to 200 hosts."); hosts.Add(CodeNetworkPolicy.Host(host, 443)); }
        else hosts.Remove(host.ToLowerInvariant());
        Directory.CreateDirectory(root);
        var path = ChatFile(root); var temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(hosts.ToArray()));
        File.Move(temp, path, true);
    }

    static string Name(CodeNetworkMode mode) => mode == CodeNetworkMode.Ask ? "ask" : "off";

    /// <summary>/network in a session: ask and off choose for this session, default follows the computer, and
    /// "computer ask" or "computer off" changes the computer's setting.</summary>
    public static string SessionCommand(string rest, CodeSession session, string root)
    {
        var words = rest.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        switch (words.FirstOrDefault())
        {
            case "ask": session.Network = CodeNetworkMode.Ask; break;
            case "off": session.Network = CodeNetworkMode.Offline; break;
            case "default": session.Network = Computer(root); break;
            case "computer": return Command(string.Join(' ', words.Skip(1)), root);
            case null: break;
            default:
                return "/network                 " + Help + "\n" +
                       "/network ask             Ask before a sandboxed command or web_fetch/web_search reaches a new host over HTTPS\n" +
                       "/network off             No internet in this session: commands in Auto are offline and the web tools are not offered\n" +
                       "/network default         Follow the computer's setting\n" +
                       "/network computer ask    Change the computer's setting (or: computer off)";
        }
        var said = session.Network == CodeNetworkMode.Ask
            ? "This session asks before each new host: sandboxed commands in Auto and web_fetch/web_search. An answer lasts for that request; Always keeps a host for this project."
            : "This session has no internet: commands in Auto are offline and web_fetch/web_search are not offered.";
        said += "\nThe computer's setting is " + Name(Computer(root)) + ".";
        var allowed = AlwaysAllowed(root, session.Workspace);
        if (allowed.Count > 0) said += "\nAlways allowed in this project: " + string.Join(", ", allowed);
        return said;
    }

    /// <summary>The computer's setting, shared by the command line and the app.</summary>
    public static string Command(string rest, string root)
    {
        var word = rest.Trim().ToLowerInvariant();
        if (word is "ask" or "off") SetComputer(root, word == "ask" ? CodeNetworkMode.Ask : CodeNetworkMode.Offline);
        else if (word.Length > 0) return "/network computer ask    New sessions and Chat ask before each new host over HTTPS\n/network computer off    New sessions and Chat have no internet";
        return Computer(root) == CodeNetworkMode.Ask
            ? "On this computer, new sessions and Chat ask before each new host. Only HTTPS to public hosts is reachable: not this computer, not your local network, not plain HTTP."
            : "On this computer, new sessions and Chat have no internet. /network computer ask lets them ask for a host over HTTPS.";
    }
}
