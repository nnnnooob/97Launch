using System.Globalization;

namespace Launcher.App.Services;

public static class GameInputKey
{
    private static readonly Dictionary<string, int> LegacyCodes = CreateCodes();
    private static Dictionary<string, int> CreateCodes()
    {
        var codes = new Dictionary<string, int>(StringComparer.Ordinal);
        var letters = new[] { 30, 48, 46, 32, 18, 33, 34, 35, 23, 36, 37, 38, 50, 49, 24, 25, 16, 19, 31, 20, 22, 47, 17, 45, 21, 44 };
        for (var i = 0; i < 26; i++) codes["key.keyboard." + (char)('a' + i)] = letters[i];
        for (var i = 0; i < 10; i++) codes["key.keyboard." + i] = i == 0 ? 11 : i + 1;
        for (var i = 1; i <= 12; i++) codes["key.keyboard.f" + i] = i <= 10 ? 58 + i : 76 + i;
        var special = new Dictionary<string, int>
        {
            ["escape"] = 1, ["space"] = 57, ["tab"] = 15, ["enter"] = 28, ["backspace"] = 14,
            ["left.shift"] = 42, ["right.shift"] = 54, ["left.control"] = 29, ["right.control"] = 157,
            ["left.alt"] = 56, ["right.alt"] = 184, ["left.win"] = 219, ["right.win"] = 220,
            ["up"] = 200, ["down"] = 208, ["left"] = 203, ["right"] = 205, ["home"] = 199,
            ["end"] = 207, ["page.up"] = 201, ["page.down"] = 209, ["insert"] = 210, ["delete"] = 211,
            ["minus"] = 12, ["equal"] = 13, ["left.bracket"] = 26, ["right.bracket"] = 27,
            ["semicolon"] = 39, ["apostrophe"] = 40, ["grave.accent"] = 41, ["backslash"] = 43,
            ["comma"] = 51, ["period"] = 52, ["slash"] = 53, ["caps.lock"] = 58, ["num.lock"] = 69,
            ["scroll.lock"] = 70, ["print.screen"] = 183, ["pause"] = 197,
            ["keypad.add"] = 78, ["keypad.subtract"] = 74, ["keypad.multiply"] = 55,
            ["keypad.divide"] = 181, ["keypad.decimal"] = 83, ["keypad.enter"] = 156,
        };
        foreach (var pair in special) codes["key.keyboard." + pair.Key] = pair.Value;
        var keypad = new[] { 82, 79, 80, 81, 75, 76, 77, 71, 72, 73 };
        for (var i = 0; i < 10; i++) codes["key.keyboard.keypad." + i] = keypad[i];
        codes["key.mouse.left"] = -100; codes["key.mouse.right"] = -99; codes["key.mouse.middle"] = -98;
        codes["key.mouse.4"] = -97; codes["key.mouse.5"] = -96;
        return codes;
    }

    public static string? Decode(string value)
    {
        var parts = value.Trim().Split(':');
        if (parts.Skip(1).Any(p => p.Length > 0 && !p.Equals("NONE", StringComparison.OrdinalIgnoreCase))) return null;
        if (LegacyCodes.ContainsKey(parts[0])) return parts[0];
        var earlyModern = parts[0].StartsWith("key.", StringComparison.Ordinal) ? "key.keyboard." + parts[0][4..] : "";
        if (LegacyCodes.ContainsKey(earlyModern)) return earlyModern;
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var code)) return null;
        return LegacyCodes.FirstOrDefault(p => p.Value == code).Key;
    }

    public static string Encode(string key, bool modern)
        => modern ? key : LegacyCodes[key].ToString(CultureInfo.InvariantCulture);

    public static string Display(string key) => key switch
    {
        "key.keyboard.left.shift" => "左 Shift", "key.keyboard.right.shift" => "右 Shift",
        "key.keyboard.left.control" => "左 Ctrl", "key.keyboard.right.control" => "右 Ctrl",
        "key.keyboard.left.alt" => "左 Alt", "key.keyboard.right.alt" => "右 Alt",
        "key.keyboard.left.win" => "左 Win", "key.keyboard.right.win" => "右 Win",
        "key.keyboard.space" => "空格", "key.keyboard.enter" => "Enter", "key.keyboard.escape" => "Esc",
        "key.keyboard.up" => "↑", "key.keyboard.down" => "↓", "key.keyboard.left" => "←", "key.keyboard.right" => "→",
        "key.mouse.left" => "鼠标左键", "key.mouse.right" => "鼠标右键", "key.mouse.middle" => "鼠标中键",
        "key.mouse.4" => "鼠标侧键 1", "key.mouse.5" => "鼠标侧键 2",
        _ => key.Replace("key.keyboard.keypad.", "小键盘 ").Replace("key.keyboard.", "").Replace('.', ' ').ToUpperInvariant(),
    };

    public static string? FromVirtualKey(int key, uint scanCode, bool extended)
    {
        string? name = key switch
        {
            >= 65 and <= 90 => ((char)(key + 32)).ToString(), >= 48 and <= 57 => ((char)key).ToString(),
            >= 112 and <= 123 => "f" + (key - 111), >= 96 and <= 105 => "keypad." + (key - 96),
            8 => "backspace", 9 => "tab", 13 => extended ? "keypad.enter" : "enter", 27 => "escape", 32 => "space",
            16 => scanCode == 54 ? "right.shift" : "left.shift", 160 => "left.shift", 161 => "right.shift",
            17 => extended ? "right.control" : "left.control", 162 => "left.control", 163 => "right.control",
            18 => extended ? "right.alt" : "left.alt", 164 => "left.alt", 165 => "right.alt",
            91 => "left.win", 92 => "right.win", 33 => "page.up", 34 => "page.down", 35 => "end", 36 => "home",
            37 => "left", 38 => "up", 39 => "right", 40 => "down", 45 => "insert", 46 => "delete",
            20 => "caps.lock", 144 => "num.lock", 145 => "scroll.lock", 44 => "print.screen", 19 => "pause",
            106 => "keypad.multiply", 107 => "keypad.add", 109 => "keypad.subtract", 110 => "keypad.decimal", 111 => "keypad.divide",
            186 => "semicolon", 187 => "equal", 188 => "comma", 189 => "minus", 190 => "period", 191 => "slash",
            192 => "grave.accent", 219 => "left.bracket", 220 => "backslash", 221 => "right.bracket", 222 => "apostrophe",
            _ => null,
        };
        return name is null ? null : "key.keyboard." + name;
    }
}
