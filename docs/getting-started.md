# Getting started

## Install

Download from the project's GitHub releases (built by `.github/workflows/release.yml`; no .NET needed):

| Download | What it is |
|---|---|
| `Hubwright-win-x64.zip` / `Hubwright-win-arm64.zip` | The desktop app with the `hubwright` CLI beside it. Unzip and run `Hubwright.Desktop.exe`. Needs the WebView2 runtime (already on Windows 11). |
| `hubwright-cli-<os>.zip` / `.tar.gz` | The CLI alone, for Windows, Linux or macOS. |
| `SHA256SUMS.txt` | Checksums to verify the downloads. |

Release builds are signed only once the maintainer adds a code-signing certificate; until then
Windows SmartScreen may warn on first run. A winget manifest template is in `packaging/winget/`.

## Build from source

You need the .NET 10 SDK. On Windows, for the desktop app, you also need the WebView2 runtime
(already installed on Windows 11).

```powershell
git clone <this repo>; cd hubwright
dotnet build Hubwright.slnx
dotnet test tests/Hubwright.Tests
```

`hubwright` below means `src/Hubwright.Cli/bin/Debug/net10.0/hubwright`. Run
`dotnet publish src/Hubwright.Cli -c Release` for a standalone build.

## The journey

Hubwright follows the six stages of an intranet. **Home** in the desktop app, and `hubwright status`
in the CLI, show where you are in each one and what to do next.

| Stage | What you do | Desktop | CLI |
|---|---|---|---|
| 1 · Connect | Prerequisites, register the read-only app, sign in, first scan | Setup | `setup`, `login`, `scan`, `import` |
| 2 · Assess | Grade, findings with Microsoft's sources, hub structure, reports | Score, Findings, Structure, Reports (each warns about the sites you lack permissions on) | `assess`, `findings`, `access`, `facts` |
| 3 · Plan | Intranet plan from your scan (or from scratch when there is none), brand and look, site walk-through | Intranet plan (with its Design check), Site designs, Brand and look (Theme and logo, Page mockup), Site templates | `proposal`, `design`, `blueprint`, `theme`, `preview`, `templates` |
| 4 · Build | Dry run, confirm, then automatic and guided steps: structure, look, then launch (organization news, welcome drafts, brand center, Teams dashboard) | Build | `build` |
| 5 · Secure | Security findings, and the baseline in three profiles: check, preview, apply, restore | Security, Baseline | `secure`, `secure baseline\|plan\|check\|preview\|run\|restore` |
| 6 · Run | Drift from the plan and baseline, grade across scans, owner lists, status to share, scheduled scans | Drift and trend, Owners, History, Audit log | `drift`, `trend`, `owners`, `summary`, `schedule`, `diff`, `audit` |
| Anywhere | What Hubwright reads and changes, with which permissions; AI over the same read-only tools; best practices as data | Home (What Hubwright does in your tenant), Ask AI (side pane), AI suggestions, Best practices, Settings | `scope`, `ask`, `chat`, `mcp`, `proposals`, `skills`, `doctor` |

## 1. Explore without a tenant

```powershell
hubwright sample fabrikam            # messy: subsites, one overloaded hub, Anyone links, ownerless sites
hubwright sample northwind           # healthy
hubwright assess --tenant fabrikam --format html --open
hubwright findings --tenant fabrikam --severity high
hubwright preview --tenant fabrikam --from snapshot --primary "#6b4fd8" --open
```

## 2. Start from scratch

Interactive questionnaire:

```powershell
hubwright blueprint new --tenant contoso
```

Or answers from a file (`docs/examples/answers.json`):

```powershell
hubwright blueprint new --tenant contoso --answers docs/examples/answers.json
hubwright blueprint check  --tenant contoso      # the design against every rule
hubwright preview          --tenant contoso --layout extended --open
hubwright blueprint export --tenant contoso      # provision.ps1, site-script.json, theme.ps1, theme.json, mockup.html
```

Answer fields: `organization`, `tenantPrefix`, `employees`, `departments`, `regions`, `languages`,
`goals` (news, policies, onboarding, collaboration, communities, copilot), `externalCollaboration`,
`allowAnyoneLinks`, `brandColor`, `headerLayout` (minimal, compact, standard, extended).

## 3. Assess your tenant

