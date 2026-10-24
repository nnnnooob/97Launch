using CommunityToolkit.Mvvm.ComponentModel;

namespace Launcher.App.Views;

/// <summary>
/// Fields shown in <see cref="DeviceCodeDialog"/>.
/// </summary>
/// <remarks>
/// Kept separate from the accounts view model because the dialog has its own lifetime:
/// it is created per sign-in and thrown away when the code is consumed or cancelled.
/// </remarks>
public sealed partial class DeviceCodeViewModel : ObservableObject
{
    [ObservableProperty]
    private string _deviceCode = "";

    [ObservableProperty]
    private string _verificationUri = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _errorText = "";

    public bool HasError => !string.IsNullOrEmpty(ErrorText);
}
