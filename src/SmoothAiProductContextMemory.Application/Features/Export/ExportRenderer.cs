using System.Globalization;
using System.Text;

namespace SmoothAiProductContextMemory.Application.Features.Export;

/// <summary>
/// Hand-rolled Markdown + YAML frontmatter. Closed key list, fixed order, LF scaffolding.
/// </summary>
public static class ExportRenderer
{
    public static string RenderGroup(ExportGroupDocument group, bool includeHistory)
    {
        var yaml = new List<(string Key, string Value, bool Literal)>
        {
            ("generated", "true", true),
            ("uuid", group.Uuid.ToString("D"), true),
            ("scope", group.ScopeDimension, false),
            ("scope_identifier", group.ScopeIdentifier ?? "null", group.ScopeIdentifier is null),
            ("initiative", group.InitiativeName, false),
            ("initiative_status", group.InitiativeStatus, false),
            ("repo", group.Repo ?? "null", group.Repo is null),
            ("repo_url", group.RepoUrl ?? "null", group.RepoUrl is null),
        };

        var builder = new StringBuilder();
        WriteFrontMatter(builder, yaml, extra: w =>
        {
            w.Append("tickets:");
            if (group.Tickets.Count == 0)
            {
                w.AppendLine(" []");
                return;
            }

            w.AppendLine();
            foreach (ExportTicket ticket in group.Tickets)
            {
                w.Append("  - provider: ").AppendLine(YamlScalar(ticket.Provider));
                w.Append("    key: ").AppendLine(YamlScalar(ticket.Key));
                w.Append("    url: ").AppendLine(YamlScalar(ticket.Url));
            }
        });

        builder.AppendLine(ExportScopeMarks.GeneratedComment);
        builder.AppendLine();
        WriteBanner(builder, group.ScopeDimension);

        string heading = group.CurrentDescription?.Name ?? $"group-{group.Uuid:D}";
        builder.Append("# ").AppendLine(heading);
        builder.AppendLine();
        builder.AppendLine($"group_uuid: `{group.Uuid:D}`");
        builder.AppendLine();

        if (group.CurrentDescription is not null)
        {
            builder.AppendLine(group.CurrentDescription.Body);
            builder.AppendLine();
        }

        if (includeHistory)
        {
            foreach (ExportGroupDescription historic in group.HistoricalDescriptions)
            {
                builder.AppendLine($"## Description version {historic.Version}");
                builder.AppendLine();
                builder.AppendLine($"name: {historic.Name}");
                builder.AppendLine($"created_on: {FormatTimestamp(historic.CreatedOn)}");
                builder.AppendLine();
                builder.AppendLine(historic.Body);
                builder.AppendLine();
            }
        }

        return Finish(builder);
    }

    public static string RenderMemory(ExportMemoryDocument memory, bool includeHistory)
    {
        ExportVersionBody current = memory.Current;
        var yaml = new List<(string Key, string Value, bool Literal)>
        {
            ("generated", "true", true),
            ("uuid", memory.Uuid.ToString("D"), true),
            ("lineage_id", memory.LineageId.ToString("D"), true),
            ("group_uuid", memory.GroupUuid.ToString("D"), true),
            ("subject_slug", memory.SubjectSlug, false),
            ("name", memory.Name, false),
            ("kind", current.Kind, false),
            ("status", current.Status, false),
            ("confidence", current.Confidence.ToString(CultureInfo.InvariantCulture), true),
            ("scope", memory.ScopeDimension, false),
            ("scope_identifier", memory.ScopeIdentifier ?? "null", memory.ScopeIdentifier is null),
            ("version", current.Version.ToString(CultureInfo.InvariantCulture), true),
            ("is_current", current.IsCurrent ? "true" : "false", true),
            ("valid_from", FormatTimestamp(current.ValidFrom), true),
            ("valid_until", current.ValidUntil is null ? "null" : FormatTimestamp(current.ValidUntil.Value), true),
            ("created_on", FormatTimestamp(current.CreatedOn), true),
        };

        var builder = new StringBuilder();
        WriteFrontMatter(builder, yaml, extra: w =>
        {
            WriteStringList(w, "facets", memory.Facets);
            WriteStringList(w, "tags", memory.Tags);
            WriteSources(w, current.Sources);
        });

        builder.AppendLine(ExportScopeMarks.GeneratedComment);
        builder.AppendLine();
        WriteBanner(builder, memory.ScopeDimension);

        builder.Append("# ").AppendLine(memory.Name);
        builder.AppendLine();
        builder.AppendLine($"group_uuid: `{memory.GroupUuid:D}`");
        builder.AppendLine();
        builder.AppendLine("## Subject");
        builder.AppendLine();
        builder.AppendLine(memory.Description);
        builder.AppendLine();

        WriteVersionBody(builder, current, heading: "## Claim", includeMetadataBlock: false);

        if (memory.Links.Count > 0)
        {
            builder.AppendLine("## Links");
            builder.AppendLine();
            foreach (ExportLink link in memory.Links)
            {
                builder.Append("- ").Append(link.Direction)
                    .Append(' ').Append(link.Relation)
                    .Append(" `").Append(link.OtherUuid.ToString("D")).Append("` ")
                    .Append(link.OtherSubject)
                    .Append(" (group_uuid: `").Append(link.OtherGroupUuid.ToString("D")).Append("`) — ")
                    .AppendLine(link.Reason);
            }

            builder.AppendLine();
        }

        if (includeHistory)
        {
            foreach (ExportVersionBody historic in memory.HistoricalVersions)
            {
                WriteVersionBody(builder, historic, heading: $"## Version {historic.Version}", includeMetadataBlock: true);
            }
        }

        return Finish(builder);
    }

