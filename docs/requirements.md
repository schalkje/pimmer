# Pimmer – Requirements

## 1. Context

- I use many PIM eligibilities every day, across Entra roles, PIM for Groups and Azure resources, with two accounts: **user** and **admin**. Both accounts are in the **same tenant**.
- The Azure portal PIM experience is slow, deeply nested, handles one item at a time, and has no favorites or history.
- Pimmer is a **personal, local app** on my laptop:
  - It signs in interactively as me.
  - It has no backend and no deployment.
  - It runs single-user.

Priority: **M** = must have (MVP), **S** = should have, **C** = could have.

## 2. Functional requirements

### 2.1 Accounts & sign-in

| ID | Requirement | Prio |
|---|---|---|
| F-ACC-1 | Sign in interactively with my work account (delegated, as myself). Use Windows SSO (WAM) where possible. | M |
| F-ACC-2 | Support **multiple accounts** (user + admin) **at the same time in one app instance**. | M |
| F-ACC-3 | Remember signed-in accounts between app restarts (secure token cache). No re-login unless a token expires or policy requires it. | M |
| F-ACC-4 | Clearly label every item with the account it belongs to (colour/badge). Filter by account. | M |
| F-ACC-5 | Sign out or remove an account. | M |
| F-ACC-6 | Handle MFA and Conditional Access step-up (claims challenge) transparently during activation. | M |
| F-ACC-7 | Fallback: allow starting a second instance with a separate profile (`--profile admin`). | C |

### 2.2 Overview / listing

| ID | Requirement | Prio |
|---|---|---|
| F-LST-1 | Show all **eligible** assignments for all accounts in one list, covering **Entra roles**, **PIM for Groups** (member/owner) and **Azure resource roles** (MG, subscription, RG, resource). | M |
| F-LST-2 | Show all **active** PIM assignments with **time remaining** (countdown) and end time. | M |
| F-LST-3 | Show **pending** requests (awaiting approval) and allow cancelling them. | S |
| F-LST-4 | For each item, show: name, type (role/group/resource), scope (readable name plus path), account, state (eligible/active/pending), and requirements (MFA, justification, ticket, approval, max duration). | M |
| F-LST-5 | Manual refresh, plus automatic refresh of active items (for example every 60 s) and on window focus. | M |
| F-LST-6 | Open the item in the Azure portal (deep link) as an escape hatch. | C |

### 2.3 Search & filter

| ID | Requirement | Prio |
|---|---|---|
| F-SRC-1 | Instant search-as-you-type over name, scope, group name, subscription name and role description. | M |
| F-SRC-2 | Filter chips: type (Entra/Group/Azure), state (eligible/active/pending), account, favorites, custom group. | M |
| F-SRC-3 | Keyboard first: `Ctrl+K` / `/` focuses search, arrow keys navigate, `Enter` activates. | S |
| F-SRC-4 | Fuzzy matching (for example "kv prod" matches "Key Vault Administrator – rg-prod-kv"). | S |

### 2.4 Activation

| ID | Requirement | Prio |
|---|---|---|
| F-ACT-1 | Activating an eligible item always opens the activation dialog, prefilled with defaults (last used duration, and the justification per item). It takes two clicks, or `Enter` twice from search. | M |
| F-ACT-2 | Activation dialog with duration (clamped to the policy maximum), justification, and ticket number/system when required. Required fields are known up front from policy. | M |
| F-ACT-3 | **Bulk activate**: select multiple items, or a whole custom group, and activate them with one shared justification and duration. Each item still honours its own policy maximum. | M |
| F-ACT-4 | Show per-item progress and result: Activating → Active / Pending approval / Failed (with a friendly error). | M |
| F-ACT-5 | **Deactivate** an active item, individually or in bulk. | M |
| F-ACT-6 | **Extend** an active item (`selfExtend`) when it is close to expiry, if policy allows. | S |
| F-ACT-7 | Every used justification is saved automatically and offered in a dropdown. Saved messages can be removed, not edited. The last message is remembered per PIM object, and one saved message can be set as the default for an object. The dialog always shows the justification field (optional unless policy requires it) with a length limit. | S |
| F-ACT-8 | Optional scheduled start ("activate at 09:00"). | C |

