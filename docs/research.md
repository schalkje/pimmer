# Pimmer – Research

> Goal: a fast, simple local app to view and activate Microsoft Entra **PIM** eligibilities (Entra roles, PIM for Groups, Azure resource roles) for one or more of my own accounts (user + admin), because the Azure portal PIM blade is slow and clumsy.
>
> Date: 2026-11-20. Star counts and activity were taken from the GitHub API on that date.

## 1. Summary

- There are many open-source PIM tools, but nearly all are **CLIs, TUIs or PowerShell modules**. Only a few have a real GUI, and none combines favorites, groups, history and multiple accounts in one polished UI.
- Everything needed is available through **supported, documented APIs**:
  - **Microsoft Graph** for Entra roles and PIM for Groups.
  - **Azure Resource Manager (ARM)** for Azure resource roles.
  - No need for the undocumented portal backend (`api.azrbac.mspim.azure.com`).
- Two token audiences are needed per account: `https://graph.microsoft.com` and `https://management.azure.com`.
- The hard parts are:
  1. **Auth/consent**: which client ID to use, and whether admin consent is needed.
  2. **Step-up**: MFA or Conditional Access authentication context (claims challenge) when activating.
  3. **Policy rules**: max duration, justification, ticket and approval. Read these up front so activations don't fail.
