using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using FfxivEchoes.Triggers.Models;

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
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        File.WriteAllText(path, json, new UTF8Encoding(false));
    }
}