### 2.5 Favorites, groups, recent

| ID | Requirement | Prio |
|---|---|---|
| F-ORG-1 | Mark any eligible item as a **favorite** (star). Favorites show at the top / in their own view. | M |
| F-ORG-2 | Create **custom groups** (for example "Daily ops", "Release prod", "Incident"), add/remove items, rename, reorder, delete. An item can be in several groups. | M |
| F-ORG-3 | Activate a whole group in one action (uses F-ACT-3). Groups may contain items from both accounts. | M |
| F-ORG-4 | Per-group default justification and duration. Without a default, the last message used for the group is prefilled. | S |
| F-ORG-5 | **Recently used** view: the last N activations (item, account, time, duration, justification), with "activate again" (opens the dialog). | M |
| F-ORG-6 | Sort by "most used" (activation count). | S |
| F-ORG-7 | Export/import favorites and groups as JSON (backup, new laptop). | C |

### 2.6 Notifications

| ID | Requirement | Prio |
|---|---|---|
| F-NOT-1 | Windows toast when an activation completes, fails, or is approved. | S |
| F-NOT-2 | Toast X minutes (default 10) before an active role expires, with an "Extend" action. | S |
| F-NOT-3 | System tray icon showing the active count, with quick access to favorites. Minimize to tray. | C |

## 3. Non-functional requirements

| ID | Requirement |
|---|---|
| N-1 | **Speed.** App window visible in < 2 s. Cached list shown immediately, then refreshed in the background. Full refresh for both accounts in < 5 s for about 200 eligibilities. |
| N-2 | **Simplicity.** One main screen. Activation in ≤ 2 clicks for favorites. No nested blades. |
| N-3 | **Security.** Delegated permissions only, least-privilege scopes. Tokens stored only through MSAL / WAM (OS-protected). No secrets in config. No listening network port. Local data contains no tokens. |
| N-4 | **Local only.** No backend, no telemetry. The only network calls go to `login.microsoftonline.com`, `graph.microsoft.com` and `management.azure.com`. |
| N-5 | **Supported APIs only.** Microsoft Graph v1.0 and ARM `Microsoft.Authorization` (documented api-versions). No undocumented portal APIs. |
| N-6 | **Robustness.** Handle throttling (`429` / `Retry-After`), partial failures (one plane or account failing must not break the others), and token expiry. |
| N-7 | **Platform.** Windows 11 laptop, single-user. Easy to run: one `dotnet run` or a self-contained exe. |
| N-8 | **Built-in auth, configurable.** Defaults to the built-in Microsoft Graph PowerShell client ID (Graph) and the Azure PowerShell client ID (ARM), so no app registration is needed. Client IDs and tenant stay configurable. |
| N-9 | **Accessibility / UX.** Dark/light theme following Windows. Keyboard navigable. Readable at 100–150 % scaling. |

## 4. Out of scope (for now)

- Admin features: managing eligibilities, editing PIM policies, approving other people's requests (could come later via `pimctl`-style approvals).
- Multi-tenant (the design keeps it possible).
- macOS/Linux, mobile, or a shared/hosted web version.
- Access reviews and reporting.

## 5. User stories (MVP acceptance)

1. **Morning routine.** I open Pimmer and see both accounts already signed in. I click ▶ on my "Daily ops" group (3 Entra roles from admin + 2 Azure RG roles from user), and all 5 are active within about 1 minute, with countdowns visible.
2. **Search.** I type "sql prod", find "SQL DB Contributor – rg-sql-prod", press Enter, enter a ticket number (the policy requires it), and the role becomes active.
3. **Step-up.** I activate Global Reader, which requires authentication context `c1`. A Windows Hello / MFA prompt appears, then the activation succeeds without me restarting anything.
4. **Recent.** I open "Recent" and re-activate yesterday's role with the same justification in two clicks.
5. **Status.** The "Active" view shows everything I have active across both accounts and when it expires. I deactivate one I no longer need.
