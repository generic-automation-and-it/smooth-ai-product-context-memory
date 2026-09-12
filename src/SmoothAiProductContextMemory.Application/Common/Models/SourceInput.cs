namespace SmoothAiProductContextMemory.Application.Common.Models;

public sealed record SourceInput(string Kind, string Reference, DateTimeOffset? CapturedAt);
