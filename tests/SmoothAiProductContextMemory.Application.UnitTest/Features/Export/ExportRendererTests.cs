using SmoothAiProductContextMemory.Application.Features.Export;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Application.UnitTest.Features.Export;

public class ExportRendererTests
{
    private static readonly DateTimeOffset ValidFrom = new(2024, 1, 15, 10, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset CreatedOn = new(2024, 2, 1, 8, 0, 0, TimeSpan.Zero);
    private static readonly Guid MemoryUuid = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid LineageId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid GroupUuid = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid OtherUuid = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
    private static readonly Guid OtherGroupUuid = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");

    [Fact]
    public void Memory_frontmatter_keys_are_in_fixed_order()
    {
        string markdown = ExportRenderer.RenderMemory(ProductMemory(), includeHistory: false);
        string[] keys = FrontMatterKeys(markdown);

        keys.ShouldBe(
        [
            "generated",
            "uuid",
            "lineage_id",
            "group_uuid",
            "subject_slug",
            "name",
            "kind",
            "status",
            "confidence",
            "scope",
            "scope_identifier",
            "version",
            "is_current",
            "valid_from",
            "valid_until",
            "created_on",
            "facets",
            "tags",
            "sources",
        ]);
    }

    [Fact]
    public void Memory_file_contains_group_uuid_in_body()
    {
        string markdown = ExportRenderer.RenderMemory(ProductMemory(), includeHistory: false);
        markdown.ShouldContain($"group_uuid: `{GroupUuid:D}`");
        markdown.ShouldContain("generated: true");
        markdown.ShouldContain(ExportScopeMarks.GeneratedComment);
    }

    [Fact]
    public void Time_axes_are_distinct_keys()
    {
        string markdown = ExportRenderer.RenderMemory(ProductMemory(), includeHistory: false);
        markdown.ShouldContain($"valid_from: {ExportRenderer.FormatTimestamp(ValidFrom)}");
        markdown.ShouldContain("valid_until: null");
        markdown.ShouldContain($"created_on: {ExportRenderer.FormatTimestamp(CreatedOn)}");
        markdown.ShouldNotContain("exported_at");
    }

    [Fact]
    public void Program_and_self_get_banners_product_does_not()
    {
        ExportRenderer.RenderMemory(ProductMemory() with { ScopeDimension = MemoryGroup.ScopeDimensionValue.Program }, false)
            .ShouldContain(ExportScopeMarks.ProgramBanner);
        ExportRenderer.RenderMemory(ProductMemory() with { ScopeDimension = MemoryGroup.ScopeDimensionValue.Self }, false)
            .ShouldContain(ExportScopeMarks.SelfBanner);
        ExportRenderer.RenderMemory(ProductMemory(), false)
            .ShouldNotContain(ExportScopeMarks.ProgramBanner);
        ExportRenderer.RenderMemory(ProductMemory(), false)
            .ShouldNotContain(ExportScopeMarks.SelfBanner);
    }

    [Fact]
    public void Default_omits_historical_claim_history_includes_it()
    {
        ExportMemoryDocument memory = ProductMemory() with
        {
            HistoricalVersions =
            [
                CurrentVersion() with
                {
                    Version = 1,
                    IsCurrent = false,
                    Statement = "CLAIM_SUPERSEDED_V1",
                    BlobState = BlobRenderState.None,
                    BlobText = null,
                },
            ],
        };

        string currentOnly = ExportRenderer.RenderMemory(memory, includeHistory: false);
        currentOnly.ShouldContain("CLAIM_CURRENT_V2");
        currentOnly.ShouldNotContain("CLAIM_SUPERSEDED_V1");
        currentOnly.ShouldNotContain("## Version 1");

        string withHistory = ExportRenderer.RenderMemory(memory, includeHistory: true);
        withHistory.ShouldContain("CLAIM_CURRENT_V2");
        withHistory.ShouldContain("CLAIM_SUPERSEDED_V1");
        withHistory.ShouldContain("## Version 1");
    }

    [Fact]
    public void Blob_is_inlined_and_missing_blob_is_noted()
    {
        string inlined = ExportRenderer.RenderMemory(
            ProductMemory() with { Current = CurrentVersion() with { BlobState = BlobRenderState.Inlined, BlobText = "inlined-body" } },
            false);
        inlined.ShouldContain("inlined-body");

        string missing = ExportRenderer.RenderMemory(
            ProductMemory() with { Current = CurrentVersion() with { BlobState = BlobRenderState.Missing, BlobText = null } },
            false);
        missing.ShouldContain(ExportScopeMarks.MissingBlobNote);
    }

    [Fact]
    public void Encoding_is_lf_with_single_trailing_newline()
    {
        string markdown = ExportRenderer.RenderMemory(ProductMemory(), includeHistory: false);
        markdown.ShouldNotContain("\r");
        markdown.ShouldEndWith("\n");
        markdown.TrimEnd('\n').ShouldNotEndWith("\n");
    }

    [Fact]
    public void Render_is_deterministic()
    {
        ExportMemoryDocument memory = ProductMemory();
        ExportRenderer.RenderMemory(memory, false).ShouldBe(ExportRenderer.RenderMemory(memory, false));
    }

    [Fact]
    public void Frontmatter_escapes_newlines_and_tabs_in_free_text()
    {
        ExportMemoryDocument memory = ProductMemory() with { Name = "Line1\nLine2\tTabbed \"quoted\"" };
        string markdown = ExportRenderer.RenderMemory(memory, includeHistory: false);

        markdown.ShouldContain("name: \"Line1\\nLine2\\tTabbed \\\"quoted\\\"\"");
        int end = markdown.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        string frontmatter = markdown[..end];
        frontmatter.Split('\n').ShouldAllBe(line => !line.StartsWith("Line2", StringComparison.Ordinal));
    }

    [Fact]
    public void Frontmatter_quotes_numeric_looking_free_text()
    {
        ExportMemoryDocument memory = ProductMemory() with { Name = "2024" };
        string markdown = ExportRenderer.RenderMemory(memory, includeHistory: false);

        markdown.ShouldContain("name: \"2024\"");
    }

    [Fact]
    public void Group_render_includes_scope_initiative_and_tickets()
    {
        var group = new ExportGroupDocument(
            GroupUuid,
            MemoryGroup.ScopeDimensionValue.Program,
            "initiative-x",
            "to-be-decided",
            "active",
            "kingstown",
            "https://example.invalid/repo",
            [new ExportTicket("jira", "ACM-1", "https://example.invalid/ACM-1")],
            new ExportGroupDescription(2, "Export group", "Current body", CreatedOn),
            [new ExportGroupDescription(1, "Old name", "Old body", ValidFrom)]);

        string currentOnly = ExportRenderer.RenderGroup(group, includeHistory: false);
        currentOnly.ShouldContain("scope: program");
        currentOnly.ShouldContain(ExportScopeMarks.ProgramBanner);
        currentOnly.ShouldContain("initiative: to-be-decided");
        currentOnly.ShouldContain("key: ACM-1");
        currentOnly.ShouldContain("Current body");
        currentOnly.ShouldNotContain("Old body");

        string withHistory = ExportRenderer.RenderGroup(group, includeHistory: true);
        withHistory.ShouldContain("Old body");
        withHistory.ShouldContain("## Description version 1");
    }

    [Fact]
    public void Links_are_rendered_with_other_group_uuid()
    {
        ExportMemoryDocument memory = ProductMemory() with
        {
            Links =
            [
                new ExportLink("outgoing", "depends_on", "needs it", OtherUuid, "Other subject", OtherGroupUuid),
            ],
        };

        string markdown = ExportRenderer.RenderMemory(memory, false);
        markdown.ShouldContain("outgoing depends_on");
        markdown.ShouldContain($"`{OtherUuid:D}`");
        markdown.ShouldContain($"group_uuid: `{OtherGroupUuid:D}`");
        markdown.ShouldContain("needs it");
    }

    private static ExportMemoryDocument ProductMemory() => new(
        MemoryUuid,
        LineageId,
        GroupUuid,
        "postgres-storage",
        "Postgres storage",
        "PostgreSQL is the storage engine",
        MemoryGroup.ScopeDimensionValue.Product,
        null,
        ["architecture"],
        ["persistence"],
        CurrentVersion(),
        [],
        []);

    private static ExportVersionBody CurrentVersion() => new(
        2,
        true,
        MemoryVersion.KindValue.Decision,
        MemoryVersion.MemoryVersionStatus.Approved,
        80,
        ValidFrom,
        null,
        CreatedOn,
        "CLAIM_CURRENT_V2",
        "Summary of claim",
        [new ExportSource("jira", "ACM-1", ValidFrom)],
        BlobRenderState.None,
        null);

    private static string[] FrontMatterKeys(string markdown)
    {
        int start = markdown.IndexOf("---\n", StringComparison.Ordinal);
        int end = markdown.IndexOf("\n---\n", start + 4, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0);
        end.ShouldBeGreaterThan(start);

        return markdown[(start + 4)..end]
            .Split('\n')
            .Where(line => line.Length > 0 && !line.StartsWith(' ') && !line.StartsWith('-'))
            .Select(line => line.Split(':')[0])
            .ToArray();
    }
}
