using CSweet.Agent.SDK;

namespace CSweet.Agents.ChiefOfStaff.Profiles;

/// <summary>
/// Result of resolving a business operating profile key.
/// Replaces the silent fallback-to-general so unknown keys are explicit (ISP/LSP hardening).
/// </summary>
public sealed record OperatingProfileResolution(bool Found, BusinessOperatingProfile Profile);

/// <summary>
/// Abstraction over the business-operating-profile registry so new profiles can be
/// registered without modifying consumers (OCP) and the agent depends on an
/// abstraction rather than the static <see cref="BusinessOperatingProfiles"/> class (DIP).
/// </summary>
public interface IOperatingProfileRegistry
{
    IReadOnlyList<AgentConfigurationOption> ConfigurationOptions { get; }

    IReadOnlyList<BusinessOperatingProfile> SuggestedProfiles { get; }

    /// <summary>Resolves a settings object to a profile, falling back to general for unknown keys.</summary>
    BusinessOperatingProfile Resolve(AgentSettings settings);

    /// <summary>Finds a profile by key, or null when unknown.</summary>
    BusinessOperatingProfile? Find(string key);

    /// <summary>Resolves a key explicitly, reporting whether the key was known.</summary>
    OperatingProfileResolution ResolveExplicit(string? key);
}
