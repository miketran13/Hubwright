# Permissions

Hubwright signs in **as you** (delegated permissions, public client). Scans only read and see what
you can see; changes happen only in a Build run you select and confirm. For tenant settings, you
need the **SharePoint Administrator** role.

## The app registration

`tools/register-app.ps1` (or **Setup → Register the app…** in the desktop app) creates one app,
**Hubwright**, in your Entra ID. It is a public client with two redirects: `http://localhost` for
browser and device-code sign-in, and the Windows broker redirect. **It is read-only unless you ask
for write** (least privilege).

| API | Delegated permission | Used for |
|---|---|---|
| Microsoft Graph | `Sites.Read.All` | sites |
| Microsoft Graph | `Group.Read.All` | Microsoft 365 group-connected sites |
| Microsoft Graph | `User.Read.All` | owners |
| Microsoft Graph | `Reports.Read.All` | the SharePoint site usage report: last activity, storage, and every site in the tenant (see below) |
| SharePoint | `AllSites.Read` | navigation, lists, role assignments, pages, hubs, search, home site |
| Microsoft Graph | `SharePointTenantSettings.ReadWrite.All` | **build only, with `-Write`:** tenant sharing level |
| SharePoint | `AllSites.FullControl` | **build only, with `-Write`:** create sites, register, associate and rename hubs, add and apply themes |

An administrator grants consent. Your app id goes in `~/.hubwright/hubwright.json`. Nothing about
your tenant is stored in the repository.

- **Read-only until you build.** When you reach the Build stage, **Build → Add write permissions to
  the app…** (or `tools/register-app.ps1 -AppId <id> -Write`) adds the write permissions; an admin
  consents again, then **Build → Grant write permissions** (or `hubwright build login`) signs you in
  with them.
- **Remove them after the build.** `tools/register-app.ps1 -AppId <id>` without `-Write` sets the
  app back to read-only. Revoke the old consent in the Entra admin center (Enterprise applications →
  Hubwright → Permissions) so tokens can no longer carry the write scopes.
- **Why this matters with one app.** Once an admin has consented to the write permissions, Entra ID
  can include them in any token Hubwright gets for SharePoint, including during a scan. Hubwright's
  code still cannot write outside a build run: the scan client only sends GET, and the one write
  client refuses anything outside its allow-list. Keeping the app read-only between builds removes
  even that possibility.

### Things to verify on your tenant

The live collector has been run only against a demo tenant, **never against a production tenant**
(see the disclaimer in the README). These are the parts most likely to need adjustment:

- **Tenant settings and themes** use the admin host (`https://<tenant>-admin.sharepoint.com/_api/SPO.Tenant`,
  `/_api/thememanager/GetTenantThemingOptions`) with a `.default` token. If they come back "access
  denied", the scan records a scan issue and those rules show as not assessed. The PnP collector
  (`Get-PnPTenant`, `Get-PnPTenantTheme`) reads the same settings with PnP's own permissions.
- **Activity** comes from the Graph usage report. If your tenant conceals user, group and site names
  in reports (Microsoft 365 admin center → Settings → Org settings → Reports), the report has no
  URLs and inactivity rules are not assessed.

### Sites you are not a member of

