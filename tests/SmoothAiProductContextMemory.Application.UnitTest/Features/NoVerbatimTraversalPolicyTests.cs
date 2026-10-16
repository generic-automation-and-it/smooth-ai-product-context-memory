using FluentValidation.TestHelper;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Application.Common.Models;
using SmoothAiProductContextMemory.Application.Features.Links;
using SmoothAiProductContextMemory.Application.Features.Tickets;

namespace SmoothAiProductContextMemory.Application.UnitTest.Features;

/// <summary>
/// The no-verbatim-traversal policy (HLD-003): traversal input from callers is never executed verbatim
/// against the graph engine. These tests pin the absence that makes a future expression-input feature a
/// deliberate change, not a silent one — depth and bound parameters are validated, and the request
/// surfaces expose no raw query/expression field for a caller's Cypher to reach.
/// </summary>
public class NoVerbatimTraversalPolicyTests
{
    private readonly FindTicketPaths.Validator _ticketValidator = new();

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(6)]
    public void Ticket_traversal_rejects_depth_outside_the_bound(int maxDepth)
    {
        var result = _ticketValidator.TestValidate(Request() with { MaxDepth = maxDepth });
        result.ShouldHaveValidationErrorFor(x => x.MaxDepth);
    }

    [Fact]
    public void Ticket_traversal_rejects_a_cypher_fragment_as_a_direction()
    {
        // Direction is enumerated, so a caller-supplied Cypher fragment can never become part of a
        // statement; it is refused at the request boundary rather than reaching the graph engine.
        var result = _ticketValidator.TestValidate(Request() with
        {
            Direction = "outbound) MATCH (n) DETACH DELETE n //",
        });
        result.ShouldHaveValidationErrorFor(x => x.Direction);
    }

    [Fact]
    public void Ticket_traversal_rejects_a_null_control_character_in_kind()
    {
        var result = _ticketValidator.TestValidate(Request() with { Kind = "decision\0injected" });
        result.ShouldHaveValidationErrorFor(x => x.Kind);
    }

    [Fact]
    public void Ticket_traversal_rejects_zero_and_over_cap_limits()
    {
        var result = _ticketValidator.TestValidate(Request() with
        {
            PathLimit = 0,
            MemoryLimit = MemorySearchDefaults.MaxLimit + 1,
        });
        result.ShouldHaveValidationErrorFor(x => x.PathLimit);
        result.ShouldHaveValidationErrorFor(x => x.MemoryLimit);
    }

    [Fact]
    public void Traversal_requests_expose_no_raw_query_or_cypher_field()
    {
        // The no-verbatim rule in its testable form: neither traversal request has a property a caller
        // could use to supply a raw query/expression. If one is ever added, this pins the moment a new
        // expression-input entry point was introduced, so it must face the opt-in + default-off flag.
        string[] forbidden = ["query", "cypher", "expression", "where", "condition"];

        TicketRequestRawFields(typeof(FindTicketPaths.Request), forbidden).ShouldBeEmpty();
        TicketRequestRawFields(typeof(FindPaths.Request), forbidden).ShouldBeEmpty();
    }

    private static IEnumerable<string> TicketRequestRawFields(Type request, IReadOnlyList<string> forbidden) =>
        request.GetProperties()
            .Select(p => p.Name)
            .Where(name => forbidden.Any(key => name.Contains(key, StringComparison.OrdinalIgnoreCase)));

    private static FindTicketPaths.Request Request() =>
        new(new TicketIdentity("jira", "GPA-1"), MaxDepth: 3);
}
