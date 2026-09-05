using CSweet.Agent.SDK;

namespace CSweet.Agents.ChiefOfStaff;

internal sealed record ChiefFocusOption(string Id, string Label, string Description);

internal sealed record LeadershipCoverageItem(string Id, string Title, string Description);

internal sealed record BusinessOperatingProfile(
    string Key,
    string Label,
    string PromptOverlay,
    IReadOnlyList<ChiefFocusOption> FocusOptions,
    IReadOnlyList<LeadershipCoverageItem> LeadershipCoverage);

internal static class BusinessOperatingProfiles
{
    public const string ConfigurationKey = "businessOperatingProfile";
    public const string CustomDescriptionKey = "customBusinessDescription";
    public const string GeneralKey = "general";

    private static readonly IReadOnlyDictionary<string, BusinessOperatingProfile> Profiles =
        new[]
        {
            Profile("general", "General", "Use no industry-specific organizational bias.",
                ("product", "Product", "Clarify the offering, customer outcome, and accountable product leadership."),
                ("research", "Research", "Reduce a major market, customer, scientific, or strategic uncertainty."),
                ("financial", "Financial", "Establish financial controls, runway, targets, and financial leadership coverage."),
                ("legal", "Legal", "Establish legal, regulatory, intellectual-property, and compliance ownership.")),
            Profile("game-studio", "Game Studio", "The Creative Director owns each game's creative/product authority and initial team design. The Game Producer owns delivery, reports to that Creative Director, and is proposed through the Creative Director's approved plan. The Chief owns CEO-direct leadership gaps, company-wide constraints, approval routing, and backlog administration; once creative ownership is pending or active, do not design that project's subordinate team.",
                ("creative-product", "Creative & Product", "Clarify who owns the creative vision and who owns player and product outcomes."),
                ("research", "Player & Market Research", "Reduce uncertainty about the audience, genre, market, or player needs."),
                ("financial", "Financial", "Establish funding, runway, budget, and financial leadership coverage."),
                ("legal", "Legal & IP", "Establish intellectual-property, publishing, licensing, privacy, and legal ownership.")),
            Profile("saas", "SaaS", "Bias organizational analysis toward product, technology, recurring-revenue operations, and go-to-market ownership without assuming which vacancy comes first.",
                ("product", "Product", "Clarify customer outcomes, product leadership, and the product operating model."),
                ("technology", "Technology", "Establish technical leadership, architecture accountability, reliability, and security ownership."),
                ("go-to-market", "Go to Market", "Establish acquisition, sales, onboarding, retention, and revenue ownership."),
                ("financial-legal", "Financial & Legal", "Establish financial controls, contracting, privacy, and compliance ownership.")),
            Profile("ecommerce", "E-commerce", "Bias organizational analysis toward merchandising and product, growth, fulfillment operations, and financial controls.",
                ("product", "Product & Merchandising", "Clarify the offer, assortment, customer experience, and accountable ownership."),
                ("growth", "Growth", "Establish customer acquisition, retention, brand, and channel ownership."),
                ("operations", "Operations", "Establish inventory, fulfillment, service, and supply-chain ownership."),
                ("financial", "Financial", "Establish unit economics, cash controls, tax, and financial leadership coverage.")),
            Profile("professional-services", "Professional Services", "Bias organizational analysis toward offering ownership, delivery capacity, business development, and financial controls.",
                ("offering", "Offering", "Clarify the service, target client, differentiation, and accountable offering owner."),
                ("market", "Market & Sales", "Establish research, positioning, pipeline, and client-acquisition ownership."),
                ("delivery", "Delivery Operations", "Establish delivery quality, capacity, scheduling, and client-success ownership."),
                ("financial-legal", "Financial & Legal", "Establish pricing, margin, contracts, and professional-risk ownership.")),
            Profile("media-content", "Media & Content", "Bias organizational analysis toward creative or editorial direction, audience growth, production operations, and monetization.",
                ("creative", "Creative & Editorial", "Clarify the voice, format, portfolio, and accountable creative ownership."),
                ("audience", "Audience & Growth", "Establish audience research, distribution, community, and growth ownership."),
                ("production", "Production", "Establish production planning, throughput, quality, and publishing ownership."),
                ("financial-legal", "Financial & Legal", "Establish monetization, rights, sponsorship, and financial controls.")),
            Profile("custom", "Custom", "Use the owner's custom business description as a hypothesis, while preserving the Chief of Staff's core role boundaries.",
                ("product", "Product or Offering", "Clarify the primary customer outcome and accountable owner."),
                ("research", "Research", "Reduce the most important business uncertainty."),
                ("financial", "Financial", "Establish financial controls and leadership coverage."),
                ("legal", "Legal", "Establish legal and regulatory ownership."))
        }.ToDictionary(x => x.Key, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<AgentConfigurationOption> ConfigurationOptions { get; } =
        Profiles.Values.Select(x => new AgentConfigurationOption(x.Key, x.Label)).ToList();

    public static BusinessOperatingProfile Resolve(AgentSettings settings)
    {
        var key = settings.GetString(ConfigurationKey) ?? GeneralKey;
        return Profiles.TryGetValue(key, out var profile) ? profile : Profiles[GeneralKey];
    }

    private static BusinessOperatingProfile Profile(
        string key,
        string label,
        string overlay,
        params (string Id, string Label, string Description)[] options)
    {
        var focus = options.Select(x => new ChiefFocusOption(x.Id, x.Label, x.Description)).ToList();
        var coverage = focus.Select(x => new LeadershipCoverageItem(
            x.Id,
            $"Assess {x.Label.ToLowerInvariant()} leadership coverage",
            $"Determine whether {x.Label.ToLowerInvariant()} needs a CEO-owned responsibility, an occupied leadership role, or a future hire. {x.Description}"))
            .ToList();
        return new BusinessOperatingProfile(key, label, overlay, focus, coverage);
    }
}
