using System.Security.Cryptography;
using System.Text;

namespace Launcher.Core.Instances;

/// <summary>
/// Stable, path-derived identity for an instance.
/// </summary>
/// <remarks>
/// <para>
/// The identity has to survive a re-scan: the instances page rebuilds its item
/// records every time the folder is refreshed, and the launch page and settings
/// dialog hold on to the instance the user picked. A fresh GUID per construction
/// makes every rebuild look like a different instance, so the selection drops and
/// the dialog edits nothing. See <c>InstancesViewModel</c>'s selection reset for
/// the symptom that used to cause.
/// </para>
/// <para>
/// The path is therefore the key, normalised the same way the rest of the app
/// compares folders so that <c>D:\Games\.minecraft\</c> and
/// <c>d:\games\.minecraft</c> are one instance. It is hashed rather than stored
/// raw to keep the value short and to keep a user's directory layout out of the
/// log lines that print an item's id.
/// </para>
/// </remarks>
public static class InstanceIdentity
{
    /// <summary>
    /// Normalises an instance path into a comparable form.
    /// </summary>
    /// <remarks>
    /// Windows paths are case-insensitive and tolerate a trailing separator and
    /// forward slashes, so all three are removed before comparison. The result is
    /// not a valid path for IO purposes — only for identity.
    /// </remarks>
    public static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return path
            .Replace('/', '\\')
            .TrimEnd('\\', ' ')
            .ToUpperInvariant();
    }

    /// <summary>
    /// A short, stable id for an instance at <paramref name="path"/>.
    /// </summary>
    /// <remarks>
    /// SHA-256 truncated to 128 bits: collisions are not a practical concern for
    /// the number of instances a user has, and truncating keeps the value
    /// readable in a log line. Cryptographic strength is not needed here — this is
    /// a checksum over a path, not a secret.
    /// </remarks>
    public static string ForPath(string path)
    {
        var normalized = NormalizePath(path);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant();
    }
}
