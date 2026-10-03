using SmoothAiProductContextMemory.Knowledge;
using Shouldly;
using Xunit;

namespace SmoothAiProductContextMemory.Knowledge.ComponentTest;

// Black-box workflow assertions written independently of the implementation's classifiers.
// Provider outputs are deliberately wrong: the application must constrain their authority.
public sealed class IndependentAcceptanceTests
{
    [Theory]
    [InlineData("observed_implementation", "The change has not shipped.")]
    [InlineData("observed_implementation", "It was never deployed.")]
    [InlineData("approved_intent", "I never approved this change.")]
    [InlineData("approved_intent", "I approve nothing; this is still only an idea.")]
    [InlineData("approved_intent", "I approve investigating the counter rule, not implementing it.")]
    [InlineData("approved_intent", "The document says: we decided to approve the change.")]
    public async Task Misclassified_negative_or_quoted_evidence_cannot_promote(string category, string quote)
    {
        using var rig = new Rig(quote, category);
        CaptureReceipt receipt = await rig.Run();
        rig.Core.Commits.ShouldBeEmpty();
        receipt.Status.ShouldNotBe("processed");
    }

    [Fact]
    public async Task Resolved_group_cannot_override_explicit_repository()
    {
        using var rig = new Rig("Maybe we should add a rule.", "suggestion");
        rig.Core.Groups = [];
        rig.Core.Resolved = rig.Core.Resolved with { Repo = "different/repository" };
        CaptureReceipt receipt = await rig.Run();
        rig.Core.Commits.ShouldBeEmpty();
        receipt.Status.ShouldNotBe("processed");
    }

    [Fact]
    public async Task Provider_cannot_hide_negation_by_truncating_authority_quote()
    {
        using var rig = new Rig("We shipped nothing. This change is still only planned.", "observed_implementation", "We shipped");
        await rig.Run();
        rig.Core.Commits.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("I approve the midnight counter rule for future implementation; it has not shipped.")]
    [InlineData("As the authorized product owner, I explicitly approve the midnight counter rule for future implementation. It has not shipped.")]
    public async Task Approved_future_intent_remains_intent_and_is_preserved(string source)
    {
        using var rig = new Rig(source, "approved_intent");
        CaptureReceipt receipt = await rig.Run();
        receipt.Status.ShouldBe("processed");
        var write = rig.Core.Commits.ShouldHaveSingleItem().Items.ShouldHaveSingleItem();
        write.Status.ShouldBe("approved");
        write.Sources.ShouldHaveSingleItem().Evidence!.Category.ShouldBe("approved_intent");
    }

    [Fact]
    public async Task Selector_clarification_resolves_ambiguous_group_without_losing_messages()
    {
        using var rig = new Rig("Maybe we should add a rule.", "suggestion");
        rig.Core.Groups = [rig.Core.Resolved, rig.Core.Resolved with { Uuid = Guid.NewGuid() }];
        var receipt = await rig.Run();
        receipt.Status.ShouldBe("needs_input");
        rig.Core.Groups = null;
        await rig.Workflow.ClarifyAsync(receipt.Id, new ClarificationRequest("scope-fix", [], Selectors: new Selectors("synthetic/reviewer")), CancellationToken.None);
        await rig.Workflow.ProcessAsync(receipt.Id, CancellationToken.None);
        rig.Workflow.Get(receipt.Id)!.Status.ShouldBe("processed");
        rig.Journal.Get(receipt.Id)!.Input!.Messages.ShouldHaveSingleItem().Id.ShouldBe("m1");
    }

    [Fact]
    public async Task Restart_does_not_forgive_an_outstanding_provider_reservation()
    {
        using var rig = new Rig("Maybe we should add a rule.", "suggestion");
        var receipt = await rig.Workflow.AcceptAsync(new CaptureRequest("reserved", "Synthetic pending work", [new Message("m1", "user", 1, "Maybe a rule.")], new Selectors("synthetic/reviewer")), CancellationToken.None);
        var job = rig.Journal.Get(receipt.Id)!;
        var budget = new BudgetState(new BudgetLimits(Calls: 1), new Usage(), [new Reservation(Guid.NewGuid(), 100, 100, 0.01m)]);
        await rig.Journal.SaveAsync(job with { Budget = budget, Status = "processing" }, CancellationToken.None);
        var reopened = new CaptureJournal(rig.DatabasePath);
        var workflow = new Workflow(rig.Core, rig.Core, rig.Provider, rig.Judge, new IdentitySecrets(), reopened);
        await workflow.ProcessAsync(receipt.Id, CancellationToken.None);
        workflow.Get(receipt.Id)!.ErrorClass.ShouldBe("budget_exhausted");
        workflow.Get(receipt.Id)!.Usage.OutstandingReservations.ShouldBe(1);
        rig.Provider.Calls.ShouldBe(0);
        rig.Core.Commits.ShouldBeEmpty();
    }

