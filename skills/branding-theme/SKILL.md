---
id: branding-theme
title: "Branding and themes"
version: 1.0.0
category: branding
appliesTo: both
owner: hubwright-maintainers
description: "Organization themes only, accessible colours, one look per hub family."
sources:
  - url: https://learn.microsoft.com/en-us/sharepoint/dev/declarative-customization/site-theming/sharepoint-site-theming-overview
    title: "SharePoint site theming"
    reviewed: 2026-10-27
  - url: https://www.w3.org/TR/WCAG21/
    title: "Web Content Accessibility Guidelines (WCAG) 2.1"
    reviewed: 2026-10-27
  - url: https://learn.microsoft.com/en-us/sharepoint/planning-hub-sites
    title: "Planning your SharePoint hub sites"
    reviewed: 2026-10-27
---

# Branding and themes

Define custom themes from your brand and **make them the only themes** people can pick (hide the
default themes). Themes are JSON: the classic palette slots (themePrimary … white), and in the
current schema up to 16 accent-and-background pairs.

**Accessibility:** WCAG 2.1 success criterion 1.4.3 asks for a contrast ratio of at least
**4.5:1** for normal text and **3:1** for large text. Hubwright's preview studio checks body
text, secondary text, links, hover links, button labels and large headings for every theme, and
suggests the nearest brand colour that passes.

Don't create hubs just to share a theme. Use the theme picker or a site design instead.
