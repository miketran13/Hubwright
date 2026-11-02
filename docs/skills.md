# Writing skills and rules

A **skill** is one folder of best practice. Hubwright ships 14 skills in `skills/`. You can add your
own or replace a built-in one without touching code.

```
skills/<id>/
  SKILL.md         front matter + guidance (the AI reads this to explain findings)
  rules.yaml       checks
  questions.yaml   optional: decisions the intranet proposal asks (no scan can answer them)
  templates.yaml   optional: ready-made sites (Microsoft site templates, look book packages)
  hints.yaml       optional: what, why, how and an example for suggestions and implementation steps
```

## Layers

Skills load in three layers. A later layer **replaces** a skill with the same id:

1. **Built-in**: `skills/` next to the executable (or in the repository).
2. **Organization**: the folder named in `hubwright.json` → `skills.organizationDirectory`, for
   example a file share or a git checkout your team maintains.
3. **User**: `~/.hubwright/skills/`. `hubwright skills new` and `skills fork` write here.

When your copy is older than the built-in version it replaces, `skills validate` warns you, so you
can review what changed upstream.

Per tenant, `~/.hubwright/tenants/<name>/overrides.yaml` disables rules or changes thresholds
without forking (see `docs/examples/overrides.yaml`).

## SKILL.md

```markdown
---
id: hub-sites                 # lower-case-with-hyphens, same as the folder
title: Hub sites
version: 1.0.0                # bump when rules change
category: hubs                # the score category
appliesTo: both               # both | existing | scratch
owner: your-team
description: One sentence.
sources:
  - url: https://learn.microsoft.com/en-us/sharepoint/planning-hub-sites
    title: Planning your SharePoint hub sites
    reviewed: 2026-10-27
---

# Hub sites
Plain-language guidance: what, why, how to fix.
```

## rules.yaml

```yaml
rules:
  - id: hubs.nav-sites              # area.name, unique across all skills
    title: Too many links in hub navigation
    scope: hub                      # tenant | site | hub
    filter: "associatedSites > 0"   # optional: which targets to look at
    when: "navLinks > $maxLinks"    # the VIOLATION: true raises a finding
    params: { maxLinks: 100 }       # $names; tenants can override them
    severity: medium                # info | low | medium | high | critical
    weight: 2                       # optional; defaults from severity
    basis: documented               # documented | heuristic
    source: https://learn.microsoft.com/en-us/sharepoint/planning-hub-sites
    quote: "From a user experience and performance perspective, the recommended number of links is no more than 100."
    reviewed: 2026-10-27
    message: "{title} has {navLinks} navigation links."   # {fact} and {$param} placeholders
    fix: Group sites under labels, or use audience targeting.
```

**Advisory rules** (`kind: advisory`, or no `when`) are recommendations Hubwright can't verify from
tenant data, such as "use DLP to block Anyone links". They are listed under "To confirm" and never
scored.

### The evidence rule

- `basis: documented` **requires** `source`, `quote` (the sentence, word for word) and `reviewed`.
  `skills validate` fails without them, and so does CI.
- `basis: heuristic` is for judgment calls. Reports label them as heuristics. Give them a parameter
  so each tenant can set its own threshold.
- `hubwright skills refresh` fetches each source and checks the quote is still on the page (ignoring
  markup, case, whitespace, and quote and dash styles). When a quote disappears it files a proposal.
  With `--ai`, the skills librarian drafts the edit. Accepting a proposal copies the skill to your
  layer with a new version and today's review date. Built-in files are never edited in place.

## questions.yaml

Decisions the intranet proposal asks in its Decisions step. Give `choices` for a pick list (the
user can still type their own answer) and a `default` from the choices:

```yaml
questions:
  - id: life.inactive
    text: "What happens to a site nobody has used for a while?"
    why: "The inactive site policy notifies owners, then can make the site read-only and archive it."
    choices: ["Owners confirm, then read-only, then archive", "Report only"]
    default: "Owners confirm, then read-only, then archive"
```

## templates.yaml

