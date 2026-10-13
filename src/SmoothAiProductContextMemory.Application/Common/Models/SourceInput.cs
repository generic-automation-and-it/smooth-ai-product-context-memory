using NodaTime;
namespace SmoothAiProductContextMemory.Application.Common.Models;

public sealed record SourceInput(string Kind, string Reference, Instant? CapturedAt);
