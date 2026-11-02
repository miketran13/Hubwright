# 🏗️ Hubwright

**Plan, build, secure and run your SharePoint intranet, the way Microsoft recommends.**

Hubwright is an open-source tool for SharePoint admins and intranet owners. It walks you through
six stages: **Connect** your tenant, **Assess** it against Microsoft's intranet guidance (every
rule cites the page it comes from), **Plan** the target intranet with its structure, look and
policies, **Build** it with documented APIs and PnP PowerShell, **Secure** it with a baseline, and
**Run** it with drift checks and history. Scans only read; nothing changes until you pick the steps
and confirm them.

> [!WARNING]
> **Demo and test use only, not for production.** Hubwright is a proof of concept. It has been
> tried only on sample data and on demo and test tenants, never on a production tenant. The Build
> and Secure stages can create sites, change hubs, themes and tenant sharing settings. Don't run
> them against a tenant that people rely on. Use a developer or test tenant, review every step and
> request before you confirm, and check the results yourself. The software is provided "as is",
> without warranty of any kind (see [LICENSE](LICENSE) and [Disclaimer](#disclaimer)).

> _Screenshot or GIF of the desktop app goes here._

## What it does

| | |
|---|---|
| **Assess** | 76 rules in 17 skills, plus a catalogue of 25 built-in site templates and 30 look book packages, and 19 site design patterns: information architecture, hubs, navigation, home site, permissions, sharing, Copilot readiness, lifecycle, content, modernization, branding, performance, governance and search. You get a score per category, findings with fixes, and a "not assessed" list for anything it couldn't read. |
| **Cite** | Each rule is either **Microsoft-documented**, with the source URL and the exact sentence it enforces, or a **heuristic** with a threshold you can tune. `hubwright skills refresh` checks every quote against the live page, so you know when Microsoft's guidance changes. |
| **Propose** | After a scan, Hubwright asks a few questions (pre-filled from what it found) and writes an intranet proposal: target hubs and sites, a plan for every existing site, new sites built from SharePoint's own site templates and the look book packages, the theme, policies and a phased roadmap. The target is checked by the same rules, so you see the score move. |
| **Design** | Starting from scratch? A short questionnaire produces a blueprint: home site, hubs and sites, navigation, sharing and lifecycle policies, sensitivity labels and an accessible theme. The blueprint is then checked against the same rules. |
| **Preview** | A theme designer generates the SharePoint palette from your brand colour and checks WCAG 2.1 contrast. A home page mockup shows it in all four header layouts, desktop and mobile, along with the hub structure. You can share it as one HTML file. |
| **Export** | Exports a PnP PowerShell provisioning script, a site script, and the theme as JSON plus `Add-SPOTheme`. Review them and run them yourself. |
| **Build** | Or build the plan from the app: pick everything or single steps, preview the exact requests, confirm, and Hubwright creates the sites, registers and associates hubs, sets the home site and navigation, applies templates, theme, header and logo, makes hubs organization news sites and drafts a welcome post on each (never published), and drafts each site's designed home page for its owner to publish (or publishes it and makes it the home page, for a site where you choose Publish and confirm). Steps without a documented API (brand center, app bar, multilingual, the Teams dashboard, labels) open in Hubwright's own browser pane with the values to enter. Every write is audited; nothing is ever deleted or archived automatically. |
| **Secure** | A security baseline in three profiles (Standard, Strict, Regulated): sharing level, default links, Anyone link expiry and permissions, guest expiry and resharing, legacy authentication, idle sign-out, plus guided steps for unmanaged devices, labels, DLP, SharePoint Advanced Management and backup. Every setting is cited, read first, applied after you confirm, and can be restored. |
| **Run** | Drift from the plan and the baseline since they were built, the grade across scans, owner lists and findings for Excel, a status to paste into Teams, and scheduled read-only scans. |
| **Ask** | An AI assessor, information architect, brand designer and governance advisor work over Hubwright's read-only tools. It can use Claude or Copilot (subscription CLIs), the Anthropic API, **Azure AI Foundry**, or a **local model** (Ollama, Foundry Local, LM Studio). AI is optional: everything above works without it. |

## Quick start

```powershell
dotnet build Hubwright.slnx          # Windows; on macOS/Linux: dotnet build src/Hubwright.Cli
$hubwright = "src/Hubwright.Cli/bin/Debug/net10.0/hubwright"

& $hubwright sample fabrikam                    # a made-up messy intranet
& $hubwright assess --tenant fabrikam           # Markdown report
& $hubwright assess --tenant fabrikam --format html --open
& $hubwright blueprint new --tenant contoso --answers docs/examples/answers.json
& $hubwright preview --tenant contoso --open    # theme + home page mockup
& $hubwright theme generate --primary "#f2c811" # fails WCAG, suggests a passing colour
```

Desktop app (Windows): `./run.ps1 -Preview`, which loads the sample tenants into `~/.hubwright-preview`.

Your own tenant: see [docs/getting-started.md](docs/getting-started.md). In short, run
`tools/register-app.ps1` (it creates a read-only app), then `hubwright login` (it opens your browser
and finds your tenant), then `hubwright scan` and `hubwright status`. In the desktop app, **Setup**
walks you through the same four steps, **Home** shows where you are in the journey, and every CLI
command has a page. If you can't consent to an app, run
`tools/collect-snapshot.ps1` with PnP PowerShell and use `hubwright import`.

## Skills: best practice as data

Every check lives in `skills/<id>/` as `SKILL.md` (guidance the AI reads) and `rules.yaml` (checks
in a small, safe expression language):

```yaml
- id: hubs.nav-sites
  title: Too many links in hub navigation
  scope: hub
  when: navLinks > $maxLinks
  params: { maxLinks: 100 }
  basis: documented
  source: https://learn.microsoft.com/en-us/sharepoint/planning-hub-sites
  quote: "From a user experience and performance perspective, the recommended number of links is no more than 100."
  reviewed: 2026-10-27
```

Your organization can override a built-in skill by copying it into its own folder. Each tenant can
tune thresholds in `overrides.yaml`. To share a new best practice with everyone, open a pull request.
See [docs/skills.md](docs/skills.md).

## Docs

- [Getting started](docs/getting-started.md)
- [Intranet playbook](docs/intranet-playbook.md): roles, launch checklist, governance cadence
- [Architecture](docs/architecture.md)
- [Writing skills and rules](docs/skills.md)
- [Permissions](docs/permissions.md)
- [Roadmap](docs/roadmap.md)

## Status

Hubwright is an early demo. The rules, scoring, proposal, build plan, theme, preview, exports,
skills and CLI are covered by tests. Sign-in, the live collector and the PnP build steps (templates,
headers, home site) have been tried on a demo tenant; the REST build steps and the PnP collector
script have not yet. Expect gaps, and please open an issue with what you see.

## Disclaimer

Hubwright is a community demo, shared for learning and evaluation. It is not a supported product
and is not intended for production use.

- It has been tested only with sample data and demo or test tenants.
- Its rules, scores and proposals are guidance, not professional, legal or compliance advice.
  Microsoft's guidance changes; check the cited sources and your own policies before you act.
- Changes made through the Build and Secure stages (sites, hubs, themes, sharing and security
  settings, published pages) are your responsibility. Try them on a test tenant first and keep
  your own record of the settings you change.
- AI answers can be wrong. The numbers come from the rules, but read the explanations critically.

The software is provided "as is", without warranty of any kind, express or implied. The authors
and contributors are not liable for any claim, damage or other liability arising from its use (see
[LICENSE](LICENSE)).

## License

MIT licensed. The desktop's colour icons are Fluent UI System Icons (MIT, `src/Hubwright.Desktop/Assets/Icons`). Hubwright is not affiliated with or endorsed by Microsoft. SharePoint and Microsoft 365
are trademarks of Microsoft.
