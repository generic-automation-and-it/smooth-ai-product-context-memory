using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Shouldly;
using SmoothAiProductContextMemory.Knowledge;
using SmoothAiProductContextMemory.KnowledgeHost;
using Xunit;

namespace SmoothAiProductContextMemory.Knowledge.UnitTest;

public sealed class SafetyTests
{
    [Fact]
    public async Task Concurrent_reservations_share_one_limit_and_survive_serialization()
    {
        BudgetState? saved = null;
        var ledger = new BudgetLedger(BudgetLedger.Start(new BudgetLimits(1, 10000, 1000, 2)), state => { saved = state; return Task.CompletedTask; });
        var tasks = Enumerable.Range(0, 2).Select(async _ => { try { await ledger.ReserveAsync("state", 500, 1, 1); return true; } catch (KnowledgeException) { return false; } });
        (await Task.WhenAll(tasks)).Count(ok => ok).ShouldBe(1);
        var restored = new BudgetLedger(KnowledgeJson.Deserialize<BudgetState>(KnowledgeJson.Serialize(saved)), _ => Task.CompletedTask);
        (await Should.ThrowAsync<KnowledgeException>(() => restored.ReserveAsync("again", 500, 1, 1))).Code.ShouldBe("budget_exhausted");
    }

    [Fact]
    public async Task Missing_usage_charges_the_full_reservation()
    {
        var ledger = new BudgetLedger(BudgetLedger.Start(new BudgetLimits()), _ => Task.CompletedTask);
        var reservation = await ledger.ReserveAsync("state", 500, 1, 1);
        await ledger.SettleAsync(reservation, null, null, 1, 1);
        ledger.Usage.Calls.ShouldBe(1);
        ledger.Usage.InputTokens.ShouldBe(reservation.InputTokens);
        ledger.Usage.OutputTokens.ShouldBe(500);
        ledger.Usage.EstimatedDollars.ShouldBe(reservation.Dollars);
    }

    [Fact]
    public async Task Reported_usage_overrun_stops_the_path()
    {
        var ledger = new BudgetLedger(BudgetLedger.Start(new BudgetLimits()), _ => Task.CompletedTask);
        var reservation = await ledger.ReserveAsync("state", 100, 1, 1);
        (await Should.ThrowAsync<KnowledgeException>(() => ledger.SettleAsync(reservation, 10, 101, 1, 1))).Code.ShouldBe("provider_usage_exceeded_reservation");
        ledger.Usage.OutputTokens.ShouldBe(101);
    }

    [Fact]
    public async Task Journal_receipt_replay_backup_and_restore_preserve_budget()
    {
        string directory = NewDirectory();
        var journal = new CaptureJournal(Path.Combine(directory, "captures.db"));
        var input = new CaptureRequest("same", "task", [new Message("one", "user", 0, "Learning")]);
        var job = await journal.AcceptAsync(input, "hash", TestContext.Current.CancellationToken);
        var ledger = new BudgetLedger(job.Budget, async state => { job = job with { Budget = state }; await journal.SaveAsync(job, TestContext.Current.CancellationToken); });
        await ledger.ReserveAsync("state", 500, 1, 1);
        (await journal.AcceptAsync(input, "hash", TestContext.Current.CancellationToken)).Id.ShouldBe(job.Id);
        (await Should.ThrowAsync<KnowledgeException>(() => journal.AcceptAsync(input, "different", TestContext.Current.CancellationToken))).Code.ShouldBe("idempotency_conflict");
        string backup = Path.Combine(directory, "backup.db");
        journal.Backup(backup);
        var restored = new CaptureJournal(backup);
        restored.Get(job.Id)!.Budget.Reservations.Count.ShouldBe(1);
        restored.Pending().ShouldContain(job.Id);
    }

    [Fact]
    public async Task Journal_full_refuses_an_acknowledgement()
    {
        var journal = new CaptureJournal(Path.Combine(NewDirectory(), "captures.db"), 1024);
        (await Should.ThrowAsync<KnowledgeException>(() => journal.AcceptAsync(new CaptureRequest("key", "task", [new Message("one", "user", 0, "Learning")]), "hash", TestContext.Current.CancellationToken))).Code.ShouldBe("journal_full");
        journal.Pending().ShouldBeEmpty();
    }

