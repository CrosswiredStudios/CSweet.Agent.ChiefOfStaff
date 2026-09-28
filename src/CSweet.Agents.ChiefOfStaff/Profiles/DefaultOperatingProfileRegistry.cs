using CSweet.Agent.SDK;

namespace CSweet.Agents.ChiefOfStaff.Profiles;

/// <summary>
/// Default <see cref="IOperatingProfileRegistry"/> backed by the static
/// <see cref="BusinessOperatingProfiles"/> catalog. Preserves current behavior
/// while exposing an injectable seam (DIP/OCP).
/// </summary>
public sealed class DefaultOperatingProfileRegistry : IOperatingProfileRegistry
{
    public IReadOnlyList<AgentConfigurationOption> ConfigurationOptions =>
        BusinessOperatingProfiles.ConfigurationOptions;

    public IReadOnlyList<BusinessOperatingProfile> SuggestedProfiles =>
        BusinessOperatingProfiles.SuggestedProfiles;

    public BusinessOperatingProfile Resolve(AgentSettings settings) =>
        BusinessOperatingProfiles.Resolve(settings);

    public BusinessOperatingProfile? Find(string key) =>
        BusinessOperatingProfiles.Find(key);

    public OperatingProfileResolution ResolveExplicit(string? key)
    {
        var profile = string.IsNullOrWhiteSpace(key)
            ? null
            : BusinessOperatingProfiles.Find(key);
        if (profile is not null)
            return new OperatingProfileResolution(true, profile);
        var fallback = BusinessOperatingProfiles.Find(BusinessOperatingProfiles.GeneralKey)
            ?? throw new InvalidOperationException("The general operating profile is not registered.");
        return new OperatingProfileResolution(false, fallback);
    }
}
