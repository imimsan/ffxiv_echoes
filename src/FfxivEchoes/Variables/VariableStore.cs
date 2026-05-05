using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.Json;
using Dalamud.Plugin.Services;
using FfxivEchoes.Events;

namespace FfxivEchoes.Variables;

/// <summary>
/// トリガー間で共有される状態変数（SPEC.md §4.5 / §3.2）。
/// </summary>
/// <remarks>
/// 戦闘終了時（CombatEndedEvent）に自動リセット。永続化オプションは未対応。
/// 値は数値（double）／文字列／真偽／ベクトル（stored_position 用）の混在を許容。
/// </remarks>
public sealed class VariableStore : IDisposable
{
    private readonly Dictionary<string, object?> _variables = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly IDisposable _combatEndSub;
    private readonly IPluginLog _log;

    public VariableStore(IEventBus bus, IPluginLog log)
    {
        _log = log;
        _combatEndSub = bus.Subscribe<CombatEndedEvent>(_ => ResetAll());
    }

    public void Dispose() => _combatEndSub.Dispose();

    public void Set(string name, object? value)
    {
        lock (_gate)
        {
            _variables[name] = value;
        }
        _log.Debug("[FfxivEchoes] Variable set: {Name} = {Value}", name, value ?? "null");
    }

    public void Increment(string name, double delta = 1.0)
    {
        lock (_gate)
        {
            var current = _variables.TryGetValue(name, out var v) ? ToDouble(v) : 0.0;
            _variables[name] = current + delta;
        }
        _log.Debug("[FfxivEchoes] Variable inc: {Name} += {Delta}", name, delta);
    }

    public void Decrement(string name, double delta = 1.0)
    {
        Increment(name, -delta);
    }

    public void Reset(string name)
    {
        lock (_gate)
        {
            _variables.Remove(name);
        }
    }

    public void ResetAll()
    {
        lock (_gate)
        {
            _variables.Clear();
        }
        _log.Debug("[FfxivEchoes] Variables reset (combat end)");
    }

    public object? Get(string name)
    {
        lock (_gate)
        {
            return _variables.TryGetValue(name, out var v) ? v : null;
        }
    }

    public IReadOnlyDictionary<string, object?> Snapshot()
    {
        lock (_gate)
        {
            return new Dictionary<string, object?>(_variables, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>set_variable の operation と value（JsonElement?）を実行する。</summary>
    public void Apply(string name, string operation, JsonElement? value)
    {
        switch (operation.ToLowerInvariant())
        {
            case "set":
                Set(name, JsonElementToValue(value));
                break;
            case "increment":
                Increment(name, value is { ValueKind: JsonValueKind.Number } v ? v.GetDouble() : 1.0);
                break;
            case "decrement":
                Decrement(name, value is { ValueKind: JsonValueKind.Number } v2 ? v2.GetDouble() : 1.0);
                break;
            case "reset":
                Reset(name);
                break;
            default:
                _log.Warning("[FfxivEchoes] 未知の set_variable operation: {Op}", operation);
                break;
        }
    }

    private static object? JsonElementToValue(JsonElement? el) => el?.ValueKind switch
    {
        JsonValueKind.True => (object)true,
        JsonValueKind.False => (object)false,
        JsonValueKind.Number => el.Value.GetDouble(),
        JsonValueKind.String => el.Value.GetString(),
        JsonValueKind.Null or JsonValueKind.Undefined or null => null,
        _ => el.Value.GetRawText(),
    };

    private static double ToDouble(object? v) => v switch
    {
        double d => d,
        int i => i,
        long l => l,
        float f => f,
        string s when double.TryParse(s, out var p) => p,
        bool b => b ? 1.0 : 0.0,
        _ => 0.0,
    };

    /// <summary>P4 で使用：Vector3 を変数に格納（位置保存）。</summary>
    public void StorePosition(string name, Vector3 pos)
    {
        Set(name, pos);
    }

    public Vector3? GetPosition(string name)
    {
        return Get(name) is Vector3 v ? v : null;
    }
}
