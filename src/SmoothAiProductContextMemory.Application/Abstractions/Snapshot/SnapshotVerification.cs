namespace SmoothAiProductContextMemory.Application.Abstractions.Snapshot;

/// <summary>Outcome of an offline verification. <see cref="IsClean"/> is the gate a script can test on
/// (exit 0 only when no finding exists, NFR-01).</summary>
public sealed record SnapshotVerification(bool IsClean, IReadOnlyList<SnapshotFinding> Findings);

/// <summary>A single verification finding, naming the affected archive member.</summary>
public sealed record SnapshotFinding(
    SnapshotFindingKind Kind,
    string? EntryName,
    string Message);

/// <summary>
/// The mutation classes verify distinguishes. <see cref="Corruption"/> is transit damage to the
/// archive; <see cref="CaptureTimeInconsistency"/> is a blob whose content hash disagrees with the
/// address the database cites — a cross-store inconsistency that existed at capture, reported
/// distinctly because it demands store investigation rather than a retaken snapshot (LADR-02).
/// </summary>
public enum SnapshotFindingKind
{
    Corruption,
    MissingEntry,
    UnexpectedEntry,
    CaptureTimeInconsistency,
    CountMismatch,
}
