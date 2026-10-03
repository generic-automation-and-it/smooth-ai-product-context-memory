using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SmoothAiProductContextMemory.Knowledge;

namespace SmoothAiProductContextMemory.KnowledgeHost;

public sealed class CoreReader(HttpClient client) : ICoreReader
{
    public async Task<CorpusState> StateAsync(CancellationToken ct) => await client.GetFromJsonAsync<CorpusState>("api/context/corpus-state", ct) ?? throw new KnowledgeException("core_empty");
    public async Task<IReadOnlyList<Group>> LookupAsync(Selectors selectors, CancellationToken ct)
    {
        var result = await PostAsync<JsonElement>("api/context/groups/lookup", new { Tickets = Tickets(selectors), selectors.Repo, selectors.ScopeDimension, selectors.ScopeIdentifier }, ct);
        return KnowledgeJson.Deserialize<Group[]>(result.GetProperty("items").GetRawText());
    }
    public async Task<IReadOnlyList<Evidence>> SearchAsync(string query, Selectors selectors, DateTimeOffset asOf, bool historical, CancellationToken ct)
        => await SearchAsync(query, selectors, asOf, historical, ct, null);
    public async Task<IReadOnlyList<Evidence>> SearchAsync(string query, Selectors selectors, DateTimeOffset asOf, bool historical, CancellationToken ct, RecallAttribution? attribution)
    {
        var result = await PostAsync<JsonElement>("api/context/query", new { Query = query, selectors.Repo, selectors.TicketProvider, selectors.TicketKey, selectors.ScopeDimension, IncludeProposed = true, CurrentOnly = !historical, AsOf = asOf, Limit = 20, RecallPurpose = attribution?.RecallPurpose ?? "direct_retrieval", CallerRequestId = attribution?.CallerRequestId }, ct);
        return KnowledgeJson.Deserialize<Evidence[]>(result.GetProperty("items").GetRawText()).Where(e => selectors.ScopeIdentifier is null || e.ScopeIdentifier == selectors.ScopeIdentifier).ToArray();
    }
    public async Task<IReadOnlyList<Evidence>> NeighborsAsync(Evidence seed, int depth, int limit, CancellationToken ct)
    {
        var result = await PostAsync<JsonElement>("api/context/paths", new { SourceUuid = seed.Uuid, MaxDepth = depth, Direction = "either", ScopeDimension = seed.ScopeDimension, Limit = limit }, ct);
        return result.GetProperty("paths").EnumerateArray().Select(p => KnowledgeJson.Deserialize<Evidence>(p.GetProperty("endpoint").GetRawText())).DistinctBy(e => (e.Uuid, e.Version)).ToArray();
    }
    public async Task<string> BodyAsync(Evidence record, CancellationToken ct)
    {
        using var response = await client.GetAsync($"api/context/memories/{record.Uuid}/versions/{record.Version}/blob?scope={Uri.EscapeDataString(record.ScopeDimension)}", ct);
        if (!response.IsSuccessStatusCode) throw new KnowledgeException("core_body_" + (int)response.StatusCode);
        return await response.Content.ReadAsStringAsync(ct);
    }
    public async Task<bool> HasOperationAsync(string key, CancellationToken ct)
    {
        using var response = await client.GetAsync("api/context/operations/" + Uri.EscapeDataString(key), ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        if (!response.IsSuccessStatusCode) throw new KnowledgeException("core_receipt_" + (int)response.StatusCode);
        return true;
    }
    private async Task<T> PostAsync<T>(string path, object data, CancellationToken ct)
    {
        using var response = await client.PostAsJsonAsync(path, data, ct);
        if (!response.IsSuccessStatusCode) throw new KnowledgeException("core_read_" + (int)response.StatusCode);
        return await response.Content.ReadFromJsonAsync<T>(ct) ?? throw new KnowledgeException("core_empty");
    }
    public static IReadOnlyList<Ticket>? Tickets(Selectors selectors) => selectors.TicketProvider is not null && selectors.TicketKey is not null ? [new Ticket(selectors.TicketProvider, selectors.TicketKey)] : null;
}

public sealed class CoreCommitter(HttpClient client) : ICoreCommitter
{
    public Task<Group> ResolveAsync(Selectors selectors, string task, string operationKey, CorpusState state, CancellationToken ct) => PostAsync<Group>("api/context/groups/resolve", new { Tickets = CoreReader.Tickets(selectors), selectors.Repo, selectors.ScopeDimension, selectors.ScopeIdentifier, Name = "Captured learning", Body = task, OperationKey = operationKey, ExpectedCorpusEpoch = state.Epoch, ExpectedCorpusRevision = state.Revision }, ct);
    public Task<CommitResult> CommitAsync(CommitRequest request, CancellationToken ct) => PostAsync<CommitResult>("api/context/memories", new
    {
        request.GroupUuid,
        Items = request.Items.Select(item => new
        {
            item.Uuid, item.Name, item.Description, item.Statement, item.ContentSummary, item.Kind, item.Facets, item.Tags, item.Status, item.Confidence, item.Content,
            Sources = item.Sources.Select(source => new { source.Kind, source.Reference, source.CapturedAt, source.Evidence }),
            item.ValidFrom, item.ValidUntil, item.SummaryModel, item.SummaryPromptVersion, item.CreateUuid, item.ExpectedVersion
        }),
        request.Links, request.LabelsProposed, request.DryRun, request.OperationKey, request.ExpectedCorpusEpoch, request.ExpectedCorpusRevision
    }, ct);
    private async Task<T> PostAsync<T>(string path, object data, CancellationToken ct)
    {
        using var response = await client.PostAsJsonAsync(path, data, KnowledgeJson.Options, ct);
        if (response.StatusCode == HttpStatusCode.Conflict) throw new KnowledgeException("concurrent_corpus_change");
        if (!response.IsSuccessStatusCode) throw new KnowledgeException("core_commit_" + (int)response.StatusCode);
        return await response.Content.ReadFromJsonAsync<T>(ct) ?? throw new KnowledgeException("core_empty");
    }
}
