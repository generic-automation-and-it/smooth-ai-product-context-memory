using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Application.Features.Export;

public enum BlobRenderState
{
    None,
    Inlined,
    Missing,
    NonText,
}

public sealed record ExportTicket(string Provider, string Key, string Url);

public sealed record ExportGroupDescription(int Version, string Name, string Body, DateTimeOffset CreatedOn);

public sealed record ExportGroupDocument(
    Guid Uuid,
    string ScopeDimension,
    string? ScopeIdentifier,
    string InitiativeName,
    string InitiativeStatus,
    string? Repo,
    string? RepoUrl,
    IReadOnlyList<ExportTicket> Tickets,
    ExportGroupDescription? CurrentDescription,
    IReadOnlyList<ExportGroupDescription> HistoricalDescriptions);

public sealed record ExportSource(string Kind, string Reference, DateTimeOffset? CapturedAt);

public sealed record ExportLink(
    string Direction,
    string Relation,
    string Reason,
    Guid OtherUuid,
    string OtherSubject,
    Guid OtherGroupUuid);

public sealed record ExportVersionBody(
    int Version,
    bool IsCurrent,
    string Kind,
    string Status,
    short Confidence,
    DateTimeOffset ValidFrom,
    DateTimeOffset? ValidUntil,
    DateTimeOffset CreatedOn,
    string Statement,
    string ContentSummary,
    IReadOnlyList<ExportSource> Sources,
    BlobRenderState BlobState,
    string? BlobText);

public sealed record ExportMemoryDocument(
    Guid Uuid,
    Guid LineageId,
    Guid GroupUuid,
    string SubjectSlug,
    string Name,
    string Description,
    string ScopeDimension,
    string? ScopeIdentifier,
    IReadOnlyList<string> Facets,
    IReadOnlyList<string> Tags,
    ExportVersionBody Current,
    IReadOnlyList<ExportVersionBody> HistoricalVersions,
    IReadOnlyList<ExportLink> Links);

public static class ExportScopeMarks
{
    public const string GeneratedComment = "<!-- GENERATED — do not edit. Regenerable projection of the store. -->";

    public const string ProgramBanner =
        "> **PROGRAMME — not shipped product fact.** This is programme knowledge, not citable as shipped product behaviour.";

    public const string SelfBanner =
        "> **SELF — personal preference.** This is a personal preference, not a product fact.";

    public const string MissingBlobNote =
        "_Blob missing. Metadata only — document body was not reconstructed._";

    public const string NonTextBlobNote =
        "_Blob is not UTF-8 text and was omitted._";

    public const string NoBlobNote =
        "_No blob stored for this version._";

    public static string? Banner(string scopeDimension) =>
        scopeDimension switch
        {
            MemoryGroup.ScopeDimensionValue.Program => ProgramBanner,
            MemoryGroup.ScopeDimensionValue.Self => SelfBanner,
            _ => null,
        };
}
