using System.Globalization;
using KeyManagement.Domain.Assets;

namespace KeyManagement.Domain.Custody;

/// <summary>
/// The legal moves for an asset's custody state and for a checkout's lifecycle.
/// </summary>
/// <remarks>
/// <para>
/// Kept as data rather than scattered across <c>if</c> statements so the whole machine can be
/// read, and tested, in one place. Entities call into it; nothing sets a state without asking.
/// </para>
/// <para>
/// A move to the state something is already in is not legal. Device reports repeat, so a
/// caller that may receive the same state twice compares before transitioning — treating a
/// repeat as a transition would write an audit record for a change that never happened.
/// </para>
/// </remarks>
public static class CustodyTransitions
{
    // Faulted and Unknown are reachable from every real state: a cabinet can fault or fall
    // silent at any moment, and both are resolved by reconciliation rather than by the
    // normal flow.
    private static readonly Dictionary<AssetCustodyState, AssetCustodyState[]> AssetMoves = new()
    {
        [AssetCustodyState.Available] =
        [
            AssetCustodyState.CheckoutPending, AssetCustodyState.Faulted, AssetCustodyState.Unknown,
        ],

        // Back to Available when the unlock fails or the holder never takes it.
        [AssetCustodyState.CheckoutPending] =
        [
            AssetCustodyState.CheckedOut, AssetCustodyState.Available,
            AssetCustodyState.Faulted, AssetCustodyState.Unknown,
        ],

        [AssetCustodyState.CheckedOut] =
        [
            AssetCustodyState.ReturnPending, AssetCustodyState.Faulted, AssetCustodyState.Unknown,
        ],

        // Back to CheckedOut when a return is started and not completed.
        [AssetCustodyState.ReturnPending] =
        [
            AssetCustodyState.Available, AssetCustodyState.CheckedOut,
            AssetCustodyState.Faulted, AssetCustodyState.Unknown,
        ],

        [AssetCustodyState.Faulted] =
        [
            AssetCustodyState.Available, AssetCustodyState.CheckedOut, AssetCustodyState.Unknown,
        ],

        [AssetCustodyState.Unknown] =
        [
            AssetCustodyState.Available, AssetCustodyState.CheckedOut, AssetCustodyState.Faulted,
        ],
    };

    // Denied, Returned and Abandoned are terminal: each is a settled fact about a request,
    // and a settled fact does not change afterwards.
    private static readonly Dictionary<CheckoutState, CheckoutState[]> CheckoutMoves = new()
    {
        [CheckoutState.Pending] = [CheckoutState.Active, CheckoutState.Abandoned],
        [CheckoutState.Active] = [CheckoutState.Overdue, CheckoutState.Returned],
        [CheckoutState.Overdue] = [CheckoutState.Returned],
        [CheckoutState.Denied] = [],
        [CheckoutState.Returned] = [],
        [CheckoutState.Abandoned] = [],
    };

    /// <summary>Whether an asset may move between two custody states.</summary>
    /// <param name="from">The current state.</param>
    /// <param name="to">The proposed state.</param>
    /// <returns><see langword="true"/> when the move is allowed.</returns>
    public static bool IsLegal(AssetCustodyState from, AssetCustodyState to) =>
        AssetMoves.TryGetValue(from, out var allowed) && Array.IndexOf(allowed, to) >= 0;

    /// <summary>Whether a checkout may move between two lifecycle states.</summary>
    /// <param name="from">The current state.</param>
    /// <param name="to">The proposed state.</param>
    /// <returns><see langword="true"/> when the move is allowed.</returns>
    public static bool IsLegal(CheckoutState from, CheckoutState to) =>
        CheckoutMoves.TryGetValue(from, out var allowed) && Array.IndexOf(allowed, to) >= 0;

    /// <summary>The states an asset may move to from the given one.</summary>
    /// <param name="from">The current state.</param>
    /// <returns>The allowed destinations, empty if there are none.</returns>
    public static IReadOnlyList<AssetCustodyState> AllowedFrom(AssetCustodyState from) =>
        AssetMoves.TryGetValue(from, out var allowed) ? allowed : [];

    /// <summary>The states a checkout may move to from the given one.</summary>
    /// <param name="from">The current state.</param>
    /// <returns>The allowed destinations, empty if there are none.</returns>
    public static IReadOnlyList<CheckoutState> AllowedFrom(CheckoutState from) =>
        CheckoutMoves.TryGetValue(from, out var allowed) ? allowed : [];

    /// <summary>Rejects an illegal custody move.</summary>
    /// <param name="from">The current state.</param>
    /// <param name="to">The proposed state.</param>
    /// <param name="asset">The asset being moved, named in the message.</param>
    /// <exception cref="InvalidCustodyTransitionException">The move is not allowed.</exception>
    public static void EnsureLegal(AssetCustodyState from, AssetCustodyState to, AssetId asset)
    {
        if (!IsLegal(from, to))
        {
            throw new InvalidCustodyTransitionException(string.Format(
                CultureInfo.InvariantCulture,
                "Asset {0} cannot move from {1} to {2}.",
                asset,
                from,
                to));
        }
    }

    /// <summary>Rejects an illegal checkout move.</summary>
    /// <param name="from">The current state.</param>
    /// <param name="to">The proposed state.</param>
    /// <param name="checkout">The checkout being moved, named in the message.</param>
    /// <exception cref="InvalidCustodyTransitionException">The move is not allowed.</exception>
    public static void EnsureLegal(CheckoutState from, CheckoutState to, CheckoutId checkout)
    {
        if (!IsLegal(from, to))
        {
            throw new InvalidCustodyTransitionException(string.Format(
                CultureInfo.InvariantCulture,
                "Checkout {0} cannot move from {1} to {2}.",
                checkout,
                from,
                to));
        }
    }
}
