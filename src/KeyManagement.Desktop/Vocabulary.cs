using System.Collections.Generic;

namespace KeyManagement.Desktop;

/// <summary>
/// Turns the model's state names into the words this industry actually uses on screen.
/// </summary>
/// <remarks>
/// <para>
/// The domain keeps its own vocabulary, which is precise and unambiguous in code. Operators
/// read something else: positions rather than slots, items rather than assets, in cabinet and
/// out of cabinet rather than Available and CheckedOut. Translating in one place keeps the code
/// honest and the screen familiar.
/// </para>
/// <para>
/// Every state also maps to a style class, so a view never picks a colour and the board follows
/// the theme.
/// </para>
/// </remarks>
public static class Vocabulary
{
    private static readonly Dictionary<string, (string Word, string StyleClass)> CustodyStates =
        new(System.StringComparer.Ordinal)
        {
            ["Available"] = ("In cabinet", "in"),
            ["CheckoutPending"] = ("Released", "released"),
            ["CheckedOut"] = ("Out of cabinet", "out"),
            ["ReturnPending"] = ("Awaiting return", "released"),
            ["Faulted"] = ("Fault", "fault"),
            ["Unknown"] = ("Not confirmed", "unconfirmed"),
        };

    private static readonly Dictionary<string, string> CheckoutStates =
        new(System.StringComparer.Ordinal)
        {
            ["Pending"] = "Released",
            ["Denied"] = "Refused",
            ["Active"] = "Out of cabinet",
            ["Overdue"] = "Overdue",
            ["Returned"] = "Returned",
            ["Abandoned"] = "Not collected",
        };

    private static readonly Dictionary<string, (string Word, string Led)> CabinetStatuses =
        new(System.StringComparer.Ordinal)
        {
            ["Online"] = ("Online", "online"),
            ["Offline"] = ("Offline", "offline"),
            ["NeverConnected"] = ("Never connected", "connecting"),
        };

    private static readonly Dictionary<string, string> ActivityTypes =
        new(System.StringComparer.Ordinal)
        {
            ["SignInSucceeded"] = "Signed in",
            ["SignInFailed"] = "Sign-in refused",
            ["CheckoutRequested"] = "Item requested",
            ["CheckoutAuthorized"] = "Item released",
            ["CheckoutDenied"] = "Request refused",
            ["CheckoutCompleted"] = "Item taken",
            ["ReturnRequested"] = "Return started",
            ["ReturnCompleted"] = "Item returned",
            ["CustodyReconciled"] = "Custody reconciled",
            ["CabinetOnline"] = "Cabinet online",
            ["CabinetOffline"] = "Cabinet offline",
            ["SlotFaulted"] = "Position fault",
            ["UnauthorizedSlotChange"] = "Unauthorised removal",
            ["ConfigurationChanged"] = "Configuration changed",
        };

    /// <summary>What an item's custody state is called on screen.</summary>
    /// <param name="custodyState">The model's state name.</param>
    /// <returns>The operator-facing word.</returns>
    public static string CustodyWord(string? custodyState) =>
        Lookup(CustodyStates, custodyState).Word ?? custodyState ?? "Unknown";

    /// <summary>The style class carrying an item's custody state.</summary>
    /// <param name="custodyState">The model's state name.</param>
    /// <returns>The class name a tile or chip is given.</returns>
    public static string CustodyClass(string? custodyState) =>
        Lookup(CustodyStates, custodyState).StyleClass ?? "unconfirmed";

    /// <summary>What a checkout's state is called on screen.</summary>
    /// <param name="checkoutState">The model's state name.</param>
    /// <returns>The operator-facing word.</returns>
    public static string CheckoutWord(string? checkoutState) =>
        checkoutState is not null && CheckoutStates.TryGetValue(checkoutState, out var word)
            ? word
            : checkoutState ?? string.Empty;

    /// <summary>What a cabinet's link state is called on screen.</summary>
    /// <param name="status">The model's status name.</param>
    /// <returns>The operator-facing word.</returns>
    public static string CabinetWord(string? status) =>
        status is not null && CabinetStatuses.TryGetValue(status, out var found)
            ? found.Word
            : status ?? "Unknown";

    /// <summary>The style class for the connection indicator.</summary>
    /// <param name="status">The model's status name.</param>
    /// <returns>The class name the indicator is given.</returns>
    public static string CabinetLed(string? status) =>
        status is not null && CabinetStatuses.TryGetValue(status, out var found)
            ? found.Led
            : "connecting";

    /// <summary>What an audit record is called on screen.</summary>
    /// <param name="activityType">The model's event type name.</param>
    /// <returns>A short phrase for the activity list.</returns>
    public static string ActivityWord(string? activityType) =>
        activityType is not null && ActivityTypes.TryGetValue(activityType, out var word)
            ? word
            : activityType ?? string.Empty;

    /// <summary>Whether an activity type represents a refusal, which the list marks.</summary>
    /// <param name="activityType">The model's event type name.</param>
    /// <returns><see langword="true"/> for a refusal or a fault.</returns>
    public static bool IsRefusal(string? activityType) =>
        activityType is "CheckoutDenied" or "SignInFailed"
            or "SlotFaulted" or "UnauthorizedSlotChange";

    private static (string? Word, string? StyleClass) Lookup(
        Dictionary<string, (string Word, string StyleClass)> map,
        string? key) =>
        key is not null && map.TryGetValue(key, out var found) ? found : (null, null);
}
