---
id: copilot-readiness
title: "Copilot readiness and oversharing"
version: 1.0.0
category: copilot-readiness
appliesTo: both
owner: hubwright-maintainers
description: "SharePoint Advanced Management steps Microsoft recommends before Copilot: assess, govern, restrict."
sources:
  - url: https://learn.microsoft.com/en-us/microsoft-365/backup/backup-overview
    title: "Overview of Microsoft 365 Backup"
    reviewed: 2026-10-30
  - url: https://learn.microsoft.com/en-us/microsoft-365/copilot/configure-secure-governed-data-foundation-microsoft-365-copilot
    title: "Configure a secure and governed foundation for Microsoft Copilot"
    reviewed: 2026-10-27
---

# Copilot readiness and oversharing

Copilot and agents read through Microsoft Graph and respect permissions. Anything a person can
open, Copilot can surface. Microsoft's preparation steps:

1. **Content Management Assessment.** Run it, then rerun it every 30 days.
2. **Lifecycle and archive.** Inactive, ownerless and attestation policies, plus Microsoft 365
   Archive. Archived content isn't used by Copilot.
3. **Prevent oversharing.** Tighten sharing settings and use the data access governance reports:
   site permissions baseline, EEEU and sharing links over the last 28 days. Use **Restricted
   Access Control** for sensitive sites and **Restricted Content Discovery** for high-risk ones.
4. The **SharePoint Admin Agent** for questions across the tenant.
5. **Microsoft 365 Backup** for business continuity.

High risk is where signals overlap: sensitive content with Anyone links, public sites without
owners, large audiences with broken inheritance.
