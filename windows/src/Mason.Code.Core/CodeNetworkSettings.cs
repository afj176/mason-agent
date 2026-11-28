using System.Text.Json;

namespace Mason.Code;

/// <summary>
/// The internet setting, for the computer (network.json, {"mode":"ask"|"offline"}) and for each session (/network).
/// Unset, the default: sandboxed commands in Auto have no network, as before, and web_fetch/web_search ask per host.
/// "ask": commands and the web tools both ask per host. "off": neither; the web group is not offered and Chat has no
/// web tools. A new session starts with the computer's setting. "Always" answers are approval rules with the tool
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
            if (!File.Exists(path) || new FileInfo(path).Length > 4096) return CodeNetworkMode.Default;
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            return (json.RootElement.TryGetProperty("mode", out var mode) ? mode.GetString() : null) switch { "offline" => CodeNetworkMode.Off, "ask" => CodeNetworkMode.Ask, _ => CodeNetworkMode.Default };
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException) { return CodeNetworkMode.Default; }
    }

    /// <summary>Saves the computer's setting; Default removes it, so the computer is back to the default.</summary>
    public static void SetComputer(string root, CodeNetworkMode mode)
    {
        var path = File_(root);
        if (mode == CodeNetworkMode.Default) { if (File.Exists(path)) File.Delete(path); return; }
        Directory.CreateDirectory(root);
        var temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new { mode = mode == CodeNetworkMode.Ask ? "ask" : "offline" }));
        File.Move(temp, path, true);
    }

    /// <summary>The settings in the order the app lists them.</summary>
    public static readonly CodeNetworkMode[] Modes = [CodeNetworkMode.Default, CodeNetworkMode.Ask, CodeNetworkMode.Off];
    public static readonly string[] ModeLabels = ["Default: commands offline, web asks", "Ask for commands and web", "Off: no internet"];

    /// <summary>Whether sandboxed commands in Auto may ask for a host: only with "ask".</summary>
    public static bool CommandsAsk(CodeNetworkMode mode) => mode == CodeNetworkMode.Ask;
    /// <summary>Whether web_fetch and web_search are offered: with "ask" and the default, never with "off".</summary>
    public static bool WebOffered(CodeNetworkMode mode) => mode != CodeNetworkMode.Off;

    /// <summary>What a setting means, in one plain sentence.</summary>
    public static string Describe(CodeNetworkMode mode) => mode switch
    {
        CodeNetworkMode.Ask => "Commands in Auto and web_fetch/web_search ask before each new host.",
        CodeNetworkMode.Off => "No internet: commands in Auto are offline and web_fetch/web_search are not offered.",
        _ => "Default: commands in Auto are offline; web_fetch/web_search ask before each new host.",
    };

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

    static string Name(CodeNetworkMode mode) => mode switch { CodeNetworkMode.Ask => "ask", CodeNetworkMode.Off => "off", _ => "default" };

    /// <summary>/network in a session: ask, off or default choose for this session, and "computer ask|off|default"
    /// changes the computer's setting.</summary>
    public static string SessionCommand(string rest, CodeSession session, string root)
    {
        var words = rest.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        switch (words.FirstOrDefault())
        {
            case "ask": session.Network = CodeNetworkMode.Ask; break;
            case "off": session.Network = CodeNetworkMode.Off; break;
            case "default": session.Network = CodeNetworkMode.Default; break;
            case "computer": return Command(string.Join(' ', words.Skip(1)), root);
            case null: break;
            default:
                return "/network                      " + Help + "\n" +
                       "/network default              Commands in Auto are offline; web_fetch/web_search ask before each new host\n" +
                       "/network ask                  Commands in Auto and web_fetch/web_search ask before each new host\n" +
                       "/network off                  No internet: commands are offline and the web tools are not offered\n" +
                       "/network computer ask|off|default   The setting new sessions and Chat start with";
        }
        var said = "This session: " + Describe(session.Network);
        if (session.Network != CodeNetworkMode.Off) said += " An answer lasts for that request; Always keeps a host for this project.";
        said += "\nThe computer's setting is " + Name(Computer(root)) + ".";
        var allowed = AlwaysAllowed(root, session.Workspace);
        if (allowed.Count > 0) said += "\nAlways allowed in this project: " + string.Join(", ", allowed);
        return said;
    }

    /// <summary>The computer's setting, shared by the command line and the app.</summary>
    public static string Command(string rest, string root)
    {
        var word = rest.Trim().ToLowerInvariant();
        if (word is "ask" or "off" or "default") SetComputer(root, word == "ask" ? CodeNetworkMode.Ask : word == "off" ? CodeNetworkMode.Off : CodeNetworkMode.Default);
        else if (word.Length > 0) return "/network computer default   New sessions: commands offline, web tools ask. Chat: web tools ask\n/network computer ask       New sessions: commands and web tools ask. Chat: web tools ask\n/network computer off       New sessions and Chat have no internet";
        return "On this computer, new sessions start with: " + Describe(Computer(root)) + (Computer(root) == CodeNetworkMode.Off ? " Chat has no web tools." : " In Chat, web_fetch/web_search ask before each new host.") +
            "\nOnly HTTPS to public hosts is reachable: not this computer, not your local network, not plain HTTP.";
    }
}
