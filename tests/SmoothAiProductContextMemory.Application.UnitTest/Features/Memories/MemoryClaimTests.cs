using System.Text;
using NodaTime;
using Shouldly;
using SmoothAiProductContextMemory.Application.Common.Models;
using SmoothAiProductContextMemory.Application.Features.Memories;
using SmoothAiProductContextMemory.Domain;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Application.UnitTest.Features.Memories;

public class MemoryClaimTests
{
    [Fact]
    public void Different_statement_is_a_difference() =>
        Same(Write(statement: "one"), Write(statement: "two")).ShouldBeFalse();

    [Fact]
    public void Different_content_summary_is_a_difference() =>
        Same(Write(contentSummary: "one"), Write(contentSummary: "two")).ShouldBeFalse();

    [Fact]
    public void Different_kind_is_a_difference() =>
        Same(Write(kind: "decision"), Write(kind: "rule")).ShouldBeFalse();

    [Fact]
    public void Different_status_is_a_difference() =>
        Same(Write(status: "proposed"), Write(status: "approved")).ShouldBeFalse();

    [Fact]
    public void Different_confidence_is_a_difference() =>
        Same(Write(confidence: 10), Write(confidence: 20)).ShouldBeFalse();

    [Fact]
    public void Different_source_reference_is_a_difference()
    {
        var left = Write(sources: [new("jira", "K-1", null)]);
        var right = Write(sources: [new("jira", "K-2", null)]);
        Same(left, right).ShouldBeFalse();
    }

    [Fact]
    public void Different_source_captured_at_is_a_difference()
    {
        Instant at = Instant.FromUtc(2026, 1, 1, 0, 0);
        var left = Write(sources: [new("jira", "K-1", at)]);
        var right = Write(sources: [new("jira", "K-1", at.Plus(Duration.FromSeconds(1)))]);
        Same(left, right).ShouldBeFalse();
    }

    [Fact]
    public void Different_valid_until_is_a_difference()
    {
        Instant at = Instant.FromUtc(2026, 1, 1, 0, 0);
        Same(Write(validUntil: at), Write(validUntil: at.Plus(Duration.FromDays(1)))).ShouldBeFalse();
    }

    [Fact]
    public void Different_content_is_a_difference() =>
        Same(Write(content: "body"), Write(content: "other body")).ShouldBeFalse();

    [Fact]
    public void Valid_from_difference_is_not_a_difference()
    {
        Instant at = Instant.FromUtc(2026, 1, 1, 0, 0);
        Same(Write(validFrom: at), Write(validFrom: at.Plus(Duration.FromHours(1)))).ShouldBeTrue();
    }

    [Fact]
    public void Summary_model_difference_is_not_a_difference() =>
        Same(Write(summaryModel: "a"), Write(summaryModel: "b")).ShouldBeTrue();

    [Fact]
    public void Valid_until_within_same_microsecond_is_not_a_difference()
    {
        Instant at = Instant.FromUtc(2026, 1, 1, 0, 0);
        Same(Write(validUntil: at), Write(validUntil: at.Plus(Duration.FromTicks(9)))).ShouldBeTrue();
    }

    [Fact]
    public void Null_and_empty_sources_are_equal() =>
        Same(Write(sources: null), Write(sources: [])).ShouldBeTrue();

    [Fact]
    public void Null_and_empty_content_are_equal() =>
        Same(Write(content: null), Write(content: "")).ShouldBeTrue();

    [Fact]
    public void Claim_from_write_equals_claim_from_persisted_version()
    {
        SetMemories.MemoryWrite write = Write(statement: "A claim", content: "body");
        MemoryClaim fromWrite = MemoryClaim.FromWrite(write);

        MemoryVersion version = new()
        {
            Statement = write.Statement,
            ContentSummary = write.ContentSummary,
            Kind = write.Kind,
            Status = write.Status,
            Confidence = write.Confidence,
            Sources = write.Sources?
                .Select(s => SourceDocument.Create(s.Kind, s.Reference, s.CapturedAt))
                .ToList() ?? [],
            ValidFrom = write.ValidFrom,
            ValidUntil = write.ValidUntil,
            BlobAddress = ContentAddress.Compute(Encoding.UTF8.GetBytes(write.Content!)),
        };
        MemoryClaim fromVersion = MemoryClaim.FromVersion(version);

        MemoryClaim.Same(fromWrite, fromVersion).ShouldBeTrue();
    }

    private static bool Same(SetMemories.MemoryWrite left, SetMemories.MemoryWrite right) =>
        MemoryClaim.Same(MemoryClaim.FromWrite(left), MemoryClaim.FromWrite(right));

    private static SetMemories.MemoryWrite Write(
        string statement = "Statement",
        string contentSummary = "Summary",
        string kind = "decision",
        string status = "approved",
        short confidence = 80,
        IReadOnlyList<(string Kind, string Reference, Instant? CapturedAt)>? sources = null,
        string? content = "blob-body",
        Instant? validFrom = null,
        Instant? validUntil = null,
        string? summaryModel = "model")
    {
        Instant from = validFrom ?? Instant.FromUtc(2026, 1, 1, 0, 0);
        return new SetMemories.MemoryWrite(
            null,
            "Name",
            "Description",
            statement,
            contentSummary,
            kind,
            ["architecture"],
            ["tag"],
            status,
            confidence,
            content,
            sources?.Select(s => new SourceInput(s.Kind, s.Reference, s.CapturedAt)).ToList(),
            from,
            validUntil,
            summaryModel,
            "prompt");
    }
}
