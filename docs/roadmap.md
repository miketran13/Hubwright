# Roadmap

Hubwright follows an intranet owner through six stages: Connect, Assess, Plan, Build, Secure and
Run.

## Done
- **Connect:**
  - the Setup page has four steps: prerequisites, a read-only app registration, browser sign-in and the first scan;
  - Home and `hubwright status` show the journey;
  - installers come from `release.yml`, with self-contained zips, checksums, an SBOM and optional signing.
- **Assess:** 17 skills and 76 rules (59 Microsoft-documented with verified quotes, 17 heuristic),
  with layering, validation and live source-drift checks (weekly in CI with `--strict`). The rule
  language uses three-valued logic, and scoring leaves unknowns out.
- **Plan:**
  - one plan, from the scan or from scratch, checked by the same rules;
  - a structure preview and a clickable site prototype;
  - Brand and look: the theme generator with WCAG checks, logo checks and the page mockup;
  - 25 Microsoft site templates and 30 look book packages, with sources;
  - site designs: 19 purpose patterns (site-design skill) give every site a home page, navigation and the practices behind them, previewed with sample content; the architect can propose a redesign that waits for you to accept it.
- **Build:**
  - documented REST: site creation, hubs, themes and sharing;
  - PnP PowerShell: home site, navigation, templates, header, logo, organization news sites, draft welcome posts and draft designed home pages (the owner publishes them);
  - guided steps: brand center, app bar, multilingual, audience targeting, the Teams dashboard, labels and policies.

  Each step checks first, you type the tenant name to confirm, everything goes to a hash-chained audit log, and nothing is ever deleted.
- **Secure:**
  - the security baseline in three profiles: 20 cited settings, applied through Graph or `Set-PnPTenant` allow-lists or guided, read first and restorable;
  - security findings.
- **Run:**
  - drift from the plan and the baseline, and the grade across scans;
  - owner lists and findings as CSV, and a status summary for Teams or email;
  - scheduled read-only scans.
- **AI:** roles over read-only tools, through the Claude/Copilot CLIs, Anthropic, Azure AI Foundry, GitHub Models or local OpenAI-compatible models. There is an Ask side pane on every page and an MCP server.

## Next
1. **Run the collectors and the Secure stage against real tenants**, and fix what breaks: the admin settings permissions, the Graph settings read, SAM policy state and the hub navigation endpoint.
2. **Read more tenant settings** so fewer baseline settings show as "not read":
   - Graph `sharepointSettings` (legacy authentication, resharing, idle sign-out);
   - unmanaged-device policy;
   - sensitivity labels and the DAG reports.
3. **Harvest the remaining sources:**
   - InfoPath and workflow retirement notices;
   - custom script and the current external sharing article;
   - the multi-accent theme schema and Restricted Content Discovery.
4. **An MSIX package** with a store-signed identity, once a signing certificate exists.
5. **A multi-tenant view** for partners who manage several tenants.
6. **App-only scheduled scans** with a certificate, as a separate opt-in with its risk explained (see permissions.md).
