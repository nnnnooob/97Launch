using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Serilog.Core;
using Serilog.Events;

namespace Launcher.App.Services;

/// <summary>
/// In-app log lines shown in the shell's log pane.
/// </summary>
/// <remarks>
/// The same Serilog events feed the rolling file and this bounded, UI-thread
/// collection. Append also goes through Serilog so activity lines reach both.
/// </remarks>
public sealed class LogBuffer : ILogEventSink
{
    public static LogBuffer Shared { get; } = new();
    private readonly DispatcherQueue? _dispatcher = DispatcherQueue.GetForCurrentThread();
    private readonly Serilog.Formatting.Display.MessageTemplateTextFormatter _messageFormatter = new(
        "{Message:lj}", System.Globalization.CultureInfo.InvariantCulture);
    private LogEntry? _previous;
    /// <summary>
    /// Upper bound on retained lines. The launcher can log heavily during a
    /// download; without a cap the collection grows for the life of the process.
    /// </summary>
    private const int MaxLines = 2000;

    private readonly object _sync = new();

    /// <summary>Observable so the pane's ListView updates as lines arrive.</summary>
    public ObservableCollection<LogEntry> Entries { get; } = new();

    public void Append(string message, LogEntryKind kind = LogEntryKind.Info)
    {
        Serilog.Log.Write(kind switch
        {
            LogEntryKind.Warning => LogEventLevel.Warning,
            LogEntryKind.Error => LogEventLevel.Error,
            _ => LogEventLevel.Information
        }, "{ActivityMessage:l}", message);
    }

    public void Emit(LogEvent logEvent)
    {
        if (logEvent.Level < LogEventLevel.Information) return;
        if (logEvent.Properties.TryGetValue("SourceContext", out var source)
            && source is ScalarValue { Value: string name }
            && (name.StartsWith("Microsoft.", StringComparison.Ordinal) || name.StartsWith("System.", StringComparison.Ordinal))) return;
        using var rendered = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        _messageFormatter.Format(logEvent, rendered);
        var message = rendered.ToString();
        if (logEvent.Exception is { } error) message += $" · {error.GetType().Name}：{error.Message}";
        message = LogPrivacy.Sanitize(message).Replace('\r', ' ').Replace('\n', ' ');
        if (message.Length > 1500) message = message[..1500] + "…（完整信息见日志文件）";
        var kind = logEvent.Level >= LogEventLevel.Error ? LogEntryKind.Error
            : logEvent.Level == LogEventLevel.Warning ? LogEntryKind.Warning : LogEntryKind.Info;
        var entry = new LogEntry(logEvent.Timestamp.LocalDateTime, kind, message);
        if (_dispatcher is { HasThreadAccess: false }) _dispatcher.TryEnqueue(() => Add(entry));
        else Add(entry);
    }

    private void Add(LogEntry entry)
    {
        lock (_sync)
        {
            if (_previous is { } previous && previous.Message == entry.Message && previous.Kind == entry.Kind
                && entry.Timestamp - previous.Timestamp < TimeSpan.FromSeconds(2)) return;
            _previous = entry;
            Entries.Add(entry);

            // Trim in batches rather than per-add: removing the first item on
            // every append would generate a collection change per keystroke of
            // output once the buffer is full.
            if (Entries.Count > MaxLines + 200)
            {
                for (var i = 0; i < 200; i++)
                {
                    Entries.RemoveAt(0);
                }
            }
        }
    }
}

/// <summary>Severity of an in-app log line, used for colouring.</summary>
public enum LogEntryKind
{
    Info,
    Warning,
    Error,
}

/// <summary>One line in the activity log.</summary>
public sealed record LogEntry(DateTime Timestamp, LogEntryKind Kind, string Message)
{
    public string TimeDisplay => Timestamp.ToString("HH:mm:ss");

    public string KindDisplay => Kind switch
    {
        LogEntryKind.Warning => "WARN",
        LogEntryKind.Error => "ERR ",
        _ => "INFO",
    };
}