    [Fact]
    public async Task Disabled_host_needs_no_keys_or_runtime_resources()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> { ["KnowledgeService:Enabled"] = "false" })));
        using var client = factory.CreateClient();
        (await client.GetAsync("/alive", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync("/health", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await client.PostAsync("/api/knowledge/context", new StringContent("{}"), TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        factory.Services.GetService(typeof(CaptureJournal)).ShouldBeNull();
        factory.Services.GetService(typeof(IGenerativeProvider)).ShouldBeNull();
    }

    [Fact]
    public async Task Jev_adapter_uses_documented_choice_wire_and_actual_model_identity()
    {
        var handler = new CaptureHandler("""{"model":"jev-1.13.0","answers":{"route":{"type":"choice","choice":"read","confidence":0.9,"probabilities":{"read":1}}},"usage":{"input_tokens":22,"output_tokens":4}}""");
        var provider = new JevProvider(new HttpClient(handler), "synthetic-key", "jev-latest");
        var result = await provider.ChoicesAsync(new { question = "q" }, new Dictionary<string, ChoiceQuestion> { ["route"] = new("Route", new Dictionary<string, string> { ["read"] = "Read evidence" }) }, TestContext.Current.CancellationToken);
        result.Model.ShouldBe("jev-1.13.0");
        result.InputTokens.ShouldBe(22);
        var body = JsonDocument.Parse(handler.Body!);
        body.RootElement.GetProperty("questions").GetProperty("route").GetProperty("type").GetString().ShouldBe("choice");
        handler.Uri!.AbsoluteUri.ShouldBe("https://api.typesafe.ai/v1/systemone");
    }

    [Fact]
    public async Task Openai_adapter_requires_complete_strict_structured_output()
    {
        var handler = new CaptureHandler("""{"model":"gpt-6-luna","choices":[{"finish_reason":"stop","message":{"content":"{\"questions\":[\"q\"],\"queries\":[\"subject\"]}","refusal":null}}],"usage":{"prompt_tokens":20,"completion_tokens":12}}""");
        var provider = new OpenAiProvider(new HttpClient(handler), "synthetic-key", "gpt-6-luna", null);
        var result = await provider.GenerateAsync<Intent>("Extract", new { text = "data" }, 100, false, TestContext.Current.CancellationToken);
        result.Value.Queries.ShouldBe(["subject"]);
        var body = JsonDocument.Parse(handler.Body!);
        body.RootElement.GetProperty("store").GetBoolean().ShouldBeFalse();
        body.RootElement.GetProperty("max_completion_tokens").GetInt32().ShouldBe(100);
        body.RootElement.GetProperty("response_format").GetProperty("json_schema").GetProperty("strict").GetBoolean().ShouldBeTrue();
        body.RootElement.GetProperty("response_format").GetProperty("json_schema").GetProperty("schema").GetProperty("additionalProperties").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Incomplete_openai_response_is_classified()
    {
        var provider = new OpenAiProvider(new HttpClient(new CaptureHandler("""{"choices":[{"finish_reason":"length"}]}""")), "synthetic-key", "model", null);
        (await Should.ThrowAsync<KnowledgeException>(() => provider.GenerateAsync<Intent>("Extract", new { }, 100, false, TestContext.Current.CancellationToken))).Code.ShouldBe("openai_incomplete");
    }

    [Fact]
    public void Extraction_schema_limits_categories_to_the_valid_lifecycle_contract()
    {
        var schema = OpenAiProvider.Schema(typeof(Extraction));
        var categories = schema["properties"]!["claims"]!["items"]!["properties"]!["category"]!["enum"]!.AsArray().Select(value => value!.GetValue<string>()).ToArray();
        categories.ShouldBe(["suggestion", "document_approval", "approved_intent", "observed_implementation", "unknown"]);
    }

    [Fact]
    public async Task Secret_detector_failure_fails_closed()
    {
        var filter = new SecretFilter("definitely-no-python-executable", "missing.py");
        (await Should.ThrowAsync<KnowledgeException>(() => filter.SanitizeAsync("input", TestContext.Current.CancellationToken))).Code.ShouldBe("secret_detector_failed");
    }

    [Fact]
    public async Task Packaged_atomicity_detector_enforces_the_shared_direct_and_service_fixtures()
    {
        var detector = new AtomicityDetector(OperatingSystem.IsWindows() ? "python" : "python3", Path.Combine(AppContext.BaseDirectory, "atomicity.py"));
        using var fixtures = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "atomicity-fixtures.json"), TestContext.Current.CancellationToken));
        foreach (var fixture in fixtures.RootElement.GetProperty("cases").EnumerateArray())
        {
            string statement = fixture.GetProperty("statement").GetString()!;
            var candidate = new Candidate("Rule", "Rule", statement, statement, "fact", "suggestion", ["m1"], null, null, "Synthetic");
            if (fixture.GetProperty("verdict").GetString() == "bundled") (await Should.ThrowAsync<KnowledgeException>(() => detector.ValidateAsync([candidate], TestContext.Current.CancellationToken))).Code.ShouldBe("non_atomic_claims");
            else await detector.ValidateAsync([candidate], TestContext.Current.CancellationToken);
            foreach (var condition in fixture.GetProperty("necessaryConditions").EnumerateArray()) candidate.Statement.ShouldContain(condition.GetString()!);
        }
    }

    [Fact]
    public async Task Commit_wire_omits_response_only_source_version_and_retains_evidence_version()
    {
        var handler = new CaptureHandler("""{"created":1,"versioned":0,"linked":0,"diverged":0,"skipped":0,"labelsProposed":0,"items":[]}""");
        var committer = new CoreCommitter(new HttpClient(handler) { BaseAddress = new Uri("http://synthetic.invalid/") });
        var evidence = new EvidenceMetadata(1, "suggestion", null, null, "product", null, null, null);
        var item = new MemoryWrite(null, "Rule", "Subject", "Proposed rule", "Proposed rule", "fact", [], [], "proposed", 50, "Evidence", [new Source("conversation", "message:namespace:m1", null, evidence)], DateTimeOffset.UtcNow, null, "service", "v1", Guid.NewGuid(), null);
        await committer.CommitAsync(new CommitRequest(Guid.NewGuid(), [item], [], [], true, null, Guid.NewGuid(), 1), TestContext.Current.CancellationToken);
        using var body = JsonDocument.Parse(handler.Body!);
        var source = body.RootElement.GetProperty("items")[0].GetProperty("sources")[0];
        source.TryGetProperty("v", out _).ShouldBeFalse();
        source.GetProperty("evidence").GetProperty("v").GetInt32().ShouldBe(1);
        body.RootElement.GetProperty("expectedCorpusRevision").GetInt64().ShouldBe(1);
    }

    [Fact]
    public async Task Packaged_detector_sanitizes_all_string_fields_and_preserves_ordinary_prose()
    {
        var filter = new SecretFilter(OperatingSystem.IsWindows() ? "python" : "python3", Path.Combine(AppContext.BaseDirectory, "redact.py"));
        var input = new CaptureRequest("key", "task", [new Message("one", "user", 0, "DEPLOY_TOKEN=s3cr3tvalue99\nsort key = created_on\npartition key: groupUuid\n-----BEGIN PUBLIC KEY----- is public")], new Selectors("https://user:password@host/repo"));
        var clean = await filter.SanitizeAsync(input, TestContext.Current.CancellationToken);
        clean.Messages[0].Text.ShouldNotContain("s3cr3tvalue99");
        clean.Messages[0].Text.ShouldContain("sort key = created_on");
        clean.Messages[0].Text.ShouldContain("partition key: groupUuid");
        clean.Messages[0].Text.ShouldContain("BEGIN PUBLIC KEY");
        clean.Selectors!.Repo.ShouldBe("https://user:<redacted>@host/repo");
        KnowledgeJson.Serialize(await filter.SanitizeAsync(clean, TestContext.Current.CancellationToken)).ShouldBe(KnowledgeJson.Serialize(clean));
    }

    private static string NewDirectory() { string path = Path.Combine(Path.GetTempPath(), "knowledge-tests", Guid.NewGuid().ToString()); Directory.CreateDirectory(path); return path; }
    private sealed class CaptureHandler(string response) : HttpMessageHandler
    {
        public string? Body { get; private set; }
        public Uri? Uri { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, System.Text.Encoding.UTF8, "application/json") };
        }
    }
}
