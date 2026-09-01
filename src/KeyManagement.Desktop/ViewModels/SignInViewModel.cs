using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KeyManagement.Desktop.Services;

namespace KeyManagement.Desktop.ViewModels;

/// <summary>
/// Sign-in.
/// </summary>
public sealed partial class SignInViewModel : ViewModelBase
{
    private readonly IKeyManagementClient _client;
    private readonly INavigationService _navigation;

    /// <summary>Creates the screen.</summary>
    /// <param name="client">The server.</param>
    /// <param name="navigation">Where to go once signed in.</param>
    public SignInViewModel(IKeyManagementClient client, INavigationService navigation)
    {
        _client = client;
        _navigation = navigation;
    }

    /// <summary>The holder's sign-in name.</summary>
    [ObservableProperty]
    [NotifyDataErrorInfo]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    [Required(ErrorMessage = "Enter your user name.")]
    public partial string UserName { get; set; } = string.Empty;

    /// <summary>The holder's password.</summary>
    [ObservableProperty]
    [NotifyDataErrorInfo]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    [Required(ErrorMessage = "Enter your password.")]
    public partial string Password { get; set; } = string.Empty;

    /// <summary>Whether the credentials are complete enough to send.</summary>
    public bool CanSignIn =>
        !string.IsNullOrWhiteSpace(UserName) && !string.IsNullOrWhiteSpace(Password);

    /// <summary>Signs in and moves to the board.</summary>
    /// <returns>A task that completes when the attempt does.</returns>
    [RelayCommand(CanExecute = nameof(CanSignIn))]
    private Task SignInAsync() => RunAsync(async token =>
    {
        var result = await _client.SignInAsync(UserName, Password, token).ConfigureAwait(true);

        if (!result.Success)
        {
            // The server's message is deliberately the same for an unknown account and a wrong
            // password, and it is shown as written rather than reinterpreted here.
            ErrorMessage = result.Message;
            return;
        }

        Password = string.Empty;
        _navigation.Show(Destination.SystemViewer);
    });
}
