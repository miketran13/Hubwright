---
id: site-lifecycle
title: "Site lifecycle"
version: 1.0.0
category: lifecycle
appliesTo: both
owner: hubwright-maintainers
description: "Ownership, inactivity and attestation policies, archive, and storage."
sources:
  - url: https://learn.microsoft.com/en-us/sharepoint/site-lifecycle-management
    title: "SharePoint site lifecycle management"
    reviewed: 2026-10-27
  - url: https://learn.microsoft.com/en-us/sharepoint/inactive-site-policy
    title: "Manage inactive sites using inactive site policies"
    reviewed: 2026-10-27
  - url: https://learn.microsoft.com/en-us/office365/servicedescriptions/sharepoint-online-service-description/sharepoint-online-limits
    title: "SharePoint limits (service description)"
    reviewed: 2026-10-27
---

# Site lifecycle

Sites multiply, and people leave. Microsoft's recommended strategy **combines all three** site
lifecycle policies (SharePoint Advanced Management):

- **Site ownership policy:** a minimum number of owners (Microsoft's example is two).
- **Inactive site policy:** activity across SharePoint, Teams, Viva Engage and Exchange. Owners
  get three monthly notifications, then the site can become read-only, then be archived after
  3, 6, 9 or 12 months. Archiving needs Microsoft 365 Archive. Home sites and root sites are out
  of scope.
- **Site attestation policy:** owners periodically confirm the site is still needed.

Avoid overlapping policies of the same type. Watch tenant storage, because a tenant above its
limit risks read-only mode.

Hubwright flags sites inactive for **6 months** by default, which is the period Microsoft's policy
report uses. Change `inactiveDays` per tenant in overrides.yaml.
