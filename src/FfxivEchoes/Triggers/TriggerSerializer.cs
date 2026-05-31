using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using FfxivEchoes.Triggers.Models;
using FfxivEchoes.Utils;

namespace FfxivEchoes.Triggers;

/// <summary>
/// <see cref="TriggerFile"/> を整形済み JSON に書き出す。
/// </summary>
public static class TriggerSerializer
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize(TriggerFile file)
    {
        return JsonSerializer.Serialize(file, WriteOptions);
    }

    public static void WriteToFile(TriggerFile file, string path)
    {
        var json = Serialize(file);
        // アトミック書き込み：書き込み途中の失敗で既存トリガーファイル（119KB 級）が破損・
        // 全トリガー消失するのを防ぐ。一時ファイルへ書いてから rename で差し替える。
        AtomicFileWriter.WriteAllText(path, json, new UTF8Encoding(false));
    }
}
