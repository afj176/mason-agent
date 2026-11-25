using System.Text;
using System.Text.Json;
using Mason;

/// <summary>Runs a GGUF model inside this process through llama-server-impl.dll. Needs a real llama.cpp build and model:
/// --embedded-llama-check PATH\llama-server.exe PATH\model.gguf</summary>
static class EmbeddedLlamaChecks
{
    public static async Task Run(string server, string model)
    {
        void Check(bool passed, string name) { Console.WriteLine((passed ? "PASS " : "FAIL ") + name); if (!passed) throw new Exception(name); }
        var root = Path.Combine(Path.GetTempPath(), "mason-embedded-" + Guid.NewGuid().ToString("N")[..8]);
        var stub = Path.Combine(root, "stub", "llama-server.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(stub)!); File.WriteAllBytes(stub, [0x4D, 0x5A]);
        Check(EmbeddedLlamaServer.Unavailable(stub)?.Contains("llama-server-impl.dll") == true, "a build without llama-server-impl.dll is reported, not loaded");
        Check(EmbeddedLlamaServer.Unavailable(server) == null, "the selected build can be embedded");

        var devices = EmbeddedLlamaServer.Load(server, null);
        Console.WriteLine("devices: " + string.Join(", ", devices));
        bool gpu = devices.Contains("CUDA0");
        string[] Arguments(int port) => ["-m", model, "--host", "127.0.0.1", "--port", port.ToString(), "--alias", "embedded-check", "-c", "8192", "--parallel", "1",
            "--device", gpu ? "CUDA0" : "none", "--split-mode", "none", "-ngl", gpu ? "99" : "0", "-b", "256", "-ub", "128", "--reasoning", "auto", "--spec-type", "none"];
        var other = Path.Combine(root, "other", "llama-server.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(other)!); File.WriteAllBytes(other, [0x4D, 0x5A]);
        File.Copy(EmbeddedLlamaServer.ImplPath(server), EmbeddedLlamaServer.ImplPath(other));
        Check(EmbeddedLlamaServer.Unavailable(other)?.Contains("Restart Mason") == true, "a second llama.cpp build is refused once one is loaded");

        // Stop while loading: the server's shutdown handler is not installed yet, so the stop must wait for it.
        var early = EmbeddedLlamaServer.Start(server, Arguments(18391), Path.Combine(root, "early.log"), null);
        early.Stop();
        await early.Exited.WaitAsync(TimeSpan.FromMinutes(5));
        Check(!early.IsRunning && early.ExitCode == 0, "a stop requested during loading ends the server cleanly");

        for (int round = 1; round <= 2; round++)
        {
            int port = 18391 + round;
            var log = Path.Combine(root, $"round-{round}.log");
            var host = EmbeddedLlamaServer.Start(server, Arguments(port), log, null);
            Check(host.Embedded && host.Lifetime == null && host.ProcessId == Environment.ProcessId, $"round {round}: the model runs in this process");
            Check(EmbeddedLlamaServer.Unavailable(server)?.Contains("Another model") == true, $"round {round}: a second embedded model is refused while one runs");
            var profile = new AiEndpointProfile { Name = "embedded", Endpoint = $"http://127.0.0.1:{port}/", Model = "embedded-check", ManagedCapability = AiCapability.Chat };
            using var client = new LocalAiClient();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                if (!host.IsRunning) throw new Exception("embedded server exited early: " + string.Join("\n", File.ReadLines(log).TakeLast(10)));
                var listing = await client.Check(profile, AiCapability.Models, "", CancellationToken.None);
                if (listing.State == "Passed" && listing.Models?.Contains("embedded-check") == true) break;
                if (clock.Elapsed > TimeSpan.FromMinutes(5)) throw new TimeoutException("embedded server did not become ready");
                await Task.Delay(500);
            }
            Console.WriteLine($"round {round}: ready in {clock.Elapsed.TotalSeconds:F1}s");
            var info = await client.ChatServer(profile, "", CancellationToken.None);
            Check(info.Llama && info.IdentityVerified && info.Context == 8192, $"round {round}: Mason's client sees llama.cpp props, the model ID and the loaded context");
            var chat = await client.Check(profile, AiCapability.Chat, "", CancellationToken.None);
            Check(chat.State == "Passed", $"round {round}: chat capability check passes");

            using var http = new HttpClient { BaseAddress = new Uri(profile.Endpoint) };
            var schema = """{"model":"embedded-check","messages":[{"role":"user","content":"Return JSON with name set to Mason and n set to 3."}],"max_tokens":64,"temperature":0,"response_format":{"type":"json_object","schema":{"type":"object","properties":{"name":{"type":"string"},"n":{"type":"integer"}},"required":["name","n"],"additionalProperties":false}}}""";
            using var response = await http.PostAsync("v1/chat/completions", new StringContent(schema, Encoding.UTF8, "application/json"));
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            using var produced = JsonDocument.Parse(body.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()!);
            Check(produced.RootElement.TryGetProperty("name", out _) && produced.RootElement.GetProperty("n").ValueKind == JsonValueKind.Number, $"round {round}: a JSON-schema response is grammar-constrained");

            host.Stop();
            await host.Exited.WaitAsync(TimeSpan.FromMinutes(1));
            Check(!host.IsRunning && host.ExitCode == 0, $"round {round}: terminate unloads the model and returns 0");
            Check(EmbeddedLlamaServer.Unavailable(server) == null, $"round {round}: the slot is free again");
        }
        try { Directory.Delete(root, true); } catch (IOException) { }
        Console.WriteLine("embedded llama.cpp checks passed");
    }
}
