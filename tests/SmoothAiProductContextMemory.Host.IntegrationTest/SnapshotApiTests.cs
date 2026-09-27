using System.Net;
using System.Text.Json;

namespace SmoothAiProductContextMemory.Host.IntegrationTest;

/// <summary>
/// L2 coverage for the HLD-006 HTTP surfaces: the accepted-then-poll snapshot trigger writes an
/// archive and publishes its result, and preflight then reports that snapshot (LADR-04 / LADR-07).
/// </summary>
public sealed class SnapshotApiTests(HostWebAppFixture fixture) : IClassFixture<HostWebAppFixture>
{
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http = fixture.HttpClient;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Snapshot_is_accepted_then_polls_to_completed_and_preflight_reports_it()
    {
        using HttpResponseMessage accepted = await _http.PostAsync("/api/context/snapshot", content: null, Ct);
        accepted.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        JsonElement job = await ReadJsonAsync(accepted);
        Guid jobId = job.GetProperty("id").GetGuid();

        JsonElement status = await PollUntilFinishedAsync(jobId);

        status.GetProperty("status").GetString().ShouldBe("Completed");
        string resultPath = status.GetProperty("resultPath").GetString()!;
        File.Exists(resultPath).ShouldBeTrue();
        Path.GetDirectoryName(Path.GetFullPath(resultPath)).ShouldBe(Path.GetFullPath(fixture.SnapshotDirectory));

        using HttpResponseMessage preflight = await _http.PostAsync("/api/context/snapshot/preflight", content: null, Ct);
        preflight.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement report = await ReadJsonAsync(preflight);
        report.GetProperty("lastSnapshotAt").ValueKind.ShouldBe(JsonValueKind.String);
        report.GetProperty("recency").GetString().ShouldNotBe("never");
    }

    private async Task<JsonElement> PollUntilFinishedAsync(Guid jobId)
    {
        DateTime deadline = DateTime.UtcNow + PollTimeout;
        while (true)
        {
            using HttpResponseMessage response = await _http.GetAsync("/api/context/snapshot/status", Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            JsonElement status = await ReadJsonAsync(response);
            if (status.TryGetProperty("id", out JsonElement id)
                && id.GetGuid() == jobId
                && status.GetProperty("status").GetString() != "Running")
            {
                return status;
            }

            DateTime.UtcNow.ShouldBeLessThan(deadline, "snapshot job did not finish before the poll timeout");
            await Task.Delay(TimeSpan.FromMilliseconds(250), Ct);
        }
    }

    private async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        await using Stream stream = await response.Content.ReadAsStreamAsync(Ct);
        using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: Ct);
        return document.RootElement.Clone();
    }
}
