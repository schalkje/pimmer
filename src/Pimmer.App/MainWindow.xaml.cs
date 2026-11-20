using System.IO;
using System.Windows;
using Microsoft.AspNetCore.Components.WebView.Wpf;
using Pimmer.App.Components;
using Pimmer.Core.Services;

namespace Pimmer.App;

public partial class MainWindow : Window
{
    private static readonly TimeSpan FocusRefreshThrottle = TimeSpan.FromSeconds(30);

    public MainWindow(IServiceProvider services, PimService pim, string dataDirectory, string? profile)
    {
        InitializeComponent();
        Title = profile is null ? "Pimmer" : $"Pimmer — {profile}";

        WebView.Services = services;
        // Separate WebView2 data folder per profile so two instances can run side by side.
        WebView.BlazorWebViewInitializing += (_, e) => e.UserDataFolder = Path.Combine(dataDirectory, "WebView2");
        WebView.RootComponents.Add(new RootComponent { Selector = "#app", ComponentType = typeof(Main) });

        Activated += (_, _) =>
        {
            if (pim.Accounts.Count > 0 && (pim.LastRefreshed is null || DateTimeOffset.Now - pim.LastRefreshed > FocusRefreshThrottle))
                _ = pim.RefreshAsync();
        };
    }
}
