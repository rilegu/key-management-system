using Avalonia.Controls;
using Avalonia.Input;
using KeyManagement.Desktop.ViewModels;

namespace KeyManagement.Desktop.Views;

/// <summary>
/// The position board and its detail panels.
/// </summary>
public partial class SystemViewerView : UserControl
{
    /// <summary>Creates the view.</summary>
    public SystemViewerView() => InitializeComponent();

    /// <summary>
    /// Selects the position that was clicked.
    /// </summary>
    /// <param name="sender">The tile.</param>
    /// <param name="e">The click.</param>
    /// <remarks>
    /// A handler rather than a command per tile: the tiles live in an <c>ItemsControl</c>, and
    /// binding a command through the item's own data context to the screen's would need a
    /// relative-source lookup in every template. This reads the tile and hands it over.
    /// </remarks>
    private void OnPositionPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: PositionViewModel position }
            && DataContext is SystemViewerViewModel screen)
        {
            screen.SelectPositionCommand.Execute(position);
        }
    }
}
