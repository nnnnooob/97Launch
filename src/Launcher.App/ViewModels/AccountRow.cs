using Launcher.App.Services.Accounts;
using Microsoft.UI.Xaml;

namespace Launcher.App.ViewModels;

/// <summary>
/// One row in the account list.
/// </summary>
/// <remarks>
/// A wrapper rather than the account itself, so the "this is the active account"
/// flag belongs to the row and not to the stored account. Putting it on the account
/// meant the list and the manager shared one mutable object, and a rebuild could
/// show two badges or none, depending on the order the collection updated in.
/// </remarks>
public sealed class AccountRow
{
    public AccountRow(Account account, bool isCurrent)
    {
        Account = account;
        IsCurrent = isCurrent;
    }

    public Account Account { get; }

    public string Name => Account.Name;

    public string TypeDisplay => Account.TypeDisplay;

    public string Avatar => Account.Avatar;

    public bool IsCurrent { get; }

    /// <summary>
    /// <see cref="IsCurrent"/> as a visibility, since WinUI has no built-in
    /// bool-to-visibility conversion for a plain binding.
    /// </summary>
    public Visibility CurrentVisibility =>
        IsCurrent ? Visibility.Visible : Visibility.Collapsed;
}
