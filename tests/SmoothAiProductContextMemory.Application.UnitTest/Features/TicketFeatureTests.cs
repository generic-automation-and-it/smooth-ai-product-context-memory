using System.Text.Json;
using FluentValidation.TestHelper;
using Microsoft.Extensions.Logging.Abstractions;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Application.Features.Tickets;

namespace SmoothAiProductContextMemory.Application.UnitTest.Features;

public sealed class TicketFeatureTests
{
    private static readonly TicketIdentity Child = new("jira", "APP-2");
    private static readonly TicketIdentity Parent = new("jira", "APP-1");
    private readonly SetTicketParent.Validator _parentValidator = new();
    private readonly FindTicketPaths.Validator _pathsValidator = new();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("invalid\0value")]
    public void Parent_change_rejects_empty_identity_and_declaration_fields(string? value)
    {
        _parentValidator.TestValidate(Change() with { Child = new(value!, "key") })
            .ShouldHaveValidationErrorFor("Child.Provider");
        _parentValidator.TestValidate(Change() with { Parent = new("provider", value!) })
            .ShouldHaveValidationErrorFor("Parent.Key");
        _parentValidator.TestValidate(Change() with { ExpectedParent = new(value!, "key") })
            .ShouldHaveValidationErrorFor("ExpectedParent.Provider");
        _parentValidator.TestValidate(Change() with { Reason = value! }).ShouldHaveValidationErrorFor(x => x.Reason);
        _parentValidator.TestValidate(Change() with { Source = value! }).ShouldHaveValidationErrorFor(x => x.Source);
    }

    [Fact]
    public void Parent_change_bounds_all_identity_and_declaration_strings()
    {
        TicketIdentity longProvider = new(new string('p', 513), "key");
        TicketIdentity longKey = new("provider", new string('k', 513));
        foreach (TicketIdentity identity in new[] { longProvider, longKey })
        {
            _parentValidator.TestValidate(Change() with { Child = identity }).IsValid.ShouldBeFalse();
            _parentValidator.TestValidate(Change() with { Parent = identity }).IsValid.ShouldBeFalse();
            _parentValidator.TestValidate(Change() with { ExpectedParent = identity }).IsValid.ShouldBeFalse();
        }

        _parentValidator.TestValidate(Change() with { Reason = new string('r', 4001) })
            .ShouldHaveValidationErrorFor(x => x.Reason);
        _parentValidator.TestValidate(Change() with { Source = new string('s', 4001) })
            .ShouldHaveValidationErrorFor(x => x.Source);
        _parentValidator.TestValidate(Change() with
        {
            Child = new(new string('p', 512), new string('k', 512)),
            Reason = new string('r', 4000),
            Source = new string('s', 4000),
        }).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Parent_change_rejects_missing_child_and_self_parent_but_preserves_exact_identity()
    {
        _parentValidator.TestValidate(Change() with { Child = null! }).ShouldHaveValidationErrorFor(x => x.Child);
        _parentValidator.TestValidate(Change() with { Parent = Child with { } })
            .ShouldHaveValidationErrorFor(x => x.Parent);
        _parentValidator.TestValidate(Change() with { Parent = Child with { Provider = "JIRA" } })
            .ShouldNotHaveAnyValidationErrors();
        _parentValidator.TestValidate(Change() with { Parent = Child with { Key = " APP-2 " } })
            .ShouldNotHaveAnyValidationErrors();
        _parentValidator.TestValidate(Change() with { Parent = null, ExpectedParent = Parent })
            .ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Removal_requires_explicit_parent_null_on_the_wire()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<SetTicketParent.Request>(
            """{"child":{"provider":"jira","key":"APP-2"},"reason":"remove","source":"user"}""", options));
        SetTicketParent.Request? request = JsonSerializer.Deserialize<SetTicketParent.Request>(
            """{"child":{"provider":"jira","key":"APP-2"},"parent":null,"reason":"remove","source":"user"}""", options);
        request.ShouldNotBeNull().Parent.ShouldBeNull();
        request.ExpectedParent.ShouldBeNull();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(6)]
    public void Paths_reject_unusable_depth(int depth) =>
        _pathsValidator.TestValidate(new FindTicketPaths.Request(Child, depth)).ShouldHaveValidationErrorFor(x => x.MaxDepth);

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void Paths_accept_depth_boundaries(int depth) =>
        _pathsValidator.TestValidate(new FindTicketPaths.Request(Child, depth)).ShouldNotHaveAnyValidationErrors();

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(201)]
    public void Paths_reject_unusable_caps(int limit)
    {
        _pathsValidator.TestValidate(new FindTicketPaths.Request(Child, 2, PathLimit: limit)).ShouldHaveValidationErrorFor(x => x.PathLimit);
        _pathsValidator.TestValidate(new FindTicketPaths.Request(Child, 2, MemoryLimit: limit)).ShouldHaveValidationErrorFor(x => x.MemoryLimit);
    }

    [Theory]
    [InlineData("sideways")]
    [InlineData("Outbound")]
    [InlineData("")]
    public void Paths_reject_unknown_direction(string direction) =>
        _pathsValidator.TestValidate(new FindTicketPaths.Request(Child, 2, Direction: direction)).ShouldHaveValidationErrorFor(x => x.Direction);

    [Fact]
    public void Paths_require_identity_and_bound_optional_filters()
    {
        _pathsValidator.TestValidate(new FindTicketPaths.Request(null!, 2)).ShouldHaveValidationErrorFor(x => x.Anchor);
        _pathsValidator.TestValidate(new FindTicketPaths.Request(new("", "key"), 2)).ShouldHaveValidationErrorFor("Anchor.Provider");
        _pathsValidator.TestValidate(new FindTicketPaths.Request(new("provider", " "), 2)).ShouldHaveValidationErrorFor("Anchor.Key");
        _pathsValidator.TestValidate(new FindTicketPaths.Request(new(new string('p', 513), "key"), 2))
            .ShouldHaveValidationErrorFor("Anchor.Provider");
        _pathsValidator.TestValidate(new FindTicketPaths.Request(new("provider", new string('k', 513)), 2))
            .ShouldHaveValidationErrorFor("Anchor.Key");
        _pathsValidator.TestValidate(new FindTicketPaths.Request(Child, 2, ScopeDimension: new string('s', 33)))
            .ShouldHaveValidationErrorFor(x => x.ScopeDimension);
        _pathsValidator.TestValidate(new FindTicketPaths.Request(Child, 2, Kind: new string('k', 65)))
            .ShouldHaveValidationErrorFor(x => x.Kind);
        _pathsValidator.TestValidate(new FindTicketPaths.Request(Child, 2, PathLimit: 200, MemoryLimit: 200))
            .ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Missing_wire_depth_stays_zero_and_is_rejected()
    {
        FindTicketPaths.Request request = JsonSerializer.Deserialize<FindTicketPaths.Request>(
            """{"anchor":{"provider":"jira","key":"APP-2"}}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        request.MaxDepth.ShouldBe(0);
        request.PathLimit.ShouldBe(50);
        request.MemoryLimit.ShouldBe(50);
        _pathsValidator.TestValidate(request).ShouldHaveValidationErrorFor(x => x.MaxDepth);
    }

    [Fact]
    public void Wire_depth_requires_a_json_number() =>
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<FindTicketPaths.Request>(
            """{"anchor":{"provider":"jira","key":"APP-2"},"maxDepth":"2"}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    [Theory]
    [InlineData(null, null, true)]
    [InlineData("product", "product", true)]
    [InlineData("customer", "customer", true)]
    [InlineData("self", "self", true)]
    [InlineData("program", "program", false)]
    public async Task Paths_separate_hop_visibility_from_memory_narrowing_without_ticket_consent(
        string? scope, string? required, bool hidesProgram)
    {
        var graph = new RecordingGraph();
        var handler = new FindTicketPaths.Handler(graph, NullLogger<FindTicketPaths.Handler>.Instance);
        TicketTraversalResult result = await handler.Handle(new(Child, 3, ScopeDimension: scope), TestContext.Current.CancellationToken);
        graph.Query.ShouldNotBeNull().RequiredScopeDimension.ShouldBe(required);
        graph.Query.HiddenDimensions.ShouldBe(hidesProgram ? ["program"] : Array.Empty<string>());
        graph.Query.Anchor.ShouldBe(Child);
        result.ShouldBeSameAs(graph.Result);
        graph.Token.ShouldBe(TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(null, TraversalDirection.Outbound)]
    [InlineData("outbound", TraversalDirection.Outbound)]
    [InlineData("inbound", TraversalDirection.Inbound)]
    [InlineData("either", TraversalDirection.Either)]
    public async Task Paths_forward_bounds_direction_and_kind(string? direction, TraversalDirection expected)
    {
        var graph = new RecordingGraph();
        var handler = new FindTicketPaths.Handler(graph, NullLogger<FindTicketPaths.Handler>.Instance);
        await handler.Handle(new(Child, 5, direction, Kind: "decision", PathLimit: 7, MemoryLimit: 9),
            TestContext.Current.CancellationToken);
        graph.Query.ShouldNotBeNull().Direction.ShouldBe(expected);
        graph.Query.MaxDepth.ShouldBe(5);
        graph.Query.Kind.ShouldBe("decision");
        graph.Query.PathLimit.ShouldBe(7);
        graph.Query.MemoryLimit.ShouldBe(9);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Parent_change_forwards_declaration_and_changed_receipt(bool changed)
    {
        var graph = new RecordingGraph { Changed = changed };
        var handler = new SetTicketParent.Handler(graph, NullLogger<SetTicketParent.Handler>.Instance);
        SetTicketParent.Request request = Change() with { ObservedAt = DateTimeOffset.UtcNow, ExpectedParent = new("jira", "APP-0") };
        SetTicketParent.Response response = await handler.Handle(request, TestContext.Current.CancellationToken);
        response.Changed.ShouldBe(changed);
        graph.Change.ShouldBe(new TicketParentChange(Child, Parent, request.ExpectedParent, request.Reason, request.Source, request.ObservedAt));
        graph.Token.ShouldBe(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Parent_removal_preserves_null_parent_and_expected_identity()
    {
        var graph = new RecordingGraph { Changed = true };
        var handler = new SetTicketParent.Handler(graph, NullLogger<SetTicketParent.Handler>.Instance);
        await handler.Handle(Change() with { Parent = null, ExpectedParent = Parent }, TestContext.Current.CancellationToken);
        graph.Change.ShouldNotBeNull().Parent.ShouldBeNull();
        graph.Change.ExpectedParent.ShouldBe(Parent);
    }

    private static SetTicketParent.Request Change() => new(Child, Parent, null, "Declared hierarchy", "Practitioner");

    private sealed class RecordingGraph : ITicketGraph
    {
        public TicketTraversalQuery? Query { get; private set; }
        public TicketParentChange? Change { get; private set; }
        public CancellationToken Token { get; private set; }
        public bool Changed { get; init; }
        public TicketTraversalResult Result { get; } = new([], [], new(3, 50, 50, false, false, false));

        public Task LockAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool> ChangeParentAsync(TicketParentChange change, CancellationToken cancellationToken)
        {
            Change = change;
            Token = cancellationToken;
            return Task.FromResult(Changed);
        }

        public Task<TicketTraversalResult> TraverseAsync(TicketTraversalQuery query, CancellationToken cancellationToken)
        {
            Query = query;
            Token = cancellationToken;
            return Task.FromResult(Result);
        }
    }
}
