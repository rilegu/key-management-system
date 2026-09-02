using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace KeyManagement.Desktop.ViewModels;

/// <summary>
/// Small conversions the administration screen needs.
/// </summary>
public static class AdministrationConverters
{
    /// <summary>
    /// Turns "is active" into the label for the button that changes it.
    /// </summary>
    /// <remarks>
    /// The button says what it will do, not what is true now. A button labelled with the
    /// current state is the classic way to get someone to click the wrong thing.
    /// </remarks>
    public static readonly IValueConverter SuspendOrReinstate =
        new FuncValueConverter<bool, string>(isActive => isActive ? "Suspend" : "Reinstate");

    /// <summary>Turns a count into a sentence fragment that reads correctly at one.</summary>
    public static readonly IValueConverter ItemCount =
        new FuncValueConverter<int, string>(
            count => count.ToString(CultureInfo.CurrentCulture) + (count == 1 ? " item" : " items"));
}