> [!WARNING]
> Hubwright is a demo, tested only on sample data and demo or test tenants. Use a developer or test
> tenant, not production: the Build and Secure stages change real settings. See the
> [disclaimer](../README.md#disclaimer).

```powershell
./tools/register-app.ps1                         # once, with an admin who can consent
hubwright login                                   # opens your browser; finds contoso.sharepoint.com from Graph
hubwright scan                                    # or: hubwright scan --scope https://contoso.sharepoint.com/sites/hr-hub
hubwright assess --format html --open
hubwright diff                                    # after a second scan: what improved
```

**In the desktop app:** open **Setup** and follow its four steps. Step 1 (PowerShell 7 and PnP
PowerShell) is needed only to build, so you can skip it for now. **Register the app…** (once) opens
PowerShell, signs you in with your browser, creates a read-only app and opens the admin consent page.
Then **Sign in**, finish in your browser, and **Scan now**. Home then shows your grade and the next step.

**How you sign in** (`hubwright login --method …`, or the Setup page):

| Method | What happens |
|---|---|
| `browser` (default) | Your default browser opens the Microsoft sign-in page. Close the tab when it says you're signed in. Works on Windows, macOS and Linux. |
| `windows` | The Windows account broker (WAM): single sign-on with the work account already on this PC. |
| `device` | A code to enter at microsoft.com/devicelogin from any device. For servers and remote sessions without a browser. |

You don't need to type the tenant name: after sign-in, Hubwright asks Graph for the root site
(`GET /v1.0/sites/root`) and uses its address. If you did type one and it was wrong, Hubwright
tells you and uses the one Graph reported.

Everything the CLI does is also in the desktop app: see the table under "The journey" above.

Can't register an app? Collect offline with PnP PowerShell:

```powershell
./tools/collect-snapshot.ps1 -Tenant contoso -ClientId <your PnP app id>
hubwright import snapshot.json
```

### Turn the scan into an intranet proposal

After a scan, the Dashboard offers **Start an intranet proposal** (or open **Intranet proposal**).
Hubwright pre-fills the questions from what it found: your organization's name, the functions and
places in your hub names, your brand colour from the tenant theme, and your sharing setup. You
confirm or change them, choose how far to move from today (keep what works, balanced,
restructure), and answer the skills' decisions. The proposal then contains:

- the target structure: home site, one hub per function, a Locations hub for places, with the hubs
  and sites you already have reused;
- a plan for **every existing site**: keep, move to a hub, modernize, review, archive or retire,
  with the reasons and the rules behind them;
- new sites with the best-fitting **SharePoint site template** (Microsoft's built-in ones, applied
  from Settings → Apply a site template) or **look book package** (`Invoke-PnPTenantTemplate`);
- the theme with its contrast checks, a live home page mockup, policies, and a four-phase roadmap;
- the target checked against the same rules, so you see the score move (Fabrikam: 45 → 98);
- optionally an executive summary from the AI architect, written from those numbers.

```powershell
hubwright proposal new --open                       # asks the questions in the terminal
hubwright proposal answers --out a.json             # or: edit the seeded answers…
hubwright proposal new --answers a.json --ai        # …and build from them, with the AI summary
hubwright proposal use                              # make it the blueprint: preview, export scripts
hubwright templates gallery --open                  # every template and package, sketched in your theme
```

**Structure preview.** The proposal's Target structure section is interactive: the target (home
site, hubs and their sites, new or kept), **Today → Target** side by side, and the list of
**Moves**. Select a site to see where it is today, where it goes, what happens to it and why. Every
roadmap line and every site's plan has a **What, why, how** panel with an example and the Microsoft
quotes behind it. `hubwright proposal structure --open` opens the preview on its own.

**Walk through the new intranet.** Below the structure, the proposal (and the Build page's
**Site preview** tab) shows every proposed site as a page: home site, hubs and their sites, kept and
new, in the new theme and logo, with the app bar's global navigation, each hub's mega menu and
breadcrumbs. Pages are filled with sample content from each site's template sections (news,
events, quick links, documents, people, FAQ…), labelled as sample. ← and → (or Back and Forward)
move between pages; 📱 shows phone width; **Open in site preview** in the structure view jumps to a
site. `hubwright proposal sites --open` opens it on its own.

**Theme and logo.** **Theme and logo…** on the proposal (or on the Build page's Look tab) opens
the editor: brand and background colours, header layout, every palette slot if you want, and your
logo. The logo is checked against Microsoft's guidance (fits within 64 x 64 to 192 x 64 pixels; a
square thumbnail) and its colours become theme suggestions, each adjusted to pass WCAG 2.1 AA. Save
updates the proposal and the implementation's theme steps; a theme that was already applied is set
to run again.

### Build the plan

