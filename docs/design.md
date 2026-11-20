# Pimmer – Application Design

See [research.md](research.md) for API details and [requirements.md](requirements.md) for the IDs referenced below.

## 1. Technology choice

| Concern | Choice | Why |
|---|---|---|
| Runtime | **.NET 10 LTS** (C#) | Mature MSAL support, WAM broker, single self-contained exe. Long-term support until Nov 2028. |
| Shell | **WPF + BlazorWebView** (Blazor Hybrid, WebView2) | Native window (needed as the WAM parent HWND) with a modern web UI. No HTTP server or localhost port (N-3). |
| UI kit | **Microsoft Fluent UI Blazor** (`Microsoft.FluentUI.AspNetCore.Components`) | Looks like Azure/Windows 11, has DataGrid, search, dialogs and theme following the OS. |
| Auth | **MSAL.NET** + `Microsoft.Identity.Client.Broker` (WAM) + `Microsoft.Identity.Client.Extensions.Msal` (DPAPI cache) | Windows SSO, multiple accounts, claims challenge, device-compliance Conditional Access (F-ACC-*). |
| HTTP | `HttpClient` + typed clients, plus `Microsoft.Extensions.Http.Resilience` (Polly) | Retry on `429` / `5xx` honouring `Retry-After` (N-6). |
| Graph calls | Raw REST with `System.Text.Json` DTOs (Graph SDK optional) | Small surface (about 10 endpoints). Fewer dependencies. `$batch` is easy to hand-roll. |
| Local storage | **SQLite** via `Microsoft.Data.Sqlite` (or a single JSON file for the MVP) | Favorites, groups, history and caches. No tokens are stored here. |
| Notifications | `Microsoft.Toolkit.Uwp.Notifications` (toast), `H.NotifyIcon.Wpf` (tray) | F-NOT-*. |

Alternative if cross-platform is ever needed: the same Blazor UI in **Photino.Blazor** or **.NET MAUI Blazor**. The core library stays the same.

## 2. Architecture

```
┌──────────────────────────── Pimmer.App (WPF host) ────────────────────────────┐
│  MainWindow ─ BlazorWebView ─► Blazor UI (Pages/Components, Fluent UI)        │
│        │                         │  binds to                                  │
│        │                         ▼                                            │
│        │                 State: PimStore (in-memory, observable)              │
│        │                         │                                            │
│  Tray / Toasts ◄──── NotificationService ◄── ExpiryWatcher (timer)            │
└────────┼─────────────────────────┼────────────────────────────────────────────┘
         │ HWND                    │
┌────────▼─────────────────────────▼──────── Pimmer.Core (class library) ───────┐
│ AccountService (MSAL + WAM, multi-account, claims challenge)                  │
│ PimService (facade) ── aggregates providers per account, in parallel          │
│    ├─ EntraRoleProvider      → Graph  /roleManagement/directory/...           │
│    ├─ GroupPimProvider       → Graph  /identityGovernance/privilegedAccess/...│
│    └─ AzureResourceProvider  → ARM    /providers/Microsoft.Authorization/...  │
│ PolicyService (rule parsing + cache)                                          │
│ ActivationService (single + bulk, poll for completion, error mapping)         │
│ LocalStore (SQLite: favorites, groups, history, settings, cache)              │
└───────────────────────────────────────────────────────────────────────────────┘
```

Keeping the UI-free `Pimmer.Core` separate makes it unit-testable. It also lets a future CLI (`pimmer activate "Daily ops"`) reuse it.

### 2.1 Solution layout

```
pimmer/
  src/
    Pimmer.Core/          # domain, providers, services, storage
    Pimmer.App/           # WPF + Blazor Hybrid UI
  tests/
    Pimmer.Core.Tests/    # xUnit, provider tests with recorded HTTP fixtures
  docs/
```

## 3. Domain model

A single unified model covers all three planes:

```csharp
enum PimKind { EntraRole, Group, AzureResource }
enum PimState { Eligible, Active, PendingApproval }
enum GroupAccess { Member, Owner }

record AccountRef(string HomeAccountId, string Upn, string ObjectId, string TenantId, string Label, string Color);

record PimItem(
    string Key,                 // stable: "{kind}|{accountOid}|{roleOrGroupId}|{scope}|{access}"
    AccountRef Account,
    PimKind Kind,
    string DisplayName,         // role name or group name
    string? Description,
    string ScopeId,             // "/", "/administrativeUnits/..", groupId, ARM scope
    string ScopeDisplay,        // "Directory", "Sub: prod-001 / RG: rg-sql"
    GroupAccess? Access,        // groups only
    string RoleDefinitionId,
    string EligibilityScheduleId,
    PimState State,
    DateTimeOffset? ActiveUntil,
    PolicyInfo? Policy);

record PolicyInfo(
    TimeSpan MaxDuration,
    bool RequiresJustification,
    bool RequiresTicket,
    bool RequiresMfa,
    string? AuthContextId,      // e.g. "c1"
    bool RequiresApproval);
```

`Key` is the identity used by favorites, groups and history. It stays stable across refreshes and app restarts.

## 4. Authentication design

- **Decision: use the built-in first-party client IDs; no app registration needed.** There is one `IPublicClientApplication` per client ID:
  - Graph: `14d82eec-204b-4c2f-b7e8-296a70dab67e` (Microsoft Graph PowerShell).
  - ARM: `1950a258-227b-4e31-a9cf-717495945fc2` (Azure PowerShell). ARM `user_impersonation` is pre-authorized for it.

  Both IDs stay configurable (N-8). If the built-in IDs ever stop working, an own app registration can replace them with a single client ID for both audiences.
- **Consent:** none needed. Pimmer requests `/.default`, i.e. only the permissions an admin has already granted to the client tenant-wide. Asking for explicit scopes that aren't pre-granted triggers an "Approval required" (admin consent) screen in tenants that block user consent. This was observed in our tenant.
- **Builder:** `.WithBroker(new BrokerOptions(BrokerOptions.OperatingSystems.Windows))`, `.WithParentActivityOrWindow(() => hwnd)`, `.WithAuthority(AzureCloudInstance.AzurePublic, tenantId)`.
- **Token cache:** persisted with `MsalCacheHelper` (DPAPI) in `%LOCALAPPDATA%\Pimmer\msal.cache`. WAM also keeps its own cache.
- **Scopes requested per audience:**
  - Graph: `https://graph.microsoft.com/.default`. In our tenant, Graph PowerShell is admin-consented for `RoleManagement.ReadWrite.Directory`, `RoleAssignmentSchedule.ReadWrite.Directory`, `PrivilegedAccess.ReadWrite.AzureADGroup`, `PrivilegedAssignmentSchedule.ReadWrite.AzureADGroup` and `RoleManagementPolicy.ReadWrite.AzureADGroup`. These are higher-privileged supersets of the least-privilege scopes, but still delegated, so they are capped by the user's own rights.
  - ARM: `https://management.azure.com/.default` (`user_impersonation`).
- **Add account:** `AcquireTokenInteractive(...).WithPrompt(Prompt.SelectAccount)`. Store `HomeAccountId` plus a user-chosen label and colour ("User" blue, "Admin" red) in `LocalStore`.
- **Token acquisition:** `GetTokenAsync(account, audience, claims?)`.
  1. Try `AcquireTokenSilent`.
  2. On `MsalUiRequiredException`, fall back to interactive with `.WithAccount(account)`.
  3. If `claims` is set, use `.WithClaims(claims)`.
- **Claims challenge handling** (F-ACC-6):
  1. If the policy has `AuthContextId`, build `{"access_token":{"acrs":{"essential":true,"value":"c1"}}}` and acquire a step-up token **before** the request.
  2. Otherwise, a `DelegatingHandler` detects `401` + `WWW-Authenticate` with `insufficient_claims`, extracts and base64-decodes `claims`, calls `GetTokenAsync(..., claims)` interactively, and retries once.
  3. For bulk activations, reuse the step-up token for all items of the same account and auth context, so the user sees one prompt.
- **Profile fallback** (F-ACC-7): `--profile <name>` changes the data folder to `%LOCALAPPDATA%\Pimmer\<name>\`, giving a separate cache, store and window title.

## 5. Data access

### 5.1 Refresh pipeline (F-LST-*)

```
RefreshAll():
  parallel for each account:
    parallel:
      Entra: eligible + active          (Graph $batch: 2–3 requests in 1 call)
      Groups: eligible + active (+group names via $expand / $batch)
      Azure: eligible + active          (ARM asTarget() at tenant root)
  merge → PimItem list (Active overrides Eligible for the same Key)
  enrich with policy from PolicyService (cache hit or lazy load)
  publish to PimStore → UI re-renders
```

- **Startup:** render the last snapshot from `LocalStore.cache` immediately (N-1), then run `RefreshAll` in the background.
- **Failure isolation:** each provider/account pair returns `Result<T>`. A failure shows a small warning badge ("Azure roles for Admin: failed – retry") and does not clear the other data (N-6).
- **Polling:** active items are polled every 60 s, eligibilities every 15 min, and both on window focus. After an activation, that item is polled every 10 s until it becomes active or 2 min pass.
- **Scope display names for ARM:** resolve subscription names once via `GET /subscriptions?api-version=2022-12-01` and cache them. RG and resource names are parsed from the scope path.

### 5.2 Policy service

- **Lazy loading:** policies are loaded on first need (opening the activation dialog, or before bulk activation). They are also prefetched in the background for favorites.
- **Cache:** keyed by `(kind, scope, roleDefinitionId)`, with a 60-minute TTL, persisted.
- **Parsing:** rules are parsed into `PolicyInfo`. Only `EndUser`/`Assignment`-target rules are relevant for self-activation:
  - expiration: `maximumDuration`
  - enablement: `enabledRules` containing `Justification` / `Ticketing` / `MultiFactorAuthentication`
  - authenticationContext: `isEnabled`, `claimValue`
  - approval: `setting.isApprovalRequired`

### 5.3 Activation service (F-ACT-*)

```
ActivateAsync(items, ActivationRequest { Duration, Justification, Ticket? }):
  ensure policies loaded for all items
  validate per item: required fields present; duration = min(requested, policy.Max)
  group items by (account, authContext) → acquire step-up token once per group
  run with max parallelism 4:
    Entra  → POST roleAssignmentScheduleRequests { action: selfActivate, ... }
    Group  → POST group/assignmentScheduleRequests { accessId, action: selfActivate, ... }
    Azure  → PUT  {scope}/.../roleAssignmentScheduleRequests/{Guid.NewGuid()} { requestType: SelfActivate, linkedRoleEligibilityScheduleId, ... }
  map response status:
    Provisioned/Granted → Active (poll to confirm)
    PendingApproval     → Pending
    error               → Failed + friendly message (ErrorMapper)
  write history row per item; raise toast
```

- **Deactivate:** same flow with `selfDeactivate` / `SelfDeactivate`.
- **Extend:** `selfExtend` / `SelfExtend`.
- **`ErrorMapper`** translates known codes into friendly text: `RoleAssignmentExists`, `ActiveDurationTooShort`, `ExpirationRule`, `JustificationRule`, `TicketingRule`, `MfaRule`, `AuthorizationFailed`, and `429`.

## 6. Local storage (SQLite, `%LOCALAPPDATA%\Pimmer\pimmer.db`)

| Table | Columns |
|---|---|
| `accounts` | home_account_id PK, upn, object_id, tenant_id, label, color, sort_order |
| `favorites` | item_key PK, added_at |
| `groups` | id PK, name, sort_order, default_duration, default_justification |
| `group_items` | group_id, item_key, sort_order |
| `history` | id PK, item_key, account_id, display_name, scope_display, action, duration, justification, ticket, result, at |
| `settings` | key PK, value (JSON) |
| `cache` | key PK, json, fetched_at (snapshot of items, policies, subscription names) |

- **Orphaned keys:** an item key whose eligibility disappeared is shown greyed out ("no longer eligible") in favorites and groups instead of being deleted silently.
- **Export/import** (F-ORG-7): dump `accounts` (labels only), `favorites`, `groups` and `group_items` to JSON.

## 7. UI design

### 7.1 Main window (single screen)

```
┌ Pimmer ─────────────────────────────────────────────────────────── ◐  ⚙  ─┐
│ [🔍 Search roles, groups, scopes…            Ctrl+K]   (User●) (Admin●) +  │
├──────────────┬─────────────────────────────────────────────────────────────┤
│ ★ Favorites  │ Filters: [Entra][Groups][Azure]  [Eligible][Active][Pending]│
│ ⚡ Active (4) │─────────────────────────────────────────────────────────────│
│ 🕑 Recent     │ ☐ ★ Global Reader          Entra   Directory     Admin●  ▶  │
│ ⏳ Pending (1)│     🔐MFA  📝Justif.  max 8h                                  │
│ ▦ All        │ ☐ ★ SQL DB Contributor     Azure   prod-001/rg-sql  User● ▶  │
│──────────────│     🎫Ticket  max 4h                                          │
│ My groups  + │ ☑   sg-pim-keyvault-admins Group   member          Admin● ●  │
│  ▸ Daily ops ▶│     ACTIVE · 1h 42m left                       [Extend][■]  │
│  ▸ Release   ▶│ …                                                           │
│  ▸ Incident  ▶│                                                             │
├──────────────┴─────────────────────────────────────────────────────────────┤
│ 2 selected   [Activate selected…]  [Deactivate]  [Add to group ▾]          │
│ Status: refreshed 07:58 · Azure (Admin) ⚠ retry                            │
└────────────────────────────────────────────────────────────────────────────┘
```

**Left navigation**
- Smart views: Favorites, Active, Recent, Pending, All.
- Custom groups, each with its own ▶ "activate group" button (F-ORG-3).
- Drag items onto a group to add them.

**List rows**
- Star toggle, name, kind badge, scope, account chip (coloured), requirement icons from policy, and state.
- Primary action:
  - ▶ Activate if eligible.
  - ■ Deactivate and Extend if active.
  - Cancel if pending.

**Header**
- Global search with instant fuzzy filtering (F-SRC-*).
- Account chips toggle the filter by account.
- `+` adds an account.

**Footer**
- Bulk action bar when items are selected.
- Status line with per-source health.

### 7.2 Activation dialog

```
┌ Activate 3 items ───────────────────────────────┐
│ Global Reader (Admin)            max 8h  🔐 c1  │
│ SQL DB Contributor (User)        max 4h  🎫      │
│ sg-pim-keyvault-admins (Admin)   max 2h         │
│                                                 │
│ Duration   [ 2h ▾ ]  (capped per item to max)   │
│ Justification [Investigating INC123      ▾]     │
│ Ticket #   [INC123]  System [ServiceNow]        │
│                                                 │
│                 [Cancel]  [Activate (Enter)]    │
└─────────────────────────────────────────────────┘
```

- **Required fields:** the dialog only shows ticket and justification fields when at least one item requires them. Required fields are validated before submit.
- **Defaults:** the last-used duration and justification are prefilled. Group defaults override them (F-ORG-4).
- **Quick activate:** from a favorite or Recent, ▶ activates immediately with the defaults when policy allows it (no ticket required). Shift+click opens the dialog.
- **Progress:** after submit, each row shows ⏳ → ✅ Active / 🕓 Pending / ❌ error text with Retry.

### 7.3 Settings

- **Accounts:** label, colour, order, remove.
- **Auth:** tenant ID, Graph client ID, ARM client ID (prefilled defaults). Includes a "Test sign-in" button.
- **Defaults:** default duration, expiry warning minutes, refresh intervals, theme, and start minimized to tray.

## 8. Security considerations

- **Least privilege:** delegated tokens only; the app can never do more than the signed-in user can do in the portal.
- **No listening port:** Blazor Hybrid uses no localhost HTTP server, so no local-port token theft is possible.
- **Token storage:** tokens live only in the WAM / MSAL caches (DPAPI-protected). SQLite holds metadata only.
- **Justifications** are stored locally in history. They may contain ticket numbers but no secrets.
- **Logging:** logs go to `%LOCALAPPDATA%\Pimmer\logs` and never include tokens or authorization headers.

## 9. Testing strategy

- **Unit tests (`Pimmer.Core.Tests`):**
  - `PolicyParser` against sample rule JSON.
  - The merge logic (eligible + active → `PimItem`).
  - Item `Key` stability.
  - `ErrorMapper`.
  - Duration clamping.
  - Bulk grouping by account and auth context.
- **Provider tests:** fake `HttpMessageHandler` with recorded JSON fixtures (sanitized) for each endpoint, including the `401` claims challenge and `429` retry.
- **Manual smoke checklist:** the user stories in requirements §5, run against the real tenant.

## 10. Delivery plan

| Phase | Scope |
|---|---|
| **0. Spike (½–1 day)** | Console app: sign in via WAM with both accounts. Verify first-party client IDs get the Graph PIM scopes and ARM `user_impersonation`. Call all 6 list endpoints. Activate and deactivate one item per plane. Confirm tenant-root `asTarget()` coverage. |
| **1. MVP** | Core + UI: accounts, unified list, search and filters, activate/deactivate (single and bulk), favorites, custom groups, recent, active view with countdown, policy-aware dialog, claims challenge. |
| **2. Comfort** | Toasts, expiry warning + extend, tray icon, pending view/cancel, keyboard shortcuts, fuzzy search, most-used sort. |
| **3. Nice to have** | Export/import, scheduled activation, portal deep links, optional CLI reusing `Pimmer.Core`. |

## 11. Open questions / decisions

1. ~~**App registration.**~~ **Decided:** use the built-in Microsoft Graph PowerShell client ID for Graph and the Azure PowerShell client ID for ARM. The spike confirms that the PIM scopes are granted (consent prompt or pre-consented). An own app registration is the fallback only.
2. **Conditional Access.** Do our PIM policies use authentication contexts (`acrs`)? This affects how often step-up prompts appear.
3. **Device compliance.** Does Conditional Access require a compliant device? WAM handles this; a plain browser flow might not.
4. ~~**UI kit.**~~ **Decided for the MVP:** plain Blazor with custom Fluent-style CSS (see §12).

## 12. MVP as built (deviations from the design above)

| Area | Design | MVP | Why |
|---|---|---|---|
| Target framework | .NET 10 | `net10.0-windows10.0.19041.0` (app), `net10.0` (core) | BlazorWebView 10's WebView2 composition control needs the Windows SDK projection. |
| UI kit | Fluent UI Blazor | Plain Blazor + `wwwroot/css/app.css` (Fluent-like, light/dark via `prefers-color-scheme`) | Fewer dependencies and less version risk. Can be swapped later. |
| Storage | SQLite `pimmer.db` | JSON: `pimmer.json` (settings, accounts, favorites, groups, history) + `cache.json` (items, policies). Atomic writes. | Small data volume. The design allowed JSON for the MVP. |
| HTTP resilience | Polly | Hand-rolled retry for `429`/`503`/`504` honouring `Retry-After` in `PimApi` | Small surface. |
| Graph `$batch` | Used | Not used. Plain parallel calls per (account, plane). | Low request count for one user. Add if throttling shows up. |
| Notifications | Windows toasts + tray | In-app toasts, including the expiry warning with an Extend action. No tray. | F-NOT-* are S/C priority. |
| Drag & drop to groups | Yes | Multi-select + "Add to group…" | Simpler. |
| Group dialogs | Rich dialogs | Browser `prompt`/`confirm` for name and defaults | Simpler. |
| Logging to file | `logs` folder | Not yet | |
| Export/import, Test sign-in | Planned | Not yet | Phase 3. |
| Scoped Entra role names | Not covered | `$expand=roleDefinition,directoryScope` on the Entra list endpoints returns the app registration / enterprise app / admin unit name. Direct `/applications/{id}` lookups return 403 with the pre-consented scopes. | Shown as the row headline and searchable. |
| Row layout | Role first | Scoped items lead with the target (Azure resource/RG/subscription, app, group) in larger text, with the role as a colour-coded pill | Easier scanning when there are many identical roles. |

**Project layout**
- `src/Pimmer.Core`: models, `PimService` facade, providers, `MsalTokenService`, `PimApi`, `LocalStore`.
- `src/Pimmer.App`: WPF host + Blazor components (`Main`, `ActivationDialog`, `SettingsDialog`).
- `tests/Pimmer.Core.Tests`: xunit tests for parsing, merge, search, scope display, claims and validation.

**Running**
- `dotnet run --project src\Pimmer.App` uses `%LOCALAPPDATA%\Pimmer`.
- `--profile <name>` uses an isolated data folder (`%LOCALAPPDATA%\Pimmer\profiles\<name>`). This lets you run a second instance, e.g. under a different Windows user. One instance already supports several accounts.
