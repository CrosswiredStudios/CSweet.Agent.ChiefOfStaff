namespace CSweet.Agents.ChiefOfStaff.Formatting;

/// <summary>
/// Default response-policy formatting. Behavior-preserving extraction from
/// <c>ChiefOfStaffAgent</c> (SRP split).
/// </summary>
public sealed class ResponsePolicyFormatter : IResponsePolicyFormatter
{
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

        var containsRecommendation = lines.Any(IsHiringRecommendationLine);
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

            var parts = System.Text.RegularExpressions.Regex.Split(
                line,
                @"(?<=[.!?;:])\s+(?=(?:Who|What|When|Where|Why|How|Which|Do|Does|Did|Is|Are|Can|Could|Would|Should|Will)\b)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
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
        if (line.TrimEnd().EndsWith("?", StringComparison.Ordinal) ||
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

    public IEnumerable<string> SplitResponseSegments(string value) =>
        System.Text.RegularExpressions.Regex.Split(
            value,
            @"\n|(?<=[.!?;:])\s+(?=(?:Who|What|When|Where|Why|How|Which|Do|Does|Did|Is|Are|Can|Could|Would|Should|Will)\b)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
}
