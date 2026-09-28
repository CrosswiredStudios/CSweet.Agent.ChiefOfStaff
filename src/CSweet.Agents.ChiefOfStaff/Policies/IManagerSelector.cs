namespace CSweet.Agents.ChiefOfStaff.Policies;

/// <summary>
/// Selects a project manager from staffing candidates.
/// Extracts the inline <c>OrderBy(x.Id).First()</c> policy so alternative
/// selection strategies can be substituted without modifying the agent (OCP/DIP).
/// </summary>
/// <typeparam name="TCandidate">The SDK staffing-candidate type.</typeparam>
public interface IManagerSelector<TCandidate>
{
    TCandidate? Select(IReadOnlyList<TCandidate> candidates);
}
