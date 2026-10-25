namespace Launcher.Core.Downloads;

public static class ResourceFileName
{
    public static IReadOnlyList<string> Examples { get; } =
    ["【机械动力】create-1.21.1-6.0.4.jar", "[机械动力] create-1.21.1-6.0.4.jar",
     "机械动力-create-1.21.1-6.0.4.jar", "create-1.21.1-6.0.4-机械动力.jar", "create-1.21.1-6.0.4.jar"];
    public const string Default = "create-1.21.1-6.0.4.jar";

    public static string Format(string projectName, string original, string format)
    {
        original = Path.GetFileName(original);
        var extension = Path.GetExtension(original);
        var stem = Path.GetFileNameWithoutExtension(original);
        var name = string.Concat(projectName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');
        if (name.Length == 0) name = "resource";
        var index = Examples.ToList().IndexOf(format);
        var result = index switch
        {
            0 => $"【{name}】{stem}", 1 => $"[{name}] {stem}", 2 => $"{name}-{stem}", 3 => $"{stem}-{name}", _ => stem,
        };
        return result[..Math.Min(result.Length, Math.Max(1, 220 - extension.Length))] + extension;
    }
}
