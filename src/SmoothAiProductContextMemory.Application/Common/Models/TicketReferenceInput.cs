namespace SmoothAiProductContextMemory.Application.Common.Models;

/// <summary>
/// A request-level ticket reference: a memory (or a whole batch) declares it is about a ticket it
/// does not own. <see cref="Reason"/> is optional context for the <c>ABOUT</c> edge; callers that do
/// not supply one use a default (see <c>SetMemories</c>).
/// </summary>
public sealed record TicketReferenceInput(string Provider, string Key, string? Reason = null);
