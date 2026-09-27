using SmoothAiProductContextMemory.Application.Abstractions.Snapshot;

namespace SmoothAiProductContextMemory.Application.Common.Exceptions;

/// <summary>
/// Restore was refused because the archive failed offline verification before any mutation.
/// The message carries shape only — count and referenced-blob count — so it stays single-line and
/// content-free (NFR-05); the per-finding detail travels in <see cref="Findings"/> payload and is
/// rendered only at the CLI boundary, never in a message that could be serialised into a 500 body.
/// </summary>
public sealed class ArchiveVerificationFailedException : Exception
{
    public ArchiveVerificationFailedException(
        IReadOnlyList<SnapshotFinding> findings,
        int referencedBlobCount)
        : base(
            $"Archive failed verification with {findings.Count} finding(s), "
            + $"{referencedBlobCount} referenced blob(s) affected; restore refused so no mutation is attempted.")
    {
        Findings = findings;
        ReferencedBlobCount = referencedBlobCount;
    }

    public IReadOnlyList<SnapshotFinding> Findings { get; }

    public int ReferencedBlobCount { get; }
}
