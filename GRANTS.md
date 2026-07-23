# Chief of Staff capability grants

This document is the human-readable grant catalog for the C-Sweet Chief of Staff agent.
The source of truth for installation authorization remains
[`csweet-plugin.json`](csweet-plugin.json). This catalog was last verified against manifest
version `1.7.0`.

Serialized capability names are sourced from the authoritative `CapabilityCatalog` in
`CSweet.Agent.SDK` 0.6.0; manifest-audit tests reject names missing from that catalog.

## How to read this catalog

- **Required grants** are capabilities the installation asks C-Sweet for permission to invoke.
- **Provided capabilities** are brokered operations this agent exposes to C-Sweet or another
  authorized agent. They are not permissions granted to this installation.
- `organization` scope restricts access to the organization containing the installation.
- `user` scope restricts access to the current authorized user's data.
- Event subscriptions and publications are transport contracts, not capability grants, and remain
  documented in the manifest.

## Required grants by service and feature

### AI runtime

| Grant | Scope | Feature |
|---|---|---|
| `platform.llm.chat-stream.v1` | organization | Generate streamed Chief of Staff responses. |

### Business and operating context

| Grant | Scope | Feature |
|---|---|---|
| `platform.business-profile.read.v1` | organization | Read the authoritative business profile. |
| `platform.business-profile.update-explicit.v1` | organization | Save low-risk facts explicitly supplied by the owner. |
| `platform.business-profile.propose-update.v1` | organization | Propose sensitive or inferred profile changes for review. |
| `platform.organization.snapshot.read.v1` | organization | Read people, roles, reporting lines, objectives, workstreams, and workers. |
| `platform.business-pattern.search.v1` | organization | Find operating patterns appropriate to the business and lifecycle stage. |
| `platform.management-cycle.read.v1` | organization | Follow the configured management cadence. |

### Work planning and organizational design

| Grant | Scope | Feature |
|---|---|---|
| `platform.workstream.plan.propose.v1` | organization | Propose outcome-oriented managed workstreams. |
| `platform.workforce.search.v1` | organization | Find suitable current, installed, marketplace, or human resources. |
| `platform.workforce-plan.propose.v1` | organization | Propose an explainable organization and staffing plan. |
| `platform.approval.propose.v1` | organization | Present separately gated actions for approval. |

### Finance and budget controls

| Grant | Scope | Feature |
|---|---|---|
| `platform.finance-profile.read.v1` | organization | Read financial goals, limits, and controls. |
| `platform.finance-profile.propose-update.v1` | organization | Propose changes to financial goals or controls. |
| `platform.budget.evaluate.v1` | organization | Evaluate hard budget constraints before paid work. |

### Hiring management

| Grant | Scope | Feature |
|---|---|---|
| `platform.hiring-recommendation.list.v1` | organization | Read this Chief installation's ranked role backlog. |
| `platform.hiring-recommendation.upsert.v1` | organization | Maintain ranked hiring recommendations. |
| `platform.hiring-workflow.stage.v1` | organization | Stage an install-and-hire workflow for owner approval. |

These grants are Chief-owned. Product Manager installations deliberately do not receive them.

### Memory

| Grant | Scope | Feature |
|---|---|---|
| `memory.business.read.v1` | organization | Recall approved organization-level business context. |
| `memory.business.propose.v1` | organization | Propose organization-level business memories. |
| `memory.user.read.v1` | user | Recall approved context for the current user. |
| `memory.user.propose.v1` | user | Propose memories for the current user. |

### Executive communication

| Grant | Scope | Feature |
|---|---|---|
| `communication.chat.read.v1` | organization | Locate the Chief's protected CEO conversation for delegated executive questions. |
| `communication.message.send.v1` | organization | Send messages to conversations in which the Chief is a participant. |

### Product Manager collaboration

| Grant | Scope | Provider | Feature |
|---|---|---|---|
| `product-management.plan.v1` | organization | Product Manager | Request product strategy, roadmap, and product-team recommendations. |
| `product-management.context.update.v1` | organization | Product Manager | Push authoritative role, decision, and context updates to Product Manager direct reports. |

Cross-agent calls target an exact installation and both agents validate the current organization
and reporting relationship rather than trusting caller-supplied identity.

## Capabilities provided by the Chief

### General agent and management services

| Capability | Consumer | Feature |
|---|---|---|
| `assistant.converse.v1` | C-Sweet | Answer a scoped user or executive request. |
| `assistant.summarize-activity.v1` | C-Sweet | Summarize current operating activity. |
| `assistant.plan-work.v1` | C-Sweet | Produce an executive work plan. |
| `management.check-in.v1` | C-Sweet management cycle | Return a management status report. |
| `agent.configuration.describe.v1` | C-Sweet | Describe configurable settings. |
| `agent.configuration.update.v1` | C-Sweet | Validate and apply configurable settings. |

### Product leadership coordination

| Capability | Consumer | Feature |
|---|---|---|
| `management.product-role-brief.v1` | Product Manager | Return the validated mandate, outcomes, measures, constraints, decision rights, team context, and gaps. |
| `management.product-plan.review.v1` | Product Manager | Review product strategy, product-team structure, reporting lines, and hiring sequence. |
| `management.product-escalation.v1` | Product Manager | Accept a product decision or information gap and route it through the Chief's CEO workflow. |

## Security boundary

- The agent has no credential declarations and no web access.
- Read, proposal, update, approval, and hiring capabilities are separate grants.
- A successful proposal does not imply approval or execution.
- Memory is supporting context and cannot override current platform records.
- Any manifest change must update this catalog and its manifest-version marker in the same change.
