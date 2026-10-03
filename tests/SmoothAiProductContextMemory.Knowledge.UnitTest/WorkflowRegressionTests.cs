using Shouldly;
using SmoothAiProductContextMemory.Knowledge;
using Xunit;

namespace SmoothAiProductContextMemory.Knowledge.UnitTest;

public sealed class WorkflowRegressionTests
{
    [Fact]
    public async Task Changed_attribution_is_retained_even_when_message_id_and_text_are_unchanged()
    {
        using var rig = new Rig();
        rig.Judge.Relation = "new";
        await rig.Run([new Message("m1", "user", 0, "Propose a six hour window.")], "original", "conversation-b");
        var original = rig.Core.Commits.Single().Items.Single();
        Guid target = original.CreateUuid!.Value;
        rig.Core.Records = [new Evidence(target, rig.Core.Group.Uuid, 1, original.Name, original.Description, original.Statement, original.ContentSummary, original.Kind, original.Status, original.Confidence, "product", null, original.ValidFrom, null, true, [], [], original.Sources, DateTimeOffset.UtcNow, original.Content, true)];
        rig.Provider.Target = target;
        rig.Judge.Relation = "equivalent";
        var revised = await rig.Run([new Message("m1", "assistant", 2, "Propose a six hour window.", DateTimeOffset.Parse("2026-10-03T12:00:00Z"))], "attribution-revised", "conversation-b");
        revised.Changes.Single().Action.ShouldBe("attach_evidence");
        var write = rig.Core.Commits.Last().Items.Single();
        write.Sources.Count.ShouldBe(2);
        write.Sources.Select(source => source.Reference).Distinct().Count().ShouldBe(2);
        write.Content.ShouldContain("[assistant, order 2");
        write.Sources.ShouldContain(source => source.Reference == original.Sources.Single().Reference);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Twenty_five_mixed_claims_survive_budget_continuation_and_preflight_is_atomic(bool rejectPreflight)
    {
        using var rig = new Rig();
        var existing = new Evidence(Guid.NewGuid(), rig.Core.Group.Uuid, 1, "Rule 0", "Rule 0", "Existing rule", "Existing rule", "fact", "proposed", 80, "product", null, DateTimeOffset.UtcNow.AddDays(-1), null, true, [], [], [], DateTimeOffset.UtcNow, "Existing rule", true);
        rig.Core.Records = [existing];
        rig.Core.RejectPreflight = rejectPreflight;
        rig.Provider.Target = existing.Uuid;
        rig.Provider.Claims = Enumerable.Range(0, 25).Select(index => new Candidate("Rule " + index, "counter proposal " + index, "Synthetic rule " + index, "Evidence for rule " + index, "fact", "suggestion", ["m1"], null, null, "Synthetic")).ToArray();
        rig.Judge.ChooseRelation = state => KnowledgeJson.Serialize(state).Contains("\"name\":\"Rule 0\"", StringComparison.Ordinal) && JsonCandidateName(state) == "Rule 0" ? "equivalent" : "new";
        var ack = await rig.Workflow.AcceptAsync(new CaptureRequest("long", "Mixed synthetic capture", [new Message("m1", "user", 0, "Twenty five independent proposal facts.")], new Selectors("synthetic/review")), TestContext.Current.CancellationToken);
        var job = rig.Journal.Get(ack.Id)!;
        await rig.Journal.SaveAsync(job with { Budget = job.Budget with { Limits = job.Budget.Limits with { Calls = 4 } } }, TestContext.Current.CancellationToken);
        await rig.Workflow.ProcessAsync(ack.Id, TestContext.Current.CancellationToken);
        rig.Journal.Get(ack.Id)!.Candidates!.Count.ShouldBe(25);
        rig.Workflow.Get(ack.Id)!.ErrorClass.ShouldBe("budget_exhausted");
        rig.Core.Commits.ShouldBeEmpty();
        int spent = rig.Journal.Get(ack.Id)!.Budget.Usage.Calls;
        for (int continuation = 0; continuation < 2; continuation++)
        {
            await rig.Workflow.ClarifyAsync(ack.Id, new ClarificationRequest("allowance-" + continuation, [], new BudgetLimits(12, 100000, 12000, 2, 120)), TestContext.Current.CancellationToken);
            rig.Journal.Get(ack.Id)!.Budget.Usage.Calls.ShouldBeGreaterThanOrEqualTo(spent);
            await rig.Workflow.ProcessAsync(ack.Id, TestContext.Current.CancellationToken);
        }
        rig.Journal.Get(ack.Id)!.Plan!.Count.ShouldBe(25);
        if (rejectPreflight) { rig.Core.Commits.ShouldBeEmpty(); rig.Workflow.Get(ack.Id)!.ErrorClass.ShouldBe("core_commit_400"); }
        else { rig.Core.Commits.Count.ShouldBe(1); rig.Core.Commits.Single().Items.Count.ShouldBe(25); rig.Core.Commits.Single().Items.Count(item => item.Uuid is not null).ShouldBe(1); rig.Workflow.Get(ack.Id)!.Status.ShouldBe("processed"); }
    }

    private static string? JsonCandidateName(object state)
    {
        using var json = System.Text.Json.JsonDocument.Parse(KnowledgeJson.Serialize(state));
        return json.RootElement.GetProperty("candidate").GetProperty("name").GetString();
    }

    [Fact]
    public async Task Source_revision_is_retained_while_unchanged_incremental_evidence_is_skipped()
    {
        using var rig = new Rig();
        rig.Judge.Relation = "new";
        await rig.Run([new Message("m1", "user", 0, "Propose a six hour window.")], "first", "conversation-a");
        var original = rig.Core.Commits.Single().Items.Single();
        Guid target = original.CreateUuid!.Value;
        rig.Core.Records = [new Evidence(target, rig.Core.Group.Uuid, 1, original.Name, original.Description, original.Statement, original.ContentSummary, original.Kind, original.Status, original.Confidence, "product", null, original.ValidFrom, null, true, [], [], original.Sources, DateTimeOffset.UtcNow, original.Content, true)];
        rig.Provider.Target = target;
        rig.Judge.Relation = "equivalent";
        var repeated = await rig.Run([new Message("m1", "user", 0, "Propose a six hour window.")], "repeat", "conversation-a");
        repeated.Changes.Single().Action.ShouldBe("skip");
        rig.Core.Commits.Count.ShouldBe(1);
        var revised = await rig.Run([new Message("m1", "user", 0, "Propose a six hour window with a review reminder.")], "revision", "conversation-a");
        revised.Changes.Single().Action.ShouldBe("attach_evidence");
        var updated = rig.Core.Commits.Last().Items.Single();
        updated.Sources.Count.ShouldBe(2);
        updated.Sources.Select(source => source.Reference).Distinct().Count().ShouldBe(2);
        updated.Content.ShouldContain("with a review reminder");
        updated.Sources.ShouldContain(source => source.Reference == original.Sources.Single().Reference);
    }

    [Fact]
    public async Task Source_internal_correction_with_no_corpus_target_gets_one_bounded_review()
    {
        using var rig = new Rig();
        rig.Core.Records = [new Evidence(Guid.NewGuid(), rig.Core.Group.Uuid, 1, "Unrelated", "Unrelated", "Other fact", "Other fact", "fact", "proposed", 50, "product", null, DateTimeOffset.UtcNow.AddDays(-1), null, true, [], [], [], DateTimeOffset.UtcNow, "Other fact", true)];
        var receipt = await rig.Run([new Message("m1", "user", 0, "Correction: use six, not twelve, in this proposal.")]);
        receipt.Status.ShouldBe("processed");
        rig.Provider.StrongerComparisons.ShouldBe(1);
        rig.Journal.Get(receipt.Id)!.ComparisonRepairAttempts.ShouldBe(1);
        receipt.Changes.Single().Action.ShouldBe("create");
        rig.Core.Commits.Single().Items.Single().Statement.ShouldBe("The proposal uses six.");
    }

    [Fact]
    public async Task Omitted_message_is_durably_disclosed_instead_of_reported_complete()
    {
        using var rig = new Rig();
        var receipt = await rig.Run([new Message("m1", "user", 0, "Use six."), new Message("m2", "user", 1, "A separate rule.")]);
        receipt.Status.ShouldBe("needs_input");
        receipt.Deferred.ShouldContain("Unexamined source: m2");
        new CaptureJournal(rig.Path).Get(receipt.Id)!.Deferred.ShouldContain("Unexamined source: m2");
    }

    private sealed class Rig : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "knowledge-regression-" + Guid.NewGuid() + ".db");
        public Core Core { get; } = new();
        public Provider Provider { get; } = new();
        public Jev Judge { get; } = new();
        public CaptureJournal Journal { get; }
        public Workflow Workflow { get; }
        public Rig() { Journal = new CaptureJournal(Path); Workflow = new Workflow(Core, Core, Provider, Judge, new Secrets(), Journal); }
        public async Task<CaptureReceipt> Run(IReadOnlyList<Message> messages, string key = "key", string? sourceNamespace = null)
        {
            var receipt = await Workflow.AcceptAsync(new CaptureRequest(key, "Synthetic proposal", messages, new Selectors("synthetic/review"), SourceNamespace: sourceNamespace), TestContext.Current.CancellationToken);
            await Workflow.ProcessAsync(receipt.Id, TestContext.Current.CancellationToken);
            return Workflow.Get(receipt.Id)!;
        }
        public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); foreach (string suffix in new[] { "", "-wal", "-shm" }) File.Delete(Path + suffix); }
    }
    private sealed class Secrets : ISecretFilter { public Task<T> SanitizeAsync<T>(T value, CancellationToken ct) => Task.FromResult(value); }
    private sealed class Provider : IGenerativeProvider
    {
        public string Model => "synthetic";
        public int StrongerComparisons { get; private set; }
        public Guid? Target { get; set; }
        public IReadOnlyList<Candidate>? Claims { get; set; }
        public Task<ProviderResult<T>> GenerateAsync<T>(string instructions, object data, int outputLimit, bool stronger, CancellationToken ct)
        {
            object result;
            if (typeof(T) == typeof(Extraction)) result = new Extraction(Claims ?? [new Candidate("Counter", "counter proposal", "The proposal uses six.", "Twelve was superseded by six.", "fact", "suggestion", ["m1"], null, null, "Synthetic")]);
            else { if (stronger) StrongerComparisons++; result = Target is not null ? new Comparison("equivalent", Target, "Same stored rule.") : new Comparison(stronger ? "new" : "correction", null, "Only the handoff changed; no corpus target."); }
            return Task.FromResult(new ProviderResult<T>((T)result, Model, 10, 10));
        }
    }
    private sealed class Jev : IJevProvider
    {
        public string Model => "synthetic";
        public string? Relation { get; set; }
        public Func<object, string>? ChooseRelation { get; set; }
        public Task<ProviderResult<IReadOnlyDictionary<string, string>>> ChoicesAsync(object state, IReadOnlyDictionary<string, ChoiceQuestion> questions, CancellationToken ct)
        {
            IReadOnlyDictionary<string, string> result = questions.ToDictionary(question => question.Key, question => question.Value.Criteria.ContainsKey("correction") ? ChooseRelation?.Invoke(state) ?? Relation ?? "correction" : "new");
            return Task.FromResult(new ProviderResult<IReadOnlyDictionary<string, string>>(result, Model, 10, 10));
        }
    }
    private sealed class Core : ICoreReader, ICoreCommitter
    {
        public Group Group { get; } = new(Guid.NewGuid(), false, "product", null, "synthetic/review", "Synthetic", []);
        public IReadOnlyList<Evidence> Records { get; set; } = [];
        public List<CommitRequest> Commits { get; } = [];
        public bool RejectPreflight { get; set; }
        private readonly CorpusState state = new(Guid.NewGuid(), 1);
        public Task<CorpusState> StateAsync(CancellationToken ct) => Task.FromResult(state);
        public Task<IReadOnlyList<Group>> LookupAsync(Selectors selectors, CancellationToken ct) => Task.FromResult<IReadOnlyList<Group>>([Group]);
        public Task<IReadOnlyList<Evidence>> SearchAsync(string query, Selectors selectors, DateTimeOffset asOf, bool historical, CancellationToken ct) => Task.FromResult(Records);
        public Task<IReadOnlyList<Evidence>> NeighborsAsync(Evidence seed, int depth, int limit, CancellationToken ct) => Task.FromResult<IReadOnlyList<Evidence>>([]);
        public Task<string> BodyAsync(Evidence record, CancellationToken ct) => Task.FromResult(record.Body);
        public Task<bool> HasOperationAsync(string key, CancellationToken ct) => Task.FromResult(true);
        public Task<Group> ResolveAsync(Selectors selectors, string task, string operationKey, CorpusState corpus, CancellationToken ct) => Task.FromResult(Group);
        public Task<CommitResult> CommitAsync(CommitRequest request, CancellationToken ct) { if (request.DryRun && RejectPreflight) throw new KnowledgeException("core_commit_400"); if (!request.DryRun) Commits.Add(request); return Task.FromResult(new CommitResult(1, 0, 0, 0, 0, 0, [])); }
    }
}
