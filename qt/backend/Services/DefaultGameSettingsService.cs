using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Launcher.App.ViewModels;

namespace Launcher.App.Services;

public sealed record DefaultGameSettings(bool Enabled = false, double FieldOfView = 70, double Brightness = 50,
    double RenderDistance = 12, double MouseSensitivity = 100, string SprintKey = "key.keyboard.left.control",
    string SneakKey = "key.keyboard.left.shift", string PerspectiveKey = "key.keyboard.f5")
{
    public DefaultGameSettings Normalize() => this with
    {
        FieldOfView = Clamp(FieldOfView, 30, 110, 70), Brightness = Clamp(Brightness, 0, 100, 50),
        RenderDistance = Clamp(RenderDistance, 2, 32, 12), MouseSensitivity = Clamp(MouseSensitivity, 0, 200, 100),
        SprintKey = GameInputKey.Decode(SprintKey ?? "") ?? "key.keyboard.left.control",
        SneakKey = GameInputKey.Decode(SneakKey ?? "") ?? "key.keyboard.left.shift",
        PerspectiveKey = GameInputKey.Decode(PerspectiveKey ?? "") ?? "key.keyboard.f5",
    };
    private static double Clamp(double value, double min, double max, double fallback)
        => Math.Round(Math.Clamp(double.IsFinite(value) ? value : fallback, min, max));
}

public static class DefaultGameSettingsService
{
    // ponytail: one gate for these small configuration writes; split by game directory if launches become parallel.
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static string EligibilityPath(string instance) => Path.Combine(instance, "97Launch", "GameDefaultsEligibility.json");
    private static string AppliedPath(string directory) => Path.Combine(directory, "97Launch", "GameDefaultsApplied.json");

    public static void RecordNewInstance(string instance)
    {
        var path = EligibilityPath(instance);
        if (!File.Exists(path)) AtomicWrite(path, JsonSerializer.Serialize(new { State = "pending", InstalledAt = DateTimeOffset.Now }));
    }

    public static string OptionsSource(string directory)
    {
        var actual = Path.Combine(directory, "options.txt");
        if (File.Exists(actual)) return actual;
        var defaults = Path.Combine(directory, "config", "defaultoptions");
        if (File.Exists(Path.Combine(defaults, "options.txt")) || File.Exists(Path.Combine(defaults, "keybindings.txt")))
            return Path.Combine(defaults, "options.txt");
        var yosbr = Path.Combine(directory, "config", "yosbr", "options.txt");
        return File.Exists(yosbr) ? yosbr : actual;
    }

    public static bool UsesModernKeys(string version)
    {
        var stable = Regex.Match(version, @"^(\d+)\.(\d+)");
        if (stable.Success) return int.Parse(stable.Groups[1].Value) > 1 || int.Parse(stable.Groups[2].Value) >= 13;
        var snapshot = Regex.Match(version, @"^(\d{2})w(\d{2})", RegexOptions.IgnoreCase);
        return snapshot.Success && (int.Parse(snapshot.Groups[1].Value) > 17
            || int.Parse(snapshot.Groups[1].Value) == 17 && int.Parse(snapshot.Groups[2].Value) >= 43);
    }

    private static bool SupportsPresets(string version)
    {
        var stable = Regex.Match(version, @"^(\d+)\.(\d+)(?:\.(\d+))?");
        if (stable.Success) return int.Parse(stable.Groups[1].Value) > 1 || int.Parse(stable.Groups[2].Value) > 7
            || int.Parse(stable.Groups[2].Value) == 7 && int.TryParse(stable.Groups[3].Value, out var patch) && patch >= 2;
        var snapshot = Regex.Match(version, @"^(\d{2})w(\d{2})", RegexOptions.IgnoreCase);
        return snapshot.Success && (int.Parse(snapshot.Groups[1].Value) > 13
            || int.Parse(snapshot.Groups[1].Value) == 13 && int.Parse(snapshot.Groups[2].Value) >= 36);
    }

    public static async Task ApplyAsync(InstanceItem instance, string directory, DefaultGameSettings preset, CancellationToken token)
    {
        await Gate.WaitAsync(token);
        try
        {
            await Task.Run(() => Apply(instance, directory, preset.Normalize(), token), token);
        }
        finally { Gate.Release(); }
    }

