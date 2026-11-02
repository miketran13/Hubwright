---
id: site-design
title: Site design by purpose
version: 1.1.0
category: design
appliesTo: both
description: Patterns that turn a site's purpose into a design (template, home page sections, local navigation, header, audience) and how a design is drafted and published without touching existing content.
sources:
  - url: https://learn.microsoft.com/en-us/sharepoint/information-architecture-modern-experience
    title: Introduction to SharePoint information architecture
    reviewed: 2026-10-31
  - url: https://support.microsoft.com/en-us/office/use-a-different-page-for-your-sharepoint-site-home-page-35a5022c-f84a-455d-985e-c691ab5dfa17
    title: Use a different page for your SharePoint site home page
    reviewed: 2026-10-31
  - url: https://pnp.github.io/powershell/cmdlets/Add-PnPPage.html
    title: Add-PnPPage
    reviewed: 2026-10-31
  - url: https://pnp.github.io/powershell/cmdlets/Add-PnPPageSection.html
    title: Add-PnPPageSection
    reviewed: 2026-10-31
  - url: https://pnp.github.io/powershell/cmdlets/Add-PnPPageWebPart.html
    title: Add-PnPPageWebPart
    reviewed: 2026-10-31
  - url: https://pnp.github.io/powershell/cmdlets/Get-PnPHomePage.html
    title: Get-PnPHomePage
    reviewed: 2026-10-31
  - url: https://pnp.github.io/powershell/cmdlets/Set-PnPPage.html
    title: Set-PnPPage
    reviewed: 2026-10-31
  - url: https://pnp.github.io/powershell/cmdlets/Set-PnPHomePage.html
    title: Set-PnPHomePage
    reviewed: 2026-10-31
---

# Site design by purpose

Every site should look like what it is for. Microsoft's information architecture guidance asks you
to "plan to create one site for each discrete topic, task, or unit of work", and for local
navigation: "Think about how viewers might explore your content and use local navigation to support
that exploration."

## Patterns

`patterns.yaml` lists site purposes (HR services, IT services, policies, onboarding, a project team…)
and the design each one gets:

- **template**: the ready-made SharePoint template a new site starts from (`site-templates`).
- **sections**: the home page from top to bottom: hero, news, events, quicklinks, documents,
  people, text, gallery, faq, calendar, video, conversations, tasks.
- **navigation**: the site's own links, in order. The owner creates the pages behind them.
- **headerLayout** and **audience**: who reads and who edits.
- **practices**: one sentence each, shown with the design as its explanation.

Patterns are Hubwright's judgment (`basis: heuristic`, with a `note`). Edit them, or add your own
in a user copy of this skill.

## How a site gets its design

- The home site gets the `home` pattern and hubs the `hub` pattern.
- Other sites get the pattern whose `suggestFor` words match their title (worth most) or purpose,
  among patterns of the same kind (communication or team).
- When nothing matches, the site gets the general pattern for its kind, marked **not confident**:
  the person confirms what the site is for.
- Sites that are classic, being retired or archived, or that the scan could not read get a design
  but are **blocked** from the Build until that is resolved.
- An agent can propose a redesign (`propose_site_design`). Hubwright checks it (known sections,
  real templates, plain labels) and files it in AI suggestions. Nothing changes until a person accepts it.

## Applying a design

- **Nothing existing is replaced.** Applying a template to an existing site doesn't merge with its
  content, so existing sites never get one. Hubwright drafts a new page, `Proposed-home`, with the
  design's sections as web parts and placeholder text. The page is not published.
- **The owner publishes it.** Making it the home page is a guided step. Microsoft: "You must be the
  site owner to set a different page as a homepage", and the steps start "Once you have a published
  page that you want to use for your homepage".
- **Or Hubwright publishes it, when you ask.** Site designs → Publish and make home page (or
  `hubwright design apply "<site>" --publish`) turns that step into an automatic one, after a
  confirmation that says who notices and asks for the tenant name. It reads the current home page
  first (`Get-PnPHomePage`: "Returns the URL to the page set as home page"), records it, then runs
  `Set-PnPPage -Publish` ("Publishes the page once it is saved.") and `Set-PnPHomePage` ("Allows to
  set the home page of the current site."). Nothing is deleted; to go back, make the recorded page
  the home page again. The signed-in account needs owner rights on the site.
- **Tell people first.** Changing a site's home page changes what everyone who visits sees.
  Agree the date with the site owner and tell the site's members.
