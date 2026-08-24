# C-Sweet Chief of Staff

First-party Chief of Staff agent for C-Sweet. The catalog repository name is `CSweet.Agent.ChiefOfStaff`; this checkout retains the historical `CSweetAgentChiefOfStaff` name.

The agent uses `CSweet.Agent.SDK` 3.13.0 callbacks. It receives exact-installation durable work and uses typed, live-grant platform clients. The SDK privately manages runtime connectivity, authentication, leasing, retry, progress, configuration refresh, discovery, and personal to-do draining.

It loads authoritative business, finance, organization, operating-pattern, management-cycle, memory, and hiring-backlog state. It owns executive operating context, organizational design, workforce planning, and the ranked hiring backlog. It originates only CEO-direct managerial hiring recommendations. Active Product Managers sharing the same CEO own their product-team recommendations and coordinate with the Chief through approved same-organization capability bindings; neither agent selects the other installation.

## Runtime behavior

- User-message and management-review events are durable work.
- Assistant streaming is reported as durable progress; the callback terminal result completes work.
- Onboarding is acknowledged only after its communication side effect succeeds.
- The deterministic runtime mirrors hiring recommendations to the Chief's own sequenced personal
  board. The priority role moves into Doing silently after the original recommendation and
  Marketplace action; fulfillment moves that ticket to Done and activates the next Backlog role.
- Company mutations use explicit platform capabilities and their approval/idempotency rules.
- Model tools are loaded from the live grant revision, excluding runtime-owned personal-task
  creation and suggested-action capabilities.
- Provider and service credentials never enter the process.
- CEO-approved Product Manager resource-change events retain the lead-authored reporting lines,
  are administratively reconciled into the Chief-owned hiring backlog, and are summarized to the CEO in one
  idempotent priority-ordered brief with one role-scoped Marketplace action per new or increased
  hiring recommendation.

## Build

```powershell
dotnet build CSweetAgentChiefOfStaff.slnx
dotnet test CSweetAgentChiefOfStaff.slnx
```

Requirements are .NET 10, `CSweet.Agent.SDK` 3.13.0, an approved protocol-v2 installation, an assigned employee identity for employee workflows, and the grants in [GRANTS.md](GRANTS.md).

## SDK 1.0 migration

The protocol-v1 transport APIs were removed. The agent now uses `AgentEventEnvelope`, `AgentCapabilityRequest`, `AgentWorkResult`, `AgentRuntimeContext.Platform`, `ReportProgressAsync`, `GetModelToolsAsync`, and `PlatformChatClient`. The v2 manifest contains capability schemas/timeouts/idempotency and no generic publications.

## Provided capability behavior

Each `provides` entry in `csweet-plugin.json` is an exact durable work callback. Assistant and check-in operations may generate progress and a durable result. Product role briefs and plan reviews are advisory. Only a CEO-approved lead-authored resource change is reconciled into the installation-scoped hiring backlog through explicit platform tools. Product escalation sends an external communication and uses the supplied idempotency key. Configuration update durably changes runtime configuration. Full contracts and requested authority are in [GRANTS.md](GRANTS.md).