    private static void Apply(InstanceItem instance, string directory, DefaultGameSettings preset, CancellationToken token)
    {
        var eligibility = EligibilityPath(instance.Path);
        if (!File.Exists(eligibility)) return;
        using (var record = JsonDocument.Parse(File.ReadAllText(eligibility)))
            if (record.RootElement.GetProperty("State").GetString() != "pending") return;
        var applied = AppliedPath(directory);
        var shared = !GameFolderService.PathsEqual(directory, instance.Path);
        var reason = !preset.Enabled ? "disabled" : !SupportsPresets(instance.MinecraftVersionId) ? "unsupported_game_version"
            : File.Exists(applied) ? "directory_already_processed"
            : InstanceUsageStore.Read(instance.Path).LaunchCount > 0 ? "already_played"
            : shared && File.Exists(Path.Combine(directory, "options.txt")) ? "existing_shared_settings" : null;
        var writes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var state = JsonSerializer.Serialize(new { State = reason is null ? "applied" : "skipped", Reason = reason, At = DateTimeOffset.Now });
        if (reason is not null)
        {
            if (!File.Exists(applied)) writes[applied] = state;
            writes[eligibility] = state;
            WriteTransaction(directory, writes, token);
            Serilog.Log.Information("默认游戏设置未应用：{Instance}，原因 {Reason}", instance.Name, reason);
            return;
        }

        var source = OptionsSource(directory);
        var text = ReadText(source);
        var modern = UsesModernKeys(instance.MinecraftVersionId);
        string Number(double value) => value.ToString("0.###############", CultureInfo.InvariantCulture);
        var fields = new Dictionary<string, string>
        {
            ["fov"] = Number((preset.FieldOfView - 70) / 40), ["gamma"] = Number(preset.Brightness / 100),
            ["renderDistance"] = Number(instance.MinecraftVersionId.StartsWith("1.7.", StringComparison.Ordinal)
                ? Math.Min(16, preset.RenderDistance) : preset.RenderDistance), ["mouseSensitivity"] = Number(preset.MouseSensitivity / 200),
        };
        var existingFields = Fields(text);
        var dataVersion = existingFields.TryGetValue("version", out var storedVersion) && int.TryParse(storedVersion, out var parsedVersion) ? parsedVersion : 0;
        if (text.Length == 0 && modern)
        {
            dataVersion = ClientDataVersion(instance);
            fields["version"] = dataVersion.ToString(CultureInfo.InvariantCulture);
        }
        var keys = new Dictionary<string, string>
        {
            ["key_key.sprint"] = GameInputKey.Encode(preset.SprintKey, modern),
            ["key_key.sneak"] = GameInputKey.Encode(preset.SneakKey, modern),
            ["key_key.togglePerspective"] = GameInputKey.Encode(preset.PerspectiveKey, modern),
        };
        var defaultsDirectory = Path.Combine(directory, "config", "defaultoptions");
        var defaultKeys = Path.Combine(defaultsDirectory, "keybindings.txt");
        var usingDefaultOptions = GameFolderService.PathsEqual(source, Path.Combine(defaultsDirectory, "options.txt"));
        if (!usingDefaultOptions)
        {
            // Vanilla migrates numeric bindings below data version 1344, then prefixes keyboard names at 1446.
            foreach (var key in keys)
                fields[key.Key] = !modern || dataVersion < 1344 ? GameInputKey.Encode(GameInputKey.Decode(key.Value)!, false)
                    : dataVersion < 1446 ? key.Value.Replace("key.keyboard.", "key.", StringComparison.Ordinal) : key.Value;
        }
        writes[source] = Merge(text, fields);
        if (usingDefaultOptions || File.Exists(defaultKeys))
        {
            // Match both older Forge modifiers and newer loaders; a plain key uses no modifier.
            var keyText = ReadText(defaultKeys);
            var suffix = !modern || keyText.Contains(":NONE", StringComparison.OrdinalIgnoreCase) ? ":NONE" : "";
            writes[defaultKeys] = Merge(keyText, keys.ToDictionary(k => k.Key, k => k.Value + suffix));
        }
        writes[applied] = state;
        writes[eligibility] = state;
        WriteTransaction(directory, writes, token);
        Serilog.Log.Information("已首次应用默认游戏设置：{Instance}，配置来源 {Source}", instance.Name, source);
    }

