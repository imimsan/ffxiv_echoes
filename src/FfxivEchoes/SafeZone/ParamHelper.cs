using System.Collections.Generic;
using System.Text.Json;

namespace FfxivEchoes.SafeZone;

internal static class ParamHelper
{
    public static double? GetDouble(IReadOnlyDictionary<string, JsonElement>? p, string key)
    {
        if (p is null || !p.TryGetValue(key, out var v))
        {
            return null;
        }
        return v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
    }

    public static float? GetFloat(IReadOnlyDictionary<string, JsonElement>? p, string key)
    {
        var d = GetDouble(p, key);
        return d is null ? null : (float)d.Value;
    }

    public static string? GetString(IReadOnlyDictionary<string, JsonElement>? p, string key)
    {
        if (p is null || !p.TryGetValue(key, out var v))
        {
            return null;
        }
        return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    public static int? GetInt(IReadOnlyDictionary<string, JsonElement>? p, string key)
    {
        if (p is null || !p.TryGetValue(key, out var v))
        {
            return null;
        }
        return v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
    }

    public static bool? GetBool(IReadOnlyDictionary<string, JsonElement>? p, string key)
    {
        if (p is null || !p.TryGetValue(key, out var v))
        {
            return null;
        }
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }
}
