# C-Sweet Chief of Staff

This first-party implementation uses exactly the same GitHub import, grant, isolated build,
launch, broker authorization, and employee-binding path as third-party agents. Any valid agent
may be assigned the Chief role; this implementation is a suggested catalog entry, not a privileged
runtime.

The Chief loads authoritative business, finance, organization, pattern, management-cycle, and its
own ranked hiring-backlog state before responding. It specializes in organizational design and
workforce planning: it keeps the complete role backlog current, gives the owner only a brief team
overview, and focuses each hiring conversation on the highest-priority unfilled role. Domain
execution belongs to the agents or people it recommends.

For each top-level outcome it proposes one workstream and one accountable delivery manager. It
searches capable current staff and installed digital workers first, reports disconnected
marketplaces honestly, and treats installation, permission expansion, spending, human outreach,
and engagement as separate approval gates.

## Requirements

- .NET 10 SDK
- `CSweet.Agent.SDK` 0.4.0 from NuGet.org
- A C-Sweet broker endpoint and approved agent installation

## Build

```powershell
dotnet build CSweetAgentChiefOfStaff.slnx
dotnet test CSweetAgentChiefOfStaff.slnx
```

For pre-publication SDK testing, add the directory containing `CSweet.Agent.SDK.0.4.0.nupkg` as an
additional restore source rather than changing this repository's package reference.

## Import

Push this repository publicly and paste its GitHub URL into C-Sweet's agent importer. Review and
approve only the requested permissions, events, and capabilities required by the installation.

The agent requests model responses through `platform.llm.chat-stream.v1`; provider credentials
remain inside C-Sweet and are never supplied to this process.

The Chief also produces deterministic executive briefing Markdown after activation, after a new
runtime instance starts, and when the platform's durable management cycle becomes due. Briefings
prioritize immediate actions and decisions from the authoritative operating snapshot and carry a
request identifier so the platform can deliver them exactly once.
