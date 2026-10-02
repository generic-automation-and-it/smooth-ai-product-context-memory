using SmoothAiProductContextMemory.Application.Abstractions.Snapshot;

namespace SmoothAiProductContextMemory.Application.Features.Snapshot;

/// <summary>
/// What the Host says about itself at startup, before it changes the schema: how many migrations this
/// image has still to apply, and how old the last snapshot is. An upgrade <em>is</em> a migration, so
/// the practitioner sees that fact — and their backup's age — at the moment it happens (HLD-006
/// LADR-04: visibility, never a scheduled or blocking step).
/// </summary>
public sealed record MigrationPosture(int PendingMigrationCount, string LastSnapshot)
{
    /// <summary>Stated absence, not an empty value: "we have no record" is a fact worth printing.</summary>
    public const string NoSnapshotRecorded = "no snapshot recorded";

    /// <summary>The last-snapshot text for the startup line: its recency, or the stated absence.</summary>
    public static string LastSnapshotText(SnapshotMetadata? metadata, DateTimeOffset now) =>
        metadata is null
            ? NoSnapshotRecorded
            : SnapshotRecency.Format(now - metadata.LastSnapshotAt);
}
