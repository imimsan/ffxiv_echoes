using System.Collections.Generic;
using FfxivEchoes.Triggers.Models;

namespace FfxivEchoes.Triggers;

/// <summary>
/// 1 ファイルぶんの読み込み結果。成功時は <see cref="File"/> が非 null。
/// 失敗時は <see cref="Error"/> にメッセージが入り <see cref="File"/> は null。
/// </summary>
public sealed class TriggerLoadResult
{
    public string FilePath { get; }
    public TriggerFile? File { get; }
    public string? Error { get; }
    public IReadOnlyList<string> Warnings { get; }

    public bool IsSuccess => File is not null;

    public TriggerLoadResult(string filePath, TriggerFile? file, string? error, IReadOnlyList<string>? warnings = null)
    {
        FilePath = filePath;
        File = file;
        Error = error;
        Warnings = warnings ?? new List<string>();
    }

    public static TriggerLoadResult Success(string filePath, TriggerFile file, IReadOnlyList<string>? warnings = null) =>
        new(filePath, file, null, warnings);

    public static TriggerLoadResult Failure(string filePath, string error) =>
        new(filePath, null, error);
}
