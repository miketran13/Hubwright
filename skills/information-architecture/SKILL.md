---
id: information-architecture
title: "Information architecture: the flat world"
version: 1.0.0
category: information-architecture
appliesTo: both
owner: hubwright-maintainers
description: "A flat structure of one site per unit of work, connected by hubs instead of subsites."
sources:
  - url: https://learn.microsoft.com/en-us/sharepoint/information-architecture-modern-experience
    title: "Introduction to SharePoint information architecture"
    reviewed: 2026-10-27
  - url: https://learn.microsoft.com/en-us/office365/servicedescriptions/sharepoint-online-service-description/sharepoint-online-limits
    title: "SharePoint limits (service description)"
    reviewed: 2026-10-27
  - url: https://learn.microsoft.com/en-us/sharepoint/planning-hub-sites
    title: "Planning your SharePoint hub sites"
    reviewed: 2026-10-27
---

# Information architecture: the flat world

Modern SharePoint is **flat**. Create one site for each topic, task or unit of work, then connect
sites with hubs, roll-up web parts (News, Highlighted content, Sites) and navigation links.
Subsites tie content to a URL hierarchy. Every reorganisation breaks links, and policies such as
retention apply to the whole site collection, whether you want that or not.

**Why it matters:** when the business changes you move a site from one hub to another. With
subsites you would have to move the content.

**How to fix:**
- Stop creating new subsites. Turn off subsite creation in the SharePoint admin center.
- Move high-value subsites to their own sites, then associate them with a hub.
- Use libraries and columns rather than deep folders. Microsoft says more than one or two levels
  of folder nesting creates a significant discoverability burden.
