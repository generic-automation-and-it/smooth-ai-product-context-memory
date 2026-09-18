using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SmoothAiProductContextMemory.Domain;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Host.IntegrationTest;

public sealed class BusinessCaptureRecallLifecycleTests(HostWebAppFixture fixture)
    : IClassFixture<HostWebAppFixture>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _write = fixture.HttpClient;
    private readonly HostWebAppFixture _fixture = fixture;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Requirement", "BR-04")]
    [Trait("Requirement", "BR-07")]
    [Trait("Requirement", "BR-08")]
    [Trait("Requirement", "BR-11")]
    [Trait("Requirement", "BR-13")]
    [Trait("Requirement", "BR-14")]
    public async Task Returning_practitioner_recalls_current_decision_and_preserved_reasoning()
    {
        string key = UniqueKey();
        string initiative = $"lifecycle-{key}";
        await CreateInitiative(initiative);

        Ticket measurementTicket = new("jira", $"MEM-{key}");
        Ticket decisionTicket = new("github", $"DEC-{key}");
        Guid measurementGroup = await ResolveGroup(measurementTicket, $"metrics-{key}", initiative);
        Guid decisionGroup = await ResolveGroup(decisionTicket, $"service-{key}", initiative);

        Guid measurementUuid = Guid.NewGuid();
        Guid findingUuid = Guid.NewGuid();
        Guid decisionUuid = Guid.NewGuid();
        DateTimeOffset measurementTime = new(2026, 1, 10, 9, 0, 0, TimeSpan.Zero);
        DateTimeOffset findingTime = new(2026, 1, 11, 10, 0, 0, TimeSpan.Zero);
        DateTimeOffset decisionTime = new(2026, 1, 12, 11, 0, 0, TimeSpan.Zero);
        DateTimeOffset beforeCapture = DateTimeOffset.UtcNow.AddSeconds(-1);

        JsonElement measurementReceipt = await SetMemories(
            measurementGroup,
            [Memory(
                measurementUuid,
                $"Checkout latency measurement {key}",
                "Checkout p95 latency measured 840 ms.",
                "Five production samples exceeded the latency objective.",
                "measurement",
                96,
                measurementTime,
                "benchmark-run-184",
                "benchmark",
                measurementTime,
                $"measurement-rationale-{key}",
                [$"performance-{key}"],
                [$"checkout-{key}"])],
            [],
            []);
        AssertReceipt(measurementReceipt, created: 1, versioned: 0, linked: 0, skipped: 0, labels: 0);
        measurementReceipt.GetProperty("items")[0].GetProperty("uuid").GetGuid().ShouldBe(measurementUuid);

        JsonElement decisionReceipt = await SetMemories(
            decisionGroup,
            [
                Memory(
                    findingUuid,
                    $"Checkout latency finding {key}",
                    "Repeated cache misses cause the checkout latency regression.",
                    "Trace comparison isolated repeated catalogue lookups.",
                    "finding",
                    89,
                    findingTime,
                    "trace-analysis-52",
                    "analysis",
                    findingTime,
                    $"finding-rationale-{key}",
                    [$"performance-{key}"],
                    [$"checkout-{key}"]),
                Memory(
                    decisionUuid,
                    $"Checkout caching decision {key}",
                    "Cache catalogue lookups for five minutes.",
                    "Chosen after traces connected repeated lookups to measured latency.",
                    MemoryVersion.KindValue.Decision,
                    87,
                    decisionTime,
                    "architecture-record-27",
                    "decision-record",
                    decisionTime,
                    $"decision-rationale-v1-{key}",
                    [$"architecture-{key}"],
                    [$"checkout-{key}"])
            ],
            [
                Link(measurementUuid, findingUuid, MemoryRelation.DependsOn, "Production measurements produced the cache-miss finding."),
                Link(findingUuid, decisionUuid, MemoryRelation.DependsOn, "The cache-miss finding justified the caching decision.")
            ],
            []);
        DateTimeOffset afterCapture = DateTimeOffset.UtcNow.AddSeconds(1);

        AssertReceipt(decisionReceipt, created: 2, versioned: 0, linked: 2, skipped: 0, labels: 0);
        decisionReceipt.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("uuid").GetGuid())
            .ShouldBe([findingUuid, decisionUuid]);

        using HttpClient read = CreateReadClient();
        JsonElement recalled = await Query(read, new
        {
            ticketProvider = decisionTicket.Provider,
            ticketKey = decisionTicket.Key,
            kind = MemoryVersion.KindValue.Decision,
            limit = 10,
        });
        JsonElement currentDecision = recalled.GetProperty("items").EnumerateArray().Single();
        AssertMemory(
            currentDecision,
            decisionUuid,
            "Cache catalogue lookups for five minutes.",
            "approved",
            87,
            decisionTime,
            "decision-record",
            "architecture-record-27",
            decisionTime);
        DateTimeOffset createdOn = currentDecision.GetProperty("createdOn").GetDateTimeOffset();
        createdOn.ShouldBeGreaterThanOrEqualTo(beforeCapture);
        createdOn.ShouldBeLessThanOrEqualTo(afterCapture);
        currentDecision.TryGetProperty("content", out _).ShouldBeFalse();
        currentDecision.TryGetProperty("blobAddress", out _).ShouldBeFalse();

        JsonElement paths = await PostJson(read, "/api/context/paths", new
        {
            sourceUuid = measurementUuid,
            targetUuid = decisionUuid,
            maxDepth = 3,
        });
        JsonElement path = paths.GetProperty("paths").EnumerateArray().Single();
        path.GetProperty("depth").GetInt32().ShouldBe(2);
        JsonElement[] hops = [.. path.GetProperty("hops").EnumerateArray()];
        AssertHop(
            hops[0],
            measurementUuid,
            findingUuid,
            MemoryRelation.DependsOn,
            "Production measurements produced the cache-miss finding.");
        AssertHop(
            hops[1],
            findingUuid,
            decisionUuid,
            MemoryRelation.DependsOn,
            "The cache-miss finding justified the caching decision.");
        path.GetProperty("endpoint").GetProperty("statement").GetString()
            .ShouldBe("Cache catalogue lookups for five minutes.");

        DateTimeOffset revisedTime = new(2026, 2, 3, 8, 30, 0, TimeSpan.Zero);
        JsonElement revisionReceipt = await SetMemories(
            decisionGroup,
            [Memory(
                null,
                $"Checkout caching decision {key}",
                "Cache catalogue lookups for ten minutes after load testing.",
                "Load testing showed five minutes expired before reuse during peak traffic.",
                MemoryVersion.KindValue.Decision,
                93,
                revisedTime,
                "load-test-311",
                "benchmark",
                revisedTime,
                $"decision-rationale-v2-{key}",
                [$"architecture-{key}"],
                [$"checkout-{key}"],
                uuid: decisionUuid)],
            [],
            []);
        AssertReceipt(revisionReceipt, created: 0, versioned: 1, linked: 0, skipped: 0, labels: 0);
        revisionReceipt.GetProperty("items")[0].GetProperty("versioned").GetBoolean().ShouldBeTrue();

        JsonElement current = await Query(read, new { groupUuid = decisionGroup, kind = MemoryVersion.KindValue.Decision });
        JsonElement revisedDecision = current.GetProperty("items").EnumerateArray().Single();
        revisedDecision.GetProperty("uuid").GetGuid().ShouldBe(decisionUuid);
        revisedDecision.GetProperty("version").GetInt32().ShouldBe(2);
        revisedDecision.GetProperty("statement").GetString()
            .ShouldBe("Cache catalogue lookups for ten minutes after load testing.");
        AssertSource(revisedDecision, "benchmark", "load-test-311", revisedTime);

        JsonElement history = await GetJson(read, $"/api/context/memories/{decisionUuid}/versions");
        JsonElement[] versions = [.. history.GetProperty("items").EnumerateArray()];
        versions.Length.ShouldBe(2);
        versions[0].GetProperty("statement").GetString().ShouldBe("Cache catalogue lookups for five minutes.");
        versions[0].GetProperty("contentSummary").GetString()
            .ShouldBe("Chosen after traces connected repeated lookups to measured latency.");
        AssertSource(versions[0], "decision-record", "architecture-record-27", decisionTime);
        versions[0].GetProperty("isCurrent").GetBoolean().ShouldBeFalse();
        versions[1].GetProperty("isCurrent").GetBoolean().ShouldBeTrue();
        AssertSource(versions[1], "benchmark", "load-test-311", revisedTime);

        (await GetText(read, $"/api/context/memories/{decisionUuid}/versions/1/blob"))
            .ShouldBe($"decision-rationale-v1-{key}");
        (await GetText(read, $"/api/context/memories/{decisionUuid}/versions/2/blob"))
            .ShouldBe($"decision-rationale-v2-{key}");
    }

    [Fact]
    [Trait("Requirement", "BR-04")]
    public async Task Recall_follows_ticket_tracker_repository_initiative_facet_tag_and_topic()
    {
        string key = UniqueKey();
        string initiative = $"selector-{key}";
        string sharedTicketKey = $"WORK-{key}";
        string jiraRepo = $"selector-jira-{key}";
        string githubRepo = $"selector-github-{key}";
        string jiraFacet = $"facet-jira-{key}";
        string githubTag = $"tag-github-{key}";
        string topic = $"selector-topic-{key}";
        await CreateInitiative(initiative);

        Ticket jira = new("jira", sharedTicketKey);
        Ticket github = new("github", sharedTicketKey);
        Guid jiraGroup = await ResolveGroup(jira, jiraRepo, initiative);
        Guid githubGroup = await ResolveGroup(github, githubRepo, initiative);
        Guid distractorGroup = await ResolveGroup(new Ticket("linear", $"OTHER-{key}"), $"other-{key}");
        Guid jiraMemory = Guid.NewGuid();
        Guid githubMemory = Guid.NewGuid();
        Guid distractor = Guid.NewGuid();

        await SetMemories(jiraGroup, [Memory(
            jiraMemory,
            $"Jira {topic}",
            "Jira owns this exact work item.",
            "Jira selector evidence.",
            MemoryVersion.KindValue.Reference,
            82,
            HistoricalTime(1),
            $"jira:{sharedTicketKey}",
            "ticket",
            HistoricalTime(1),
            null,
            [jiraFacet],
            [$"jira-tag-{key}"])], [], []);
        await SetMemories(githubGroup, [Memory(
            githubMemory,
            $"GitHub selector subject {key}",
            "GitHub owns a different work item with the same literal key.",
            "GitHub selector evidence.",
            MemoryVersion.KindValue.Reference,
            84,
            HistoricalTime(2),
            $"github:{sharedTicketKey}",
            "ticket",
            HistoricalTime(2),
            null,
            [$"github-facet-{key}"],
            [githubTag])], [], []);
        await SetMemories(distractorGroup, [Memory(
            distractor,
            $"Unrelated selector subject {key}",
            "This record must not cross selector boundaries.",
            "Distractor.",
            MemoryVersion.KindValue.Reference,
            70,
            HistoricalTime(3),
            $"linear:OTHER-{key}",
            "ticket",
            HistoricalTime(3))], [], []);

        using HttpClient read = CreateReadClient();
        await AssertQueryIds(read, new { ticketProvider = jira.Provider, ticketKey = jira.Key }, jiraMemory);
        await AssertQueryIds(read, new { ticketProvider = github.Provider, ticketKey = github.Key }, githubMemory);
        await AssertQueryIds(read, new { repo = jiraRepo }, jiraMemory);
        await AssertQueryIds(read, new { initiativeName = initiative }, jiraMemory, githubMemory);
        await AssertQueryIds(read, new { facets = new[] { jiraFacet } }, jiraMemory);
        await AssertQueryIds(read, new { tags = new[] { githubTag } }, githubMemory);
        await AssertQueryIds(read, new { query = topic }, jiraMemory);
    }

    [Fact]
    [Trait("Requirement", "BR-05")]
    [Trait("Requirement", "BR-06-api")]
    public async Task Topic_recall_crosses_repositories_until_repository_is_explicitly_restricted()
    {
        string key = UniqueKey();
        string topic = $"portability-{key}";
        string firstRepo = $"payments-{key}";
        string secondRepo = $"orders-{key}";
        Guid firstGroup = await ResolveGroup(new Ticket("jira", $"PAY-{key}"), firstRepo);
        Guid secondGroup = await ResolveGroup(new Ticket("github", $"ORD-{key}"), secondRepo);
        Guid distractorGroup = await ResolveGroup(new Ticket("linear", $"OPS-{key}"), $"operations-{key}");
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();
        Guid distractor = Guid.NewGuid();

        await SetMemories(firstGroup, [Memory(
            first,
            $"Payment {topic}",
            "Use idempotency keys on payment submission.",
            "Reusable payment evidence.",
            MemoryVersion.KindValue.Decision,
            90,
            HistoricalTime(1),
            "payment-adr",
            "decision-record",
            HistoricalTime(1))], [], []);
        await SetMemories(secondGroup, [Memory(
            second,
            $"Order {topic}",
            "Propagate idempotency keys into order creation.",
            "Reusable order evidence.",
            MemoryVersion.KindValue.Decision,
            88,
            HistoricalTime(2),
            "order-adr",
            "decision-record",
            HistoricalTime(2))], [], []);
        await SetMemories(distractorGroup, [Memory(
            distractor,
            $"Unrelated operations record {key}",
            "Rotate the operational dashboard.",
            "Unrelated evidence.",
            MemoryVersion.KindValue.Decision,
            75,
            HistoricalTime(3),
            "ops-runbook",
            "runbook",
            HistoricalTime(3))], [], []);

        using HttpClient read = CreateReadClient();
        await AssertQueryIds(read, new { query = topic, limit = 10 }, first, second);
        await AssertQueryIds(read, new { query = topic, repo = firstRepo, limit = 10 }, first);
        await AssertQueryIds(read, new { query = topic, repo = secondRepo, limit = 10 }, second);
    }

    [Fact]
    [Trait("Requirement", "BR-06-api")]
    [Trait("Requirement", "BR-07")]
    public async Task Bounded_recall_is_attributed_excludes_bodies_and_requires_opt_in_for_proposals()
    {
        string key = UniqueKey();
        string topic = $"bounded-{key}";
        Guid group = await ResolveGroup(new Ticket("jira", $"BOUND-{key}"), $"bounded-{key}");
        Guid oldest = Guid.NewGuid();
        Guid middle = Guid.NewGuid();
        Guid newest = Guid.NewGuid();
        Guid proposed = Guid.NewGuid();
        Guid unrelated = Guid.NewGuid();
        DateTimeOffset beforeCapture = DateTimeOffset.UtcNow.AddSeconds(-1);

        await SetMemories(group, [
            Memory(oldest, $"Old {topic}", "Old approved evidence.", "Old summary.", "finding", 71,
                HistoricalTime(1), "evidence-old", "report", HistoricalTime(1), $"raw-old-{key}"),
            Memory(middle, $"Middle {topic}", "Middle approved evidence.", "Middle summary.", "finding", 81,
                HistoricalTime(2), "evidence-middle", "report", HistoricalTime(2), $"raw-middle-{key}"),
            Memory(newest, $"New {topic}", "Newest approved evidence.", "Newest summary.", "finding", 91,
                HistoricalTime(3), "evidence-new", "report", HistoricalTime(3), $"raw-new-{key}"),
            Memory(proposed, $"Proposed {topic}", "Unapproved candidate evidence.", "Proposed summary.", "finding", 51,
                HistoricalTime(4), "proposal-note", "note", HistoricalTime(4), $"raw-proposed-{key}",
                status: MemoryVersion.MemoryVersionStatus.Proposed),
            Memory(unrelated, $"Unrelated bounded record {key}", "Does not match topic.", "Distractor.", "finding", 61,
                HistoricalTime(5), "unrelated-note", "note", HistoricalTime(5))
        ], [], []);
        DateTimeOffset afterCapture = DateTimeOffset.UtcNow.AddSeconds(1);

        using HttpClient read = CreateReadClient();
        JsonElement bounded = await Query(read, new { query = topic, limit = 2 });
        JsonElement[] items = [.. bounded.GetProperty("items").EnumerateArray()];
        items.Length.ShouldBe(2);
        items.Select(item => item.GetProperty("uuid").GetGuid()).ShouldBe([newest, middle]);
        foreach (JsonElement item in items)
        {
            item.GetProperty("sources").GetArrayLength().ShouldBe(1);
            item.GetProperty("confidence").GetInt16().ShouldBeGreaterThan((short)0);
            DateTimeOffset createdOn = item.GetProperty("createdOn").GetDateTimeOffset();
            createdOn.ShouldBeGreaterThanOrEqualTo(beforeCapture);
            createdOn.ShouldBeLessThanOrEqualTo(afterCapture);
            item.TryGetProperty("content", out _).ShouldBeFalse();
            item.TryGetProperty("blobAddress", out _).ShouldBeFalse();
        }

        await AssertQueryIds(read, new { query = topic, limit = 10 }, oldest, middle, newest);
        await AssertQueryIds(read, new { query = topic, includeProposed = true, limit = 10 }, oldest, middle, newest, proposed);
    }

    [Fact]
    [Trait("Requirement", "BR-08")]
    [Trait("Requirement", "BR-09")]
    [Trait("Requirement", "BR-13")]
    public async Task Correction_preserves_effective_date_while_world_change_moves_business_validity()
    {
        string key = UniqueKey();
        Guid group = await ResolveGroup(new Ticket("jira", $"TIME-{key}"), $"temporal-{key}");
        Guid correction = Guid.NewGuid();
        Guid worldChange = Guid.NewGuid();
        DateTimeOffset originalEffective = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset changedEffective = new(2024, 7, 1, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset beforeCapture = DateTimeOffset.UtcNow.AddSeconds(-1);

        await SetMemories(group, [Memory(
            correction,
            $"Corrected capacity record {key}",
            "Capacity was 40 requests per second.",
            "Original transcription.",
            "measurement",
            70,
            originalEffective,
            "capacity-sheet-v1",
            "spreadsheet",
            originalEffective,
            $"correction-v1-{key}")], [], []);
        await SetMemories(group, [Memory(
            null,
            $"Corrected capacity record {key}",
            "Capacity was 400 requests per second.",
            "Corrected a missing zero in the original record.",
            "measurement",
            99,
            originalEffective,
            "capacity-sheet-v2",
            "spreadsheet",
            originalEffective,
            $"correction-v2-{key}",
            uuid: correction)], [], []);

        await SetMemories(group, [Memory(
            worldChange,
            $"Live capacity record {key}",
            "Capacity is 400 requests per second.",
            "Capacity before infrastructure expansion.",
            "measurement",
            95,
            originalEffective,
            "capacity-benchmark-old",
            "benchmark",
            originalEffective,
            $"world-v1-{key}",
            validUntil: changedEffective)], [], []);
        await SetMemories(group, [Memory(
            null,
            $"Live capacity record {key}",
            "Capacity is 900 requests per second.",
            "Infrastructure expansion changed measured capacity.",
            "measurement",
            97,
            changedEffective,
            "capacity-benchmark-new",
            "benchmark",
            changedEffective,
            $"world-v2-{key}",
            uuid: worldChange)], [], []);
        DateTimeOffset afterCapture = DateTimeOffset.UtcNow.AddSeconds(1);

        using HttpClient read = CreateReadClient();
        JsonElement correctionHistory = await GetJson(read, $"/api/context/memories/{correction}/versions");
        JsonElement[] correctionVersions = [.. correctionHistory.GetProperty("items").EnumerateArray()];
        correctionVersions.Length.ShouldBe(2);
        correctionVersions.Select(item => item.GetProperty("validFrom").GetDateTimeOffset())
            .ShouldAllBe(validFrom => validFrom == originalEffective);
        correctionVersions[0].GetProperty("statement").GetString().ShouldBe("Capacity was 40 requests per second.");
        correctionVersions[1].GetProperty("statement").GetString().ShouldBe("Capacity was 400 requests per second.");
        correctionVersions[1].GetProperty("createdOn").GetDateTimeOffset()
            .ShouldBeGreaterThan(correctionVersions[0].GetProperty("createdOn").GetDateTimeOffset());

        JsonElement worldHistory = await GetJson(read, $"/api/context/memories/{worldChange}/versions");
        JsonElement[] worldVersions = [.. worldHistory.GetProperty("items").EnumerateArray()];
        worldVersions.Length.ShouldBe(2);
        worldVersions[0].GetProperty("validFrom").GetDateTimeOffset().ShouldBe(originalEffective);
        worldVersions[0].GetProperty("validUntil").GetDateTimeOffset().ShouldBe(changedEffective);
        worldVersions[1].GetProperty("validFrom").GetDateTimeOffset().ShouldBe(changedEffective);

        foreach (JsonElement version in correctionVersions.Concat(worldVersions))
        {
            DateTimeOffset createdOn = version.GetProperty("createdOn").GetDateTimeOffset();
            createdOn.ShouldBeGreaterThanOrEqualTo(beforeCapture);
            createdOn.ShouldBeLessThanOrEqualTo(afterCapture);
            createdOn.ShouldBeGreaterThan(version.GetProperty("validFrom").GetDateTimeOffset());
        }

        DateTimeOffset beforeWorldChange = new(2024, 3, 1, 0, 0, 0, TimeSpan.Zero);
        await AssertQueryIds(read, new
        {
            query = $"Live capacity record {key}",
            currentOnly = false,
            asOf = beforeWorldChange,
            limit = 10,
        }, worldChange);
        JsonElement oldWorld = await Query(read, new
        {
            query = $"Live capacity record {key}",
            currentOnly = false,
            asOf = beforeWorldChange,
            limit = 10,
        });
        oldWorld.GetProperty("items")[0].GetProperty("version").GetInt32().ShouldBe(1);

        DateTimeOffset afterWorldChangeTime = new(2024, 8, 1, 0, 0, 0, TimeSpan.Zero);
        JsonElement newWorld = await Query(read, new
        {
            query = $"Live capacity record {key}",
            currentOnly = false,
            asOf = afterWorldChangeTime,
            limit = 10,
        });
        JsonElement currentWorld = newWorld.GetProperty("items").EnumerateArray().Single();
        currentWorld.GetProperty("uuid").GetGuid().ShouldBe(worldChange);
        currentWorld.GetProperty("version").GetInt32().ShouldBe(2);

        JsonElement correctedAtHistoricalTime = await Query(read, new
        {
            query = $"Corrected capacity record {key}",
            currentOnly = false,
            asOf = beforeWorldChange,
            limit = 10,
        });
        correctedAtHistoricalTime.GetProperty("items").GetArrayLength().ShouldBe(2);
        correctedAtHistoricalTime.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("version").GetInt32())
            .ShouldBe([1, 2]);
    }

    [Fact]
    [Trait("Requirement", "BR-13-api")]
    [Trait("Requirement", "BR-14-api")]
    public async Task Mixed_capture_receipt_reports_created_versioned_connected_skipped_and_proposed()
    {
        string key = UniqueKey();
        Guid group = await ResolveGroup(new Ticket("jira", $"MIX-{key}"), $"mixed-{key}");
        Guid versionTarget = Guid.NewGuid();
        Guid existingLinkTarget = Guid.NewGuid();
        Guid created = Guid.NewGuid();
        string proposedLabel = $"mixed-label-{key}";

        await SetMemories(group, [
            Memory(versionTarget, $"Version target {key}", "Initial position.", "Initial reasoning.",
                MemoryVersion.KindValue.Decision, 80, HistoricalTime(1), "initial-adr", "decision-record", HistoricalTime(1)),
            Memory(existingLinkTarget, $"Existing link target {key}", "Supporting evidence.", "Supporting reasoning.",
                "finding", 85, HistoricalTime(1), "finding-report", "report", HistoricalTime(1))
        ], [], []);
        await PostJson(_write, "/api/context/links", Link(
            versionTarget,
            existingLinkTarget,
            MemoryRelation.RelatesTo,
            "Existing relationship before mixed capture."));

        JsonElement receipt = await SetMemories(group, [
            Memory(created, $"Created in mixed capture {key}", "Newly captured evidence.", "New evidence reasoning.",
                "measurement", 92, HistoricalTime(2), "mixed-benchmark", "benchmark", HistoricalTime(2)),
            Memory(null, $"Version target {key}", "Revised position.", "Revised reasoning.",
                MemoryVersion.KindValue.Decision, 94, HistoricalTime(2), "revised-adr", "decision-record", HistoricalTime(2),
                uuid: versionTarget)
        ], [
            Link(created, versionTarget, MemoryRelation.DependsOn, "New evidence informed the revised position."),
            Link(created, versionTarget, MemoryRelation.DependsOn, "Repeated candidate must be skipped."),
            Link(versionTarget, existingLinkTarget, MemoryRelation.RelatesTo, "Stored relationship must be skipped.")
        ], [proposedLabel]);

        AssertReceipt(receipt, created: 1, versioned: 1, linked: 1, skipped: 2, labels: 1);
        receipt.GetProperty("diverged").GetInt32().ShouldBe(0);
        JsonElement[] results = [.. receipt.GetProperty("items").EnumerateArray()];
        results[0].GetProperty("uuid").GetGuid().ShouldBe(created);
        results[0].GetProperty("versioned").GetBoolean().ShouldBeFalse();
        results[1].GetProperty("uuid").GetGuid().ShouldBe(versionTarget);
        results[1].GetProperty("versioned").GetBoolean().ShouldBeTrue();

        using HttpClient read = CreateReadClient();
        JsonElement current = await Query(read, new { groupUuid = group, limit = 10 });
        current.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("uuid").GetGuid() == versionTarget)
            .GetProperty("statement").GetString().ShouldBe("Revised position.");
        JsonElement history = await GetJson(read, $"/api/context/memories/{versionTarget}/versions");
        history.GetProperty("items").GetArrayLength().ShouldBe(2);

        JsonElement path = await PostJson(read, "/api/context/paths", new
        {
            sourceUuid = created,
            targetUuid = versionTarget,
            relation = MemoryRelation.DependsOn,
            maxDepth = 1,
        });
        path.GetProperty("paths").GetArrayLength().ShouldBe(1);
        path.GetProperty("paths")[0].GetProperty("hops").GetArrayLength().ShouldBe(1);

        JsonElement existingPath = await PostJson(read, "/api/context/paths", new
        {
            sourceUuid = versionTarget,
            targetUuid = existingLinkTarget,
            relation = MemoryRelation.RelatesTo,
            maxDepth = 1,
        });
        existingPath.GetProperty("paths").GetArrayLength().ShouldBe(1);

        JsonElement labels = await GetJson(read, "/api/context/labels");
        JsonElement label = labels.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == proposedLabel);
        label.GetProperty("status").GetString().ShouldBe(Label.LabelStatus.Draft);
    }

    private HttpClient CreateReadClient()
    {
        HttpClient client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", HostWebAppFixture.ReadToken);
        return client;
    }

    private async Task CreateInitiative(string name)
    {
        JsonElement response = await PostJson(_write, "/api/context/initiatives", new
        {
            name,
            description = "Business lifecycle integration fixture.",
        });
        response.GetProperty("created").GetBoolean().ShouldBeTrue();
    }

    private async Task<Guid> ResolveGroup(Ticket ticket, string repo, string? initiative = null)
    {
        JsonElement response = await PostJson(_write, "/api/context/groups/resolve", new
        {
            tickets = new[] { new { ticket.Provider, ticket.Key, ticket.Url } },
            repo,
            initiativeName = initiative,
            scopeDimension = MemoryGroup.ScopeDimensionValue.Product,
        });
        response.GetProperty("created").GetBoolean().ShouldBeTrue();
        response.GetProperty("repo").GetString().ShouldBe(repo);
        return response.GetProperty("uuid").GetGuid();
    }

    private Task<JsonElement> SetMemories(
        Guid groupUuid,
        IReadOnlyList<object> items,
        IReadOnlyList<object> links,
        IReadOnlyList<string> labels) =>
        PostJson(_write, "/api/context/memories", new
        {
            groupUuid,
            items,
            links,
            labelsProposed = labels,
        });

    private static object Memory(
        Guid? createUuid,
        string description,
        string statement,
        string contentSummary,
        string kind,
        short confidence,
        DateTimeOffset validFrom,
        string sourceReference,
        string sourceKind,
        DateTimeOffset sourceCapturedAt,
        string? content = null,
        string[]? facets = null,
        string[]? tags = null,
        Guid? uuid = null,
        DateTimeOffset? validUntil = null,
        string status = MemoryVersion.MemoryVersionStatus.Approved) => new
    {
        uuid,
        createUuid,
        name = description,
        description,
        statement,
        contentSummary,
        kind,
        facets = facets ?? [],
        tags = tags ?? [],
        status,
        confidence,
        content,
        sources = new[] { new { kind = sourceKind, reference = sourceReference, capturedAt = sourceCapturedAt } },
        validFrom,
        validUntil,
        summaryModel = "business-lifecycle-test-model",
        summaryPromptVersion = "1",
    };

    private static object Link(Guid sourceUuid, Guid targetUuid, string relation, string reason) => new
    {
        sourceUuid,
        targetUuid,
        relation,
        reason,
    };

    private async Task AssertQueryIds(HttpClient client, object request, params Guid[] expected)
    {
        JsonElement response = await Query(client, request);
        Guid[] actual = [.. response.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("uuid").GetGuid())];
        actual.OrderBy(uuid => uuid).ShouldBe(expected.OrderBy(uuid => uuid));
    }

    private Task<JsonElement> Query(HttpClient client, object request) =>
        PostJson(client, "/api/context/query", request);

    private async Task<JsonElement> GetJson(HttpClient client, string route)
    {
        using HttpResponseMessage response = await client.GetAsync(route, Ct);
        string payload = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, payload);
        return JsonSerializer.Deserialize<JsonElement>(payload, Json);
    }

    private async Task<string> GetText(HttpClient client, string route)
    {
        using HttpResponseMessage response = await client.GetAsync(route, Ct);
        string payload = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, payload);
        return payload;
    }

    private async Task<JsonElement> PostJson(HttpClient client, string route, object body)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(route, body, Json, Ct);
        string payload = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, payload);
        return JsonSerializer.Deserialize<JsonElement>(payload, Json);
    }

    private static void AssertReceipt(
        JsonElement receipt,
        int created,
        int versioned,
        int linked,
        int skipped,
        int labels)
    {
        receipt.GetProperty("created").GetInt32().ShouldBe(created);
        receipt.GetProperty("versioned").GetInt32().ShouldBe(versioned);
        receipt.GetProperty("linked").GetInt32().ShouldBe(linked);
        receipt.GetProperty("skipped").GetInt32().ShouldBe(skipped);
        receipt.GetProperty("labelsProposed").GetInt32().ShouldBe(labels);
    }

    private static void AssertMemory(
        JsonElement memory,
        Guid uuid,
        string statement,
        string status,
        short confidence,
        DateTimeOffset validFrom,
        string sourceKind,
        string sourceReference,
        DateTimeOffset sourceCapturedAt)
    {
        memory.GetProperty("uuid").GetGuid().ShouldBe(uuid);
        memory.GetProperty("statement").GetString().ShouldBe(statement);
        memory.GetProperty("status").GetString().ShouldBe(status);
        memory.GetProperty("confidence").GetInt16().ShouldBe(confidence);
        memory.GetProperty("validFrom").GetDateTimeOffset().ShouldBe(validFrom);
        memory.GetProperty("isCurrent").GetBoolean().ShouldBeTrue();
        AssertSource(memory, sourceKind, sourceReference, sourceCapturedAt);
    }

    private static void AssertSource(
        JsonElement memory,
        string kind,
        string reference,
        DateTimeOffset capturedAt)
    {
        JsonElement source = memory.GetProperty("sources").EnumerateArray().Single();
        source.GetProperty("kind").GetString().ShouldBe(kind);
        source.GetProperty("reference").GetString().ShouldBe(reference);
        source.GetProperty("capturedAt").GetDateTimeOffset().ShouldBe(capturedAt);
    }

    private static void AssertHop(
        JsonElement hop,
        Guid source,
        Guid target,
        string relation,
        string reason)
    {
        hop.GetProperty("sourceUuid").GetGuid().ShouldBe(source);
        hop.GetProperty("targetUuid").GetGuid().ShouldBe(target);
        hop.GetProperty("relation").GetString().ShouldBe(relation);
        hop.GetProperty("reason").GetString().ShouldBe(reason);
    }

    private static string UniqueKey() => Guid.NewGuid().ToString("N");

    private static DateTimeOffset HistoricalTime(int day) =>
        new(2025, 1, day, 12, 0, 0, TimeSpan.Zero);

    private sealed record Ticket(string Provider, string Key, string Url = "");
}
