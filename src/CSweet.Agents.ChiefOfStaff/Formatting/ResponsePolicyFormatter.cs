using System.Text.RegularExpressions;

namespace CSweet.Agents.ChiefOfStaff.Formatting;

/// <summary>
/// Default response-policy formatting. Behavior-preserving extraction from
/// <c>ChiefOfStaffAgent</c> (SRP split).
/// </summary>
public sealed class ResponsePolicyFormatter : IResponsePolicyFormatter
{
    private static readonly Regex SegmentBoundary = new(
        @"\n|(?<=[.!?;:])\s+",
        RegexOptions.CultureInvariant);
    private static readonly Regex TrailingQuestion = new(@"^[^\n.!?]*\?", RegexOptions.CultureInvariant);
    private static readonly Regex AbbreviationEnding = new(
        @"\b(?:Dr|Mr|Mrs|Ms|Prof|Inc|Ltd|Corp|Co|Dept|Jr|Sr|St|vs|etc|e\.g|i\.e)\.$|\b\p{L}\.$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex QuestionOpening = new(
        @"^\s*(?:Who|What|When|Where|Why|How|Which|Do|Does|Did|Is|Are|Was|Were|Can|Could|Would|Should|Will|May|Might|Have|Has|Had|Sound|Okay|Any|Shall|Want|Ready)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public string NormalizeRoleIdentity(string value)
    {
        var cleaned = value.Trim();
        if (cleaned.EndsWith("(Agent)", StringComparison.OrdinalIgnoreCase))
            cleaned = cleaned[..^"(Agent)".Length].TrimEnd();
        return new string(cleaned
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());
    }

    public string FormatOnboardingMessage(string value)
    {
        var lines = EnforceResponseMode(value)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .ToList();
        if (lines.Count == 0) return string.Empty;

        var containsRecommendation = lines.SelectMany(SplitResponseSegments).Any(IsHiringRecommendationLine);
        var sections = new List<string>(lines.Count + 2);
        foreach (var line in lines)
        {
            if (containsRecommendation && line.EndsWith("?", StringComparison.Ordinal))
                continue;

            if (line.StartsWith("Role Map:", StringComparison.OrdinalIgnoreCase))
            {
                sections.Add($"- **Role map:** {line["Role Map:".Length..].Trim()}");
                continue;
            }
            if (line.StartsWith("Priority 1 Hire:", StringComparison.OrdinalIgnoreCase))
            {
                sections.Add($"- **Priority 1 hire:** {line["Priority 1 Hire:".Length..].Trim()}");
                continue;
            }
            if (line.EndsWith("?", StringComparison.Ordinal))
            {
                sections.Add($"**Question for you**\n\n{line}");
                continue;
            }

            sections.Add(line);
        }

        return string.Join("\n\n", sections);
    }

    public string EnforceResponseMode(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;

        var normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = normalized.Split('\n');
        if (!SplitResponseSegments(normalized).Any(IsHiringRecommendationLine)) return value;

        var retained = new List<string>(lines.Length);
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Contains("Question for you", StringComparison.OrdinalIgnoreCase))
                continue;

            var parts = SplitResponseSegments(line);
            var statements = parts
                .Where(part => !part.TrimEnd().EndsWith("?", StringComparison.Ordinal))
                .ToList();
            if (statements.Count > 0)
                retained.Add(string.Join(" ", statements));
        }

        return string.Join("\n", retained).Trim();
    }

    public bool IsHiringRecommendationLine(string line)
    {
        if (line.Trim().Equals("Priority 1 Hire:", StringComparison.OrdinalIgnoreCase) ||
            line.TrimEnd().EndsWith("?", StringComparison.Ordinal) ||
            line.Contains("cannot recommend", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("can't recommend", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("unable to recommend", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("before I recommend", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("before I can recommend", StringComparison.OrdinalIgnoreCase))
            return false;

        return
            line.StartsWith("Priority 1 Hire:", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("priority-one hire", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("priority 1 hire", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("first hire", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("highest priority is to hire", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("should hire", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("recommend a ", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("recommend an ", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("I recommend", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("hiring backlog", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("browse candidates", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("browse marketplace", StringComparison.OrdinalIgnoreCase);
    }

    public IEnumerable<string> SplitResponseSegments(string value)
    {
        var start = 0;
        foreach (Match boundary in SegmentBoundary.Matches(value))
        {
            var preceding = value[start..boundary.Index];
            var following = value[(boundary.Index + boundary.Length)..];
            // Keep role labels and ordinary clauses together. A question opening
            // can contain abbreviation periods before its final question mark.
            if (!boundary.Value.Contains('\n') && boundary.Index > 0 && value[boundary.Index - 1] is ';' or ':' &&
                !QuestionOpening.IsMatch(following) && !TrailingQuestion.IsMatch(following))
                continue;
            // Most punctuation boundaries are independent of a question's first
            // word. A period in "Can Acme Inc. provide one?" is different: keep
            // the abbreviation inside that sentence so no question fragment leaks.
            // Abbreviations/initials can also end statements ("Project X. Sound
            // good?"). A new question after a statement takes precedence over
            // the abbreviation exception. These are bounded English heuristics,
            // not a general-purpose natural-language sentence parser.
            if (!boundary.Value.Contains('\n') && AbbreviationEnding.IsMatch(preceding) &&
                (QuestionOpening.IsMatch(preceding) || !QuestionOpening.IsMatch(following)))
                continue;

            yield return preceding;
            start = boundary.Index + boundary.Length;
        }
        yield return value[start..];
    }
}