The `site-templates` skill catalogues Microsoft's built-in site templates and the look book PnP
packages. The proposal suggests one for every new site. Each entry needs Microsoft's description,
the page it comes from, a `quote` from that page and a `reviewed` date. `skills refresh` checks
template quotes the same way it checks rule quotes.

```yaml
templates:
  - id: human-resources
    name: "Human resources"
    source: microsoft            # or pnpPackage (then package: https://…/template.pnp)
    kind: communication          # or teamGroup
    description: "Provide employees access to information regarding their benefits, career, compensation, and organization policies."
    url: https://support.microsoft.com/en-us/office/apply-and-customize-sharepoint-site-templates-39382463-0e45-4d1b-be27-0e96aeec8398
    quote: "Provide employees access to information regarding their benefits, career, compensation, and organization policies."
    reviewed: 2026-10-28
    suggestFor: ["hr", "human resources", "benefits"]   # title matches count 3, purpose 1, phrases double
    sections: ["hero", "news", "quicklinks", "documents", "faq"]   # Hubwright's sketch for previews
```

To add your organization's own templates, fork `site-templates` (or add a `templates.yaml` to any
organization skill) and use your own page as the `url`. A template with the same id in a later
layer replaces the built-in one.

## hints.yaml

Every suggestion in a proposal and every implementation step can show **what** it is, **why** it
matters, **how** to do it and an **example**. Proposals and plans refer to hints by id (for
example `register-hub`, `archive-site`, `site-logo`). The "why" is backed by the `rules` the hint
names: their Microsoft quotes are shown as the evidence. A hint can quote a page itself (`source`,
`quote`, `reviewed`), and `skills refresh` checks that quote like any other.

```yaml
hints:
  - id: associate-with-hub
    title: "Associate a site with a hub"
    what: "Joins a site to a hub's family; it takes the hub's navigation and theme."
    why: "Sites in no hub are hard to find."
    how:
      - "Check the site belongs to the hub's function."
      - "Associate it (Hubwright calls JoinHubSite)."
    example: "\"HR – Benefits\" joins HR Hub."
    rules: [ia.orphan-sites, hubs.nav-sites]
```

Quote free text that contains a colon. A roadmap line that comes from a rule needs no hint: the
rule's title, fix and quote explain it.

## The expression language

| | |
|---|---|
| Logic | `and`, `or`, `not`, parentheses |
| Compare | `==  !=  >  >=  <  <=`, `x in ['a', 'b']` |
| Math | `+ - * /` (division by zero is unknown) |
| Values | numbers, `'text'`, `true`, `false`, `null`, `$param` |
| Functions | `count(list)`, `days_since(date)`, `lower(text)`, `contains(text, part)`, `coalesce(a, b)`, `known(x)` |

Logic is **three-valued**. A fact that wasn't collected is *unknown*. A comparison with unknown is
unknown, and a rule that is unknown for a target is "not assessed": it is never a pass and never a
finding. `x == null` asks "is this missing?" and is always true or false. Kleene logic applies, so
`false and unknown` is `false` and `true or unknown` is `true`.

Text comparisons ignore case. Enum facts compare by name (`settings.sharingLevel == 'Anyone'`).

### Facts

`hubwright facts tenant|site|hub` lists every fact with its meaning. They come in two kinds:

- **Snapshot properties**, under their JSON names: `ownerCount`, `isPublic`, `kind`,
  `permissions.eeeuGrants`, `content.maxFolderDepth`, `settings.defaultLinkType`, and so on.
- **Derived facts**, computed once in `Hubwright.Core/Rules/Facts.cs`: `navLinks`, `navDepth`,
  `navEmptyLabels`, `daysSinceActivity`, `inHub`, `hubCount`, `maxHubDepth`, `associatedSites`,
  `themesFailingContrast`, and so on.

A new derived fact is a small code change (a line in `FactCatalog`) plus a test. Everything else
is data.

## Contributing a skill

1. `hubwright skills new my-skill --dir skills`, or copy an existing skill.
2. Write the guidance. Cite Microsoft Learn or other reputable sources, and quote exactly.
3. `hubwright skills validate`, then `hubwright assess --tenant fabrikam` to see your rule fire.
4. Add a test if you added a fact. Open a pull request with the **New skill or rule** template.
