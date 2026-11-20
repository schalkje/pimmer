using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Pimmer.Core.Auth;
using Pimmer.Core.Http;
using Pimmer.Core.Providers;
using Pimmer.Core.Services;
using Pimmer.Core.Storage;

namespace Pimmer.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "Pimmer – unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        var profile = ParseProfile(e.Args);
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pimmer");
        var dataDir = profile is null ? root : Path.Combine(root, "profiles", profile);

        var store = new LocalStore(dataDir);
        MainWindow? window = null;
        var tokens = new MsalTokenService(
            store.Data.Settings,
            dataDir,
            () => window is null ? IntPtr.Zero : new WindowInteropHelper(window).Handle,
            new WpfDispatcher(Dispatcher));
        var api = new PimApi(new HttpClient { Timeout = TimeSpan.FromSeconds(60) }, tokens);
        var pim = new PimService(tokens, store, [new EntraRoleProvider(api), new GroupPimProvider(api), new AzureResourceProvider(api)]);

        var services = new ServiceCollection();
        services.AddWpfBlazorWebView();
#if DEBUG
        services.AddBlazorWebViewDeveloperTools();
#endif
        services.AddSingleton(store);
        services.AddSingleton(pim);

        window = new MainWindow(services.BuildServiceProvider(), pim, dataDir, profile);
        MainWindow = window;
        window.Show();
    }

    private static string? ParseProfile(string[] args)
    {
        var i = Array.FindIndex(args, a => a.Equals("--profile", StringComparison.OrdinalIgnoreCase));
        if (i < 0 || i + 1 >= args.Length) return null;
        var name = string.Concat(args[i + 1].Where(c => char.IsLetterOrDigit(c) || c is '-' or '_'));
        return name.Length == 0 ? null : name;
    }

    private sealed class WpfDispatcher(Dispatcher dispatcher) : IUiDispatcher
    {
        public Task<T> InvokeAsync<T>(Func<Task<T>> action) => dispatcher.InvokeAsync(action).Task.Unwrap();
    }
}
