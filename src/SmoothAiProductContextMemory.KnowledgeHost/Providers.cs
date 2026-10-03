using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using SmoothAiProductContextMemory.Knowledge;

namespace SmoothAiProductContextMemory.KnowledgeHost;

public sealed class JevProvider(HttpClient client, string key, string model) : IJevProvider
{
    public string Model => model;
    public async Task<ProviderResult<IReadOnlyDictionary<string, string>>> ChoicesAsync(object state, IReadOnlyDictionary<string, ChoiceQuestion> questions, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.typesafe.ai/v1/systemone");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Content = JsonContent.Create(new { state, model, questions = questions.ToDictionary(q => q.Key, q => new { type = "choice", instructions = q.Value.Instructions, criteria = q.Value.Criteria }) });
        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new KnowledgeException("jev_http_" + (int)response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var decisions = questions.ToDictionary(q => q.Key, q => json.GetProperty("answers").GetProperty(q.Key).GetProperty("choice").GetString() ?? throw new KnowledgeException("invalid_jev_response"));
        var usage = json.GetProperty("usage");
        return new(decisions, json.GetProperty("model").GetString()!, usage.GetProperty("input_tokens").GetInt32(), usage.GetProperty("output_tokens").GetInt32());
    }
}

public sealed class OpenAiProvider(HttpClient client, string key, string model, string? strongerModel) : IGenerativeProvider
{
    public string Model => model;
    public async Task<ProviderResult<T>> GenerateAsync<T>(string instructions, object data, int outputLimit, bool stronger, CancellationToken ct)
    {
        string selected = stronger && !string.IsNullOrWhiteSpace(strongerModel) ? strongerModel : model;
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Content = JsonContent.Create(new
        {
            model = selected,
            messages = new[] { new { role = "system", content = instructions }, new { role = "user", content = KnowledgeJson.Serialize(data) } },
            max_completion_tokens = outputLimit,
            response_format = new { type = "json_schema", json_schema = new { name = typeof(T).Name, strict = true, schema = Schema(typeof(T)) } },
            store = false
        });
        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new KnowledgeException("openai_http_" + (int)response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var choice = json.GetProperty("choices")[0];
        if (choice.GetProperty("finish_reason").GetString() != "stop") throw new KnowledgeException("openai_incomplete");
        var message = choice.GetProperty("message");
        if (message.TryGetProperty("refusal", out var refusal) && refusal.ValueKind != JsonValueKind.Null) throw new KnowledgeException("openai_refused");
        string content = message.GetProperty("content").GetString() ?? throw new KnowledgeException("openai_empty");
        var result = KnowledgeJson.Deserialize<T>(content);
        var usage = json.GetProperty("usage");
        return new(result, json.GetProperty("model").GetString()!, usage.GetProperty("prompt_tokens").GetInt32(), usage.GetProperty("completion_tokens").GetInt32());
    }

    public static JsonNode Schema(Type type)
    {
        Type? nullable = Nullable.GetUnderlyingType(type);
        if (nullable is not null) return new JsonObject { ["anyOf"] = new JsonArray(Schema(nullable), new JsonObject { ["type"] = "null" }) };
        if (type == typeof(string) || type == typeof(Guid)) return new JsonObject { ["type"] = "string" };
        if (type == typeof(bool)) return new JsonObject { ["type"] = "boolean" };
        if (type == typeof(int)) return new JsonObject { ["type"] = "integer" };
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>)) return new JsonObject { ["type"] = "array", ["items"] = Schema(type.GetGenericArguments()[0]) };
        var properties = new JsonObject();
        var required = new JsonArray();
        var nullability = new System.Reflection.NullabilityInfoContext();
        foreach (var property in type.GetProperties())
        {
            string name = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
            var schema = Schema(property.PropertyType);
            if (type == typeof(Candidate) && property.Name == nameof(Candidate.Category)) schema["enum"] = new JsonArray("suggestion", "document_approval", "approved_intent", "observed_implementation", "unknown");
            if (nullability.Create(property).ReadState == System.Reflection.NullabilityState.Nullable && Nullable.GetUnderlyingType(property.PropertyType) is null)
                schema = new JsonObject { ["anyOf"] = new JsonArray(schema, new JsonObject { ["type"] = "null" }) };
            properties.Add(name, schema);
            required.Add(name);
        }
        return new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = required, ["additionalProperties"] = false };
    }
}
