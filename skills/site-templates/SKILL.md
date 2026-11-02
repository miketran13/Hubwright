---
id: site-templates
title: Site templates and ready-made sites
version: 1.0.0
category: templates
appliesTo: both
description: Microsoft's built-in SharePoint site templates and the look book PnP packages, and how to choose one for each new site in a proposal.
sources:
  - url: https://support.microsoft.com/en-us/office/apply-and-customize-sharepoint-site-templates-39382463-0e45-4d1b-be27-0e96aeec8398
    title: Apply and customize SharePoint site templates
    reviewed: 2026-10-28
  - url: https://learn.microsoft.com/en-us/sharepoint/dev/solution-guidance/applying-pnp-templates
    title: Applying PnP Templates to SharePoint Sites (the look book templates)
    reviewed: 2026-10-28
  - url: https://learn.microsoft.com/en-us/sharepoint/dev/apis/site-creation-rest
    title: Create Modern SharePoint Sites using REST
    reviewed: 2026-10-28
  - url: https://learn.microsoft.com/en-us/sharepoint/dev/declarative-customization/site-design-overview
    title: SharePoint site template and site script overview
    reviewed: 2026-10-28
---

# Site templates and ready-made sites

Every new site in a proposal should start from something ready-made rather than a blank page.

## Microsoft's built-in templates

SharePoint ships site templates for communication sites and team sites. Microsoft: "SharePoint
offers several site templates that contain pre-populated pages, page templates, news post
templates". They are applied from the site: "Navigate to site Settings and select Apply a site
template."

- Apply a template **before** content goes in. Microsoft warns: "When you apply a template to an
  existing site, your content will not automatically merge with the new template."
- A new communication site uses **Standard communication** and a new team site **Standard team**
  unless your organization sets its own default template.
- For scripted creation, communication sites use `SITEPAGEPUBLISHING#0` and team sites without a
  group `STS#3`; `SPSiteManager/create` accepts the out-of-the-box designs Standard communication,
  Showcase and Blank by id.

## Look book packages (PnP)

The SharePoint look book site is offline. Its templates are on Microsoft Learn as `.pnp` packages
for the PnP provisioning engine, applied with `Invoke-PnPTenantTemplate` by a SharePoint Online
Administrator (some also need the App Catalog). They create new sites with their own pages,
navigation and sample content: replace the sample content before launch. PnP PowerShell is
open source and has no Microsoft SLA.

## Choosing

- Hubs and department sites: **Department**, or a function template (Human resources, IT help desk).
- The home site: **Organization home** (or the look book's The Landing).
- Onboarding: **New employee onboarding**; the look book has pre-onboarding, corporate and
  departmental onboarding sites.
- Team workspaces: **Standard team** or **Project management**.
- Hubwright suggests by matching the site's title and purpose against each template's
  `suggestFor` words; a person confirms. Sketches in previews are Hubwright's wireframes, not
  Microsoft's exact layouts.
- The Syntex templates (Accounts Payable, Contracts Management) need Microsoft Syntex.

Edit `templates.yaml` to add your organization's own templates (source: microsoft for templates in
"From your organization", with your own page as the url).