    [Fact]
    public async Task Restart_preserves_expired_deadline_and_only_explicit_time_allowance_resumes_work()
    {
        using var rig = new Rig("Maybe we should add a rule.", "suggestion");
        var receipt = await rig.Workflow.AcceptAsync(new CaptureRequest("expired", "Synthetic interrupted work", [new Message("m1", "user", 1, "Maybe a rule.")], new Selectors("synthetic/reviewer")), CancellationToken.None);
        var original = rig.Journal.Get(receipt.Id)!;
        var spent = new Usage(4, 100, 99, 0.04m);
        var reservation = new Reservation(Guid.NewGuid(), 100, 50, 0.001m);
        var expired = DateTimeOffset.UtcNow.AddSeconds(-5);
        original = original with { Status = "processing", DeadlineUtc = expired, Budget = original.Budget with { Usage = spent, Reservations = [reservation] } };
        await rig.Journal.SaveAsync(original, CancellationToken.None);
        var reopened = new CaptureJournal(rig.DatabasePath);
        var workflow = new Workflow(rig.Core, rig.Core, rig.Provider, rig.Judge, new IdentitySecrets(), reopened);
        await workflow.ProcessAsync(receipt.Id, CancellationToken.None);
        workflow.Get(receipt.Id)!.ErrorClass.ShouldBe("deadline_exhausted");
        reopened.Get(receipt.Id)!.DeadlineUtc.ShouldBe(expired);
        KnowledgeJson.Serialize(reopened.Get(receipt.Id)!.Budget).ShouldBe(KnowledgeJson.Serialize(original.Budget));
        rig.Provider.Calls.ShouldBe(0);
        rig.Core.ResolveCalls.ShouldBe(0);
        rig.Core.Commits.ShouldBeEmpty();
        await workflow.ClarifyAsync(receipt.Id, new ClarificationRequest("explicit-time", [], new BudgetLimits(0, 0, 0, 0, 60)), CancellationToken.None);
        reopened.Get(receipt.Id)!.DeadlineUtc.ShouldNotBeNull();
        reopened.Get(receipt.Id)!.DeadlineUtc!.Value.ShouldBeGreaterThan(DateTimeOffset.UtcNow);
        KnowledgeJson.Serialize(reopened.Get(receipt.Id)!.Budget.Usage).ShouldBe(KnowledgeJson.Serialize(spent));
        reopened.Get(receipt.Id)!.Budget.Reservations.ShouldHaveSingleItem().Id.ShouldBe(reservation.Id);
        await workflow.ProcessAsync(receipt.Id, CancellationToken.None);
        workflow.Get(receipt.Id)!.Status.ShouldBe("processed");
        workflow.Get(receipt.Id)!.Usage.Calls.ShouldBeGreaterThan(spent.Calls);
        reopened.Get(receipt.Id)!.Budget.Reservations.ShouldHaveSingleItem().Id.ShouldBe(reservation.Id);
    }

