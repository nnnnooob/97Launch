using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using Serilog;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Launcher.App.Services.Accounts;

/// <summary>
/// Resolves and caches account head images, the way PCL builds its skin preview.
/// </summary>
/// <remarks>
/// <para>
/// PCL downloads the whole 64×64 skin rather than a pre-rendered head, then clips two
/// 8×8 tiles out of it: the face at (8,8) and the hat layer at (40,8). The hat is
/// drawn over the face only when it actually carries pixels, because on most skins
/// that region is fully transparent and compositing it would just cost a pass.
/// This does the same, so an account's avatar matches what PCL shows for it.
/// </para>
/// <para>
/// The texture URL is not guessable from a UUID: it comes from the session server's
/// signed texture property. That lookup is why avatars are cached to disk — the
/// session server rate-limits, and a launcher redraws its account list often.
/// </para>
/// </remarks>
public sealed class SkinService
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(20),
    };

    private readonly string _cacheDirectory;
    private readonly SemaphoreSlim _skinFetch = new(1, 1);

    public SkinService(string cacheDirectory)
    {
        _cacheDirectory = cacheDirectory;
    }

    /// <summary>
    /// Returns the path of a cached head for an account, fetching it if needed.
    /// </summary>
    /// <returns>The head image path, or null when no skin could be resolved.</returns>
    public async Task<string?> GetHeadAsync(Account account, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(account.Uuid) || account.Type == LoginType.Legacy)
        {
            // Offline UUIDs have no skin server behind them; the caller falls back to
            // a vanilla head.
            return null;
        }

        Directory.CreateDirectory(_cacheDirectory);

        var headPath = Path.Combine(_cacheDirectory, $"{account.Uuid.ToLowerInvariant()}_head.png");

        if (File.Exists(headPath))
        {
            return headPath;
        }

        var skinBytes = await GetSkinAsync(account, ct);
        if (skinBytes is null) return null;
        var headBytes = await ClipHeadAsync(skinBytes);

        if (headBytes is null)
        {
            return null;
        }

        await File.WriteAllBytesAsync(headPath, headBytes, ct);
        return headPath;
    }

    /// <summary>Full skin for the local 3D viewer; no credentials enter the WebView.</summary>
    public async Task<byte[]?> GetSkinAsync(Account account, CancellationToken ct)
    {
        if (account.Type == LoginType.Legacy || string.IsNullOrEmpty(account.Uuid)) return null;
        await _skinFetch.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes($"{account.Id}:{account.Type}:{account.Server}:{account.Uuid}")));
            var path = Path.Combine(_cacheDirectory, key + "_skin.png");
            if (File.Exists(path))
            {
                try
                {
                    var cached = await File.ReadAllBytesAsync(path, ct);
                    if (await IsSkinAsync(cached)) return cached;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { Log.Debug(ex, "读取皮肤缓存失败"); }
            }
            var url = await ResolveSkinUrlAsync(account, ct);
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https") return null;
            using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            const int limit = 2 * 1024 * 1024;
            if (response.Content.Headers.ContentLength > limit) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var data = new MemoryStream();
            var buffer = new byte[16384];
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                if (data.Length + read > limit) return null;
                data.Write(buffer, 0, read);
            }
            var bytes = data.ToArray();
            if (!await IsSkinAsync(bytes)) return null;
            await File.WriteAllBytesAsync(path + ".tmp", bytes, ct);
            File.Move(path + ".tmp", path, true);
            return bytes;
        }
        finally { _skinFetch.Release(); }
    }

    private static async Task<bool> IsSkinAsync(byte[] bytes)
    {
        if (bytes.Length < 8 || bytes.Length > 2 * 1024 * 1024
            || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return false;
        try
        {
            using var input = new InMemoryRandomAccessStream();
            await input.WriteAsync(bytes.AsBuffer());
            input.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(input);
            return decoder.PixelWidth is >= 64 and <= 1024 && decoder.PixelWidth % 64 == 0
                && (decoder.PixelHeight == decoder.PixelWidth || decoder.PixelHeight * 2 == decoder.PixelWidth);
        }
        catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.COMException) { return false; }
    }

    /// <summary>
    /// Finds the signed texture URL for an account's skin.
    /// </summary>
    /// <remarks>
    /// The profile endpoint differs per account type, but the response shape is the
    /// same: a <c>properties</c> array holding a base64 <c>textures</c> blob.
    /// </remarks>
    private static async Task<string?> ResolveSkinUrlAsync(Account account, CancellationToken ct)
    {
        var baseUrl = account.Type switch
        {
            LoginType.Ms => "https://sessionserver.mojang.com/session/minecraft/profile/",
            LoginType.Nide => NideSessionsUrl(account.Server),
            LoginType.Auth => AuthSessionsUrl(account.Server),
            _ => null,
        };

        if (baseUrl is null)
        {
            return null;
        }

        try
        {
            var body = await Http.GetStringAsync(baseUrl + account.Uuid, ct);
            using var doc = JsonDocument.Parse(body);

            if (!doc.RootElement.TryGetProperty("properties", out var properties))
            {
                return null;
            }

            foreach (var property in properties.EnumerateArray())
            {
                if (!property.TryGetProperty("name", out var name)
                    || name.GetString() != "textures")
                {
                    continue;
                }

                var value = property.GetProperty("value").GetString();

                if (string.IsNullOrEmpty(value))
                {
                    return null;
                }

                var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(value));
                using var textureDoc = JsonDocument.Parse(decoded);

                if (!textureDoc.RootElement.TryGetProperty("textures", out var textures)
                    || !textures.TryGetProperty("SKIN", out var skin)
                    || !skin.TryGetProperty("url", out var url))
                {
                    // The account exists but never set a skin.
                    return null;
                }

                var skinUrl = url.GetString() ?? "";

                // The session server still hands out http URLs; textures.minecraft.net
                // serves the same bytes over https.
                return skinUrl.StartsWith("http://", StringComparison.Ordinal)
                    ? "https://" + skinUrl["http://".Length..]
                    : skinUrl;
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "获取皮肤地址失败：{Name}", account.Name);
        }

        return null;
    }

    private static string NideSessionsUrl(string server)
    {
        var value = server.Trim().TrimEnd('/');

        if (value.EndsWith("/authserver", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^"/authserver".Length];
        }

        return value + "/sessionserver/session/minecraft/profile/";
    }

    private static string AuthSessionsUrl(string server)
    {
        var value = server.Trim().TrimEnd('/');

        if (value.EndsWith("/authserver", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^"/authserver".Length];
        }

        return value + "/sessionserver/session/minecraft/profile/";
    }

    /// <summary>
    /// Clips the face and the hat layer from a skin into a single square head.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Coordinates follow the 64×64 skin layout: the head's front face occupies
    /// (8,8) to (16,16) and its overlay ("hat") (40,8) to (48,16). Older 64×32 skins
    /// have no hat layer at all, so that tile is skipped when the image is too short.
    /// </para>
    /// <para>
    /// The result is upscaled with nearest-neighbour sampling: a Minecraft head is
    /// meant to look pixelated, and smooth interpolation would blur the 8×8 face into
    /// mush at avatar sizes.
    /// </para>
    /// </remarks>
    private static async Task<byte[]?> ClipHeadAsync(byte[] skinBytes)
    {
        using var input = new InMemoryRandomAccessStream();
        await input.WriteAsync(skinBytes.AsBuffer());
        input.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(input);
        var width = (int)decoder.PixelWidth;
        var height = (int)decoder.PixelHeight;

        if (width < 16 || height < 16)
        {
            return null;
        }

        using var skin = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied);

        // 8 pixels per skin pixel, matching the 64×64 grid. Derived rather than fixed
        // so a 128×128 HD skin still crops the right region.
        var scale = Math.Max(1, width / 64);
        var face = ReadTile(skin, 8 * scale, 8 * scale, 8 * scale);

        if (height >= 32 * scale)
        {
            var hat = ReadTile(skin, 40 * scale, 8 * scale, 8 * scale);
            Composite(face, hat);
        }

        return await EncodeAsync(face);
    }

    /// <summary>Copies one 8×8 skin tile out of a bitmap.</summary>
    private static byte[] ReadTile(SoftwareBitmap skin, int x, int y, int size)
    {
        // Always normalise to 8×8 output pixels: the avatar is drawn small, and the
        // texture's own resolution must not leak into the result's size.
        const int Out = 8;
        var tile = new byte[Out * Out * 4];
        var width = (int)skin.PixelWidth;
        var height = (int)skin.PixelHeight;
        var buffer = new byte[width * height * 4];
        skin.CopyToBuffer(buffer.AsBuffer());

        var step = Math.Max(1, size / Out);

        for (var ty = 0; ty < Out; ty++)
        {
            for (var tx = 0; tx < Out; tx++)
            {
                var sx = Math.Min(x + tx * step, width - 1);
                var sy = Math.Min(y + ty * step, height - 1);
                var src = (sy * width + sx) * 4;
                var dst = (ty * Out + tx) * 4;

                tile[dst] = buffer[src];
                tile[dst + 1] = buffer[src + 1];
                tile[dst + 2] = buffer[src + 2];
                tile[dst + 3] = buffer[src + 3];
            }
        }

        return tile;
    }

    /// <summary>
    /// Draws the hat layer over the face, skipping it when fully transparent.
    /// </summary>
    private static void Composite(byte[] face, byte[] hat)
    {
        var anyVisible = false;

        for (var i = 3; i < hat.Length; i += 4)
        {
            if (hat[i] != 0)
            {
                anyVisible = true;
                break;
            }
        }

        if (!anyVisible)
        {
            return;
        }

        for (var i = 0; i < face.Length; i += 4)
        {
            var alpha = hat[i + 3] / 255f;

            if (alpha <= 0)
            {
                continue;
            }

            // Both tiles are premultiplied, so the overlay wins where it is opaque.
            face[i] = (byte)Math.Min(255, hat[i] + face[i] * (1 - alpha));
            face[i + 1] = (byte)Math.Min(255, hat[i + 1] + face[i + 1] * (1 - alpha));
            face[i + 2] = (byte)Math.Min(255, hat[i + 2] + face[i + 2] * (1 - alpha));
            face[i + 3] = (byte)Math.Min(255, hat[i + 3] + face[i + 3] * (1 - alpha));
        }
    }

    /// <summary>Upscales an 8×8 head to 64×64 and encodes it as PNG.</summary>
    private static async Task<byte[]> EncodeAsync(byte[] tile)
    {
        const int Out = 64;
        var scaled = new byte[Out * Out * 4];

        for (var y = 0; y < Out; y++)
        {
            for (var x = 0; x < Out; x++)
            {
                // Nearest-neighbour: keeping the pixel grid is the whole look.
                var sx = x * 8 / Out;
                var sy = y * 8 / Out;
                var src = (sy * 8 + sx) * 4;
                var dst = (y * Out + x) * 4;

                scaled[dst] = tile[src];
                scaled[dst + 1] = tile[src + 1];
                scaled[dst + 2] = tile[src + 2];
                scaled[dst + 3] = tile[src + 3];
            }
        }

        using var output = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);

        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            Out,
            Out,
            96,
            96,
            scaled);

        await encoder.FlushAsync();

        var bytes = new byte[output.Size];
        output.Seek(0);
        await output.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);

        return bytes;
    }
}
