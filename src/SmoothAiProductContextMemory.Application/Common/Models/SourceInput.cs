using NodaTime;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Application.Common.Models;

public sealed record SourceInput(string Kind, string Reference, Instant? CapturedAt, EvidenceDocument? Evidence = null);
