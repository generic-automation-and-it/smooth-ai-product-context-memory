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
                w.AppendLineLf(" []");
                return;
            }

            w.AppendLineLf();
            foreach (ExportTicket ticket in group.Tickets)
            {
                w.Append("  - provider: ").AppendLineLf(YamlScalar(ticket.Provider));
                w.Append("    key: ").AppendLineLf(YamlScalar(ticket.Key));
                w.Append("    url: ").AppendLineLf(YamlScalar(ticket.Url));
            }
        });

        builder.AppendLineLf(ExportScopeMarks.GeneratedComment);
        builder.AppendLineLf();
        WriteBanner(builder, group.ScopeDimension);

        string heading = group.CurrentDescription?.Name ?? $"group-{group.Uuid:D}";
        builder.Append("# ").AppendLineLf(heading);
        builder.AppendLineLf();
        builder.AppendLineLf($"group_uuid: `{group.Uuid:D}`");
        builder.AppendLineLf();

        if (group.CurrentDescription is not null)
        {
            builder.AppendLineLf(group.CurrentDescription.Body);
            builder.AppendLineLf();
        }

        if (includeHistory)
        {
            foreach (ExportGroupDescription historic in group.HistoricalDescriptions)
            {
                builder.AppendLineLf($"## Description version {historic.Version}");
                builder.AppendLineLf();
                builder.AppendLineLf($"name: {historic.Name}");
                builder.AppendLineLf($"created_on: {FormatTimestamp(historic.CreatedOn)}");
                builder.AppendLineLf();
                builder.AppendLineLf(historic.Body);
                builder.AppendLineLf();
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

        builder.AppendLineLf(ExportScopeMarks.GeneratedComment);
        builder.AppendLineLf();
        WriteBanner(builder, memory.ScopeDimension);

        builder.Append("# ").AppendLineLf(memory.Name);
        builder.AppendLineLf();
        builder.AppendLineLf($"group_uuid: `{memory.GroupUuid:D}`");
        builder.AppendLineLf();
        builder.AppendLineLf("## Subject");
        builder.AppendLineLf();
        builder.AppendLineLf(memory.Description);
        builder.AppendLineLf();

        WriteVersionBody(builder, current, heading: "## Claim", includeMetadataBlock: false);

        if (memory.Links.Count > 0)
        {
            builder.AppendLineLf("## Links");
            builder.AppendLineLf();
            foreach (ExportLink link in memory.Links)
            {
                builder.Append("- ").Append(link.Direction)
                    .Append(' ').Append(link.Relation)
                    .Append(" `").Append(link.OtherUuid.ToString("D")).Append("` ")
                    .Append(link.OtherSubject)
                    .Append(" (group_uuid: `").Append(link.OtherGroupUuid.ToString("D")).Append("`) — ")
                    .AppendLineLf(link.Reason);
            }

            builder.AppendLineLf();
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
        builder.AppendLineLf(heading);
        builder.AppendLineLf();
        if (includeMetadataBlock)
        {
            builder.AppendLineLf($"kind: {version.Kind}");
            builder.AppendLineLf($"status: {version.Status}");
            builder.AppendLineLf($"valid_from: {FormatTimestamp(version.ValidFrom)}");
            builder.AppendLineLf($"valid_until: {(version.ValidUntil is null ? "null" : FormatTimestamp(version.ValidUntil.Value))}");
            builder.AppendLineLf($"created_on: {FormatTimestamp(version.CreatedOn)}");
            builder.AppendLineLf();
        }

        builder.AppendLineLf(version.Statement);
        builder.AppendLineLf();
        if (!string.IsNullOrWhiteSpace(version.ContentSummary))
        {
            builder.AppendLineLf("### Summary");
            builder.AppendLineLf();
            builder.AppendLineLf(version.ContentSummary);
            builder.AppendLineLf();
        }

        builder.AppendLineLf("### Document");
        builder.AppendLineLf();
        switch (version.BlobState)
        {
            case BlobRenderState.Inlined:
                builder.AppendLineLf(version.BlobText ?? string.Empty);
                break;
            case BlobRenderState.Missing:
                builder.AppendLineLf(ExportScopeMarks.MissingBlobNote);
                break;
            case BlobRenderState.NonText:
                builder.AppendLineLf(ExportScopeMarks.NonTextBlobNote);
                break;
            default:
                builder.AppendLineLf(ExportScopeMarks.NoBlobNote);
                break;
        }

        builder.AppendLineLf();
    }

    private static void WriteFrontMatter(
        StringBuilder builder,
        IReadOnlyList<(string Key, string Value, bool Literal)> yaml,
        Action<StringBuilder> extra)
    {
        builder.AppendLineLf("---");
        foreach ((string key, string value, bool literal) in yaml)
        {
            builder.Append(key).Append(": ").AppendLineLf(YamlScalar(value, alreadyLiteral: literal));
        }

        extra(builder);
        builder.AppendLineLf("---");
        builder.AppendLineLf();
    }

    private static void WriteBanner(StringBuilder builder, string scopeDimension)
    {
        string? banner = ExportScopeMarks.Banner(scopeDimension);
        if (banner is null)
        {
            return;
        }

        builder.AppendLineLf(banner);
        builder.AppendLineLf();
    }

    private static void WriteStringList(StringBuilder builder, string key, IReadOnlyList<string> values)
    {
        builder.Append(key).Append(':');
        if (values.Count == 0)
        {
            builder.AppendLineLf(" []");
            return;
        }

        builder.AppendLineLf();
        foreach (string value in values)
        {
            builder.Append("  - ").AppendLineLf(YamlScalar(value));
        }
    }

    private static void WriteSources(StringBuilder builder, IReadOnlyList<ExportSource> sources)
    {
        builder.Append("sources:");
        if (sources.Count == 0)
        {
            builder.AppendLineLf(" []");
            return;
        }

        builder.AppendLineLf();
        foreach (ExportSource source in sources)
        {
            builder.Append("  - kind: ").AppendLineLf(YamlScalar(source.Kind));
            builder.Append("    reference: ").AppendLineLf(YamlScalar(source.Reference));
            builder.Append("    captured_at: ").AppendLineLf(
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

    private static readonly HashSet<string> BooleanOrNullAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        "true", "false", "null", "yes", "no", "on", "off", "y", "n", "~",
    };

    private static bool NeedsQuotes(string value)
    {
        if (value.Length == 0)
        {
            return true;
        }

        if (IsBooleanOrNullAlias(value))
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

    private static bool IsBooleanOrNullAlias(string value) => BooleanOrNullAliases.Contains(value);

    private static StringBuilder AppendLineLf(this StringBuilder builder, string? value = null) =>
        value is null ? builder.Append('\n') : builder.Append(value).Append('\n');

    private static string Finish(StringBuilder builder) => builder.ToString().TrimEnd('\n', '\r') + "\n";
}
