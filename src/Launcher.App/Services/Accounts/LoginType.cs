namespace Launcher.App.Services.Accounts;

/// <summary>
/// Login methods, mirroring PCL's <c>McLoginType</c>.
/// </summary>
/// <remarks>
/// The names match PCL's exactly because the persisted key names are derived from
/// them (<c>Cache{Token}Access</c> and friends), so keeping the vocabulary
/// identical keeps the settings file readable to anyone who knows PCL.
/// </remarks>
public enum LoginType
{
    /// <summary>Offline mode: a username and a locally generated UUID.</summary>
    Legacy,

    /// <summary>Microsoft / Mojang account, via the OAuth device code flow.</summary>
    Ms,

    /// <summary>A third-party authlib-injector server.</summary>
    Auth,

    /// <summary>Nide8 ("统一通行证") authentication.</summary>
    Nide,
}

public static class LoginTypeExtensions
{
    /// <summary>
    /// The short token PCL uses as a prefix for every cache key of this type.
    /// </summary>
    public static string Token(this LoginType type) => type switch
    {
        LoginType.Ms => "Ms",
        LoginType.Legacy => "Legacy",
        LoginType.Auth => "Auth",
        LoginType.Nide => "Nide",
        _ => "Legacy",
    };

    /// <summary>
    /// The label shown next to an account in the UI.
    /// </summary>
    public static string DisplayName(this LoginType type) => type switch
    {
        LoginType.Ms => "微软正版",
        LoginType.Legacy => "离线模式",
        LoginType.Auth => "Authlib-Injector",
        LoginType.Nide => "统一通行证",
        _ => "离线模式",
    };
}