    public static (DefaultGameSettings Settings, int Count) Read(string directory, DefaultGameSettings fallback)
    {
        var source = OptionsSource(directory);
        var fields = Fields(ReadText(source));
        var keyPath = Path.Combine(directory, "config", "defaultoptions", "keybindings.txt");
        foreach (var pair in Fields(ReadText(keyPath))) fields.TryAdd(pair.Key, pair.Value);
        var count = 0;
        double Get(string name, double previous, Func<double, double> convert)
        {
            if (!fields.TryGetValue(name, out var value) || !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                || !double.IsFinite(number)) return previous;
            count++; return convert(number);
        }
        string Key(string name, string previous)
        {
            if (!fields.TryGetValue(name, out var value) || GameInputKey.Decode(value) is not { } key) return previous;
            count++; return key;
        }
        var result = fallback with
        {
            FieldOfView = Get("fov", fallback.FieldOfView, value => value * 40 + 70),
            Brightness = Get("gamma", fallback.Brightness, value => value * 100),
            RenderDistance = Get("renderDistance", fallback.RenderDistance, value => value),
            MouseSensitivity = Get("mouseSensitivity", fallback.MouseSensitivity, value => value * 200),
            SprintKey = Key("key_key.sprint", fallback.SprintKey), SneakKey = Key("key_key.sneak", fallback.SneakKey),
            PerspectiveKey = Key("key_key.togglePerspective", fallback.PerspectiveKey),
        };
        return (result.Normalize(), count);
    }

    private static Dictionary<string, string> Fields(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = line.IndexOf(':');
            if (colon > 0) result[line[..colon]] = line[(colon + 1)..];
        }
        return result;
    }

    private static int ClientDataVersion(InstanceItem instance)
    {
        var root = Path.GetDirectoryName(Path.GetDirectoryName(instance.Path))!;
        var jars = new[] { Path.Combine(instance.Path, Path.GetFileName(instance.Path) + ".jar"),
            Path.Combine(root, "versions", instance.MinecraftVersionId, instance.MinecraftVersionId + ".jar") };
        foreach (var jar in jars.Where(File.Exists))
        {
            using var archive = ZipFile.OpenRead(jar);
            var entry = archive.GetEntry("version.json");
            if (entry is null || entry.Length > 1024 * 1024) continue;
            using var stream = entry.Open();
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.TryGetProperty("world_version", out var version) && version.TryGetInt32(out var value)) return value;
        }
        return 1519; // 1.13 release: modern key syntax, for clients predating embedded version.json.
    }

    private static string ReadText(string path)
    {
        if (!File.Exists(path)) return "";
        if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new IOException("游戏设置文件过大：" + path);
        return File.ReadAllText(path, Utf8);
    }

    public static string Merge(string text, IReadOnlyDictionary<string, string> fields)
    {
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        foreach (var pair in fields)
        {
            var expression = new Regex("(?m)^" + Regex.Escape(pair.Key) + @":[^\r\n]*");
            if (expression.IsMatch(text)) text = expression.Replace(text, _ => pair.Key + ":" + pair.Value);
            else text += (text.Length > 0 && !text.EndsWith('\n') ? newline : "") + pair.Key + ":" + pair.Value + newline;
        }
        return text;
    }

    private static void WriteTransaction(string directory, IReadOnlyDictionary<string, string> writes, CancellationToken token)
    {
        var originals = new Dictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase);
        var backup = Path.Combine(directory, "97Launch", "backups", "game-defaults-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
        foreach (var pair in writes)
        {
            InstanceContentService.ValidatePath(directory, pair.Key);
            originals[pair.Key] = File.Exists(pair.Key) ? File.ReadAllBytes(pair.Key) : null;
            if (originals[pair.Key] is { } bytes)
            {
                var copy = Path.Combine(backup, Path.GetRelativePath(directory, pair.Key));
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                File.WriteAllBytes(copy, bytes);
            }
        }
        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var pair in writes)
            {
                token.ThrowIfCancellationRequested();
                AtomicWrite(pair.Key, pair.Value);
                changed.Add(pair.Key);
            }
        }
        catch
        {
            foreach (var pair in originals.Where(p => changed.Contains(p.Key)))
                try { if (pair.Value is null) File.Delete(pair.Key); else File.WriteAllBytes(pair.Key, pair.Value); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { Serilog.Log.Error(ex, "恢复默认游戏设置备份失败：{Path}，备份位于 {Backup}", pair.Key, backup); }
            throw;
        }
    }

    private static void AtomicWrite(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".97launch-" + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, text, Utf8); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
