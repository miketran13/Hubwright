---
id: permissions
title: "Permissions and access"
version: 1.0.0
category: permissions
appliesTo: both
owner: hubwright-maintainers
description: "Inheritance, unique permissions, broad grants and owners."
sources:
  - url: https://learn.microsoft.com/en-us/office365/servicedescriptions/sharepoint-online-service-description/sharepoint-online-limits
    title: "SharePoint limits (service description)"
    reviewed: 2026-10-27
  - url: https://learn.microsoft.com/en-us/microsoft-365/copilot/configure-secure-governed-data-foundation-microsoft-365-copilot
    title: "Configure a secure and governed foundation for Microsoft Copilot"
    reviewed: 2026-10-27
  - url: https://learn.microsoft.com/en-us/sharepoint/site-lifecycle-management
    title: "SharePoint site lifecycle management"
    reviewed: 2026-10-27
---

# Permissions and access

Keep permissions simple. Use the site's Owners, Members and Visitors (or the Microsoft 365
group), and break inheritance only when a document set truly needs it.

- Microsoft supports 50,000 unique permissions per list or library, but recommends staying
  under **5,000**.
- You can't break or restore inheritance on a list, library or folder with more than 100,000
  items.
- Grants to "Everyone" or "Everyone except external users" (EEEU), broken inheritance across
  large audiences, and public sites without an owner are the risk signals Microsoft lists for
  oversharing. Copilot respects permissions, so overshared content shows up in answers.
- Every site needs accountable owners. Microsoft's own policy example requires at least two.
