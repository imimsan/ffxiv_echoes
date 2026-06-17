using System;
using System.Numerics;
using System.Text.Json;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FfxivEchoes.Triggers.Models;

namespace FfxivEchoes.SafeZone.Presets;

/// <summary>
/// ノックバック着地点予測（SPEC.md §6.1 拡張）。
/// self を <c>source</c> から外向きに <c>distance</c>（吹き飛び量 m）だけ投射した着地点を返す。
/// between_actors の中点（<see cref="BetweenActorsPreset"/>）と異なり、実際に吹き飛ばされる先を出すため、
/// 「ここでノックバックを受けると落ちる／AoE に入る」を事前提示できる。
/// </summary>
/// <remarks>
/// params:
///   source: "self" / "boss" / "arena_center" / アクター名 / [x,y,z]（既定 "boss"）。ノックバックの発生源。
///   distance: 吹き飛び量 m（既定 15。耐性無し基準。技ごとに実測値を入れる）。
///   clamp_to_arena: bool（既定 true）。アリーナ境界で止まる（壁あり想定）。
///   arena_radius: 円形アリーナ半径 m。指定時のみ clamp する。未指定なら clamp せず純投射。
///
/// 符号: 外向き = source→self 方向。dir = normalize(self - source)。
/// これを (source - self) と取り違えると AoE 発生源へ誘導 ＝ 全滅誘導になるため、テストで固定している。
/// 回転行列を使わない純ベクトル差分なので、BossRelativePreset の rotation 符号の罠は無い。
/// </remarks>
public sealed class KnockbackProjectionPreset : ISafeZonePreset
{
    public string Method => "knockback_projection";

    private const float DegenerateEpsilon = 0.01f;

    private readonly IObjectTable _objectTable;

    public KnockbackProjectionPreset(IObjectTable objectTable)
    {
        _objectTable = objectTable;
    }

    public SafeZoneResult? Calculate(SafeZoneCalculation calc, SafeZoneContext ctx)
    {
        var sourceSpec = ParamHelper.GetString(calc.Params, "source") ?? "boss";
        var distance = ParamHelper.GetFloat(calc.Params, "distance") ?? 15f;
        var clampToArena = ParamHelper.GetBool(calc.Params, "clamp_to_arena") ?? true;
        var arenaRadius = ParamHelper.GetFloat(calc.Params, "arena_radius");

        var source = ResolveSource(calc, ctx, sourceSpec);
        if (source is null)
        {
            return null;
        }

        var self = ctx.SelfPosition;
        var landing = ProjectLanding(self, source.Value, ctx.ArenaCenter, distance, clampToArena, arenaRadius);
        if (landing is null)
        {
            return null;
        }

        return new SafeZoneResult(landing.Value, DirectionInfo.Compute(self, landing.Value));
    }

    /// <summary>
    /// 純幾何の着地点計算（Dalamud 型に依存しないのでテスト可能）。
    /// self を source から外向き（source→self）に distance 投射する。source==self の縮退時は
    /// arenaCenter からの外向きにフォールバック（中央 KB 想定）、二重縮退なら null。
    /// </summary>
    public static Vector3? ProjectLanding(
        Vector3 self, Vector3 source, Vector3 arenaCenter, float distance, bool clampToArena, float? arenaRadius)
    {
        // 外向き単位ベクトル（XZ 平面のみ、Y 無視）。符号は必ず (self - source)。
        var dir = OutwardXz(self, source);
        if (dir is null)
        {
            dir = OutwardXz(self, arenaCenter);
            if (dir is null)
            {
                // 二重縮退（自分がちょうど中心）。方向が定義できないので null（無音＝安全側）。
                return null;
            }
        }

        var landing = new Vector3(
            self.X + dir.Value.X * distance,
            self.Y,
            self.Z + dir.Value.Z * distance);

        if (clampToArena && arenaRadius is { } r && r > 0f)
        {
            landing = ClampToCircle(landing, arenaCenter, r);
        }

        return landing;
    }

    /// <summary>from→to の XZ 単位ベクトル（Y=0）。距離が極小（縮退）なら null。</summary>
    private static Vector3? OutwardXz(Vector3 from, Vector3 to)
    {
        var dx = from.X - to.X;
        var dz = from.Z - to.Z;
        var len = MathF.Sqrt(dx * dx + dz * dz);
        if (len < DegenerateEpsilon)
        {
            return null;
        }
        return new Vector3(dx / len, 0f, dz / len);
    }

    /// <summary>landing を中心 center・半径 r の円内へ縮める（XZ のみ、Y 維持）。</summary>
    private static Vector3 ClampToCircle(Vector3 landing, Vector3 center, float r)
    {
        var dx = landing.X - center.X;
        var dz = landing.Z - center.Z;
        var d = MathF.Sqrt(dx * dx + dz * dz);
        if (d <= r || d < DegenerateEpsilon)
        {
            return landing;
        }
        var scale = r / d;
        return new Vector3(center.X + dx * scale, landing.Y, center.Z + dz * scale);
    }

    private Vector3? ResolveSource(SafeZoneCalculation calc, SafeZoneContext ctx, string spec)
    {
        switch (spec.ToLowerInvariant())
        {
            case "self":
                return ctx.SelfPosition;
            case "boss":
                return ctx.Boss is { } b ? new Vector3(b.Position.X, b.Position.Y, b.Position.Z) : null;
            case "arena_center":
            case "center":
                return ctx.ArenaCenter;
        }

        // [x,y,z] 固定座標指定にも対応（source が JSON 配列のとき）。
        if (TryGetFixedPoint(calc.Params, "source", out var fixedPoint))
        {
            return fixedPoint;
        }

        foreach (var obj in _objectTable)
        {
            if (obj is IBattleChara c && c.Name.TextValue.Contains(spec))
            {
                return new Vector3(c.Position.X, c.Position.Y, c.Position.Z);
            }
        }
        return null;
    }

    private static bool TryGetFixedPoint(
        System.Collections.Generic.IReadOnlyDictionary<string, JsonElement>? p, string key, out Vector3 point)
    {
        point = default;
        if (p is null || !p.TryGetValue(key, out var v) || v.ValueKind != JsonValueKind.Array)
        {
            return false;
        }
        var arr = v.EnumerateArray();
        var values = new System.Collections.Generic.List<float>(3);
        foreach (var e in arr)
        {
            if (e.ValueKind != JsonValueKind.Number)
            {
                return false;
            }
            values.Add((float)e.GetDouble());
        }
        if (values.Count < 3)
        {
            return false;
        }
        point = new Vector3(values[0], values[1], values[2]);
        return true;
    }
}
