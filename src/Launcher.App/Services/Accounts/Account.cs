namespace Launcher.App.Services.Accounts;

/// <summary>
/// One stored account.
/// </summary>
/// <remarks>
/// Flattened from PCL's per-type cache keys into a single record, because PCL's
/// "one set of keys holds the current login" model cannot represent several
/// accounts of the same type at once, and a list of saved accounts is what the
/// account panel needs. The individual fields still correspond one-to-one to PCL's
/// cache keys so the login code can be read against it.
/// </remarks>
public sealed class Account
{
    /// <summary>Stable local identifier, independent of the account's game name.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public LoginType Type { get; set; }

    /// <summary>
    /// The in-game name. For Microsoft accounts this is the Minecraft profile name,
    /// which is not the same as the Microsoft email address.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>Player UUID: 32 hex characters, no dashes.</summary>
    public string Uuid { get; set; } = "";

    /// <summary>
    /// Login name typed by the user, kept for authlib and Nide accounts so the form
    /// can be prefilled. This is the email, not the in-game name.
    /// </summary>
    public string LoginName { get; set; } = "";

    /// <summary>
    /// The password, only when the user asked to remember it.
    /// </summary>
    /// <remarks>
    /// Stored encrypted with DPAPI. PCL stores the same thing (its login settings
    /// are marked <c>Encrypted:=True</c>), so this matches its behaviour rather
    /// than being more cautious than the launcher users are used to.
    /// </remarks>
    public string Password { get; set; } = "";

    /// <summary>Authlib or Nide server base URL, empty for other types.</summary>
    public string Server { get; set; } = "";

    /// <summary>Microsoft OAuth refresh token.</summary>
    public string RefreshToken { get; set; } = "";

    /// <summary>Access token for the current session (Microsoft, authlib, Nide).</summary>
    public string AccessToken { get; set; } = "";

    /// <summary>Client token returned by authlib / Nide servers.</summary>
    public string ClientToken { get; set; } = "";

    /// <summary>Unix seconds at which <see cref="AccessToken"/> stops being usable.</summary>
    public long ExpiresAt { get; set; }

    /// <summary>Raw Microsoft profile JSON, kept for the skin viewer.</summary>
    public string ProfileJson { get; set; } = "";

    /// <summary>When this account was added, used as a stable sort fallback.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>Cached avatar path, empty when there is no avatar yet.</summary>
    public string AvatarPath { get; set; } = "";

    /// <summary>
    /// The head image URI to show for this account.
    /// </summary>
    /// <remarks>
    /// A fetched skin lives on disk, while the bundled fallbacks are package URIs, so
    /// the two are converted into the single form an Image can bind to. Not cached in
    /// a field: the value changes once the skin arrives, and the binding re-reads it.
    /// </remarks>
    public string Avatar
    {
        get
        {
            if (!string.IsNullOrEmpty(AvatarPath) && System.IO.File.Exists(AvatarPath))
            {
                return new Uri(AvatarPath).AbsoluteUri;
            }

            return AccountAvatar.ForOfflineName(Name);
        }
    }



    /// <summary>Label for the account type badge.</summary>
    public string TypeDisplay => Type.DisplayName();

    /// <summary>The UUID in the dashed form the UI and the game display.</summary>
    public string UuidDisplay => OfflineUuid.ToDashed(Uuid);

    /// <summary>Access token usability, accounting for the expiry margin.</summary>
    public bool HasValidToken =>
        !string.IsNullOrEmpty(AccessToken)
        && ExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>
    /// Whether the account can start the game without any further interaction.
    /// </summary>
    /// <remarks>
    /// Offline accounts are always ready. Microsoft accounts need a live access
    /// token but can refresh one from the stored refresh token, so a refresh token
    /// alone counts. Server-backed accounts can re-authenticate only when a
    /// password was remembered.
    /// </remarks>
    public bool IsUsable => Type switch
    {
        LoginType.Legacy => !string.IsNullOrWhiteSpace(Name),
        LoginType.Ms => HasValidToken || !string.IsNullOrEmpty(RefreshToken),
        _ => HasValidToken || !string.IsNullOrEmpty(Password),
    };

    /// <summary>Two accounts are the same when they authenticate the same player.</summary>
    public bool Matches(LoginType type, string name, string server) =>
        Type == type
        && string.Equals(Name, name, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Server, server, StringComparison.OrdinalIgnoreCase);
}
