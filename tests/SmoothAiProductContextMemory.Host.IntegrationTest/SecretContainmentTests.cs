using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Host.IntegrationTest;

/// <summary>
/// HLD-002 NFR-01's server-side log containment: a secret-shaped value that reaches the Host — the
/// client gate missed it, or a caller skipped the gate — must appear in no log line at any level and
/// in no error response body. The Host does not redact (the client-side scrub is the gate), so this
/// asserts containment of what it is sent, not detection.
/// </summary>
public sealed class SecretContainmentTests(SecretContainmentWebAppFixture fixture)
    : IClassFixture<SecretContainmentWebAppFixture>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http = fixture.HttpClient;

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_write_carrying_a_secret_shaped_value_logs_it_nowhere()
    {
        string planted = PlantedSecret();
        Guid group = await ResolveGroup();

        using HttpResponseMessage response = await _http.PostAsJsonAsync(
            "/api/context/memories", WriteBody(group, planted), Json, Ct);
        string payload = await response.Content.ReadAsStringAsync(Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, payload);
        payload.ShouldNotContain(planted);
        ShouldNotBeLogged(planted);
    }

    [Fact]
    public async Task A_rejected_write_carrying_a_secret_shaped_value_echoes_it_nowhere()
    {
        string planted = PlantedSecret();
        Guid group = await ResolveGroup();

        // Validation: the planted value is in free text and in the two fields whose rules fail.
        object invalid = WriteBody(group, planted, status: planted, kind: planted + new string('x', 64));
        using HttpResponseMessage rejected = await _http.PostAsJsonAsync("/api/context/memories", invalid, Json, Ct);
        string rejectedBody = await rejected.Content.ReadAsStringAsync(Ct);

        // Deserialisation: the planted value is where a number belongs, the path that quotes a
        // JsonException message into the problem detail.
        string unreadable = JsonSerializer.Serialize(WriteBody(group, planted), Json)
            .Replace("\"confidence\":80", $"\"confidence\":\"{planted}\"", StringComparison.Ordinal);
        using HttpResponseMessage malformed = await _http.PostAsync(
            "/api/context/memories",
            new StringContent(unreadable, System.Text.Encoding.UTF8, "application/json"),
            Ct);
        string malformedBody = await malformed.Content.ReadAsStringAsync(Ct);

        rejected.StatusCode.ShouldBe(HttpStatusCode.BadRequest, rejectedBody);
        rejectedBody.ShouldNotContain(planted);
        malformed.StatusCode.ShouldBe(HttpStatusCode.BadRequest, malformedBody);
        malformedBody.ShouldContain("confidence");
        malformedBody.ShouldNotContain(planted);
        ShouldNotBeLogged(planted);
    }

    /// <summary>
    /// An absence assertion over a capture that cannot see the value proves nothing, so a control
    /// value is first logged at Debug through the application's own pipeline and must be found.
    /// </summary>
    private void ShouldNotBeLogged(string planted)
    {
        string control = PlantedSecret();
        fixture.Services.GetRequiredService<ILogger<SecretContainmentTests>>()
            .LogDebug("Containment control {Control}", control);

        IReadOnlyList<(LogLevel Level, string Text)> records = fixture.Logs.Records;
        records.ShouldContain(r => r.Level == LogLevel.Debug && r.Text.Contains(control, StringComparison.Ordinal));
        records.ShouldContain(r => r.Level == LogLevel.Trace || r.Level == LogLevel.Debug);

        string[] offenders = [.. records
            .Where(r => r.Text.Contains(planted, StringComparison.Ordinal))
            .Select(r => r.Text)];
        offenders.ShouldBeEmpty($"A planted secret reached the logs: {string.Join(" | ", offenders)}");
    }

    private static string PlantedSecret() => $"ghp_{Guid.NewGuid():N}{Guid.NewGuid():N}"[..40];

    private async Task<Guid> ResolveGroup()
    {
        using HttpResponseMessage response = await _http.PostAsJsonAsync(
            "/api/context/groups/resolve",
            new { scopeDimension = MemoryGroup.ScopeDimensionValue.Product, tickets = Array.Empty<object>() },
            Json,
            Ct);
        string payload = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, payload);
        return JsonSerializer.Deserialize<JsonElement>(payload, Json).GetProperty("uuid").GetGuid();
    }

    private static object WriteBody(
        Guid groupUuid,
        string planted,
        string status = MemoryVersion.MemoryVersionStatus.Approved,
        string kind = MemoryVersion.KindValue.Decision) => new
        {
            groupUuid,
            items = new[]
            {
                new
                {
                    uuid = (Guid?)null,
                    name = "Name",
                    description = $"Containment subject {Guid.NewGuid():N}",
                    statement = $"The deploy token is {planted}",
                    contentSummary = $"token={planted}",
                    kind,
                    facets = new[] { "architecture" },
                    tags = Array.Empty<string>(),
                    status,
                    confidence = (short)80,
                    content = $"export GITHUB_TOKEN={planted}",
                    sources = new[] { new { kind = "chat", reference = planted, capturedAt = DateTimeOffset.UtcNow } },
                    validFrom = DateTimeOffset.UtcNow.AddDays(-1),
                    validUntil = (DateTimeOffset?)null,
                    summaryModel = "containment-test-model",
                    summaryPromptVersion = "1",
                },
            },
            links = (object?)null,
            labelsProposed = (object?)null,
        };
}
