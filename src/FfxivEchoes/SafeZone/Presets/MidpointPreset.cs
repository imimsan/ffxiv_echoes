using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FfxivEchoes.Triggers.Models;

namespace FfxivEchoes.SafeZone.Presets;

/// <summary>
/// 複数アクターの中点（SPEC.md §6.1）。
/// params: { actors: [name 文字列の配列。"boss" / "self" / 名前部分一致] }
/// </summary>
public sealed class MidpointPreset : ISafeZonePreset
{
    public string Method => "midpoint";

    private readonly IObjectTable _objectTable;

    public MidpointPreset(IObjectTable objectTable)
    {
        _objectTable = objectTable;
    }

    public SafeZoneResult? Calculate(SafeZoneCalculation calc, SafeZoneContext ctx)
    {
        if (calc.Params is null || !calc.Params.TryGetValue("actors", out var actorsEl) ||
            actorsEl.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            return null;
        }

        var positions = new List<Vector3>();
        foreach (var spec in actorsEl.EnumerateArray())
        {
            if (spec.ValueKind != System.Text.Json.JsonValueKind.String)
            {
                continue;
            }
            var s = spec.GetString() ?? string.Empty;
            var pos = ResolveActorPosition(ctx, s);
            if (pos is { } p)
            {
                positions.Add(p);
            }
        }

        if (positions.Count == 0)
        {
            return null;
        }

        var sum = Vector3.Zero;
        foreach (var p in positions)
        {
            sum += p;
        }
        var mid = sum / positions.Count;
        return new SafeZoneResult(mid, DirectionInfo.Compute(ctx.SelfPosition, mid));
    }

    private Vector3? ResolveActorPosition(SafeZoneContext ctx, string spec)
    {
        switch (spec.ToLowerInvariant())
        {
            case "self":
                return ctx.SelfPosition;
            case "boss":
                return ctx.Boss is { } b ? new Vector3(b.Position.X, b.Position.Y, b.Position.Z) : null;
        }
        // 名前部分一致で IObjectTable から検索
        foreach (var obj in _objectTable)
        {
            if (obj is IBattleChara c && c.Name.TextValue.Contains(spec))
            {
                return new Vector3(c.Position.X, c.Position.Y, c.Position.Z);
            }
        }
        return null;
    }
}
