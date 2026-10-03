using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmoothAiProductContextMemory.Knowledge;

public static class KnowledgeJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Deserialize<T>(string value) => JsonSerializer.Deserialize<T>(value, Options) ?? throw new KnowledgeException("invalid_response");
}

public sealed record Selectors(string? Repo = null, string? TicketProvider = null, string? TicketKey = null, string ScopeDimension = "product", string? ScopeIdentifier = null);
public sealed record ContextRequest(string Question, Selectors? Selectors = null, DateTimeOffset? AsOf = null, bool Historical = false, bool IncludePortableUnderstandings = false, int BriefTokens = 2000);
public sealed record RecallAttribution(string RecallPurpose, Guid CallerRequestId);
public sealed record Message(string Id, string Role, int Order, string Text, DateTimeOffset? Timestamp = null);
public sealed record CaptureRequest(string IdempotencyKey, string Task, IReadOnlyList<Message> Messages, Selectors? Selectors = null, bool Preview = false, string? PreviousCursor = null, string? SourceNamespace = null);
public sealed record ClarificationRequest(string IdempotencyKey, IReadOnlyList<Message> Messages, BudgetLimits? AdditionalBudget = null, Selectors? Selectors = null);
public sealed record ReconciliationRequest(string IdempotencyKey, Guid PreviousCorpusEpoch, Guid ExpectedCorpusEpoch, bool AcknowledgeRestore = false, BudgetLimits? AdditionalBudget = null);
public sealed record ReconciliationSummary(int Generation, Guid PreviousCorpusEpoch, Guid CorpusEpoch, IReadOnlyList<string> PreviousOperationKeys, string? PendingOperationKey, string? PlanHash, Usage PreviousUsage, DateTimeOffset At, string? PendingPayloadHash = null);
public sealed record ReconciliationAudit(ReconciliationSummary Summary, IReadOnlyList<CommittedBatch> PreviousCommitted, IReadOnlyList<Change>? PreviousPlan, CommitRequest? PreviousPendingCommit, string? PreviousPendingGroupOperation);
public sealed record RecoveryContext(Guid PreviousCorpusEpoch, Guid ExpectedCorpusEpoch, IReadOnlyList<string> AffectedOperationKeys, IReadOnlyList<string> MissingOperationKeys, string Reason);
public sealed record EvidenceMetadata(int V, string Category, string? Applicability, string? Authority, string? ScopeDimension, string? ScopeIdentifier, string? AuthorityReference, string? AuthorityQuote);
public sealed record Source(string Kind, string Reference, DateTimeOffset? CapturedAt, EvidenceMetadata? Evidence = null, int V = 1);
public sealed record Evidence(Guid Uuid, Guid GroupUuid, int Version, string Name, string Description, string Statement, string ContentSummary, string Kind, string Status, short Confidence, string ScopeDimension, string? ScopeIdentifier, DateTimeOffset ValidFrom, DateTimeOffset? ValidUntil, bool IsCurrent, IReadOnlyList<string> Facets, IReadOnlyList<string> Tags, IReadOnlyList<Source> Sources, DateTimeOffset CreatedOn, string Body = "", bool? HasBody = null);
public sealed record Coverage(string Question, string Outcome, string Reason);
public sealed record CitedClaim(string Text, IReadOnlyList<string> Citations, string Conditions, string Lifecycle, string Scope, bool Analysis = false);
public sealed record Synthesis(IReadOnlyList<CitedClaim> Claims, IReadOnlyList<string> Gaps, IReadOnlyList<string> Conflicts);
public sealed record ContextResponse(Guid RequestId, string Brief, IReadOnlyList<Evidence> Evidence, IReadOnlyList<Coverage> Coverage, IReadOnlyList<string> Conflicts, string StopReason, Usage Usage, DateTimeOffset EffectiveAsOf, bool Historical, string WorkflowVersion, IReadOnlyList<Step>? Steps = null);
public sealed record Intent(IReadOnlyList<string> Questions, IReadOnlyList<string> Queries);
public sealed record Candidate(string Name, string Subject, string Statement, string Content, string Kind, string Category, IReadOnlyList<string> SourceIds, string? AuthorityReference, string? AuthorityQuote, string Applicability);
public sealed record DeferredClaim(IReadOnlyList<string> SourceIds, string Subject, string Reason);
public sealed record Extraction(IReadOnlyList<Candidate> Claims, IReadOnlyList<string>? UnexaminedSourceIds = null, bool HasMore = false, IReadOnlyList<DeferredClaim>? UnexaminedClaims = null);
public sealed record Comparison(string Relation, Guid? TargetUuid, string Reason);
public sealed record Change(string Action, Candidate Candidate, Evidence? Target, Guid CreateUuid, string Reason);
public sealed record CorpusState(Guid Epoch, long Revision);
public sealed record Ticket(string Provider, string Key, string Url = "");
public sealed record Group(Guid Uuid, bool Created, string ScopeDimension, string? ScopeIdentifier, string? Repo, string InitiativeName, IReadOnlyList<Ticket> Tickets);
public sealed record MemoryWrite(Guid? Uuid, string Name, string Description, string Statement, string ContentSummary, string Kind, IReadOnlyList<string> Facets, IReadOnlyList<string> Tags, string Status, short Confidence, string Content, IReadOnlyList<Source> Sources, DateTimeOffset ValidFrom, DateTimeOffset? ValidUntil, string SummaryModel, string SummaryPromptVersion, Guid? CreateUuid, int? ExpectedVersion);
public sealed record LinkWrite(Guid SourceUuid, Guid TargetUuid, string Relation, string Reason);
public sealed record CommitRequest(Guid GroupUuid, IReadOnlyList<MemoryWrite> Items, IReadOnlyList<LinkWrite> Links, IReadOnlyList<string> LabelsProposed, bool DryRun, string? OperationKey, Guid? ExpectedCorpusEpoch, long? ExpectedCorpusRevision);
public sealed record CommitResult(int Created, int Versioned, int Linked, int Diverged, int Skipped, int LabelsProposed, IReadOnlyList<CommitItem> Items);
public sealed record CommitItem(Guid? Uuid, string? BlobAddress, bool Versioned);
public sealed record CommittedBatch(string OperationKey, CommitResult Result);
public sealed record Usage(int Calls = 0, int InputTokens = 0, int OutputTokens = 0, decimal EstimatedDollars = 0, bool Estimated = true, int OutstandingReservations = 0);
public sealed record BudgetLimits(int Calls = 12, int InputTokens = 100000, int OutputTokens = 12000, decimal Dollars = 2, int DeadlineSeconds = 120);
public sealed record ProviderRates(decimal Input = 0.125m, decimal Output = 0.5m, decimal StrongerInput = 2.5m, decimal StrongerOutput = 10m, decimal JevInput = 0.042m, decimal JevOutput = 0m);
public sealed record Reservation(Guid Id, int InputTokens, int OutputTokens, decimal Dollars);
public sealed record BudgetState(BudgetLimits Limits, Usage Usage, IReadOnlyList<Reservation> Reservations);
public sealed record Step(string Node, string Outcome, string? Model, int DurationMs, DateTimeOffset At);
public sealed record CaptureReceipt(Guid Id, string Status, string Cursor, IReadOnlyList<Change> Changes, IReadOnlyList<CommittedBatch> Committed, IReadOnlyList<string> Deferred, IReadOnlyList<string> Questions, Usage Usage, string? ErrorClass, string WorkflowVersion, IReadOnlyList<Step>? Steps = null, IReadOnlyList<ReconciliationSummary>? Reconciliations = null, RecoveryContext? ReconciliationRequired = null, DateTimeOffset? DeadlineUtc = null);
public sealed record CaptureJob(Guid Id, string Hash, CaptureRequest? Input, string WorkflowVersion, string Status, BudgetState Budget, CorpusState? Corpus, Guid? GroupUuid, IReadOnlyList<Change>? Plan, CommitRequest? PendingCommit, string? PendingGroupOperation, IReadOnlyList<CommittedBatch> Committed, IReadOnlyList<string> Deferred, IReadOnlyList<string> Questions, IReadOnlyList<Step> Steps, string? ErrorClass, DateTimeOffset UpdatedAt, IReadOnlyList<Candidate>? Candidates = null, int ComparisonIndex = 0, string? SourceNamespace = null, int ExtractionRepairAttempts = 0, int ComparisonRepairAttempts = 0, int Generation = 0, IReadOnlyList<ReconciliationAudit>? Reconciliations = null, DateTimeOffset? DeadlineUtc = null, Extraction? LastExtraction = null);
public sealed class KnowledgeException(string code, string? detailCode = null) : Exception(code) { public string Code { get; } = code; public string? DetailCode { get; } = detailCode; }

