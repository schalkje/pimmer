<p align="center">
  <img src="assets/pimmer-logo.png" alt="Pimmer — Privileged access. Simplified." width="480">
</p>

A fast, local Windows app for Microsoft Entra **PIM**. It covers Entra roles, PIM for Groups and Azure resource roles, for one or more accounts side by side.

Features:
- Search, with Ctrl+K and Enter to activate
- Favorites
- Custom groups, with one-click group activation
- Recent activations
- Active view with countdowns, extend and deactivate
- Pending requests
- Policy-aware activation dialog for duration, justification and ticket requirements
- Conditional Access step-up

No app registration and no backend: it signs in as you via the Windows broker (WAM), using the Microsoft Graph PowerShell and Azure PowerShell client IDs. Both are configurable in Settings.

## Run

Requires the .NET 10 SDK and the WebView2 runtime.

```powershell
dotnet run --project src\Pimmer.App
# isolated second instance / data folder:
dotnet run --project src\Pimmer.App -- --profile admin
dotnet test
```

Click **Sign in** and use **＋** in the top bar to add your second (admin) account. Account labels and colours are set in ⚙ Settings. Data is stored in `%LOCALAPPDATA%\Pimmer`. Tokens stay in the DPAPI-protected MSAL/WAM cache.

Pimmer requests only permissions your tenant has already granted to these Microsoft apps (`/.default`), so no consent or approval screen should appear. If you see "Approval required", cancel it and check that you're running the latest build. Tokens are delegated: Pimmer can never do more than your account can do in the portal.

## Docs

- [Research](docs/research.md)
- [Requirements](docs/requirements.md)
- [Design](docs/design.md). §12 lists what the MVP implements and where it deviates.
