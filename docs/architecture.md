# Architecture

```
Hubwright.Runtime     model backends as one IChatClient (claude/copilot CLIs, Anthropic, Foundry,
                     GitHub Models, Local), MCP bridge for the CLIs, run log, cost metering, ~/.hubwright
Hubwright.Core        pure: TenantSnapshot, rule language + facts + engine, scoring, blueprints,
                     theme generator + WCAG, report writers, page mockup renderer
Hubwright.Skills      skill folders → rules; layering; validator; source drift; scaffolding
Hubwright.Connectors  config, tenant store, proposals, audit log, secrets, MSAL sign-in,
                     read-only HTTP, the live collector, the docs fetcher
Hubwright.Agents      roles, read-only tool catalog, agent runner
Hubwright.Engine      session, sample tenants, diffs, skill refresher, exporters, MCP server
Hubwright.Cli         `hubwright`
Hubwright.Desktop     WinUI 3 app (Windows); everything else is cross-platform net10.0
```

## The flow

```
 tenant ──(read-only collector / PnP script)──▶ TenantSnapshot ──▶ RuleEngine ◀── skills (rules.yaml)
                                                     │                  │
 questionnaire ──▶ BlueprintGenerator ──▶ Blueprint ─┘(ToSnapshot)      ▼
                                              │                    Assessment ──▶ reports, dashboard
                                              ▼                         │
                                 MockupRenderer, Exporters              ▼
                                                              AI roles (explain, plan, propose)
```

**Snapshot first.** A scan writes one JSON file under `~/.hubwright/tenants/<name>/snapshots/`.
Rules, reports, diffs, previews and the AI all read snapshots. Only the collector talks to the
tenant. A blueprint becomes a snapshot too (`Blueprint.ToSnapshot`), so a design is checked by the
same rules as the tenant it will replace.

**Rules decide; the AI explains.** Findings, counts and scores come from the rule engine. The AI
gets them through tools (`get_snapshot_summary`, `list_findings`, `get_rule`, `query_sites`,
`get_skill`, `check_blueprint`, `preview_theme`, `get_site_design`, `list_site_patterns`) and
writes the narrative, plans and drafts. Its only writes are `propose_blueprint` and
`propose_site_design`, which file a proposal for a person to accept.

**Unknown is not pass.** A collector that can't read something records a `ScanIssue`. The facts
that depend on it are unknown, and the rules over them are "not assessed" and left out of the
score.

## Where to change things

| To… | Change |
|---|---|
| add or tune a best practice | a skill folder (no code) |
| let rules see new data | the snapshot model, a collector, and a fact in `Rules/Facts.cs` |
| add an AI capability | a tool in `Agents/ToolCatalog.cs`, a role prompt in `Agents/Roles.cs` |
| add an export | `Engine/Exporters.cs` (CSV: `Core/Monitoring/Drift.cs` `CsvExport`) |
| add a journey stage rule | `Core/Journey/IntranetJourney.cs` (pure), fed by `Engine/JourneyStatus.cs` |
| add a security baseline setting | `skills/security-baseline/baseline.yaml` (cited); a new Graph property or `Set-PnPTenant` parameter also needs `SecurityBaseline.GraphProperties` or `PnpScripts.TenantParameters`, and a test |
| add a build or launch step | `Core/Implementation/ImplementationPlanner*.cs`, a hint in a skill's `hints.yaml`, a test |
| add a Run check | `Core/Monitoring` (pure) and `Engine/Monitoring.cs` (`IntranetMonitor`) |
| add a CLI command | a file per stage in `src/Hubwright.Cli` (`Commands` partial class) and its desktop page |
| change the mockup | `Core/Preview/MockupRenderer.cs` |
| add or tune a site design pattern | `skills/site-design/patterns.yaml` (no code); a new section kind also needs `SiteDesigner.Sections`, its sketch in `SitePrototype` and its web part in `PnpScripts.WebPartFor` |
| explain a new write path to people | `Engine/Awareness.cs` (Home's scope card and `hubwright scope`) |