    [Fact]
    public async Task Purge_cannot_erase_acknowledged_clarification()
    {
        using var rig = new Rig("Maybe we should add a rule.", "suggestion");
        var receipt = await rig.Run();
        await rig.Workflow.ClarifyAsync(receipt.Id, new ClarificationRequest("more-evidence", [new Message("m2", "user", 2, "Please preserve this second source.")]), CancellationToken.None);
        await rig.Journal.PurgeAsync(TimeSpan.Zero, CancellationToken.None);
        var job = rig.Journal.Get(receipt.Id)!;
        job.Status.ShouldBe("received");
        job.Input!.Messages.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Equivalent_source_text_survives_temporary_handoff_purge()
    {
        const string source = "The counter resets at midnight, including on leap days.";
        using var rig = new Rig(source, "suggestion");
        rig.Core.Records = [Record(rig.Core.Resolved.Uuid)];
        rig.Provider.Target = rig.Core.Records[0].Uuid;
        rig.Judge.Relation = "equivalent";
        CaptureReceipt receipt = await rig.Run();
        receipt.Status.ShouldBe("processed");
        rig.Core.Commits.ShouldHaveSingleItem().Items.ShouldHaveSingleItem().Content.ShouldContain(source);
        await rig.Journal.PurgeAsync(TimeSpan.Zero, CancellationToken.None);
        rig.Journal.Get(receipt.Id)!.Input.ShouldBeNull();
        rig.Core.Commits[0].Items[0].Content.ShouldContain("user");
    }

    [Fact]
    public async Task Equivalent_facets_from_one_source_produce_one_target_version()
    {
        using var rig = new Rig("The counter resets at midnight, including on leap days.", "suggestion");
        rig.Core.Records = [Record(rig.Core.Resolved.Uuid)];
        rig.Provider.Target = rig.Core.Records[0].Uuid;
        rig.Judge.Relation = "equivalent";
        rig.Provider.AdditionalCandidates = [new Candidate("Counter exception", "counter leap days", "The midnight reset also applies on leap days.", "A leap-day condition.", "fact", "suggestion", ["m1"], null, null, "This project")];
        var receipt = await rig.Run();
        receipt.Status.ShouldBe("processed");
        rig.Core.Commits.SelectMany(commit => commit.Items).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Equivalent_evidence_preserves_existing_confidence()
    {
        using var rig = new Rig("The counter resets at midnight.", "suggestion");
        rig.Core.Records = [Record(rig.Core.Resolved.Uuid) with { Confidence = 97, Status = "approved" }];
        rig.Provider.Target = rig.Core.Records[0].Uuid;
        rig.Judge.Relation = "equivalent";
        var receipt = await rig.Run();
        receipt.Status.ShouldBe("processed");
        var write = rig.Core.Commits.ShouldHaveSingleItem().Items.ShouldHaveSingleItem();
        write.Confidence.ShouldBe((short)97);
        write.Status.ShouldBe("approved");
    }

    [Fact]
    public async Task Coalesced_evidence_retains_each_sources_own_authority()
    {
        const string first = "I approve the midnight counter reset rule for future implementation.";
        const string second = "We agreed that the counter should reset at midnight in the future release.";
        using var rig = new Rig(first, "approved_intent");
        rig.Core.Records = [Record(rig.Core.Resolved.Uuid)];
        rig.Provider.Target = rig.Core.Records[0].Uuid;
        rig.Judge.Relation = "equivalent";
        rig.Provider.AdditionalCandidates = [new Candidate("Counter", "counter reset", "The counter resets at midnight.", "Equivalent approval.", "fact", "approved_intent", ["m2"], "m2", second, "This project")];
        var receipt = await rig.Workflow.AcceptAsync(new CaptureRequest("two-approvals", "Preserve both attributed approvals", [new Message("m1", "user", 1, first), new Message("m2", "user", 2, second)], new Selectors("synthetic/reviewer")), CancellationToken.None);
        await rig.Workflow.ProcessAsync(receipt.Id, CancellationToken.None);
        rig.Workflow.Get(receipt.Id)!.Status.ShouldBe("processed");
        var write = rig.Core.Commits.ShouldHaveSingleItem().Items.ShouldHaveSingleItem();
        write.Sources.Count.ShouldBe(2);
        var later = write.Sources.Single(source => source.Reference.EndsWith(":m2", StringComparison.Ordinal));
        later.Evidence!.AuthorityReference.ShouldBe(later.Reference);
        later.Evidence.AuthorityQuote.ShouldBe(second);
        write.Content.ShouldContain(first);
        write.Content.ShouldContain(second);
    }

    [Fact]
    public async Task Independent_authority_judgement_can_veto_extractor_promotion()
    {
        using var rig = new Rig("I approve the investigation into a future counter rule.", "approved_intent");
        rig.Judge.AuthoritySupport = "unsupported";
        var receipt = await rig.Run();
        receipt.ErrorClass.ShouldBe("authority_evidence_missing");
        rig.Core.Commits.ShouldBeEmpty();
    }

    [Fact]
    public async Task Recall_origin_and_caller_identity_distinguish_internal_passes_from_capture_comparison()
    {
        using var rig = new Rig("Maybe the counter resets at midnight.", "suggestion");
        rig.Judge.CoverageAnswer = "missing";
        var context = await rig.Workflow.ContextAsync(new ContextRequest("What is the counter rule?", new Selectors("synthetic/reviewer")), CancellationToken.None);
        rig.Core.Attributions.Count.ShouldBe(2);
        rig.Core.Attributions.ShouldAllBe(attribution => attribution.RecallPurpose == "service_retrieval" && attribution.CallerRequestId == context.RequestId);
        rig.Core.Attributions.Clear();
        var receipt = await rig.Run();
        receipt.Status.ShouldBe("processed");
        rig.Core.Attributions.ShouldNotBeEmpty();
        rig.Core.Attributions.ShouldAllBe(attribution => attribution.RecallPurpose == "capture_comparison" && attribution.CallerRequestId == receipt.Id);
    }

    [Fact]
    public async Task Canonical_redactor_covers_ingress_retrieved_material_model_output_journal_and_commit()
    {
        const string ingress = "syntheticIngress99231";
        const string retrieved = "syntheticRetrieved77319";
        const string generated = "syntheticGenerated55412";
        using var rig = new Rig("A counter proposal. DEPLOY_TOKEN=" + ingress, "suggestion");
        rig.Core.Records = [Record(rig.Core.Resolved.Uuid) with { Body = "Legacy note. DEPLOY_TOKEN=" + retrieved }];
        rig.Provider.CurrentCandidate = rig.Provider.CurrentCandidate with { Content = "Derived detail. DEPLOY_TOKEN=" + generated };
        var filter = new SmoothAiProductContextMemory.KnowledgeHost.SecretFilter(OperatingSystem.IsWindows() ? "python" : "python3", Path.Combine(AppContext.BaseDirectory, "redact.py"));
        var workflow = new Workflow(rig.Core, rig.Core, rig.Provider, rig.Judge, filter, rig.Journal);
        var receipt = await workflow.AcceptAsync(new CaptureRequest("redaction-boundaries", "Capture synthetic evidence", [new Message("m1", "user", 1, "A counter proposal. DEPLOY_TOKEN=" + ingress)], new Selectors("synthetic/reviewer")), CancellationToken.None);
        KnowledgeJson.Serialize(rig.Journal.Get(receipt.Id)).ShouldNotContain(ingress);
        await workflow.ProcessAsync(receipt.Id, CancellationToken.None);
        workflow.Get(receipt.Id)!.Status.ShouldBe("processed");
        var outputs = new[] { KnowledgeJson.Serialize(rig.Journal.Get(receipt.Id)), KnowledgeJson.Serialize(workflow.Get(receipt.Id)), KnowledgeJson.Serialize(rig.Core.Commits) }.Concat(rig.Provider.Inputs).Concat(rig.Judge.Inputs);
        foreach (var output in outputs)
            foreach (var marker in new[] { ingress, retrieved, generated }) output.ShouldNotContain(marker);
        rig.Core.Commits.ShouldHaveSingleItem().Items.ShouldHaveSingleItem().Content.ShouldContain("<redacted>");
    }

    [Fact]
    public async Task Preview_resolves_existing_group_without_mutation_and_predicts_evidence_attachment()
    {
        using var rig = new Rig("The counter resets at midnight.", "suggestion");
        rig.Core.Records = [Record(rig.Core.Resolved.Uuid)];
        rig.Provider.Target = rig.Core.Records[0].Uuid;
        rig.Judge.Relation = "equivalent";
        var receipt = await rig.Run(preview: true);
        receipt.Status.ShouldBe("processed");
        receipt.Changes.ShouldHaveSingleItem().Action.ShouldBe("attach_evidence");
        rig.Core.Commits.ShouldBeEmpty();
        rig.Core.ResolveCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Reopened_journal_replays_exact_pending_commit_without_new_provider_work()
    {
        using var rig = new Rig("Maybe we should add a rule.", "suggestion");
        rig.Core.FailAfterCommit = true;
        CaptureReceipt receipt = await rig.Run();
        rig.Core.Commits.ShouldHaveSingleItem();
        var job = rig.Journal.Get(receipt.Id)!;
        job.PendingCommit.ShouldNotBeNull();
        // Simulate the durable processing checkpoint recovered after a process kill.
        await rig.Journal.SaveAsync(job with { Status = "processing" }, CancellationToken.None);
        var reopened = new CaptureJournal(rig.DatabasePath);
        var workflow = new Workflow(rig.Core, rig.Core, rig.Provider, rig.Judge, new IdentitySecrets(), reopened);
        int calls = rig.Provider.Calls;
        await workflow.ProcessAsync(receipt.Id, CancellationToken.None);
        rig.Core.Commits.Count.ShouldBe(2);
        KnowledgeJson.Serialize(rig.Core.Commits[0]).ShouldBe(KnowledgeJson.Serialize(rig.Core.Commits[1]));
        rig.Provider.Calls.ShouldBe(calls);
        workflow.Get(receipt.Id)!.Status.ShouldBe("processed");
        workflow.Get(receipt.Id)!.Committed.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Restored_corpus_epoch_pauses_pending_commit_instead_of_replaying()
    {
        using var rig = new Rig("Maybe we should add a rule.", "suggestion");
        rig.Core.FailAfterCommit = true;
        var receipt = await rig.Run();
        var job = rig.Journal.Get(receipt.Id)!;
        await rig.Journal.SaveAsync(job with { Status = "processing" }, CancellationToken.None);
        rig.Core.State = rig.Core.State with { Epoch = Guid.NewGuid() };
        var reopened = new CaptureJournal(rig.DatabasePath);
        var workflow = new Workflow(rig.Core, rig.Core, rig.Provider, rig.Judge, new IdentitySecrets(), reopened);
        await workflow.ProcessAsync(receipt.Id, CancellationToken.None);
        workflow.Get(receipt.Id)!.ErrorClass.ShouldBe("corpus_epoch_changed");
        rig.Core.Commits.ShouldHaveSingleItem();
        await Should.ThrowAsync<KnowledgeException>(() => workflow.ClarifyAsync(receipt.Id, new ClarificationRequest("resume", [], new BudgetLimits()), CancellationToken.None));
    }

    [Fact]
    public async Task Explicit_restore_reconciliation_preserves_budget_and_replans_without_replaying_old_payload()
    {
        using var rig = new Rig("Maybe we should add a rule.", "suggestion");
        rig.Core.FailAfterCommit = true;
        var receipt = await rig.Run();
        var old = rig.Journal.Get(receipt.Id)!;
        var reservation = new Reservation(Guid.NewGuid(), 81, 32, 0.002m);
        old = old with { Budget = old.Budget with { Reservations = [reservation] } };
        await rig.Journal.SaveAsync(old, CancellationToken.None);
        var pending = old.PendingCommit!;
        rig.Core.State = rig.Core.State with { Epoch = Guid.NewGuid() };
        var observed = await rig.Workflow.GetAsync(receipt.Id, CancellationToken.None);
        observed!.ReconciliationRequired.ShouldNotBeNull();
        observed.ReconciliationRequired.PreviousCorpusEpoch.ShouldBe(old.Corpus!.Epoch);
        observed.ReconciliationRequired.ExpectedCorpusEpoch.ShouldBe(rig.Core.State.Epoch);
        observed.ReconciliationRequired.AffectedOperationKeys.ShouldContain(pending.OperationKey!);
        var request = new ReconciliationRequest("explicit-restore", observed.ReconciliationRequired.PreviousCorpusEpoch, observed.ReconciliationRequired.ExpectedCorpusEpoch, true);
        await rig.Workflow.ReconcileAsync(receipt.Id, request, CancellationToken.None);
        var current = rig.Journal.Get(receipt.Id)!;
        KnowledgeJson.Serialize(current.Budget).ShouldBe(KnowledgeJson.Serialize(old.Budget));
        current.Generation.ShouldBe(old.Generation + 1);
        current.PendingCommit.ShouldBeNull();
        current.Plan.ShouldBeNull();
        current.Candidates.ShouldBeNull();
        current.Reconciliations.ShouldNotBeNull();
        KnowledgeJson.Serialize(current.Reconciliations.ShouldHaveSingleItem().PreviousPendingCommit).ShouldBe(KnowledgeJson.Serialize(pending));
        var reopened = new CaptureJournal(rig.DatabasePath);
        var workflow = new Workflow(rig.Core, rig.Core, rig.Provider, rig.Judge, new IdentitySecrets(), reopened);
        var replay = await workflow.ReconcileAsync(receipt.Id, request, CancellationToken.None);
        replay.Reconciliations.ShouldHaveSingleItem();
        reopened.Get(receipt.Id)!.Generation.ShouldBe(current.Generation);
        int previousCalls = rig.Provider.Calls;
        await workflow.ProcessAsync(receipt.Id, CancellationToken.None);
        workflow.Get(receipt.Id)!.Status.ShouldBe("processed");
        rig.Provider.Calls.ShouldBeGreaterThan(previousCalls);
        rig.Core.Commits.Count.ShouldBe(2);
        rig.Core.Commits[1].OperationKey.ShouldNotBe(pending.OperationKey);
        rig.Core.Commits[1].ExpectedCorpusEpoch.ShouldBe(rig.Core.State.Epoch);
        reopened.Get(receipt.Id)!.Budget.Reservations.ShouldContain(item => item.Id == reservation.Id);
        await reopened.PurgeAsync(TimeSpan.Zero, CancellationToken.None);
        var purged = reopened.Get(receipt.Id)!;
        purged.Input.ShouldBeNull();
        var audit = purged.Reconciliations.ShouldHaveSingleItem();
        audit.PreviousPendingCommit.ShouldBeNull();
        audit.PreviousPlan.ShouldNotBeNull();
        audit.PreviousPlan.ShouldAllBe(change => change.Candidate.Content == "" && (change.Target == null || change.Target.Body == ""));
        audit.Summary.PendingOperationKey.ShouldBe(pending.OperationKey);
        audit.Summary.PreviousUsage.OutstandingReservations.ShouldBe(1);
    }

    [Fact]
    public async Task Restore_reconciliation_requires_explicit_acknowledgement_and_current_epoch()
    {
        using var rig = new Rig("Maybe we should add a rule.", "suggestion");
        var receipt = await rig.Run();
        var old = rig.Journal.Get(receipt.Id)!;
        rig.Core.State = rig.Core.State with { Epoch = Guid.NewGuid() };
        var request = new ReconciliationRequest("restore-guard", old.Corpus!.Epoch, rig.Core.State.Epoch);
        await Should.ThrowAsync<KnowledgeException>(() => rig.Workflow.ReconcileAsync(receipt.Id, request, CancellationToken.None));
        await Should.ThrowAsync<KnowledgeException>(() => rig.Workflow.ReconcileAsync(receipt.Id, request with { AcknowledgeRestore = true, ExpectedCorpusEpoch = Guid.NewGuid() }, CancellationToken.None));
        KnowledgeJson.Serialize(rig.Journal.Get(receipt.Id)).ShouldBe(KnowledgeJson.Serialize(old));
    }

    [Fact]
    public async Task Readonly_restore_detection_blocks_clarification_without_changing_allowance_or_journal()
    {
        using var rig = new Rig("Maybe we should add a rule.", "suggestion");
        var receipt = await rig.Run();
        var original = rig.Journal.Get(receipt.Id)!;
        rig.Core.State = rig.Core.State with { Epoch = Guid.NewGuid() };
        rig.Core.Commits.Clear();
        var observed = await rig.Workflow.GetAsync(receipt.Id, CancellationToken.None);
        observed!.Status.ShouldBe("partial");
        observed.ReconciliationRequired.ShouldNotBeNull();
        var error = await Should.ThrowAsync<KnowledgeException>(() => rig.Workflow.ClarifyAsync(receipt.Id, new ClarificationRequest("not-reconciliation", [], new BudgetLimits()), CancellationToken.None));
        error.Code.ShouldBe("reconciliation_required");
        KnowledgeJson.Serialize(rig.Journal.Get(receipt.Id)).ShouldBe(KnowledgeJson.Serialize(original));
        rig.Core.Commits.ShouldBeEmpty();
    }

    [Fact]
    public async Task Capture_post_replay_reports_restored_missing_commit_instead_of_stale_success()
    {
        using var rig = new Rig("Maybe we should add a rule.", "suggestion");
        var receipt = await rig.Run();
        var original = rig.Journal.Get(receipt.Id)!;
        rig.Core.State = rig.Core.State with { Epoch = Guid.NewGuid() };
        rig.Core.Commits.Clear();
        var replay = await rig.Workflow.AcceptAsync(original.Input!, CancellationToken.None);
        replay.Id.ShouldBe(receipt.Id);
        replay.Status.ShouldBe("partial");
        replay.ReconciliationRequired.ShouldNotBeNull();
        replay.ReconciliationRequired.PreviousCorpusEpoch.ShouldBe(original.Corpus!.Epoch);
        replay.ReconciliationRequired.ExpectedCorpusEpoch.ShouldBe(rig.Core.State.Epoch);
        replay.ReconciliationRequired.MissingOperationKeys.ShouldContain(original.Committed.ShouldHaveSingleItem().OperationKey);
        KnowledgeJson.Serialize(rig.Journal.Get(receipt.Id)).ShouldBe(KnowledgeJson.Serialize(original));
        rig.Core.Commits.ShouldBeEmpty();
    }

    [Fact]
    public async Task First_durable_acknowledgement_does_not_require_core_availability()
    {
        using var rig = new Rig("Maybe we should add a rule.", "suggestion");
        rig.Core.ThrowOnStateRead = true;
        var receipt = await rig.Workflow.AcceptAsync(new CaptureRequest("core-outage-ack", "Capture while core is unavailable", [new Message("m1", "user", 1, "Maybe a counter rule.")], new Selectors("synthetic/reviewer")), CancellationToken.None);
        receipt.Status.ShouldBe("received");
        rig.Journal.Get(receipt.Id)!.Input.ShouldNotBeNull();
        rig.Journal.Get(receipt.Id)!.Corpus.ShouldBeNull();
        rig.Provider.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Reconciliation_replay_after_another_restore_does_not_report_stale_success()
    {
        using var rig = new Rig("Maybe we should add a rule.", "suggestion");
        var receipt = await rig.Run();
        var firstEpoch = rig.Core.State.Epoch;
        rig.Core.State = rig.Core.State with { Epoch = Guid.NewGuid() };
        rig.Core.Commits.Clear();
        var request = new ReconciliationRequest("first-restore", firstEpoch, rig.Core.State.Epoch, true);
        await rig.Workflow.ReconcileAsync(receipt.Id, request, CancellationToken.None);
        await rig.Workflow.ProcessAsync(receipt.Id, CancellationToken.None);
        rig.Workflow.Get(receipt.Id)!.Status.ShouldBe("processed");
        var reconciledEpoch = rig.Core.State.Epoch;
        var original = rig.Journal.Get(receipt.Id)!;
        int calls = rig.Provider.Calls;
        rig.Core.State = rig.Core.State with { Epoch = Guid.NewGuid() };
        rig.Core.Commits.Clear();
        var replay = await rig.Workflow.ReconcileAsync(receipt.Id, request, CancellationToken.None);
        replay.Status.ShouldBe("partial");
        replay.ReconciliationRequired.ShouldNotBeNull();
        replay.ReconciliationRequired.PreviousCorpusEpoch.ShouldBe(reconciledEpoch);
        replay.ReconciliationRequired.ExpectedCorpusEpoch.ShouldBe(rig.Core.State.Epoch);
        KnowledgeJson.Serialize(rig.Journal.Get(receipt.Id)).ShouldBe(KnowledgeJson.Serialize(original));
        rig.Provider.Calls.ShouldBe(calls);
        rig.Core.Commits.ShouldBeEmpty();
    }

    [Fact]
    public async Task Purged_handoff_cannot_reconcile_from_missing_evidence()
    {
        using var rig = new Rig("Maybe we should add a rule.", "suggestion");
        var receipt = await rig.Run();
        var oldEpoch = rig.Journal.Get(receipt.Id)!.Corpus!.Epoch;
        await rig.Journal.PurgeAsync(TimeSpan.Zero, CancellationToken.None);
        rig.Core.State = rig.Core.State with { Epoch = Guid.NewGuid() };
        await Should.ThrowAsync<KnowledgeException>(() => rig.Workflow.ReconcileAsync(receipt.Id, new ReconciliationRequest("purged-restore", oldEpoch, rig.Core.State.Epoch, true), CancellationToken.None));
        rig.Core.Commits.Count.ShouldBe(1);
        rig.Journal.Get(receipt.Id)!.Input.ShouldBeNull();
    }

    [Fact]
    public async Task Document_approval_is_preserved_without_product_promotion()
    {
        const string source = "I approve the counter PRD as a design document. Its child midnight reset proposal remains Draft and unapproved; implementation acceptance remains open.";
        using var rig = new Rig(source, "document_approval");
        rig.Provider.CurrentCandidate = rig.Provider.CurrentCandidate with { Statement = "The counter PRD is approved as a document; its midnight reset child remains Draft.", Content = source };
        var receipt = await rig.Run();
        receipt.Status.ShouldBe("processed");
        var write = rig.Core.Commits.ShouldHaveSingleItem().Items.ShouldHaveSingleItem();
        write.Status.ShouldBe("proposed");
        write.Sources.ShouldHaveSingleItem().Evidence!.Category.ShouldBe("document_approval");
        write.Content.ShouldContain("Draft and unapproved");
    }

    [Fact]
    public async Task Mixed_existing_and_new_claims_keep_their_individual_write_actions()
    {
        using var rig = new Rig("The counter resets at midnight; a separate proposal adds an audit screen.", "suggestion");
        rig.Core.Records = [Record(rig.Core.Resolved.Uuid)];
        rig.Provider.Target = rig.Core.Records[0].Uuid;
        rig.Judge.Relations = new Queue<string>(["equivalent", "new"]);
        rig.Provider.AdditionalCandidates = [rig.Provider.CurrentCandidate with { Name = "Audit screen", Subject = "audit screen", Statement = "An audit screen is proposed." }];
        var receipt = await rig.Run();
        receipt.Status.ShouldBe("processed");
        var writes = rig.Core.Commits.ShouldHaveSingleItem().Items;
        writes.Count.ShouldBe(2);
        writes.Count(write => write.Uuid == rig.Provider.Target).ShouldBe(1);
        writes.Count(write => write.Uuid is null && write.CreateUuid is not null).ShouldBe(1);
    }

    [Fact]
    public async Task Failed_preflight_never_falls_through_to_unconditional_creates()
    {
        using var rig = new Rig("A separate proposal adds an audit screen.", "suggestion");
        rig.Core.Records = [Record(rig.Core.Resolved.Uuid)];
        rig.Provider.Target = rig.Core.Records[0].Uuid;
        rig.Judge.Relations = new Queue<string>(["equivalent", "new"]);
        rig.Provider.AdditionalCandidates = [rig.Provider.CurrentCandidate with { Name = "Audit screen", Subject = "audit screen", Statement = "An audit screen is proposed." }];
        rig.Core.RejectDryRun = true;
        var receipt = await rig.Run();
        receipt.Status.ShouldBe("partial");
        rig.Core.Commits.ShouldBeEmpty();
        var rejected = rig.Core.Preflights.ShouldHaveSingleItem();
        rejected.Items.Count.ShouldBe(2);
        rejected.Items.Count(write => write.Uuid == rig.Provider.Target).ShouldBe(1);
        rejected.Items.Count(write => write.Uuid is null).ShouldBe(1);
        rig.Journal.Get(receipt.Id)!.Plan.ShouldNotBeEmpty();
        rig.Journal.Get(receipt.Id)!.Input.ShouldNotBeNull();
    }

    [Fact]
    public async Task Concurrent_direct_writer_requires_fresh_comparison_without_losing_extraction_or_deferred_tail()
    {
        using var rig = new Rig("The counter resets at midnight; another proposal remains unexamined.", "suggestion");
        rig.Provider.HasMore = true;
        rig.Core.RejectDryRun = true;
        var receipt = await rig.Run();
        receipt.ErrorClass.ShouldBe("core_commit_400");
        var before = rig.Journal.Get(receipt.Id)!;
        var extracted = KnowledgeJson.Serialize(before.Candidates);
        var deferred = KnowledgeJson.Serialize(before.Deferred);
        int extractionCalls = rig.Provider.Calls;
        int spentCalls = before.Budget.Usage.Calls;
        rig.Core.Commits.ShouldBeEmpty();

        // A direct writer installs this claim after the first plan's rejected preflight.
        rig.Core.Records = [Record(rig.Core.Resolved.Uuid)];
        rig.Provider.Target = rig.Core.Records[0].Uuid;
        rig.Judge.Relation = "equivalent";
        rig.Core.State = rig.Core.State with { Revision = rig.Core.State.Revision + 1 };
        rig.Core.RejectDryRun = false;
        var reopened = new CaptureJournal(rig.DatabasePath);
        var workflow = new Workflow(rig.Core, rig.Core, rig.Provider, rig.Judge, new IdentitySecrets(), reopened);
        await workflow.ClarifyAsync(receipt.Id, new ClarificationRequest("refresh-comparison", [], new BudgetLimits(0, 0, 0, 0, 60)), CancellationToken.None);
        await workflow.ProcessAsync(receipt.Id, CancellationToken.None);

        var after = reopened.Get(receipt.Id)!;
        after.Status.ShouldBe("needs_input");
        KnowledgeJson.Serialize(after.Candidates).ShouldBe(extracted);
        KnowledgeJson.Serialize(after.Deferred).ShouldBe(deferred);
        after.Budget.Usage.Calls.ShouldBeGreaterThan(spentCalls);
        // Equivalent target selection uses one generative comparison, but no second extraction.
        rig.Provider.Calls.ShouldBe(extractionCalls + 1);
        var write = rig.Core.Commits.ShouldHaveSingleItem().Items.ShouldHaveSingleItem();
        write.Uuid.ShouldBe(rig.Provider.Target);
        write.CreateUuid.ShouldBeNull();
        rig.Core.Commits[0].ExpectedCorpusRevision.ShouldBe(rig.Core.State.Revision);
    }

    [Fact]
    public async Task Stronger_extraction_repair_cannot_bypass_atomicity_rejection()
    {
        using var rig = new Rig("Refunds require review; exports require encryption.", "suggestion");
        var detector = new RejectBundledAtomicity();
        var workflow = new Workflow(rig.Core, rig.Core, rig.Provider, rig.Judge, new IdentitySecrets(), rig.Journal, atomicity: detector);
        var receipt = await workflow.AcceptAsync(new CaptureRequest("bundled", "Preserve independently meaningful claims", [new Message("m1", "user", 1, "Refunds require review; exports require encryption.")], new Selectors("synthetic/reviewer")), CancellationToken.None);
        await workflow.ProcessAsync(receipt.Id, CancellationToken.None);
        rig.Core.Commits.ShouldBeEmpty();
        workflow.Get(receipt.Id)!.ErrorClass.ShouldBe("non_atomic_claims");
        detector.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task Extraction_over_one_hundred_claims_is_explicitly_rejected_without_silent_truncation()
    {
        using var rig = new Rig("Synthetic source containing many independent proposals.", "suggestion");
        rig.Provider.AdditionalCandidates = Enumerable.Range(1, 100).Select(index => rig.Provider.CurrentCandidate with { Subject = "claim " + index }).ToArray();
        var receipt = await rig.Run();
        receipt.ErrorClass.ShouldBe("claim_batch_limit");
        receipt.Status.ShouldBe("partial");
        rig.Core.Commits.ShouldBeEmpty();
        rig.Journal.Get(receipt.Id)!.Input.ShouldNotBeNull();
    }

    [Fact]
    public async Task Explicit_extraction_overflow_keeps_receipt_incomplete_and_source_durable()
    {
        using var rig = new Rig("Synthetic source containing more than twenty claims.", "suggestion");
        rig.Provider.HasMore = true;
        var receipt = await rig.Run();
        receipt.Status.ShouldBe("needs_input");
        receipt.Deferred.ShouldNotBeEmpty();
        rig.Core.Commits.ShouldHaveSingleItem();
        await rig.Journal.PurgeAsync(TimeSpan.Zero, CancellationToken.None);
        rig.Journal.Get(receipt.Id)!.Input.ShouldNotBeNull();
    }

    [Fact]
    public async Task Twenty_five_mixed_claims_survive_budget_interruptions_and_commit_only_after_full_comparison()
    {
        using var rig = new Rig("Synthetic source with one known counter claim and twenty-four independent new proposals.", "suggestion");
        rig.Core.Records = [Record(rig.Core.Resolved.Uuid)];
        rig.Provider.Target = rig.Core.Records[0].Uuid;
        rig.Provider.AdditionalCandidates = Enumerable.Range(1, 24).Select(index => rig.Provider.CurrentCandidate with { Name = "New proposal " + index, Subject = "new proposal " + index, Statement = "Independent proposal " + index }).ToArray();
        rig.Judge.Relations = new Queue<string>(new[] { "equivalent" }.Concat(Enumerable.Repeat("new", 24)));
        var receipt = await rig.Run();
        receipt.Status.ShouldBe("partial");
        receipt.ErrorClass.ShouldBe("budget_exhausted");
        rig.Journal.Get(receipt.Id)!.Candidates!.Count.ShouldBe(25);
        int firstIndex = rig.Journal.Get(receipt.Id)!.ComparisonIndex;
        firstIndex.ShouldBeGreaterThan(0);
        firstIndex.ShouldBeLessThan(25);
        rig.Core.Commits.ShouldBeEmpty();
        var reopened = new CaptureJournal(rig.DatabasePath);
        var workflow = new Workflow(rig.Core, rig.Core, rig.Provider, rig.Judge, new IdentitySecrets(), reopened);
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            await workflow.ClarifyAsync(receipt.Id, new ClarificationRequest("continue-budget-" + attempt, [], new BudgetLimits()), CancellationToken.None);
            await workflow.ProcessAsync(receipt.Id, CancellationToken.None);
            if (attempt == 1)
            {
                workflow.Get(receipt.Id)!.Status.ShouldBe("partial");
                rig.Core.Commits.ShouldBeEmpty();
                reopened.Get(receipt.Id)!.ComparisonIndex.ShouldBeGreaterThan(firstIndex);
            }
        }
        workflow.Get(receipt.Id)!.Status.ShouldBe("processed");
        workflow.Get(receipt.Id)!.Usage.Calls.ShouldBeGreaterThan(24);
        var writes = rig.Core.Commits.ShouldHaveSingleItem().Items;
        writes.Count.ShouldBe(25);
        writes.Count(write => write.Uuid == rig.Provider.Target).ShouldBe(1);
        writes.Count(write => write.Uuid is null && write.CreateUuid is not null).ShouldBe(24);
        reopened.Get(receipt.Id)!.ComparisonIndex.ShouldBe(25);
    }

    [Fact]
    public async Task Legacy_bodyless_record_can_participate_in_equivalent_comparison()
    {
        using var rig = new Rig("The counter resets at midnight.", "suggestion");
        rig.Core.Records = [Record(rig.Core.Resolved.Uuid) with { Body = "", HasBody = false }];
        rig.Core.ThrowOnBodyFetch = true;
        rig.Provider.Target = rig.Core.Records[0].Uuid;
        rig.Judge.Relation = "equivalent";
        var receipt = await rig.Run();
        receipt.Status.ShouldBe("processed");
        receipt.Changes.ShouldHaveSingleItem().Action.ShouldBe("attach_evidence");
    }

    private static Evidence Record(Guid group) => new(Guid.NewGuid(), group, 1, "Counter", "counter reset", "The counter resets at midnight.", "Counter reset", "fact", "proposed", 50, "product", null, DateTimeOffset.UtcNow.AddDays(-1), null, true, [], [], [], DateTimeOffset.UtcNow.AddDays(-1), "The counter resets at midnight.");

    private sealed class Rig : IDisposable
    {
        public string DatabasePath { get; } = Path.Combine(Path.GetTempPath(), "mimi-independent-" + Guid.NewGuid().ToString("N") + ".db");
        public FakeCore Core { get; } = new();
        public FakeProvider Provider { get; }
        public FakeJev Judge { get; } = new();
        public CaptureJournal Journal { get; }
        public Workflow Workflow { get; }
        private readonly string text;
        public Rig(string text, string category, string? authorityQuote = null)
        {
            this.text = text;
            Provider = new FakeProvider(new Candidate("Counter", "counter reset", "The counter resets at midnight.", "A complete counter rule.", "fact", category, ["m1"], "m1", authorityQuote ?? text, "This project"));
            Journal = new CaptureJournal(DatabasePath);
            Workflow = new Workflow(Core, Core, Provider, Judge, new IdentitySecrets(), Journal);
        }
        public async Task<CaptureReceipt> Run(bool preview = false)
        {
            var receipt = await Workflow.AcceptAsync(new CaptureRequest("test", "Review synthetic behavior", [new Message("m1", "user", 1, text)], new Selectors("synthetic/reviewer"), Preview: preview), CancellationToken.None);
            await Workflow.ProcessAsync(receipt.Id, CancellationToken.None);
            return Workflow.Get(receipt.Id)!;
        }
        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (string suffix in new[] { "", "-wal", "-shm" }) File.Delete(DatabasePath + suffix);
        }
    }

    private sealed class IdentitySecrets : ISecretFilter
    {
        public Task<T> SanitizeAsync<T>(T value, CancellationToken ct) => Task.FromResult(value);
    }
    private sealed class RejectBundledAtomicity : IAtomicityDetector
    {
        public int Calls { get; private set; }
        public Task ValidateAsync(IReadOnlyList<Candidate> candidates, CancellationToken ct)
        {
            Calls++;
            throw new KnowledgeException("non_atomic_claims");
        }
    }
    private sealed class FakeProvider(Candidate candidate) : IGenerativeProvider
    {
        public string Model => "independent-fixture";
        public int Calls { get; private set; }
        public List<string> Inputs { get; } = [];
        public Guid? Target { get; set; }
        public Candidate CurrentCandidate { get; set; } = candidate;
        public bool HasMore { get; set; }
        public IReadOnlyList<Candidate> AdditionalCandidates { get; set; } = [];
        public Task<ProviderResult<T>> GenerateAsync<T>(string instructions, object data, int outputLimit, bool stronger, CancellationToken ct)
        {
            Calls++;
            Inputs.Add(KnowledgeJson.Serialize(data));
            object result = typeof(T) == typeof(Extraction) ? new Extraction([CurrentCandidate, .. AdditionalCandidates], HasMore: HasMore) : typeof(T) == typeof(Comparison) ? new Comparison("equivalent", Target, "Fixture equivalent") : typeof(T) == typeof(Intent) ? new Intent(["What is the counter rule?"], ["counter", "midnight"]) : throw new InvalidOperationException("Unexpected provider operation.");
            return Task.FromResult(new ProviderResult<T>((T)result, Model, 10, 10));
        }
    }
    private sealed class FakeJev : IJevProvider
    {
        public string Model => "independent-fixture";
        public string Relation { get; set; } = "new";
        public string AuthoritySupport { get; set; } = "supported";
        public string CoverageAnswer { get; set; } = "supported";
        public Queue<string> Relations { get; set; } = new();
        public List<string> Inputs { get; } = [];
        public Task<ProviderResult<IReadOnlyDictionary<string, string>>> ChoicesAsync(object state, IReadOnlyDictionary<string, ChoiceQuestion> questions, CancellationToken ct)
        {
            Inputs.Add(KnowledgeJson.Serialize(state));
            IReadOnlyDictionary<string, string> answers = questions.ToDictionary(q => q.Key, q => q.Key == "relation" ? (Relations.TryDequeue(out var next) ? next : Relation) : q.Key == "authority_support" ? AuthoritySupport : CoverageAnswer);
            return Task.FromResult(new ProviderResult<IReadOnlyDictionary<string, string>>(answers, Model, 10, 10));
        }
    }
    private sealed class FakeCore : ICoreReader, ICoreCommitter
    {
        public Group Resolved { get; set; } = new(Guid.NewGuid(), false, "product", null, "synthetic/reviewer", "default", []);
        public IReadOnlyList<Group>? Groups { get; set; }
        public IReadOnlyList<Evidence> Records { get; set; } = [];
        public List<CommitRequest> Commits { get; } = [];
        public List<CommitRequest> Preflights { get; } = [];
        public List<RecallAttribution> Attributions { get; } = [];
        public bool FailAfterCommit { get; set; }
        public bool ThrowOnBodyFetch { get; set; }
        public bool RejectDryRun { get; set; }
        public bool ThrowOnStateRead { get; set; }
        public int ResolveCalls { get; private set; }
        public CorpusState State { get; set; } = new(Guid.NewGuid(), 1);
        public Task<CorpusState> StateAsync(CancellationToken ct) => ThrowOnStateRead ? throw new HttpRequestException("Synthetic core unavailable") : Task.FromResult(State);
        public Task<IReadOnlyList<Group>> LookupAsync(Selectors selectors, CancellationToken ct) => Task.FromResult(Groups ?? (IReadOnlyList<Group>)[Resolved]);
        public Task<IReadOnlyList<Evidence>> SearchAsync(string query, Selectors selectors, DateTimeOffset asOf, bool historical, CancellationToken ct) => Task.FromResult(Records);
        public Task<IReadOnlyList<Evidence>> SearchAsync(string query, Selectors selectors, DateTimeOffset asOf, bool historical, CancellationToken ct, RecallAttribution attribution) { Attributions.Add(attribution); return Task.FromResult(Records); }
        public Task<IReadOnlyList<Evidence>> NeighborsAsync(Evidence seed, int depth, int limit, CancellationToken ct) => Task.FromResult<IReadOnlyList<Evidence>>([]);
        public Task<string> BodyAsync(Evidence record, CancellationToken ct) => ThrowOnBodyFetch ? throw new KnowledgeException("core_body_404") : Task.FromResult(record.Body);
        public Task<bool> HasOperationAsync(string key, CancellationToken ct) => Task.FromResult(Commits.Any(c => c.OperationKey == key));
        public Task<Group> ResolveAsync(Selectors selectors, string task, string operationKey, CorpusState corpus, CancellationToken ct) { ResolveCalls++; return Task.FromResult(Resolved); }
        public Task<CommitResult> CommitAsync(CommitRequest request, CancellationToken ct)
        {
            if (request.DryRun) Preflights.Add(request);
            if (request.DryRun && RejectDryRun) throw new KnowledgeException("core_commit_400");
            var result = new CommitResult(1, 0, 0, 0, 0, 0, [new CommitItem(request.Items[0].CreateUuid ?? request.Items[0].Uuid, null, request.Items[0].Uuid is not null)]);
            if (request.DryRun) return Task.FromResult(result);
            Commits.Add(request);
            if (FailAfterCommit) { FailAfterCommit = false; throw new HttpRequestException("Synthetic lost response"); }
            return Task.FromResult(result);
        }
    }
}
