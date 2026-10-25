using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Launcher.App.Services;

public sealed record SignedUpdateManifest(string Payload, string Signature, string KeyId);
public sealed record UpdateArtifact(int Format, string App, string Version, int Build, string Channel,
    string Architecture, long Size, string Sha256, long IssuedAt);
public sealed record UpdateRelease(string Id, string Version, int Build, string Channel, string Architecture,
    long Size, string Notes, long? PublishedAt, SignedUpdateManifest Manifest, string DownloadUrl);
public sealed record UpdateCheck(bool Available, UpdateRelease? Release);

internal static class UpdateTrust
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true };
    internal static string Root => Path.Combine(AppInfo.DataDirectory, "updates");
    internal static string Architecture => RuntimeInformation.ProcessArchitecture switch
        { System.Runtime.InteropServices.Architecture.X64 => "x64", System.Runtime.InteropServices.Architecture.Arm64 => "arm64", _ => "unsupported" };

    internal static UpdateArtifact Verify(UpdateRelease release)
    {
        if (!Guid.TryParseExact(release.Id, "D", out _) || release.Manifest.Payload.Length > 12000 || release.Manifest.Signature.Length > 1200)
            throw new InvalidDataException("更新清单格式无效。");
        using var pem = typeof(UpdateTrust).Assembly.GetManifestResourceStream("Launcher.UpdateSigningRoot.pem")
            ?? throw new InvalidDataException("缺少更新签名公钥。");
        using var reader = new StreamReader(pem);
        using var rsa = RSA.Create();
        rsa.ImportFromPem(reader.ReadToEnd());
        var keyId = Convert.ToHexString(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo())).ToLowerInvariant()[..16];
        var payload = Convert.FromBase64String(release.Manifest.Payload);
        if (payload.Length > 8192 || release.Manifest.KeyId != keyId ||
            !rsa.VerifyData(payload, Convert.FromBase64String(release.Manifest.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            throw new InvalidDataException("更新数字签名校验失败。");
        var artifact = JsonSerializer.Deserialize<UpdateArtifact>(payload, Json) ?? throw new InvalidDataException("更新清单为空。");
        if (artifact.Format != 1 || artifact.App != AppInfo.Name || artifact.Version != release.Version ||
            artifact.Build != release.Build || artifact.Architecture != release.Architecture || artifact.Channel != release.Channel ||
            artifact.Size != release.Size || artifact.Architecture != Architecture || artifact.Build is < 1 or > 65535 ||
            artifact.Size is < 65536 or > 536870912 || artifact.Channel is not ("preview" or "stable") ||
            artifact.Sha256.Length != 64 || artifact.Sha256.Any(c => !char.IsAsciiHexDigit(c)) ||
            artifact.IssuedAt > DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds() ||
            release.DownloadUrl != $"/api/v1/updates/{release.Id}/download")
            throw new InvalidDataException("更新版本、大小或目标架构与签名不符。");
        return artifact;
    }

    internal static string FilePath(UpdateRelease release) => Path.Combine(Root, Verify(release).Sha256 + ".exe");

    internal static async Task VerifyFileAsync(UpdateRelease release, string path, CancellationToken token = default)
    {
        var artifact = Verify(release);
        if (new FileInfo(path).Length != artifact.Size) throw new InvalidDataException("更新文件大小校验失败。");
        await using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).ToLowerInvariant();
        if (!hash.Equals(artifact.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新文件哈希校验失败，请重新下载。");
        stream.Position = 0x3c;
        using var binary = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        var pe = binary.ReadUInt32();
        if (pe > stream.Length - 6) throw new InvalidDataException("更新程序头无效。");
        stream.Position = pe;
        if (binary.ReadUInt32() != 0x00004550 || binary.ReadUInt16() != (Architecture == "x64" ? 0x8664 : 0xaa64))
            throw new InvalidDataException("更新文件架构校验失败。");
        var version = FileVersionInfo.GetVersionInfo(path);
        if(version.ProductName != "97Launch Qt") throw new InvalidDataException("此更新包不适用于 Qt 版，请等待发布对应的 Qt 更新。");
        if (version.FilePrivatePart != artifact.Build || version.ProductVersion?.Split('+')[0] != artifact.Version)
            throw new InvalidDataException("更新 EXE 的真实版本与发布清单不一致。");
    }

    internal static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(value, Json)); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
