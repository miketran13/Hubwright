---
id: security-baseline
title: "Security baseline"
version: 1.0.0
category: security
appliesTo: both
owner: hubwright-maintainers
description: "Tenant settings that keep an intranet safe by default, in three profiles (Standard, Strict, Regulated), each setting cited and applied only after you confirm."
sources:
  - url: https://learn.microsoft.com/en-us/sharepoint/turn-external-sharing-on-or-off
    title: "Manage sharing settings for SharePoint and OneDrive in Microsoft 365"
    reviewed: 2026-10-30
  - url: https://learn.microsoft.com/en-us/previous-versions/microsoft-365/solutions/best-practices-anonymous-sharing
    title: "Best practices for unauthenticated sharing (archived)"
    reviewed: 2026-10-30
  - url: https://learn.microsoft.com/en-us/sharepoint/sign-out-inactive-users
    title: "Sign out inactive users"
    reviewed: 2026-10-30
  - url: https://learn.microsoft.com/en-us/sharepoint/control-access-from-unmanaged-devices
    title: "Control access from unmanaged devices"
    reviewed: 2026-10-30
  - url: https://learn.microsoft.com/en-us/entra/identity/conditional-access/policy-block-legacy-authentication
    title: "Block legacy authentication with Conditional Access"
    reviewed: 2026-10-30
  - url: https://learn.microsoft.com/en-us/microsoft-365/copilot/configure-secure-governed-data-foundation-microsoft-365-copilot
    title: "Configure a secure and governed foundation for Microsoft Copilot"
    reviewed: 2026-10-30
  - url: https://learn.microsoft.com/en-us/microsoft-365/backup/backup-overview
    title: "Overview of Microsoft 365 Backup"
    reviewed: 2026-10-30
  - url: https://learn.microsoft.com/en-us/sharepoint/site-lifecycle-management
    title: "Site lifecycle management"
    reviewed: 2026-10-30
  - url: https://learn.microsoft.com/en-us/graph/api/sharepointsettings-update
    title: "Update sharepointSettings (Microsoft Graph)"
    reviewed: 2026-10-30
  - url: https://pnp.github.io/powershell/cmdlets/Set-PnPTenant.html
    title: "Set-PnPTenant (PnP PowerShell)"
    reviewed: 2026-10-30
---

# Security baseline

An intranet is safe when the defaults are safe: people share the right way without thinking about it.
The baseline sets the tenant's defaults in three profiles:

- **Standard**: guests who sign in, links for people in the organization by default, view-only
  Anyone links that expire, guest access that expires, legacy authentication off.
- **Strict**: Standard, plus existing guests only, links for specific people by default, idle
  session sign-out, and limited, web-only access from unmanaged devices.
- **Regulated**: Strict, plus no external sharing, unmanaged devices blocked, and sensitivity
  labels and DLP required.

How it is applied:

- **Graph** (`PATCH /admin/sharepoint/settings`) for the documented, updatable settings.
- **PnP PowerShell** `Set-PnPTenant` for link defaults and expiry. Only the allow-listed parameters are used.
- **Guided** steps for what lives in Purview, Entra ID or SharePoint Advanced Management, and for
  anything that can lock people out (unmanaged devices).

Every change reads the current value first, records it, and can be restored. Hubwright never loosens
a stricter setting: a guest expiry of 30 days already meets a 90-day baseline.

Values in days are Hubwright's defaults (Microsoft recommends an expiry, not a number). Tune them in
your own copy of this skill (`hubwright skills fork security-baseline`).
