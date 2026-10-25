using System.Diagnostics;
using System.Text.Json.Nodes;
using SmoothAiProductContextMemory.Knowledge;

namespace SmoothAiProductContextMemory.KnowledgeHost;

public sealed class SecretFilter(string python, string script) : ISecretFilter
{
    public async Task<T> SanitizeAsync<T>(T value, CancellationToken ct)
    {
        var node = JsonNode.Parse(KnowledgeJson.Serialize(value)) ?? throw new KnowledgeException("invalid_sanitizer_input");
        var strings = new List<string>();
        Collect(node, strings);
        if (strings.Count == 0) return value;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var start = new ProcessStartInfo(python) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(script);
        using var process = new Process { StartInfo = start };
        bool started = false;
        try
        {
            process.Start();
            started = true;
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.StandardInput.WriteAsync(KnowledgeJson.Serialize(strings).AsMemory(), timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            await error;
            if (process.ExitCode != 0) throw new KnowledgeException("secret_detector_failed");
            var result = JsonNode.Parse(await output)?["results"]?.AsArray() ?? throw new KnowledgeException("secret_detector_failed");
            if (result.Count != strings.Count) throw new KnowledgeException("secret_detector_failed");
            var replacements = new Queue<string>(result.Select(r => r?["redacted"]?.GetValue<string>() ?? throw new KnowledgeException("secret_detector_failed")));
            node = Replace(node, replacements);
            return KnowledgeJson.Deserialize<T>(node.ToJsonString());
        }
        catch (Exception exception) when (exception is not KnowledgeException)
        {
            if (started && !process.HasExited) process.Kill(true);
            throw new KnowledgeException("secret_detector_failed");
        }
    }

    private static void Collect(JsonNode? node, List<string> strings)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out string? text)) strings.Add(text);
        else if (node is JsonObject obj) foreach (var entry in obj) Collect(entry.Value, strings);
        else if (node is JsonArray array) foreach (var entry in array) Collect(entry, strings);
    }
    private static JsonNode Replace(JsonNode node, Queue<string> replacements)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out _)) return JsonValue.Create(replacements.Dequeue())!;
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(p => p.Key).ToArray()) if (obj[key] is { } child) obj[key] = Replace(child.DeepClone(), replacements);
        }
        else if (node is JsonArray array)
        {
            for (int index = 0; index < array.Count; index++) if (array[index] is { } entry) array[index] = Replace(entry.DeepClone(), replacements);
        }
        return node;
    }
}
