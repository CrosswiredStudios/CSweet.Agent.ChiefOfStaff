# C-Sweet Chief of Staff

First-party Chief of Staff agent for C-Sweet. The catalog repository name is `CSweet.Agent.ChiefOfStaff`; this checkout retains the historical `CSweetAgentChiefOfStaff` name.

The agent uses `CSweet.Agent.SDK` 3.8.0 callbacks. It receives exact-installation durable work and uses typed, live-grant platform clients. The SDK privately manages runtime connectivity, authentication, leasing, retry, progress, configuration refresh, discovery, and personal to-do draining.

It loads authoritative business, finance, organization, operating-pattern, management-cycle, memory, and hiring-backlog state. It owns executive operating context, organizational design, workforce planning, and the ranked hiring backlog. When an active Product Manager reports to it, coordination uses approved same-organization capability bindings; neither agent selects the other installation.

## Runtime behavior

- User-message and management-review events are durable work.
- Assistant streaming is reported as durable progress; the callback terminal result completes work.
- Onboarding is acknowledged only after its communication side effect succeeds.
- Hiring recommendations are mirrored to the Chief's own sequenced personal board. The priority
  role moves into Doing while the Chief messages its manager with a Marketplace action; fulfillment
  moves that ticket to Done and activates the next Backlog role.
- Company mutations use explicit platform capabilities and their approval/idempotency rules.
- Model tools are loaded from the live grant revision.
- Provider and service credentials never enter the process.
- Product Manager resource-change events are validated against the current reporting line,
  reconciled into the Chief-owned hiring backlog, and summarized to the Chief's manager in one
  idempotent priority-ordered brief with one role-scoped Marketplace action per new or increased
  hiring recommendation.

## Build

```powershell
dotnet build CSweetAgentChiefOfStaff.slnx
dotnet test CSweetAgentChiefOfStaff.slnx
```

Requirements are .NET 10, `CSweet.Agent.SDK` 3.8.0, an approved protocol-v2 installation, an assigned employee identity for employee workflows, and the grants in [GRANTS.md](GRANTS.md).

## SDK 1.0 migration

The protocol-v1 transport APIs were removed. The agent now uses `AgentEventEnvelope`, `AgentCapabilityRequest`, `AgentWorkResult`, `AgentRuntimeContext.Platform`, `ReportProgressAsync`, `GetModelToolsAsync`, and `PlatformChatClient`. The v2 manifest contains capability schemas/timeouts/idempotency and no generic publications.

## Provided capability behavior

Each `provides` entry in `csweet-plugin.json` is an exact durable work callback. Assistant and check-in operations may generate progress and a durable result. Product role briefs are read-only. Plan review can update the installation-scoped hiring backlog through explicit platform tools. Product escalation sends an external communication and uses the supplied idempotency key. Configuration update durably changes runtime configuration. Full contracts and requested authority are in [GRANTS.md](GRANTS.md).