    public static string FormatTimestamp(DateTimeOffset value) => value.ToString("O");

    private static void WriteVersionBody(StringBuilder builder, ExportVersionBody version, string heading, bool includeMetadataBlock)
    {
        builder.AppendLine(heading);
        builder.AppendLine();
        if (includeMetadataBlock)
        {
            builder.AppendLine($"kind: {version.Kind}");
            builder.AppendLine($"status: {version.Status}");
            builder.AppendLine($"valid_from: {FormatTimestamp(version.ValidFrom)}");
            builder.AppendLine($"valid_until: {(version.ValidUntil is null ? "null" : FormatTimestamp(version.ValidUntil.Value))}");
            builder.AppendLine($"created_on: {FormatTimestamp(version.CreatedOn)}");
            builder.AppendLine();
        }

        builder.AppendLine(version.Statement);
        builder.AppendLine();
        if (!string.IsNullOrWhiteSpace(version.ContentSummary))
        {
            builder.AppendLine("### Summary");
            builder.AppendLine();
            builder.AppendLine(version.ContentSummary);
            builder.AppendLine();
        }

        builder.AppendLine("### Document");
        builder.AppendLine();
        switch (version.BlobState)
        {
            case BlobRenderState.Inlined:
                builder.AppendLine(version.BlobText ?? string.Empty);
                break;
            case BlobRenderState.Missing:
                builder.AppendLine(ExportScopeMarks.MissingBlobNote);
                break;
            case BlobRenderState.NonText:
                builder.AppendLine(ExportScopeMarks.NonTextBlobNote);
                break;
            default:
                builder.AppendLine(ExportScopeMarks.NoBlobNote);
                break;
        }

        builder.AppendLine();
    }

    private static void WriteFrontMatter(
        StringBuilder builder,
        IReadOnlyList<(string Key, string Value, bool Literal)> yaml,
        Action<StringBuilder> extra)
    {
        builder.AppendLine("---");
        foreach ((string key, string value, bool literal) in yaml)
        {
            builder.Append(key).Append(": ").AppendLine(YamlScalar(value, alreadyLiteral: literal));
        }

        extra(builder);
        builder.AppendLine("---");
        builder.AppendLine();
    }

    private static void WriteBanner(StringBuilder builder, string scopeDimension)
    {
        string? banner = ExportScopeMarks.Banner(scopeDimension);
        if (banner is null)
        {
            return;
        }

        builder.AppendLine(banner);
        builder.AppendLine();
    }

    private static void WriteStringList(StringBuilder builder, string key, IReadOnlyList<string> values)
    {
        builder.Append(key).Append(':');
        if (values.Count == 0)
        {
            builder.AppendLine(" []");
            return;
        }

        builder.AppendLine();
        foreach (string value in values)
        {
            builder.Append("  - ").AppendLine(YamlScalar(value));
        }
    }

    private static void WriteSources(StringBuilder builder, IReadOnlyList<ExportSource> sources)
    {
        builder.Append("sources:");
        if (sources.Count == 0)
        {
            builder.AppendLine(" []");
            return;
        }

        builder.AppendLine();
        foreach (ExportSource source in sources)
        {
            builder.Append("  - kind: ").AppendLine(YamlScalar(source.Kind));
            builder.Append("    reference: ").AppendLine(YamlScalar(source.Reference));
            builder.Append("    captured_at: ").AppendLine(
                source.CapturedAt is null ? "null" : YamlScalar(FormatTimestamp(source.CapturedAt.Value), alreadyLiteral: true));
        }
    }

    private static string YamlScalar(string value, bool alreadyLiteral = false)
    {
        if (alreadyLiteral)
        {
            return value;
        }

        if (NeedsQuotes(value))
        {
            var quoted = new StringBuilder(value.Length + 2).Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '\\': quoted.Append("\\\\"); break;
                    case '"': quoted.Append("\\\""); break;
                    case '\n': quoted.Append("\\n"); break;
                    case '\r': quoted.Append("\\r"); break;
                    case '\t': quoted.Append("\\t"); break;
                    default:
                        if (char.IsControl(c))
                        {
                            quoted.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            quoted.Append(c);
                        }

                        break;
                }
            }

            return quoted.Append('"').ToString();
        }

        return value;
    }

    private static bool NeedsQuotes(string value)
    {
        if (value.Length == 0)
        {
            return true;
        }

        if (value is "true" or "false" or "null" or "yes" or "no")
        {
            return true;
        }

        // Numeric-looking strings would silently change YAML type (int/float) — force string.
        if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out _))
        {
            return true;
        }

        foreach (char c in value)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c)
                || c is ':' or '#' or '{' or '}' or '[' or ']' or ',' or '&' or '*' or '!' or '|' or '>' or '\'' or '"' or '%')
            {
                return true;
            }
        }

        return false;
    }

    private static string Finish(StringBuilder builder)
    {
        string text = builder.ToString().Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return text.TrimEnd('\n') + "\n";
    }
}
