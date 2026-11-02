# Contributing to Hubwright

Thanks for helping SharePoint admins everywhere. 🏗️

## The easiest contribution: a best practice

Most of Hubwright's value is in `skills/`. Each skill is markdown plus YAML, so you need no C#.
1. Read [docs/skills.md](docs/skills.md).
2. Add or edit a rule. A **documented** rule needs the source URL and the exact sentence it
   enforces; a **heuristic** needs a parameter so tenants can tune it.
3. Run `hubwright skills validate` and `hubwright assess --tenant fabrikam`.
4. Open a pull request using the **New skill or rule** template.

Rules are reviewed for accuracy first. Please don't cite blog posts for limits Microsoft documents.

## Code

- .NET 10. `dotnet build Hubwright.slnx` and `dotnet test tests/Hubwright.Tests` must pass. Warnings
  are errors.
- Read [CLAUDE.md](CLAUDE.md) for the project's rules (read-only tenant access, cited rules,
  unknown is not pass, a pure core).
- Keep pull requests small and focused, and add tests with behaviour changes.

## Reporting collector problems

Scans of real tenants are where Hubwright is youngest. Open an issue with the command, the scan
issues it printed, and your tenant's shape (number of sites, whether it is multi-geo). **Never
attach a snapshot from a real tenant.** It contains URLs and names.

By contributing, you agree that your contribution is licensed under the MIT License and that you
follow the [Code of Conduct](CODE_OF_CONDUCT.md).
