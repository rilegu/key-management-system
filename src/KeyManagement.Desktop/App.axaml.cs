using System;
using System.Net.Http;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using KeyManagement.Desktop.Services;
using KeyManagement.Desktop.ViewModels;
using KeyManagement.Desktop.Views;
using Microsoft.Extensions.DependencyInjection;

namespace KeyManagement.Desktop;

/// <summary>
/// The application, and the composition root.
/// </summary>
public partial class App : Application
{
    /// <summary>Where the server is, unless overridden on the command line.</summary>
    public const string DefaultServerAddress = "https://localhost:7183";

    private ServiceProvider? _services;

    /// <inheritdoc />
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var address = ServerAddress(desktop.Args);
            _services = Compose(address);

            desktop.MainWindow = new ShellWindow
            {
                DataContext = _services.GetRequiredService<ShellViewModel>(),
            };

            desktop.ShutdownRequested += (_, _) => _services?.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Builds the container.
    /// </summary>
    /// <param name="serverAddress">Where the server is.</param>
    /// <returns>The configured services.</returns>
    /// <remarks>
    /// Everything a view model depends on is an interface registered here, which is what lets
    /// the same view models run against a mock client in tests.
    /// </remarks>
    private static ServiceProvider Compose(Uri serverAddress)
    {
        var services = new ServiceCollection();

        services.AddSingleton<IKeyManagementClient>(_ =>
            new KeyManagementClient(new HttpClient { BaseAddress = serverAddress }));
        services.AddSingleton<ILiveActivityFeed>(_ => new LiveActivityFeed(serverAddress));

        services.AddSingleton<NavigationService>();
        services.AddSingleton<NotificationService>();
        services.AddSingleton<INavigationService>(s => s.GetRequiredService<NavigationService>());
        services.AddSingleton<INotificationService>(s => s.GetRequiredService<NotificationService>());

        services.AddSingleton<SignInViewModel>();
        services.AddSingleton<SystemViewerViewModel>();
        services.AddSingleton<ItemsViewModel>();
        services.AddSingleton<ActivityViewModel>();
        services.AddSingleton<ShellViewModel>();

        return services.BuildServiceProvider();
    }

    private static Uri ServerAddress(string[]? args)
    {
        // --server https://host:port, so one build can be pointed at a different deployment
        // without a configuration file next to the executable.
        if (args is not null)
        {
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (args[i] is "--server" && Uri.TryCreate(args[i + 1], UriKind.Absolute, out var given))
                {
                    return given;
                }
            }
        }

        return new Uri(DefaultServerAddress);
    }
}