- Best reference implementations to learn from:
  - [`Noble-Effeciency13/PIMActivation-Portal`](https://github.com/Noble-Effeciency13/PIMActivation-Portal): browser SPA covering all 3 planes, with claims-challenge handling, policy caching and profiles.
  - [`netr0m/az-pim-cli`](https://github.com/netr0m/az-pim-cli) and [`demoray/azure-pim-cli`](https://github.com/demoray/azure-pim-cli): ARM `asTarget()` usage.
  - [`FrodeHus/elevate`](https://github.com/FrodeHus/elevate) and [`junisconsulting/entra-pim-manager`](https://github.com/junisconsulting/entra-pim-manager): C# tray apps, the closest to the UX we want.

## 2. Existing open-source projects

| # | Repo | ★ | Last push | Stack / form | Planes | Notes |
|---|---|---|---|---|---|---|
| 1 | [kayasax/EasyPIM](https://github.com/kayasax/EasyPIM) | 237 | 2026-09 | PowerShell module | Entra, Groups, Azure | Mostly for **admins** (policy and assignment management, PIM-as-code). Not a fast activator. |
| 2 | [netr0m/az-pim-cli](https://github.com/netr0m/az-pim-cli) | 119 | 2026-11 | Go CLI | Azure, Groups, Entra | Uses ARM `$filter=asTarget()`. Graph parts reportedly need your own app registration. |
| 3 | [Noble-Effeciency13/PIMActivation](https://github.com/Noble-Effeciency13/PIMActivation) | 68 | 2026-05 | PowerShell 7 with WinForms GUI | Entra, Groups, Azure | Windows GUI, bulk activation, handles MFA, justification and ticket. |
| 4 | [JustinGrote/JAz.PIM](https://github.com/JustinGrote/JAz.PIM) | 48 | 2024-12 | PowerShell | Azure, Entra | Tidy cmdlets. Inactive since 2024. |
| 5 | [demoray/azure-pim-cli](https://github.com/demoray/azure-pim-cli) | 39 | 2026-11 | Rust CLI | Azure | `asTarget()` + `SelfActivate`. Fast. |
| 6 | [co-native-ab/pimctl](https://github.com/co-native-ab/pimctl) | 24 | 2025-04 | Go CLI | Entra, Groups, Azure | Activations and approvals. |
| 7 | [Noble-Effeciency13/PIMActivation-Portal](https://github.com/Noble-Effeciency13/PIMActivation-Portal) | 22 | 2026-09 | Vanilla JS SPA/PWA, MSAL.js | Entra, Groups, Azure | **Best reference.** No backend, PKCE, profiles in IndexedDB, `$batch`, 429 back-off, 30-minute policy cache, claims challenge. Needs its own app registration (SPA redirect URI). |
| 8 | [markorr321/Entra-PIM](https://github.com/markorr321/Entra-PIM) | 18 | 2026-03 | PowerShell console TUI | Entra, Groups | Cross-platform. |
| 9 | [seb07-cloud/pim-tui](https://github.com/seb07-cloud/pim-tui) | 12 | 2026-03 | Go TUI | – | |
| 10 | [markorr321/PIM-Global](https://github.com/markorr321/PIM-Global) | 11 | 2025-07 | PowerShell | Entra | Multi-tenant. |
| 11 | [alexverboon/Entra-PIM-Helpers](https://github.com/alexverboon/Entra-PIM-Helpers) | 10 | 2025-05 | PowerShell scripts | Entra | |
| 12 | [FrodeHus/elevate](https://github.com/FrodeHus/elevate) | 8 | 2026-11 | C# tray / menu-bar app | Entra, Groups, Azure | Close to the desired UX. Young project. |
| 13 | [junisconsulting/entra-pim-manager](https://github.com/junisconsulting/entra-pim-manager) | 3 | 2026-11 | C# Windows tray app | Entra, Groups, Azure | Close to the desired UX. Young project. |
| 14 | [CosX/azure-pim-tui](https://github.com/CosX/azure-pim-tui) | 1 | 2026-09 | Rust TUI | Azure | |
| 15 | [jeircul/pim](https://github.com/jeircul/pim), [tmeadon/tpim](https://github.com/tmeadon/tpim), [TheScriptingGuy/azure-pim-web](https://github.com/TheScriptingGuy/azure-pim-web), [kfuras/PimActivate](https://github.com/kfuras/PimActivate), [t0nys0n/AutoPIM](https://github.com/t0nys0n/AutoPIM) | 0 | 2026 | Go / Python / PowerShell | various | Small personal tools. |

**Gaps in the ecosystem**
- No tool offers **favorites + custom groups + recent history + multiple accounts side by side**.
- No browser extension and no Tauri/Electron app found.
- The PowerShell GUIs start slowly and are tied to a module install.

**Decision:** build our own small app, borrowing the API patterns from PIMActivation-Portal and the CLIs. Forking is not worth it: the GUI options are either PowerShell/WinForms or browser-SPA-only, and neither supports multiple accounts in the way we want.

## 3. APIs

### 3.1 Entra directory roles (Microsoft Graph v1.0)

| Purpose | Request | Least-privileged delegated scope |
|---|---|---|
| My eligible roles | `GET /roleManagement/directory/roleEligibilityScheduleInstances/filterByCurrentUser(on='principal')?$expand=roleDefinition` | `RoleEligibilitySchedule.Read.Directory` ✅ verified |
| My active roles | `GET /roleManagement/directory/roleAssignmentScheduleInstances/filterByCurrentUser(on='principal')?$expand=roleDefinition` | `RoleAssignmentSchedule.Read.Directory` |
| Activate / deactivate / extend | `POST /roleManagement/directory/roleAssignmentScheduleRequests` with `action` = `selfActivate` \| `selfDeactivate` \| `selfExtend` \| `selfRenew` | `RoleAssignmentSchedule.ReadWrite.Directory` ✅ verified |
| My pending requests | `GET /roleManagement/directory/roleAssignmentScheduleRequests/filterByCurrentUser(on='principal')?$filter=status eq 'PendingApproval'` | same as above |
| Policy (rules) | `GET /policies/roleManagementPolicyAssignments?$filter=scopeId eq '/' and scopeType eq 'DirectoryRole' and roleDefinitionId eq '{id}'&$expand=policy($expand=rules)` | `RoleManagementPolicy.Read.Directory` |

Activation payload:

```json
{
  "action": "selfActivate",
  "principalId": "<my object id>",
  "roleDefinitionId": "<role definition id>",
  "directoryScopeId": "/",
  "justification": "Investigating INC123",
  "scheduleInfo": {
    "startDateTime": "2026-11-20T07:00:00Z",
    "expiration": { "type": "afterDuration", "duration": "PT4H" }
  },
  "ticketInfo": { "ticketNumber": "INC123", "ticketSystem": "ServiceNow" }
}
```

`directoryScopeId` can also be an administrative-unit scope, for example `/administrativeUnits/{id}`.

### 3.2 PIM for Groups (Microsoft Graph v1.0)

| Purpose | Request | Scope |
|---|---|---|
| My eligible groups | `GET /identityGovernance/privilegedAccess/group/eligibilityScheduleInstances/filterByCurrentUser(on='principal')?$expand=group` | `PrivilegedEligibilitySchedule.Read.AzureADGroup` ✅ verified |
| My active groups | `GET /identityGovernance/privilegedAccess/group/assignmentScheduleInstances/filterByCurrentUser(on='principal')` | `PrivilegedAssignmentSchedule.Read.AzureADGroup` |
| Activate / deactivate | `POST /identityGovernance/privilegedAccess/group/assignmentScheduleRequests` with `accessId` = `member` \| `owner`, `groupId`, `principalId`, `action`, `scheduleInfo`, `justification`, `ticketInfo` | `PrivilegedAssignmentSchedule.ReadWrite.AzureADGroup` ✅ verified |
| Policy | `GET /policies/roleManagementPolicyAssignments?$filter=scopeId eq '{groupId}' and scopeType eq 'Group'&$expand=policy($expand=rules)` | `RoleManagementPolicy.Read.AzureADGroup` |

Recurring schedules are not supported for groups.

### 3.3 Azure resource roles (ARM, `Microsoft.Authorization`)

| Purpose | Request |
|---|---|
| My eligible roles (all scopes) | `GET https://management.azure.com/providers/Microsoft.Authorization/roleEligibilityScheduleInstances?$filter=asTarget()&api-version=2020-10-01` ✅ `asTarget()` is documented as "all role eligibility schedules created for the current user". |
| My active roles | `GET .../providers/Microsoft.Authorization/roleAssignmentScheduleInstances?$filter=asTarget()&api-version=2020-10-01` |
| Activate | `PUT https://management.azure.com/{scope}/providers/Microsoft.Authorization/roleAssignmentScheduleRequests/{newGuid}?api-version=2020-10-01` ✅ verified. Note: this is a **PUT** with a client-generated GUID. |
| Policy | `GET {scope}/providers/Microsoft.Authorization/roleManagementPolicyAssignments?$filter=roleDefinitionId eq '{id}'&api-version=2020-10-01` |

- Scopes for ARM calls: management group, subscription, resource group or individual resource.
- Token: audience `https://management.azure.com/`, scope `user_impersonation`.
- If tenant-root `asTarget()` does not return everything, fall back to listing per subscription and per management group, as some tools do.

Activation body:

```json
{
  "properties": {
    "principalId": "<my object id>",
    "roleDefinitionId": "/subscriptions/<sub>/providers/Microsoft.Authorization/roleDefinitions/<id>",
    "requestType": "SelfActivate",
    "linkedRoleEligibilityScheduleId": "<eligibility schedule id>",
    "justification": "Deploy hotfix",
    "scheduleInfo": {
      "startDateTime": "2026-11-20T07:00:00Z",
      "expiration": { "type": "AfterDuration", "duration": "PT2H" }
    }
  }
}
```

### 3.4 Policy rules that drive the UI

The rules come back from the policy endpoints above (`$expand=policy($expand=rules)`, or the ARM equivalent). Read them before showing the activation dialog.

| Rule | Meaning in the UI |
|---|---|
| `unifiedRoleManagementPolicyExpirationRule` (end-user assignment) | Maximum activation duration, used to clamp the duration slider. |
| `unifiedRoleManagementPolicyEnablementRule` | `Justification`, `Ticketing` or `MultiFactorAuthentication` required. |
| `unifiedRoleManagementPolicyAuthenticationContextRule` | Conditional Access authentication context (`c1`…`c25`) required. Needs a step-up token with a claims challenge. |
| `unifiedRoleManagementPolicyApprovalRule` | Approval required. The request ends in `PendingApproval`. |

## 4. Authentication options

All options use **delegated** auth: the app acts as the signed-in user, interactively, as requested.

| Option | Pros | Cons |
|---|---|---|
| **A. Own app registration** (public client, redirect `http://localhost` plus the WAM broker redirect URI) | Clean, least privilege, full control over scopes. | PIM *ReadWrite* scopes usually need **admin consent**. Requires someone who can register and consent. |
| **B. Well-known first-party client IDs**: Azure PowerShell `1950a258-227b-4e31-a9cf-717495945fc2` for ARM, Microsoft Graph PowerShell `14d82eec-204b-4c2f-b7e8-296a70dab67e` for Graph | No registration needed. ARM `user_impersonation` is pre-authorized. Graph PowerShell is often already consented in admin-heavy tenants. | Not officially meant for third-party apps. The Graph PowerShell app still needs consent for the PIM scopes (per user, or admin-granted). Behaviour can change. |
| **C. Reuse an Azure CLI / Az PowerShell login** (`AzureCliCredential`) | Zero setup for ARM. | Azure CLI's Graph token has a fixed scope set that probably lacks the PIM scopes. Multi-account support is awkward. |

**Decision (2026-11-20): option B.** Use the built-in Microsoft Graph PowerShell client ID for Graph and the Azure PowerShell client ID for ARM.
- Client IDs remain **configurable per audience**, so option A can be switched in later if needed.
- The spike confirms that the PIM scopes are granted in our tenant.

### Other authentication considerations

- **WAM broker (Windows).** MSAL.NET with `Microsoft.Identity.Client.Broker` uses the OS account manager. Benefits:
  - SSO with the Windows/Edge session.
  - Device-compliance Conditional Access policies pass.
  - Account picker for several accounts.
  - Phishing-resistant MFA (Windows Hello / FIDO2).
  - Tokens protected by the OS.
- **Multiple accounts.** One `IPublicClientApplication` with a persisted token cache holds many accounts. Each call uses `AcquireTokenSilent(scopes, account)`, so user and admin can be shown side by side.
- **Step-up / claims challenge.** Two triggers:
  - The policy requires an auth context, or
  - A request fails with `401` + `WWW-Authenticate: ... error="insufficient_claims", claims="..."`.

  In both cases, call `AcquireTokenInteractive(...).WithClaims(claims)` and retry. PIMActivation-Portal implements this pattern.
- **MFA at activation.** Graph documents that self-activate/extend requires MFA enforced in the session. WAM plus the claims challenge covers this.

## 5. Gotchas

- **Propagation.** After activating, the role can take from seconds to minutes to become effective. Show "Activating…", then poll the active-instances list (for example every 10 s for up to 2 min).
- **Approval.** Roles that need approval return status `PendingApproval`. Show these separately and allow cancelling them (`action: "selfDeactivate"` / `cancel`).
- **Errors to map to friendly messages:**
  - `RoleAssignmentExists`: already active.
  - `ActiveDurationTooShort`: deactivating less than about 5 minutes after activation.
  - `ExpirationRule` failures: duration above the maximum.
  - `JustificationRule` / `TicketingRule` failures: required field missing.
  - Claims challenge: step-up needed.
- **Throttling.** Use Graph `$batch` (max 20 requests per batch), bounded parallelism for ARM, and honour `Retry-After` on `429`.
- **Caching.** Role definitions and policies change rarely, so cache them for about 30 minutes or more. Always fetch eligible/active instances fresh, on demand and on a refresh timer.
- **Hidden portal API** (`api.azrbac.mspim.azure.com`). Undocumented. Avoid it.

## 6. Platform options considered

| Option | Verdict |
|---|---|
| Browser SPA / PWA (MSAL.js) | Light, proven by PIMActivation-Portal. **But:** needs its own app registration (SPA redirect URI), has no WAM (so device-compliance CA can be an issue), and keeps tokens in browser storage. |
| Browser extension | Same limits as the SPA, plus store packaging overhead. |
| Tauri / Electron + MSAL.js or msal-node | Nice UI. msal-node has WAM support via `@azure/msal-node-extensions`, but the toolchain is heavier. |
| TUI / CLI | Fast, but does not meet "nice simple UI" with favorites and groups. |
| **.NET desktop + WebView UI (Blazor Hybrid) + MSAL.NET with WAM** | ✅ **Recommended.** First-class WAM, multiple accounts, claims challenge, encrypted token cache, no localhost port exposed, modern web-style UI (Fluent UI Blazor), single exe. Windows-only, which is fine for a laptop. |

## 7. Sources

- Graph – create roleAssignmentScheduleRequest: https://learn.microsoft.com/en-us/graph/api/rbacapplication-post-roleassignmentschedulerequests
- Graph – filterByCurrentUser (eligible roles): https://learn.microsoft.com/en-us/graph/api/unifiedroleeligibilityscheduleinstance-filterbycurrentuser
- Graph – PIM for Groups request: https://learn.microsoft.com/en-us/graph/api/privilegedaccessgroup-post-assignmentschedulerequests
- Graph – PIM for Groups filterByCurrentUser: https://learn.microsoft.com/en-us/graph/api/privilegedaccessgroupeligibilityscheduleinstance-filterbycurrentuser
- Graph – PIM rules overview: https://learn.microsoft.com/en-us/graph/identity-governance-pim-rules-overview
- ARM – roleAssignmentScheduleRequests create: https://learn.microsoft.com/en-us/rest/api/authorization/role-assignment-schedule-requests/create?view=rest-authorization-2020-10-01
- ARM – roleEligibilityScheduleInstances list (`asTarget()`): https://learn.microsoft.com/en-us/rest/api/authorization/role-eligibility-schedule-instances/list-for-scope?view=rest-authorization-2020-10-01
- MSAL.NET WAM broker: https://learn.microsoft.com/en-us/entra/msal/dotnet/acquiring-tokens/desktop-mobile/wam
- MSAL claims challenge: https://learn.microsoft.com/en-us/entra/identity-platform/claims-challenge

## 8. Not yet verified (to check in the spike)

- Whether the first-party client IDs can obtain the Graph PIM scopes in our tenant, and whether consent prompts appear.
- Whether tenant-root `asTarget()` returns management-group, subscription, RG and resource eligibilities in one call.
- Exact error codes and texts. These currently come from community usage.
