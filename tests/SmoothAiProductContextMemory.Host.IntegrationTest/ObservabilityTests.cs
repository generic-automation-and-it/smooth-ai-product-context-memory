using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SmoothAiProductContextMemory.Domain.Entities;
using SmoothAiProductContextMemory.TestFramework.Telemetry;

namespace SmoothAiProductContextMemory.Host.IntegrationTest;

public sealed class ObservabilityTests(ObservabilityWebAppFixture fixture) : IClassFixture<ObservabilityWebAppFixture>
{
    private const string HttpClientSourceName = "System.Net.Http";
    private const string NpgsqlSourceName = "Npgsql";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http = fixture.HttpClient;

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Readiness with the migration hosted service kept — the connection-string defect showed how
    /// convincingly a dead application looks healthy when only its dependencies are checked.
    /// </summary>
    [Fact]
    public async Task Readiness_is_healthy_once_the_stack_is_actually_serving()
    {
        using HttpResponseMessage response = await _http.GetAsync("/health", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Liveness_responds()
    {
        using HttpResponseMessage response = await _http.GetAsync("/alive", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// The write pipeline crosses two stores. A trace that stopped at the HTTP boundary would show the
    /// total and hide every stage, so the request's own trace must contain both the relational spans
    /// and the object-store call.
    /// </summary>
    [Fact]
    public async Task One_request_yields_one_trace_spanning_both_stores()
    {
        Guid group = await ResolveGroup();

        await SetMemory(group, $"Trace subject {Guid.NewGuid():N}", "Trace claim", content: "trace-body");

        CapturedSpan server = FindLastServerSpan("/api/context/memories");
        IReadOnlyList<CapturedSpan> trace = fixture.Telemetry.SpansForTrace(server.TraceId);

        trace.ShouldContain(span => span.Source == NpgsqlSourceName);
        trace.ShouldContain(span => span.Source == HttpClientSourceName);
        trace.Select(span => span.TraceId).Distinct().Count().ShouldBe(1);
    }

    [Fact]
    public async Task Aspnetcore_and_http_client_metrics_report()
    {
        await _http.GetAsync("/alive", Ct);

        IReadOnlyCollection<string> meters = fixture.Telemetry.MeterNames;

        meters.ShouldContain("Microsoft.AspNetCore.Hosting");
        fixture.Telemetry.MetricTagValues.ShouldNotBeEmpty();
    }

    /// <summary>
    /// NFR-05 on the happy path: identifiers, counts and outcomes may be recorded; the content they
    /// describe may not — in no log, span attribute or metric tag.
    /// </summary>
    [Fact]
    public async Task A_successful_write_leaks_no_content_into_telemetry()
    {
        string marker = $"nfr05-happy-{Guid.NewGuid():N}";
        Guid group = await ResolveGroup();

        await SetMemory(group, $"Confidential subject {Guid.NewGuid():N}", marker, content: marker);

        ShouldNotAppearInTelemetry(marker);
    }

    /// <summary>
    /// An address plus store access is equivalent to the content (NFR-05), and the object store is
    /// reached over HTTP — so the content address arrives in the <c>url.full</c> span attribute unless
    /// something removes it. This is the assertion that fails when the scrubbing processor is disabled.
    /// </summary>
    /// <remarks>
    /// Scoped to spans and metric tags on purpose. NFR-05 bars content addresses <em>at information
    /// level</em>; <c>S3BlobStorage</c> records the address at Debug, which the NFR permits and which
    /// this task's scope explicitly excludes changing.
    /// </remarks>
    [Fact]
    public async Task A_write_leaks_no_content_address_into_spans_or_metrics()
    {
        Guid group = await ResolveGroup();

        JsonElement written = await SetMemory(
            group,
            $"Address subject {Guid.NewGuid():N}",
            "Address claim",
            content: $"address-body-{Guid.NewGuid():N}");

        string blobAddress = written.GetProperty("items")[0].GetProperty("blobAddress").GetString()
            .ShouldNotBeNull();
        string hash = blobAddress.Split('/')[^1];

        fixture.Telemetry.Spans.ShouldNotBeEmpty("No spans captured — the absence assertion would be vacuous.");
        fixture.Telemetry.Spans
            .SelectMany(span => span.SearchableText())
            .Concat(fixture.Telemetry.MetricTagValues)
            .Where(text => text.Contains(hash, StringComparison.Ordinal))
            .ShouldBeEmpty("A content address reached a span attribute or metric tag.");
    }

    /// <summary>
    /// The important case. Happy paths rarely attach payloads; error handlers frequently do. A second
    /// write of the same subject is a genuine failing write — the path where a PostgreSQL
    /// unique-violation message would otherwise carry the statement in its <c>DETAIL:</c> clause.
    /// </summary>
    [Fact]
    public async Task A_failing_write_leaks_no_content_into_telemetry()
    {
        string subject = $"Conflict subject {Guid.NewGuid():N}";
        string marker = $"nfr05-failing-{Guid.NewGuid():N}";
        Guid group = await ResolveGroup();

        await SetMemory(group, subject, "First claim", content: "first-body");

        using HttpResponseMessage conflict = await _http.PostAsJsonAsync(
            "/api/context/memories",
            SetBody(group, subject, marker, marker),
            Ct);
        conflict.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        (await conflict.Content.ReadAsStringAsync(Ct)).ShouldNotContain(marker);
        ShouldNotAppearInTelemetry(marker);
    }

    /// <summary>
    /// Guards against a vacuous pass first: an absence assertion over an empty capture proves nothing,
    /// so logs and spans must both be shown to be flowing before their contents are checked.
    /// </summary>
    private void ShouldNotAppearInTelemetry(string marker)
    {
        fixture.Logs.Records.ShouldNotBeEmpty("No log records captured — the absence assertion would be vacuous.");
        fixture.Telemetry.Spans.ShouldNotBeEmpty("No spans captured — the absence assertion would be vacuous.");

        string[] offenders = [.. fixture.AllTelemetryText().Where(text => text.Contains(marker, StringComparison.Ordinal))];

        offenders.ShouldBeEmpty($"Memory content leaked into telemetry: {string.Join(" | ", offenders)}");
    }

    private CapturedSpan FindLastServerSpan(string route) =>
        fixture.Telemetry.Spans
            .Where(span => span.Tags.TryGetValue("http.route", out string? value) && value == route)
            .LastOrDefault()
        ?? throw new InvalidOperationException(
            $"No server span captured for route '{route}'. Captured sources: "
            + string.Join(", ", fixture.Telemetry.Spans.Select(span => span.Source).Distinct()));

    private async Task<Guid> ResolveGroup()
    {
        using HttpResponseMessage response = await _http.PostAsJsonAsync(
            "/api/context/groups/resolve",
            new { scopeDimension = MemoryGroup.ScopeDimensionValue.Product, tickets = Array.Empty<object>() },
            Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement json = await response.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);
        return json.GetProperty("uuid").GetGuid();
    }

    private async Task<JsonElement> SetMemory(Guid group, string description, string statement, string? content)
    {
        using HttpResponseMessage response = await _http.PostAsJsonAsync(
            "/api/context/memories",
            SetBody(group, description, statement, content),
            Ct);
        string payload = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, payload);
        return JsonSerializer.Deserialize<JsonElement>(payload, Json);
    }

    private static object SetBody(Guid groupUuid, string description, string statement, string? content) => new
    {
        groupUuid,
        items = new[]
        {
            new
            {
                uuid = (Guid?)null,
                name = "Name",
                description,
                statement,
                contentSummary = "Summary",
                kind = MemoryVersion.KindValue.Decision,
                facets = new[] { "architecture" },
                tags = Array.Empty<string>(),
                status = MemoryVersion.MemoryVersionStatus.Approved,
                confidence = (short)80,
                content,
                sources = Array.Empty<object>(),
                validFrom = DateTimeOffset.UtcNow.AddDays(-1),
                validUntil = (DateTimeOffset?)null,
                summaryModel = "test-model",
                summaryPromptVersion = "1",
            }
        },
        links = (object?)null,
        labelsProposed = (object?)null,
    };
}
