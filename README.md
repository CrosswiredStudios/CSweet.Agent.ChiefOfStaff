# C-Sweet Chief of Staff

First-party Chief of Staff agent for C-Sweet in `CSweet.Agent.ChiefOfStaff`.

The agent uses `CSweet.Agent.SDK` 3.31.1 callbacks. It receives exact-installation durable work and uses typed, live-grant platform clients. The SDK privately manages runtime connectivity, authentication, leasing, retry, progress, configuration refresh, discovery, and personal to-do draining.

It loads authoritative business, finance, organization, operating-pattern, management-cycle, memory, and hiring-backlog state. It owns executive operating context, organizational design, workforce planning, and the ranked hiring backlog. It originates only CEO-direct managerial hiring recommendations. Active functional leads own their subordinate team recommendations and coordinate with the Chief through approved same-organization capability bindings; neither agent selects the other installation.

For the Game Studio profile, a pending or active Creative Director owns creative/product authority and initial team design. The Chief suppresses its own same-project Game Producer suggestion, removes an unsourced conflict once creative ownership becomes active, and leaves the Creative Director's approved Producer-led plan intact. A Game Producer hired from that plan reports to the Creative Director and owns delivery. When multiple game projects make the ownership scope ambiguous, the Chief asks one project-ownership question and does not mutate hiring state.

## Runtime behavior

- User-message and management-review events are durable work.
- Assistant streaming is reported as durable progress; the callback terminal result completes work.
- Onboarding is acknowledged only after its communication side effect succeeds.
- Business onboarding selects a General, Game Studio, SaaS, E-commerce, Professional Services,
  Media & Content, or Custom operating profile. The Chief first infers whether that profile fits
  the saved company information, offers a preset switch only for a clear mismatch, and then
  presents the existing bounded focus choice.
- The runtime seeds a profile-aware leadership-coverage agenda in Backlog. It exposes one focus or
  hiring decision at a time, while CEO requests and active hiring work take precedence.
- The deterministic runtime mirrors hiring recommendations to the Chief's own sequenced personal
  board. The priority role moves into Doing silently after the original recommendation and
  Marketplace action; fulfillment moves that ticket to Done and activates the next Backlog role.
- Company mutations use explicit platform capabilities and their approval/idempotency rules.
- Model tools are loaded from the live grant revision, excluding runtime-owned personal-task
  creation and suggested-action capabilities.
- Provider and service credentials never enter the process.
- CEO-approved functional-lead resource-change events retain the lead-authored reporting lines,
  are administratively reconciled into the Chief-owned hiring backlog, and produce one concise,
  idempotent notice naming the requesting agent with one role-scoped Marketplace action per new or
  increased hiring recommendation.

## Build

```powershell
dotnet build CSweetAgentChiefOfStaff.slnx
dotnet test CSweetAgentChiefOfStaff.slnx
```

Requirements are .NET 10, `CSweet.Agent.SDK` 3.31.1, an approved protocol-v2 installation, an assigned employee identity for employee workflows, and the grants in [GRANTS.md](GRANTS.md).

## SDK 1.0 migration

The protocol-v1 transport APIs were removed. The agent now uses `AgentEventEnvelope`, `AgentCapabilityRequest`, `AgentWorkResult`, `AgentRuntimeContext.Platform`, `ReportProgressAsync`, `GetModelToolsAsync`, and `PlatformChatClient`. The v2 manifest contains capability schemas/timeouts/idempotency and no generic publications.

## Provided capability behavior

Each `provides` entry in `csweet-plugin.json` is an exact durable work callback. Assistant and check-in operations may generate progress and a durable result. Product role briefs and plan reviews are advisory. Only a CEO-approved lead-authored resource change is reconciled into the installation-scoped hiring backlog through explicit platform tools. Product escalation sends an external communication and uses the supplied idempotency key. Configuration update durably changes runtime configuration. Full contracts and requested authority are in [GRANTS.md](GRANTS.md).

## Provider queue handling

Uses SDK 3.31.1 for acknowledged LLM waiting, conversation activity, and host-authoritative deadline updates. Deploy the matching C-Sweet AgentHost and reimport this package to enable the private polling protocol.

## Initial operating-profile review (2.4.0)

Immediately after hiring, the Chief assesses the selected Business Operating Profile against the saved company information using the configured model. This required orchestration step precedes leadership coverage and focus selection. The assessment is persisted by onboarding event so retries do not produce a different recommendation.

An appropriate profile, or insufficient evidence for a clearly better preset, continues directly to the existing focus phase. A clear mismatch displays the current mode and the suggested preset, with **Switch to …** and **Leave unchanged**. This decision has no Custom or Something else option. An already selected Custom profile is assessed using its description and may remain unchanged.

Accepting the switch saves only the Chief employee's profile override through the platform configuration service, preserving the model and other settings. Both choices resume focus selection through a durable event using the effective saved profile. A stale switch is rejected; Leave unchanged preserves the latest settings. Unavailable company data or invalid inference leaves onboarding retryable without creating a focus agenda.

Deploy the matching platform changes before importing 2.4.0, and approve its operating-state grants and configuration-choice event subscription. The installation defaults remain General, with Custom available during installation.

## Selectable questions (2.4.1)

The Chief prefers `ask_user` whenever it needs an answer and the tool is available, including clarifications, confirmations, and relayed Product Manager decisions. It supplies concise choices from known context so the owner can click instead of typing. Ordinary questions retain Something else; profile decisions retain only Switch and Leave unchanged. Required focus-card failures propagate to the durable callback instead of acknowledging onboarding without a card.

Restart the matching platform services after deploying profile-choice support: older AgentHost schemas reject `configurationChange`. Version 2.4.1 omits that optional property on ordinary questions. Reimporting this package gives a previously failed onboarding event a fresh package delivery, reusing its original assessment and message keys.
