using System;
using Dalamud.Plugin.Services;
using Serilog;
using Serilog.Events;

namespace FfxivEchoes.Replay.MockServices;

/// <summary>
/// stdout に書き出すだけの <see cref="IPluginLog"/> 実装。
/// </summary>
/// <remarks>
/// Dalamud の IPluginLog は Serilog ベースの messageTemplate API を提供する。
/// テンプレ展開は最低限の手抜き実装で済ませる（{Name} を順番に value で置換）。
/// </remarks>
public sealed class MockPluginLog : IPluginLog
{
    private readonly LogLevel _minLevel;

    public MockPluginLog(LogLevel minLevel = LogLevel.Information)
    {
        _minLevel = minLevel;
    }

    /// <inheritdoc />
    public LogEventLevel MinimumLogLevel { get; set; } = LogEventLevel.Information;

    /// <inheritdoc />
    public ILogger Logger { get; } = new LoggerConfiguration().CreateLogger();

    public enum LogLevel
    {
        Verbose = 0,
        Debug = 1,
        Information = 2,
        Warning = 3,
        Error = 4,
        Fatal = 5,
    }

    private void Write(LogLevel level, string tag, string messageTemplate, object?[] values, Exception? ex = null)
    {
        if (level < _minLevel) return;
        var formatted = TemplateFormatter.Format(messageTemplate, values);
        var line = $"[{level.ToString().ToUpperInvariant()[0]}] {tag} {formatted}";
        if (ex is not null)
        {
            line += $" :: {ex.GetType().Name}: {ex.Message}";
        }
        Console.WriteLine(line);
    }

    // ── IPluginLog の全 overloaded メソッド群 ──────────────────────────

    public void Fatal(string messageTemplate, params object[] values)
        => Write(LogLevel.Fatal, "", messageTemplate, values);
    public void Fatal(Exception? exception, string messageTemplate, params object[] values)
        => Write(LogLevel.Fatal, "", messageTemplate, values, exception);

    public void Error(string messageTemplate, params object[] values)
        => Write(LogLevel.Error, "", messageTemplate, values);
    public void Error(Exception? exception, string messageTemplate, params object[] values)
        => Write(LogLevel.Error, "", messageTemplate, values, exception);

    public void Warning(string messageTemplate, params object[] values)
        => Write(LogLevel.Warning, "", messageTemplate, values);
    public void Warning(Exception? exception, string messageTemplate, params object[] values)
        => Write(LogLevel.Warning, "", messageTemplate, values, exception);

    public void Information(string messageTemplate, params object[] values)
        => Write(LogLevel.Information, "", messageTemplate, values);
    public void Information(Exception? exception, string messageTemplate, params object[] values)
        => Write(LogLevel.Information, "", messageTemplate, values, exception);

    public void Info(string messageTemplate, params object[] values)
        => Information(messageTemplate, values);
    public void Info(Exception? exception, string messageTemplate, params object[] values)
        => Information(exception, messageTemplate, values);

    public void Debug(string messageTemplate, params object[] values)
        => Write(LogLevel.Debug, "", messageTemplate, values);
    public void Debug(Exception? exception, string messageTemplate, params object[] values)
        => Write(LogLevel.Debug, "", messageTemplate, values, exception);

    public void Verbose(string messageTemplate, params object[] values)
        => Write(LogLevel.Verbose, "", messageTemplate, values);
    public void Verbose(Exception? exception, string messageTemplate, params object[] values)
        => Write(LogLevel.Verbose, "", messageTemplate, values, exception);

    public void Write(LogEventLevel level, Exception? exception, string messageTemplate, params object[] values)
    {
        var mapped = level switch
        {
            LogEventLevel.Verbose => LogLevel.Verbose,
            LogEventLevel.Debug => LogLevel.Debug,
            LogEventLevel.Information => LogLevel.Information,
            LogEventLevel.Warning => LogLevel.Warning,
            LogEventLevel.Error => LogLevel.Error,
            LogEventLevel.Fatal => LogLevel.Fatal,
            _ => LogLevel.Information,
        };
        Write(mapped, "", messageTemplate, values, exception);
    }
}

/// <summary>
/// {Name}, {0}, {@Name} を順に value で置き換える最小実装。
/// 桁数指定 / format string は無視する。harness のログ可読性目的のみ。
/// </summary>
internal static class TemplateFormatter
{
    public static string Format(string template, object?[] values)
    {
        if (string.IsNullOrEmpty(template) || values is null || values.Length == 0)
        {
            return template ?? string.Empty;
        }

        var sb = new System.Text.StringBuilder(template.Length + values.Length * 8);
        var valueIdx = 0;
        var i = 0;
        while (i < template.Length)
        {
            var c = template[i];
            if (c == '{' && i + 1 < template.Length && template[i + 1] != '{')
            {
                var end = template.IndexOf('}', i + 1);
                if (end > i)
                {
                    if (valueIdx < values.Length)
                    {
                        sb.Append(values[valueIdx] ?? "(null)");
                        valueIdx++;
                    }
                    else
                    {
                        sb.Append(template, i, end - i + 1);
                    }
                    i = end + 1;
                    continue;
                }
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }
}
