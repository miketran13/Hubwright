# Intranet playbook

How to run a SharePoint intranet with Hubwright, from first scan to everyday operation. Lines marked
**(Microsoft)** follow the page linked next to them; Hubwright's skills quote it and
`hubwright skills refresh` checks the quote. Everything else is **Hubwright's recommendation**:
experience-based defaults you can change.

## Roles

| Role | Who | Owns | In Hubwright |
|---|---|---|---|
| Intranet owner | Communications or internal comms lead | The home site, global navigation, news and the roadmap | Plan, Build (structure and look), Run (summary) |
| SharePoint administrator | IT | Tenant settings, the security baseline, Hubwright's app registration | Setup, Secure, Build (automatic steps) |
| Global administrator | IT | Brand center, the Microsoft 365 header, Conditional Access | Guided steps that need this role |
| Hub owners | One per department or region | The hub's navigation, news and associated sites | Their hub's steps; owner lists |
| Site owners | Two per site | Content, members and sharing on their site | Owner attestation (Run → Owners) |
| Content authors | Anyone who publishes | News posts and pages, from the templates | Welcome drafts and page templates |

Hubwright's recommendation: name these people before you build. **(Microsoft)** "Information
architecture for your organization is most effective when many types of roles collaborate on your
intranet" ([Learn](https://learn.microsoft.com/en-us/sharepoint/information-architecture-modern-experience)).

## Before launch

1. **Connect and assess.** Use Setup's four steps. Read the high-severity findings first. "Not assessed" means
   Hubwright couldn't read it, so treat it as unknown, not as fine.
2. **Plan with stakeholders.**
   - Make the intranet plan from your scan.
   - Walk the hub owners through the site preview.
   - Agree the global navigation as a handful of task-based links.
3. **Secure first.**
   - Apply the Standard baseline, or Strict for sensitive work.
   - Start Restricted access control and the Data access governance reports for the sites the findings flag.
4. **Build in phases:**
   1. Risks.
   2. Structure: home site, hubs and navigation.
   3. Look: theme, header and logo.
   4. Launch: organization news, welcome drafts and the brand center.

   Run a dry run (Preview requests) before each phase.
5. **Content.**
   - Hub owners rewrite the welcome drafts. Hubwright marks their sample text.
   - Save the page templates.
   - Publish at least one news post per hub.
6. **The SharePoint app in Teams.** Set up the dashboard cards for the top tasks, target them by role,
   and pin the app for everyone.
7. **Pilot.** Open the intranet to a pilot group for two weeks, fix what they report, then launch.

## After launch: the governance cadence (Hubwright's recommendation)

| How often | What | Where |
|---|---|---|
| Weekly | Scheduled scan; check drift; share the status in the intranet team's channel | Run → Drift and trend (Schedule: Weekly), `hubwright summary` |
| Monthly | Review new high findings; publish news on every hub; prune navigation links nobody uses | Findings, hub navigation |
| Quarterly | Owner attestation for sites with fewer than two owners; access reviews for high-risk sites; check the baseline | Run → Owners (CSV), Secure → Baseline → Check the tenant |
| Twice a year | Refresh the skills against Microsoft's pages; review the plan with stakeholders | `hubwright skills refresh`, Intranet plan |

## When something drifts

- **A setting drifted** (for example, sharing was opened up): find out who changed it and why
  (Microsoft Purview audit). Then either apply the baseline again, or fork the skill and change the
  target if the change was right.
- **The structure drifted** (a site left its hub, the home site changed): fix it in the admin center,
  or run the build step again. The build step reads first and only changes what isn't in place.
- **Undo a baseline change:** Secure → Baseline → Restore previous values. Hubwright recorded the value
  it replaced.

## What Hubwright never does

- **No automatic deletes.** It never deletes, archives or unregisters anything.
- **Writes only from Build or Secure.** It never writes outside a Build or Secure run that you
  selected and confirmed by typing the tenant name.
- **No publishing.** It never publishes content: welcome posts are drafts.
- **Only documented calls.** It never calls an undocumented endpoint. What has no documented API is
  a guided step.

See [permissions.md](permissions.md) for the exact requests and cmdlets.
