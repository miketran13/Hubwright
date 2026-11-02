# Security

Hubwright reads SharePoint tenants with the signed-in admin's permissions, so we take security reports
seriously.

**Please don't open a public issue for a vulnerability.** Report it privately through GitHub's
"Report a vulnerability" (security advisories) on this repository. We aim to reply within a week.

In scope:
- any write that does not go through `TenantWriter` or `PnpShell`, any request, Graph property, cmdlet or
  `Set-PnPTenant` parameter outside their allow-lists, or any write without the person's confirmation;
- any way a value from a tenant, a skill or a proposal could change a generated PnP script beyond a quoted string;
- any way a scan, assessment, proposal or agent tool could send a write;
- leaking tokens, snapshots or secrets;
- a skill or rule file that could execute code;
- an agent tool that could act beyond its read-only contract.

Design notes: tokens are cached by MSAL in `~/.hubwright/msal.cache` (encrypted on Windows).
Snapshots stay on the local machine. The rule language is parsed, never evaluated as code.

Releases: every download is listed in `SHA256SUMS.txt` with an SPDX software bill of materials
(`hubwright-sbom.zip`); binaries are Authenticode-signed when the maintainer's certificate is
configured. CI fails on packages with known vulnerabilities, reviews new dependencies on pull
requests, and checks every cited Microsoft quote weekly (`.github/workflows/sources.yml`).

Maintainers: turn on GitHub secret scanning with push protection, and Dependabot alerts, for this
repository.
