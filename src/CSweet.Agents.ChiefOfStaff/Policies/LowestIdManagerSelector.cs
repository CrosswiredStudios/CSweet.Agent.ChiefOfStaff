using CSweet.Agent.SDK;

namespace CSweet.Agents.ChiefOfStaff.Policies;

/// <summary>
/// Default manager selection: lowest candidate <see cref="ProjectManagerCandidate.Id"/> first.
/// Preserves the historical inline <c>OrderBy(x.Id).First()</c> behavior as an injectable policy (OCP/DIP).
/// </summary>
public sealed class LowestIdManagerSelector : IManagerSelector<ProjectManagerCandidate>
{
    public ProjectManagerCandidate? Select(IReadOnlyList<ProjectManagerCandidate> candidates) =>
        candidates.Count == 0 ? null : candidates.OrderBy(x => x.Id).First();
}