On the proposal, select **Build…** (or open **Build**). The right side has three tabs:
**Step** (the selected step, with what, why, how and an example), **Structure** (the preview, with
each site's implementation status), **Site preview** (walk through every site, with its status) and
**Look** (the home page in your theme and logo). The plan
lists every step by phase:

- **automatic** steps Hubwright runs with documented APIs: create the home site, hubs and new sites
  (Standard communication, Showcase and Blank are applied at creation), register and rename hubs,
  associate sites with hubs, take extranets out of hubs, add and apply the theme, set tenant sharing;
- **guided** steps you do in Hubwright's browser pane on the right, with the values listed and a
  Copy button for each: hub navigation, home site and app bar, other Microsoft templates (Settings →
  Apply a site template), look book packages (the PnP command is filled in), default links, owners,
  labels, lifecycle policies, modernizing, archiving and retiring. Select **Mark done** when finished.

**PnP PowerShell steps.** The home site, hub and home site navigation, Microsoft's templates
(Human resources, Department, Organization home…), look book packages, each site's header colour and
layout, and the logo run as PnP PowerShell scripts that Hubwright writes and runs with your sign-in.
Install PowerShell 7 and PnP PowerShell once from **Setup → Prerequisites** (or
`hubwright setup --install`). Each step shows its script; **Open ↗** on every step opens
its site. The bar at the very top of Microsoft 365 is the organization theme, set by a Global
Administrator in the Microsoft 365 admin center: a guided step lists the colours.

Select **Automatic** (or tick single steps; what they depend on is included), **Preview requests**
to see every request without sending anything, then **Run selected** and type the tenant name.
Each step checks first and skips what is already in place, so you can stop and run again. Automatic
steps need the app's write permissions once: **Grant write permissions** (an admin consents). If your
app was created read-only, **Add write permissions to the app…** updates it.

```powershell
./tools/register-app.ps1 -AppId <your app id>     # only if your app was created read-only
hubwright build login                           # consent to the write permissions
hubwright build                                 # the plan for the latest proposal
hubwright build preview --all-automatic         # every request, nothing sent
hubwright build run --steps theme,home,hub-hr-hub,register-hr-hub
hubwright build done home-site                  # after a guided step
```

### Secure the tenant

```powershell
hubwright secure baseline --profile standard        # the tenant against the baseline (✓ in place, ✗ to change, ? not read)
hubwright secure plan --profile standard            # the steps: graph, pnp or guided
hubwright secure check --profile standard           # read-only: marks what is already in place
hubwright secure preview --profile standard --all-automatic
hubwright secure run --profile standard --all-automatic   # asks you to type the tenant name
hubwright secure restore --profile standard         # the values Hubwright replaced; `restore run --all-automatic` puts them back
```

In the desktop app: **Security** shows the findings and how each profile compares; **Baseline** runs
the same steps as Build, with the source quoted for each setting. Write permissions are the same
as Build's.

### Run it

```powershell
hubwright drift                                     # what changed since it was built (exit 3 when something drifted)
hubwright trend                                     # the grade of every scan
hubwright owners --csv owners.csv                   # sites with fewer than two owners, for Excel
hubwright findings --csv findings.csv
hubwright summary                                   # paste into Teams or an email
hubwright schedule weekly                           # Windows Task Scheduler, read-only, never prompts
```

See the [intranet playbook](intranet-playbook.md) for the roles and the cadence.

## 4. Add AI (optional)

Pick a backend in `~/.hubwright/hubwright.json` (or Settings in the app):

```json
{ "ai": { "backend": "local", "local": { "endpoint": "http://localhost:11434/v1", "model": "qwen3:8b" } } }
```

| Backend | Setup |
|---|---|
| `claude` | Claude Code CLI installed and logged in (subscription) |
| `copilot` | GitHub Copilot CLI installed and logged in |
| `anthropic` | `ANTHROPIC_API_KEY` |
| `foundry` | `ai.foundry.endpoint` + `deployment`, and `az login` (DefaultAzureCredential) |
| `local` | Ollama, Foundry Local or LM Studio running an OpenAI-compatible endpoint |

```powershell
hubwright doctor                                  # which backends are ready
hubwright assess --ai                             # adds the assessor's summary and 30/60/90 plan
hubwright ask "Which overshared sites should we fix first?"
hubwright chat --role architect
```

To use Hubwright's tools from Claude Desktop, VS Code or any MCP client, point the client at
`hubwright mcp --tenant contoso`.

## 5. Tune and extend

- `~/.hubwright/tenants/<name>/overrides.yaml`: disable rules or change thresholds
  (`docs/examples/overrides.yaml`).
- `hubwright skills new <id>` / `skills fork <id>`: your own skills, in `~/.hubwright/skills`.
- `hubwright skills refresh`: check every documented rule's quote against its live source.
