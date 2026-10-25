using System.Globalization;
using System.Text.RegularExpressions;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Display;

namespace Launcher.App.Services;

internal static class LogPrivacy
{
    private static readonly Regex Credentials = new(
        """(?i)(["']?(?:access[_-]?token|refresh[_-]?token|client[_-]?token|device[_-]?token|device[_-]?code|user[_-]?code|download[_-]?lease|identityToken|token|RpsTicket|password|passwd|api[_-]?key|bot[_-]?key|admin[_-]?key|authorization|secret)["']?\s*[:=]\s*)(?:"[^"]*"|'[^']*'|[^\s,;&}\]]+)""",
        RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Tokens = new(
        @"\b97[pd]_[A-Za-z0-9_-]+\b|\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+|(?i:Bearer\s+)[^\s""'<>;,]+",
        RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
    private static readonly Regex UrlSecrets = new(
        @"(?i)([?&](?:code|key|token|signature|sig)=)[^\s&#""']+|(?<=://)[^/\s:@]+:[^/@\s]+@",
        RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    public static string Sanitize(string text)
    {
        if (text.Length > 262144) text = text[..262144] + "…[日志已截断]";
        try
        {
            text = Tokens.Replace(text, "<已隐藏令牌>");
            text = Credentials.Replace(text, "$1[已隐藏]");
            return UrlSecrets.Replace(text, m => m.Groups[1].Success ? m.Groups[1].Value + "[已隐藏]" : "[已隐藏凭据]@");
        }
        catch (RegexMatchTimeoutException) { return "[日志含有无法安全处理的内容，已隐藏]"; }
    }
}

internal sealed class SafeLogFormatter : ITextFormatter
{
    private readonly MessageTemplateTextFormatter _formatter = new(
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}", CultureInfo.InvariantCulture);

    public void Format(LogEvent logEvent, TextWriter output)
    {
        using var buffer = new StringWriter(CultureInfo.InvariantCulture);
        _formatter.Format(logEvent, buffer);
        output.Write(LogPrivacy.Sanitize(buffer.ToString()));
    }
}