public interface ISecretFilter { Task<T> SanitizeAsync<T>(T value, CancellationToken ct); }
public interface IAtomicityDetector { Task ValidateAsync(IReadOnlyList<Candidate> candidates, CancellationToken ct); }
public interface ICoreReader
{
    Task<CorpusState> StateAsync(CancellationToken ct);
    Task<IReadOnlyList<Group>> LookupAsync(Selectors selectors, CancellationToken ct);
    Task<IReadOnlyList<Evidence>> SearchAsync(string query, Selectors selectors, DateTimeOffset asOf, bool historical, CancellationToken ct);
    Task<IReadOnlyList<Evidence>> SearchAsync(string query, Selectors selectors, DateTimeOffset asOf, bool historical, CancellationToken ct, RecallAttribution attribution) => SearchAsync(query, selectors, asOf, historical, ct);
    Task<IReadOnlyList<Evidence>> NeighborsAsync(Evidence seed, int depth, int limit, CancellationToken ct);
    Task<string> BodyAsync(Evidence record, CancellationToken ct);
    Task<bool> HasOperationAsync(string key, CancellationToken ct);
}
public interface ICoreCommitter
{
    Task<Group> ResolveAsync(Selectors selectors, string task, string operationKey, CorpusState state, CancellationToken ct);
    Task<CommitResult> CommitAsync(CommitRequest request, CancellationToken ct);
}
public sealed record ProviderResult<T>(T Value, string Model, int? InputTokens, int? OutputTokens);
public interface IGenerativeProvider
{
    string Model { get; }
    Task<ProviderResult<T>> GenerateAsync<T>(string instructions, object data, int outputLimit, bool stronger, CancellationToken ct);
}
public interface IJevProvider
{
    string Model { get; }
    Task<ProviderResult<IReadOnlyDictionary<string, string>>> ChoicesAsync(object state, IReadOnlyDictionary<string, ChoiceQuestion> questions, CancellationToken ct);
}
public sealed record ChoiceQuestion(string Instructions, IReadOnlyDictionary<string, string> Criteria);
