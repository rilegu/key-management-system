using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KeyManagement.Desktop.Services;

namespace KeyManagement.Desktop.ViewModels;

/// <summary>
/// The window: navigation, the current screen, the signed-in holder, and notifications.
/// </summary>
public sealed partial class ShellViewModel : ViewModelBase
{
    private static readonly TimeSpan NotificationLifetime = TimeSpan.FromSeconds(6);

    private readonly IKeyManagementClient _client;
    private readonly NavigationService _navigation;
    private readonly NotificationService _notifications;
    private readonly SignInViewModel _signIn;
    private readonly SystemViewerViewModel _systemViewer;
    private readonly ItemsViewModel _items;
    private readonly ActivityViewModel _activity;
    private DispatcherTimer? _notificationTimer;

    /// <summary>Creates the shell.</summary>
    /// <param name="client">The server.</param>
    /// <param name="navigation">Screen change requests.</param>
    /// <param name="notifications">Messages for the operator.</param>
    /// <param name="signIn">The sign-in screen.</param>
    /// <param name="systemViewer">The board.</param>
    /// <param name="items">The items table.</param>
    /// <param name="activity">The activity trail.</param>
    public ShellViewModel(
        IKeyManagementClient client,
        NavigationService navigation,
        NotificationService notifications,
        SignInViewModel signIn,
        SystemViewerViewModel systemViewer,
        ItemsViewModel items,
        ActivityViewModel activity)
    {
        _client = client;
        _navigation = navigation;
        _notifications = notifications;
        _signIn = signIn;
        _systemViewer = systemViewer;
        _items = items;
        _activity = activity;

        _navigation.Requested += destination => _ = GoToAsync(destination);
        _notifications.Raised += Show;

        Current = _signIn;
    }

    /// <summary>The screen on show.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSignedIn))]
    public partial ViewModelBase Current { get; set; }

    /// <summary>Where the shell currently is, for the navigation rail.</summary>
    [ObservableProperty]
    public partial Destination Location { get; set; } = Destination.SignIn;

    /// <summary>The signed-in holder's name, for the top bar.</summary>
    [ObservableProperty]
    public partial string HolderName { get; set; } = string.Empty;

    /// <summary>The current notification, or <see langword="null"/>.</summary>
    [ObservableProperty]
    public partial Notification? Notice { get; set; }

    /// <summary>Whether the dark theme is showing.</summary>
    [ObservableProperty]
    public partial bool IsDarkTheme { get; set; }

    /// <summary>Whether a holder is signed in, which the chrome is hidden without.</summary>
    public bool IsSignedIn => Current is not SignInViewModel;

    /// <summary>Whether the board is on show.</summary>
    public bool IsOnSystemViewer => Location == Destination.SystemViewer;

    /// <summary>Whether the items table is on show.</summary>
    public bool IsOnItems => Location == Destination.Items;

    /// <summary>Whether the activity trail is on show.</summary>
    public bool IsOnActivity => Location == Destination.Activity;

    /// <summary>Shows the board.</summary>
    /// <returns>A task that completes when the screen is ready.</returns>
    [RelayCommand]
    public Task ShowSystemViewerAsync() => GoToAsync(Destination.SystemViewer);

    /// <summary>Shows the items table.</summary>
    /// <returns>A task that completes when the screen is ready.</returns>
    [RelayCommand]
    public Task ShowItemsAsync() => GoToAsync(Destination.Items);

    /// <summary>Shows the activity trail.</summary>
    /// <returns>A task that completes when the screen is ready.</returns>
    [RelayCommand]
    public Task ShowActivityAsync() => GoToAsync(Destination.Activity);

    /// <summary>Ends the session and returns to sign-in.</summary>
    [RelayCommand]
    public void SignOut()
    {
        _client.SignOut();
        HolderName = string.Empty;
        Location = Destination.SignIn;
        Current = _signIn;
        OnPropertyChanged(nameof(IsSignedIn));
        RaiseLocationFlags();
    }

    /// <summary>Dismisses the current notification.</summary>
    [RelayCommand]
    public void DismissNotice() => Notice = null;

    /// <summary>Switches between the light and dark themes.</summary>
    /// <remarks>
    /// Light is the default: this is administration software that sits beside other business
    /// applications. Dark is for anyone running it in a control room, where a bright window is
    /// the wrong thing to stare at for a shift.
    /// </remarks>
    [RelayCommand]
    public void ToggleTheme()
    {
        IsDarkTheme = !IsDarkTheme;

        if (Application.Current is { } application)
        {
            application.RequestedThemeVariant = IsDarkTheme ? ThemeVariant.Dark : ThemeVariant.Light;
        }
    }

    private async Task GoToAsync(Destination destination)
    {
        Location = destination;
        RaiseLocationFlags();

        switch (destination)
        {
            case Destination.SystemViewer:
                Current = _systemViewer;
                HolderName = _client.Session?.DisplayName ?? string.Empty;

                // Connected once, on the first screen after sign-in, because the feed needs a
                // token and there is none before that.
                await _systemViewer.StartLiveFeedAsync().ConfigureAwait(true);
                await _systemViewer.LoadAsync().ConfigureAwait(true);
                break;

            case Destination.Items:
                Current = _items;
                await _items.LoadAsync().ConfigureAwait(true);
                break;

            case Destination.Activity:
                Current = _activity;
                await _activity.SearchAsync().ConfigureAwait(true);
                break;

            case Destination.SignIn:
            default:
                Current = _signIn;
                break;
        }

        OnPropertyChanged(nameof(IsSignedIn));
    }

    private void RaiseLocationFlags()
    {
        OnPropertyChanged(nameof(IsOnSystemViewer));
        OnPropertyChanged(nameof(IsOnItems));
        OnPropertyChanged(nameof(IsOnActivity));
    }

    private void Show(Notification notification)
    {
        Notice = notification;

        // A notice that stays until dismissed becomes wallpaper. This one clears itself, and
        // the activity feed keeps the permanent record.
        _notificationTimer?.Stop();
        _notificationTimer = new DispatcherTimer { Interval = NotificationLifetime };
        _notificationTimer.Tick += (_, _) =>
        {
            _notificationTimer?.Stop();
            Notice = null;
        };
        _notificationTimer.Start();
    }
}
