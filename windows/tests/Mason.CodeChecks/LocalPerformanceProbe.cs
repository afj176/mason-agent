using System.Text.Json;
using System.Diagnostics;
using Mason;
using Mason.Code;

static class LocalPerformanceProbe
{
    public static async Task Run(string endpoint, string exactModel, string output)
    {
        var profile = new AiEndpointProfile { Endpoint = endpoint, Model = exactModel };
        using var client = new LocalAiClient();
        var server = await client.ChatServer(profile, "", default);
        if (!server.IdentityVerified || !server.ExactContext) throw new Exception("Probe requires verified model and exact context.");
        var options = new ChatGenerationOptions { Temperature = .15, Reasoning = "default" };
        var messages = new[] { new AiMessage("system", "Be helpful, clear and practical."), new AiMessage("user", "Write a substantial CSS stylesheet with 100 numbered class rules. Return CSS only.") };
        var result = await client.StreamChat(profile, "", messages, 256, _ => { }, default, options: options, server: server);
        var adapter = new CodeLocalModel(profile, "", "default", _ => { });
        var clock = Stopwatch.StartNew();
        var action = await adapter.Next(new[] { new CodeMessage("system", CodeProtocol.Instructions + "\nAgent mode: build\nPermissions: WorkspaceEdits\nOperating system: Windows.\nWorkspace: transport-only-probe"), new CodeMessage("user", "{\"request\":\"Return one create_file action for public/probe.css containing exactly 20 simple numbered CSS class rules. This is a transport probe; no tool will execute.\",\"references\":[]}") }, _ => { }, default);
        var report = new { exactModel, loadedContext = server.Context, chat = result, chatSummary = result.PerformanceSummary, agent = new { seconds = clock.Elapsed.TotalSeconds, tool = action.Tool, performance = action.PerformanceSummary }, toolsExecuted = 0 };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }
}
