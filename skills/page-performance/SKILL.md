---
id: page-performance
title: "Page performance"
version: 1.0.0
category: performance
appliesTo: both
owner: hubwright-maintainers
description: "Fast portal pages: run Page Diagnostics, keep web parts and images light."
sources:
  - url: https://learn.microsoft.com/en-us/microsoft-365/enterprise/page-diagnostics-for-spo
    title: "Use the Page Diagnostics tool for SharePoint"
    reviewed: 2026-10-27
---

# Page performance

Run the **Page Diagnostics for SharePoint** browser extension on portal pages before publishing.
It compares each page against Microsoft's baseline rules. In its network trace, requests under
500 ms are green, 500–1,000 ms yellow, and over 1,000 ms red.

- Web parts that refresh too often hurt every visitor. Corporate news doesn't change every second.
- Don't turn on the publishing feature just to get a CDN, because it can increase load times.
- Hubwright's static checks (web part count, heavy images, embeds) are heuristics. Use them to
  decide which pages to run Page Diagnostics on first.
