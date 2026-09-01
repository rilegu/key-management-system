using System;

namespace KeyManagement.Desktop.Services;

/// <summary>
/// Navigation as an event the shell listens to.
/// </summary>
/// <remarks>
/// A view model that asked the shell directly would need a reference to it, and the shell needs
/// references to the view models. Raising an event breaks that circle and leaves both testable
/// on their own.
/// </remarks>
public sealed class NavigationService : INavigationService
{
    /// <summary>Raised when something asks to change screen.</summary>
    public event Action<Destination>? Requested;

    /// <inheritdoc />
    public void Show(Destination destination) => Requested?.Invoke(destination);
}

/// <summary>
/// Notifications as an event the shell listens to.
/// </summary>
public sealed class NotificationService : INotificationService
{
    /// <summary>Raised when something has been said to the operator.</summary>
    public event Action<Notification>? Raised;

    /// <inheritdoc />
    public void Success(string message) => Raised?.Invoke(new Notification(message, IsProblem: false));

    /// <inheritdoc />
    public void Problem(string message) => Raised?.Invoke(new Notification(message, IsProblem: true));
}

/// <summary>One line shown to the operator.</summary>
/// <param name="Message">What happened.</param>
/// <param name="IsProblem">Whether it was a refusal or a failure rather than a confirmation.</param>
public sealed record Notification(string Message, bool IsProblem);
