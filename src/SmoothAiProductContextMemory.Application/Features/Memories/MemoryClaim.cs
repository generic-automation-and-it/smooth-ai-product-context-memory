using System.Text;
using NodaTime;
using SmoothAiProductContextMemory.Application.Common.Models;
using SmoothAiProductContextMemory.Domain;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Application.Features.Memories;

using SourceRef = (string Kind, string Reference, Instant? CapturedAt);

/// <summary>
/// The claim a version actually asserts, projected to the fields that decide whether a version bump is
/// a real change. Built from either the incoming <see cref="SetMemories.MemoryWrite"/> or the stored
/// <see cref="MemoryVersion"/>, so an identical re-send can be recognised as a no-op. Deliberately
/// excludes <c>ValidFrom</c> and <c>SummaryStamp</c> (re-stamped on every capture run) and the
/// <c>Memory</c> surface (name/description/tags/facets — already ignored on a version item).
/// </summary>
/// <remarks>
/// Comparison, not value equality, is the contract: the record's own equality folds a collection field
/// in by reference, so use <see cref="Same"/>.
/// </remarks>
public sealed record MemoryClaim(
    string Statement,
    string ContentSummary,
    string Kind,
    string Status,
    short Confidence,
    IReadOnlyList<SourceRef> Sources,
    Instant? ValidUntil,
    string? ContentAddress)
{
    public static MemoryClaim FromWrite(SetMemories.MemoryWrite write) => new(
        write.Statement,
        write.ContentSummary,
        write.Kind,
        write.Status,
        write.Confidence,
        write.Sources?.Select(s => (s.Kind, s.Reference, s.CapturedAt)).ToList() ?? [],
        write.ValidUntil is { } validUntil ? TruncateToMicroseconds(validUntil) : null,
        string.IsNullOrEmpty(write.Content)
            ? null
            : SmoothAiProductContextMemory.Domain.ContentAddress.Compute(Encoding.UTF8.GetBytes(write.Content)));

    public static MemoryClaim FromVersion(MemoryVersion version) => new(
        version.Statement,
        version.ContentSummary,
        version.Kind,
        version.Status,
        version.Confidence,
        version.Sources.Select(s => (s.Kind, s.Reference, s.CapturedAt)).ToList(),
        version.ValidUntil is { } validUntil ? TruncateToMicroseconds(validUntil) : null,
        version.BlobAddress);

    /// <summary>One claim equals another only when every field is identical; a single changed
    /// field is a version. <c>ValidFrom</c> and <c>SummaryStamp</c> are outside the claim.</summary>
    public static bool Same(MemoryClaim left, MemoryClaim right) =>
        string.Equals(left.Statement, right.Statement, StringComparison.Ordinal)
        && string.Equals(left.ContentSummary, right.ContentSummary, StringComparison.Ordinal)
        && string.Equals(left.Kind, right.Kind, StringComparison.Ordinal)
        && string.Equals(left.Status, right.Status, StringComparison.Ordinal)
        && left.Confidence == right.Confidence
        && SourcesEqual(left.Sources, right.Sources)
        && left.ValidUntil == right.ValidUntil
        && string.Equals(left.ContentAddress, right.ContentAddress, StringComparison.Ordinal);

    private static bool SourcesEqual(IReadOnlyList<SourceRef> left, IReadOnlyList<SourceRef> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (int index = 0; index < left.Count; index++)
        {
            SourceRef a = left[index];
            SourceRef b = right[index];
            if (!string.Equals(a.Kind, b.Kind, StringComparison.Ordinal)
                || !string.Equals(a.Reference, b.Reference, StringComparison.Ordinal)
                || a.CapturedAt != b.CapturedAt)
            {
                return false;
            }
        }

        return true;
    }

    private static long NpgsqlEpochUnixTicks { get; } = Instant.FromUtc(2000, 1, 1, 0, 0).ToUnixTimeTicks();

    /// <summary>
    /// Npgsql encodes an <see cref="Instant"/> as nanoseconds since 2000-01-01 and PostgreSQL
    /// <c>timestamptz</c> stores microseconds, so the sub-microsecond digits a client sent are lost.
    /// Replicating that truncation here (toward zero in the 2000-epoch domain, matching integer
    /// division) keeps a claim built from a write equal to the one read back from the store.
    /// </summary>
    private static Instant TruncateToMicroseconds(Instant value)
    {
        long ticks = value.ToUnixTimeTicks() - NpgsqlEpochUnixTicks;
        return Instant.FromUnixTimeTicks(NpgsqlEpochUnixTicks + (ticks / 10) * 10);
    }
}
