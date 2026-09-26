namespace SmoothAiProductContextMemory.Application.Abstractions.Snapshot;

/// <summary>
/// Outcome of the membership walk (LADR-03): every referenced blob's resolve state, plus the
/// orphan/dangling accounting that falls out for free. Reporting only — nothing is deleted or
/// repaired (LADR-06). No memory content anywhere; addresses and identifiers only.
/// </summary>
public sealed record SnapshotWalkResult(
    IReadOnlyList<SnapshotBlob> Blobs,
    int DanglingReferences,
    int UnreferencedObjects);

/// <summary>One blob body the captured database state cites.</summary>
public sealed record SnapshotBlob(string Address, SnapshotBlobState State);

/// <summary>
/// A body's state at capture. <see cref="Ok"/> means its bytes hash to the cited address.
/// <see cref="Mismatch"/> is a capture-time cross-store inconsistency — content ≠ address — which
/// demands investigation of the store, not a retaken snapshot (LADR-02). <see cref="Missing"/> is a
/// dangling reference: the address cannot be resolved at all.
/// </summary>
public enum SnapshotBlobState
{
    Ok,
    Mismatch,
    Missing,
}
