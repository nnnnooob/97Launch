using System;
using System.Threading;
using System.Threading.Tasks;
using Launcher.App.Services.Accounts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace Launcher.App.Views;

/// <summary>
/// Shows the Microsoft device code and drives the sign-in while it is open.
/// </summary>
/// <remarks>
/// PCL puts this in a helper window rather than inline because the code has to stay
/// readable while the browser is in front. The dialog also owns the polling, so
/// cancelling it stops the login instead of leaving a background loop running.
/// </remarks>
public sealed partial class DeviceCodeDialog : ContentDialog
{
    private readonly MicrosoftLogin _login;
    private readonly CancellationTokenSource _cts;

    private readonly TaskCompletionSource<(string AccessToken, string RefreshToken)?> _result =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public DeviceCodeDialog(MicrosoftLogin login, DeviceCodePrompt prompt, CancellationToken cancellationToken = default)
    {
        InitializeComponent();

        _login = login;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _cts.Token;

        ViewModel.DeviceCode = prompt.UserCode;
        ViewModel.VerificationUri = prompt.VerificationUri;

        // PCL opens the page and copies the code as soon as the window appears, then
        // polls. The tiny delay lets the dialog finish rendering before the browser
        // takes the foreground, otherwise the user never sees the code.
        Opened += async (_, _) =>
        {
            try
            {
                await Task.Delay(600, token);
                OpenPage();
                CopyCode();
                _result.TrySetResult(await _login.WaitForDeviceCodeAsync(prompt, token));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                _result.TrySetResult(null);
            }
            catch (Exception ex)
            {
                _result.TrySetException(ex);
            }
        };

        PrimaryButtonClick += (_, args) =>
        {
            // Reopening must not close the dialog, or polling would be cancelled.
            args.Cancel = true;
            OpenPage();
        };

        SecondaryButtonClick += (_, args) =>
        {
            args.Cancel = true;
            CopyCode();
        };

        Closed += (_, _) =>
        {
            _cts.Cancel();
            _result.TrySetResult(null);
            _cts.Dispose();
        };
    }

    private DeviceCodeViewModel ViewModel { get; } = new();

    /// <summary>
    /// Completes when the browser flow finishes, or returns null if it was cancelled.
    /// </summary>
    public Task<(string AccessToken, string RefreshToken)?> ResultAsync() => _result.Task;

    /// <summary>Shows a failure inside the dialog so the window stays useful.</summary>
    public void ShowError(string message) => ViewModel.ErrorText = message;

    private void OpenPage()
    {
        try
        {
            _ = Windows.System.Launcher.LaunchUriAsync(new Uri(ViewModel.VerificationUri));
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "打开微软登录页面失败");
        }
    }

    private void CopyCode()
    {
        try
        {
            var package = new DataPackage();
            package.SetText(ViewModel.DeviceCode);
            Clipboard.SetContent(package);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "复制协助码失败");
        }
    }
}
