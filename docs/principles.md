# Hubwright Constitution

## Core Principles

### I. Read-only by default, writes only on request
Scans, assessments and proposals only read and ask only for read scopes. Writing is a separate, opt-in
step: a person picks the steps of a proposal to implement, previews the exact requests, confirms by
typing the tenant name, and consents to the app's write scopes. Writes go through one
allow-listed client, only to documented endpoints (or, for what SharePoint's REST API does not document, generated PnP PowerShell scripts limited to a fixed list of cmdlets), each checked first so nothing already in place
is changed, and each recorded in the hash-chained audit log. Deleting, archiving and unregistering
are never automatic; they are guided steps a person does.

### II. Evidence over opinion
Every rule is either Microsoft-documented (with source URL, exact quote and review date) or an
explicit heuristic with a tunable threshold. Quotes are re-checked against their sources
(`skills refresh`). Limits are never written from memory.

### III. Rules decide, AI explains
Findings, counts and scores come from deterministic rules over a snapshot. AI is optional. It
explains, plans and proposes through read-only tools, and it never invents numbers.

### IV. Unknown is not pass
Data that could not be read makes a rule "not assessed". It is never counted as passing.

### V. Skills are data
Best practice lives in versioned skill folders that anyone can add, override by layer, and
contribute by pull request. The rule language is parsed, never executed as code.

## Constraints
- .NET 10 with warnings as errors. The core, CLI and MCP server are cross-platform; the desktop is WinUI 3.
- Open source (MIT). No vendor or private branding, and no tenant data, ids or keys in the repository.
- AI backends: Claude/Copilot CLIs, Anthropic, Azure AI Foundry, GitHub Models, local OpenAI-compatible models.

## Workflow
Tests and `hubwright skills validate` gate every change in CI. A new fact or tool comes with tests.

**Version**: 1.2.0 | **Ratified**: 2026-10-27 | **Last Amended**: 2026-10-29 (Principle I: PnP PowerShell steps)