Hubwright reads as you, and the SharePoint Administrator role does not open every site's content.
Search only lists the sites you can open, so the scan also reads the site usage report, which
Microsoft describes as letting an app "read all service usage reports on behalf of the signed-in
user" when that user holds a role such as SharePoint Administrator or Reports Reader
([Authorization for usage reports](https://learn.microsoft.com/en-us/graph/reportroot-authorization)).
Every site in it that search missed is added to the snapshot, then read like any other site.

- A site that answers **access denied** is marked not readable: its activity and storage (from the
  report) are known; its owners, navigation, content, permissions and, when search missed it, its
  hub are not, so those rules are **not assessed**. The proposal lists it, and the Build plan has a
  guided step: check it in the SharePoint admin center (Active sites shows owners and sharing
  without joining the site) or with its owner.
- If the usage report can't be read (no role) or conceals site URLs, the scan says the site list
  is incomplete and tenant-wide site counts are not assessed.
- A site that opens but whose **permissions** answer access denied (you are a visitor or member,
  not an owner) is listed too: its permission and sharing rules are not assessed.
- **Score, Findings, Structure and Reports** (and `hubwright access`, and every report format) list
  these sites as a warning, with Active sites in the admin center and a CSV export. To read one, an
  admin can add a site admin under Active sites → the site → Membership
  ([Manage site admins](https://learn.microsoft.com/en-us/sharepoint/manage-site-collection-administrators)).
  Microsoft on site admins: "They also have access to any items in the site, including in subsites,
  even if permissions inheritance has been broken"
  ([site permissions reference](https://learn.microsoft.com/en-us/sharepoint/site-permissions)), so
  agree it with the owner and remove it after the scan.
- Hubwright **never adds you as a site admin** itself: that changes who can see its content, so it
  is your decision.
  Full coverage needs an identity that can read every site, which is the app-only option below.
- **Global navigation (app bar)** and **sensitivity labels** are not read by the live collector
  yet. They are recorded as scan issues.

## Building the plan

Building (the **Build** page, or `hubwright build run`) uses the app's write permissions.
The person signing in also needs the **SharePoint Administrator** role. Hubwright's writes are
limited to these documented requests (see `src/Hubwright.Connectors/Http/TenantWriter.cs`):

| Step | Request | Documentation |
|---|---|---|
| Create a site | `POST /_api/SPSiteManager/create` | [Create Modern SharePoint Sites using REST](https://learn.microsoft.com/en-us/sharepoint/dev/apis/site-creation-rest) |
| Register a hub | `POST {site}/_api/site/RegisterHubSite` | [Hub site REST API](https://learn.microsoft.com/en-us/sharepoint/dev/features/hub-site/hub-site-rest-api) |
| Associate / disassociate | `POST {site}/_api/site/JoinHubSite('{id}')` | same |
| Rename a hub | `POST /_api/HubSites/GetById?hubSiteId='{id}'` (MERGE) | same |
| Add, update, apply a theme | `POST /_api/thememanager/AddTenantTheme · UpdateTenantTheme · ApplyTheme` | [Site theming REST API](https://learn.microsoft.com/en-us/sharepoint/dev/declarative-customization/site-theming/sharepoint-site-theming-rest-api) |
| Tenant sharing and the security baseline | `PATCH https://graph.microsoft.com/v1.0/admin/sharepoint/settings`, only `sharingCapability`, `isResharingByExternalUsersEnabled`, `isRequireAcceptingUserToMatchInvitedUserEnabled`, `idleSessionSignOut`, `isLegacyAuthProtocolsEnabled` (any other property is refused) | [Update sharepointSettings](https://learn.microsoft.com/en-us/graph/api/sharepointsettings-update) |

### PnP PowerShell steps

What SharePoint's REST API doesn't document runs as **PnP PowerShell** scripts that Hubwright
generates and runs with your sign-in (Setup → Prerequisites installs PowerShell 7 and PnP PowerShell):

| Step | Cmdlet | Documentation |
|---|---|---|
| Home site | `Set-PnPHomeSite` (on the admin site) | [Set-PnPHomeSite](https://pnp.github.io/powershell/cmdlets/Set-PnPHomeSite.html) |
| Hub and home site navigation | `Get-PnPNavigationNode`, `Add-PnPNavigationNode` (adds; never removes) | [Add-PnPNavigationNode](https://pnp.github.io/powershell/cmdlets/Add-PnPNavigationNode.html) |
| Microsoft's built-in templates | `Invoke-PnPSiteDesign -Template <name>` | [Invoke-PnPSiteDesign](https://pnp.github.io/powershell/cmdlets/Invoke-PnPSiteDesign.html) |
| Look book packages | `Invoke-PnPTenantTemplate` | [Applying PnP templates](https://learn.microsoft.com/en-us/sharepoint/dev/solution-guidance/applying-pnp-templates) |
| Site header colour and layout, site logo | `Set-PnPWebHeader`, `Add-PnPFile` (to Site Assets) | [Set-PnPWebHeader](https://pnp.github.io/powershell/cmdlets/Set-PnPWebHeader.html) |
| Organization news sites | `Get-PnPOrgNewsSite`, `Add-PnPOrgNewsSite` (on the admin site) | [Add-PnPOrgNewsSite](https://pnp.github.io/powershell/cmdlets/Add-PnPOrgNewsSite.html) |
| Draft welcome posts | `Get-PnPPage`, `Add-PnPPage -PromoteAs NewsArticle` (never `-Publish`), `Add-PnPPageTextPart` | [Add-PnPPage](https://pnp.github.io/powershell/cmdlets/Add-PnPPage.html) |
| Draft designed home pages (Site designs, sites you include or apply) | `Get-PnPPage`, `Add-PnPPage -Name Proposed-home -LayoutType Home` (never `-Publish`, never `-PromoteAs HomePage`), `Add-PnPPageSection`, `Add-PnPPageWebPart`, `Add-PnPPageTextPart` | [Add-PnPPageSection](https://pnp.github.io/powershell/cmdlets/Add-PnPPageSection.html), [Add-PnPPageWebPart](https://pnp.github.io/powershell/cmdlets/Add-PnPPageWebPart.html) |
| Publish a designed page and make it the home page (only when you choose Publish for that site) | `Get-PnPPage`, `Get-PnPHomePage` (recorded first), `Set-PnPPage -Publish`, `Set-PnPHomePage -RootFolderRelativeUrl SitePages/Proposed-home.aspx` | [Set-PnPPage](https://pnp.github.io/powershell/cmdlets/Set-PnPPage.html), [Set-PnPHomePage](https://pnp.github.io/powershell/cmdlets/Set-PnPHomePage.html), [Get-PnPHomePage](https://pnp.github.io/powershell/cmdlets/Get-PnPHomePage.html) |
| Security baseline: link defaults, Anyone link expiry and permissions, guest expiry | `Get-PnPTenant`, then `Set-PnPTenant` with only `DefaultSharingLinkType`, `DefaultLinkPermission`, `RequireAnonymousLinksExpireInDays`, `FileAnonymousLinkType`, `FolderAnonymousLinkType`, `ExternalUserExpirationRequired`, `ExternalUserExpireInDays` | [Set-PnPTenant](https://pnp.github.io/powershell/cmdlets/Set-PnPTenant.html) |

Baseline steps read the current value first, write it down (so **Secure → Baseline → Restore
previous values** can put it back), and never loosen a stricter value: a 30-day guest expiry already
meets a 90-day baseline.

`Connect-PnPOnline -AccessToken` uses the token from your Hubwright sign-in; it is passed in an
environment variable and never written to the script or the audit log. PnP PowerShell is open
source with no Microsoft SLA. Templates and the home site need the SharePoint Administrator role.

**The Microsoft 365 header** (the bar above every SharePoint page) is the Microsoft 365
organization theme, not a SharePoint theme. A Global Administrator sets it in the Microsoft 365
admin center (Settings → Org settings → Organization profile → Custom themes); Hubwright lists the
colours to enter as a guided step.

**Designed home pages are drafts unless you choose Publish.** By default the site's owner finishes
the `Proposed-home` page, tells the site's members, publishes it and makes it the home page (a
guided step). In **Site designs → Publish and make home page** (or `hubwright design apply "<site>"
--publish`) Hubwright does it for that one site, after a confirmation that warns that everyone who
opens the site sees the page as it is, asks you to confirm the owner agrees, and asks for the
tenant name. It reads and records the current home page first; your account needs owner rights on
the site. Microsoft: "You must be
the site owner to set a different page as a homepage"
([Use a different page for your SharePoint site home page](https://support.microsoft.com/en-us/office/use-a-different-page-for-your-sharepoint-site-home-page-35a5022c-f84a-455d-985e-c691ab5dfa17)).
The old home page stays in Site pages, so switching back is one click.

**Home → What Hubwright does in your tenant** (or `hubwright scope`) summarises this page for
the people you work with: what is read, what can change, what never happens, who notices a change
and how to undo it. Its lists come from the code that enforces them.

Everything else in a proposal (the app bar's global navigation switch, default links, labels,
lifecycle policies, owners, modernizing, archiving, retiring, unregistering hubs) is a **guided
step**: Hubwright opens the page in its own browser pane with the values to enter, and you mark it
done. Hubwright never deletes, archives or unregisters anything itself.

Every write is logged before it is sent and again with its result (`write.send`, `write.result`),
and every step outcome too (`implement.step`); see **Audit log**.

## Scheduled scans

**Run → Drift and trend → Scheduled scans** (or `hubwright schedule weekly`) creates a Windows
scheduled task that runs `hubwright scan --no-prompt` as you. It uses your cached delegated sign-in,
reads only, and never asks you to sign in: when the sign-in can't be renewed silently, the scan stops
and the audit log records it. `hubwright schedule off` removes the task.

## Optional: app-only (unattended)

App-only access with a certificate (Sites.Read.All application permission) for scheduled scans is
on the roadmap. It is not implemented, on purpose: an unattended token that reads every site is a
larger risk than a person signing in.

## What Hubwright never does

- It sends no write request unless you run an implementation, and then only the allow-listed
  requests above, after you type the tenant name to confirm.
- It never deletes, archives or unregisters anything.
- It never runs the scripts it exports. You review and run `provision.ps1` and `theme.ps1` yourself.
- It never sends tenant data anywhere except the AI backend you choose. Choose the **Local** backend
  (Ollama, Foundry Local) to keep everything on your machine.
