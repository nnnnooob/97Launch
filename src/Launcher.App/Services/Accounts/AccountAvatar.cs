namespace Launcher.App.Services.Accounts;

/// <summary>
/// Picks the head image for an account, following PCL's per-type skin rules.
/// </summary>
/// <remarks>
/// <para>
/// The four rules are PCL's <c>SkinLegacy</c>, <c>SkinMs</c>, <c>SkinNide</c> and
/// <c>SkinAuth</c> loaders, reduced to what a launcher without a skin editor needs:
/// </para>
/// <list type="bullet">
/// <item>Offline: a vanilla Steve or Alex head, chosen from the UUID.</item>
/// <item>Microsoft / authlib / Nide: the player's own skin, fetched and clipped.</item>
/// </list>
/// <para>
/// The offline choice uses PCL's <c>McSkinSex</c> rule exactly: the XOR of the hex
/// digits at positions 7, 15, 23 and 31, odd being Alex. Using a different rule
/// would show the wrong default head compared to the game, which derives its own
/// default model from the same UUID.
/// </para>
/// </remarks>
public static class AccountAvatar
{
    /// <summary>Bundled vanilla heads, used when there is no skin to fetch.</summary>
    public const string Steve = "ms-appx:///Assets/Skins/steve_head.png";

    public const string Alex = "ms-appx:///Assets/Skins/alex_head.png";

    /// <summary>
    /// The head for an offline account name.
    /// </summary>
    public static string ForOfflineName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Steve;
        }

        return IsAlex(OfflineUuid.FromName(name.Trim())) ? Alex : Steve;
    }

    /// <summary>
    /// PCL's <c>McSkinSex</c>: whether a UUID maps to the slim (Alex) model.
    /// </summary>
    /// <remarks>
    /// Four hex nibbles spread across the UUID are XORed rather than one taken
    /// directly, so that nearby names do not all land on the same model.
    /// </remarks>
    public static bool IsAlex(string uuid)
    {
        if (uuid.Length != 32)
        {
            return false;
        }

        var a = HexAt(uuid, 7);
        var b = HexAt(uuid, 15);
        var c = HexAt(uuid, 23);
        var d = HexAt(uuid, 31);

        return (a ^ b ^ c ^ d) % 2 != 0;
    }

    private static int HexAt(string value, int index) => Convert.ToInt32(value[index].ToString(), 16);
}
