using System.Globalization;

namespace Launcher.App.Services.Accounts;

/// <summary>
/// Offline account identifier rules, copied from PCL's <c>McLoginLegacyUuid</c>.
/// </summary>
/// <remarks>
/// The format has to match exactly: servers and mods compute offline UUIDs the
/// same way, so any deviation would make the same username resolve to a different
/// player on different machines and silently break inventories and permissions.
/// </remarks>
public static class OfflineUuid
{
    /// <summary>
    /// Returns the deterministic offline UUID for a username, formatted the way
    /// PCL formats it (32 hex characters, no dashes).
    /// </summary>
    /// <remarks>
    /// <para>
    /// PCL builds it as: the username length in hex padded to 16 characters,
    /// followed by the username's stable hash in hex padded to 16 characters, then
    /// forces the version and variant nibbles by overwriting the 13th and 17th
    /// characters with '3' and '9'.
    /// </para>
    /// <para>
    /// The hash must be the classic .NET Framework string hash (implemented in
    /// <see cref="StableHashCode"/>); <see cref="string.GetHashCode()"/> is
    /// randomised per process on modern .NET and would produce a different UUID on
    /// every launch.
    /// </para>
    /// </remarks>
    public static string FromName(string name)
    {
        var full = name.Length.ToString("X").PadLeft(16, '0')
                   + StableHashCode(name).ToString("X").PadLeft(16, '0');

        // Characters 13 and 17 are overwritten with '3' and '9' to make the result
        // look like a name-based (version 3) UUID, matching PCL byte for byte.
        return full[..12] + "3" + full.Substring(13, 3) + "9" + full[17..];
    }

    /// <summary>
    /// Formats a 32-character UUID in the dashed 8-4-4-4-12 form used by the game.
    /// </summary>
    public static string ToDashed(string uuid)
    {
        if (uuid.Length != 32)
        {
            return uuid;
        }

        return uuid[..8] + "-" + uuid.Substring(8, 4) + "-" + uuid.Substring(12, 4)
               + "-" + uuid.Substring(16, 4) + "-" + uuid[20..];
    }

    /// <summary>
    /// The pre-.NET-Core string hash: unchecked, 32-bit, iterating in order.
    /// </summary>
    /// <remarks>
    /// Reproduced rather than replaced with a modern hash because the resulting
    /// UUID must equal what PCL and other launchers produce for the same name.
    /// </remarks>
    public static int StableHashCode(string text)
    {
        unchecked
        {
            var hash = 5381;

            foreach (var ch in text)
            {
                hash = ((hash << 5) + hash) ^ ch;
            }

            return hash;
        }
    }

    /// <summary>
    /// Picks the player model Minecraft derives from the UUID.
    /// </summary>
    /// <remarks>
    /// This is PCL's <c>McSkinSex</c>: the hex digits at positions 7, 15, 23 and 31
    /// are XORed, and an odd result means the slim (Alex) model. Four spread-out
    /// nibbles rather than one are used so that consecutive names do not all land on
    /// the same model.
    /// </remarks>
    public static bool IsSlim(string uuid) => AccountAvatar.IsAlex(uuid);

    /// <summary>
    /// Increments the trailing five hex digits of a UUID until it produces the
    /// requested player model, wrapping back to 00000 at FFFFF.
    /// </summary>
    /// <remarks>
    /// Copied from PCL's skin handling: the model is a function of the UUID, so the
    /// only way to force one is to walk to the nearest UUID that yields it.
    /// </remarks>
    public static string WithModel(string uuid, bool slim)
    {
        if (uuid.Length != 32)
        {
            return uuid;
        }

        var attempt = uuid;

        for (var guard = 0; guard < 0x100000; guard++)
        {
            if (IsSlim(attempt) == slim)
            {
                return attempt;
            }

            var tail = long.Parse(attempt.AsSpan(27), NumberStyles.HexNumber);
            if (tail >= 0xFFFFF)
            {
                tail = 0;
            }
            else
            {
                tail++;
            }

            attempt = attempt[..27] + tail.ToString("X").PadLeft(5, '0');
        }

        return uuid;
    }
}
