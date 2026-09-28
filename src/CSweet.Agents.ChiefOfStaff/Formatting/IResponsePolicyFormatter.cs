namespace CSweet.Agents.ChiefOfStaff.Formatting;

/// <summary>
/// Formats assistant responses (response-mode enforcement, onboarding markdown).
/// Extracted from <c>ChiefOfStaffAgent</c> so formatting has a single owner (SRP)
/// and can be substituted in tests (DIP).
/// </summary>
public interface IResponsePolicyFormatter
{
    string NormalizeRoleIdentity(string value);

    string FormatOnboardingMessage(string value);

    string EnforceResponseMode(string value);

    bool IsHiringRecommendationLine(string line);

    IEnumerable<string> SplitResponseSegments(string value);
}
