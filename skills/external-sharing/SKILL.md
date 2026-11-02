---
id: external-sharing
title: "External sharing"
version: 1.0.0
category: sharing
appliesTo: both
owner: hubwright-maintainers
description: "Sharing defaults that make the safe choice the easy one: org links by default, expiring view-only Anyone links."
sources:
  - url: https://learn.microsoft.com/en-us/previous-versions/microsoft-365/solutions/best-practices-anonymous-sharing
    title: "Best practices for unauthenticated sharing (archived)"
    reviewed: 2026-10-27
    note: "Archived page. Re-verify against https://learn.microsoft.com/en-us/sharepoint/turn-external-sharing-on-or-off during the next refresh."
  - url: https://learn.microsoft.com/en-us/microsoft-365/copilot/configure-secure-governed-data-foundation-microsoft-365-copilot
    title: "Configure a secure and governed foundation for Microsoft Copilot"
    reviewed: 2026-10-27
---

# External sharing

By default, SharePoint sets sharing to the most permissive option. Tighten it deliberately:

- **Default link:** "Only people in your organization", so nobody creates an Anyone link by
  accident.
- **Anyone links**, if you allow them, should **expire** and be **view-only** (View for files;
  View, or View and upload, for folders). Guests who need to edit get *Specific people* links,
  which require sign-in and can be audited.
- **Sites** can be stricter than the tenant, never looser.
- Use **DLP** (Microsoft Purview) to block Anyone access to sensitive content.

The best-practices page is archived. The skill flags it for re-verification against the current
sharing settings article at the next refresh.
