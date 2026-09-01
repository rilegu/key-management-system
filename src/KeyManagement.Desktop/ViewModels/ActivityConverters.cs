using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace KeyManagement.Desktop.ViewModels;

/// <summary>
/// Shows the activity filters in the words used on screen rather than the model's names.
/// </summary>
/// <remarks>
/// The filter values have to stay as the model names them, because that is what the server
/// expects. Only the display changes, which is exactly what a converter is for.
/// </remarks>
public static class ActivityConverters
{
    /// <summary>Turns an event type name into its operator-facing phrase.</summary>
    public static readonly IValueConverter TypeWord =
        new FuncValueConverter<string?, string>(type =>
            type is null ? string.Empty : ActivityViewModel.DescribeType(type));

    /// <summary>Turns a window in hours into its operator-facing phrase.</summary>
    public static readonly IValueConverter WindowWord =
        new FuncValueConverter<int, string>(ActivityViewModel.DescribeWindow);
}
