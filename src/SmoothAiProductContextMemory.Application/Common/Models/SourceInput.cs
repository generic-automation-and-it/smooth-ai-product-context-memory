using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Application.Common.Models;

public sealed record SourceInput(string Kind, string Reference, DateTimeOffset? CapturedAt, EvidenceDocument? Evidence = null);
