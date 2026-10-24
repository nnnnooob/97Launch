using System.Text.RegularExpressions;

namespace Launcher.Core.Instances;

public static class ModDiagnostics
{
    public static bool IsBuiltin(string id) => id is "minecraft" or "java" or "fabricloader" or "quilt_loader" or "forge" or "neoforge";

    public static IReadOnlyDictionary<string, string[]> Inspect(IReadOnlyList<LocalContent> files, string game, string loader)
    {
        var enabled = files.Where(f => f.Enabled).ToArray();
        var ids = enabled.SelectMany(f => (f.ProvidedMods ?? []).Append(f.ModId ?? "").Where(id => id.Length > 0)
            .Select(id => (Id: id, File: f))).GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(p => p.File).DistinctBy(f => f.Path).ToArray(), StringComparer.OrdinalIgnoreCase);
        return files.ToDictionary(f => f.Path, f =>
        {
            var issues = new List<string>();
            if (f.Error is not null) issues.Add(f.Error);
            if (!f.Enabled) return issues.ToArray();
            if (f.ModId is { Length: > 0 } id && ids.TryGetValue(id, out var duplicates) && duplicates.Length > 1)
                issues.Add("重复 Mod：" + string.Join("、", duplicates.Select(d => d.FileName)));
            if (f.ModLoaders is { Count: > 0 } loaders && !loaders.Contains(loader, StringComparer.OrdinalIgnoreCase))
                issues.Add("加载器不匹配，需要 " + string.Join(" / ", loaders));
            if (f.GameRequirements is { Count: > 0 } requirements && requirements.All(r => Matches(game, r) == false))
                issues.Add("游戏版本不匹配，需要 " + string.Join(" 或 ", requirements));
            foreach (var dependency in f.RequiredMods ?? [])
            {
                if (IsBuiltin(dependency)) continue;
                if (!ids.TryGetValue(dependency, out var installed))
                    issues.Add((files.Any(other => !other.Enabled && other.ModId == dependency) ? "前置已禁用：" : "缺少前置：") + dependency);
                else if (f.ModRequirements?.TryGetValue(dependency, out var range) == true
                    && installed.All(other => other.ModId == dependency)
                    && installed.All(other => Matches(other.Version, range) == false)) issues.Add($"前置版本不匹配：{dependency} 需要 {range}");
            }
            return issues.Distinct().ToArray();
        }, StringComparer.OrdinalIgnoreCase);
    }

    // Unknown syntax stays unknown rather than declaring a compatible Mod broken.
    public static bool? Matches(string version, string requirement)
    {
        if (string.IsNullOrWhiteSpace(requirement) || requirement == "*") return true;
        var alternatives = requirement.Split("||", StringSplitOptions.TrimEntries);
        if (alternatives.Length > 1)
        {
            var matches = alternatives.Select(r => Matches(version, r)).ToArray();
            return matches.Contains(true) ? true : matches.Contains(null) ? null : false;
        }
        if (!TryVersion(version, out var actual)) return version == requirement ? true : null;
        var interval = Regex.Match(requirement, @"^(\[|\()([^,]*),([^,\]\)]*)(\]|\))$");
        if (interval.Success)
        {
            var low = interval.Groups[2].Value.Trim(); var high = interval.Groups[3].Value.Trim();
            if (low.Length > 0 && (!TryVersion(low, out var lower) || actual < lower || actual == lower && interval.Groups[1].Value == "(")) return TryVersion(low, out _) ? false : null;
            if (high.Length > 0 && (!TryVersion(high, out var upper) || actual > upper || actual == upper && interval.Groups[4].Value == ")")) return TryVersion(high, out _) ? false : null;
            return true;
        }
        var parts = requirement.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool unknown = false;
        foreach (var part in parts)
        {
            var match = Regex.Match(part, @"^(>=|<=|>|<|=|\^|~)?(\d+(?:\.\d+)*(?:\.[xX*])?)$");
            if (!match.Success) { unknown = true; continue; }
            var target = match.Groups[2].Value;
            if (target.EndsWith(".*") || target.EndsWith(".x", StringComparison.OrdinalIgnoreCase))
            {
                if (!version.StartsWith(target[..^1], StringComparison.OrdinalIgnoreCase)) return false;
                continue;
            }
            if (!TryVersion(target, out var expected)) { unknown = true; continue; }
            var valid = match.Groups[1].Value switch
            {
                ">=" => actual >= expected, "<=" => actual <= expected, ">" => actual > expected, "<" => actual < expected,
                "~" => actual >= expected && actual < new Version(expected.Major, expected.Minor + 1, 0),
                "^" => actual >= expected && actual < (expected.Major > 0 ? new Version(expected.Major + 1, 0, 0)
                    : expected.Minor > 0 ? new Version(0, expected.Minor + 1, 0) : new Version(0, 0, expected.Build + 1)),
                _ => actual == expected,
            };
            if (!valid) return false;
        }
        return unknown ? null : true;
    }

    private static bool TryVersion(string text, out Version version)
    {
        var match = Regex.Match(text.Trim(), @"^\d+(?:\.\d+){0,3}$");
        var parts = match.Success ? text.Trim().Split('.') : [];
        return Version.TryParse(parts.Length == 0 ? "" : string.Join('.', parts.Concat(Enumerable.Repeat("0", Math.Max(0, 3 - parts.Length)))), out version!);
    }
}
