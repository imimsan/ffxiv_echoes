using System;
using Dalamud.Plugin.Services;
using LuminaAction = Lumina.Excel.Sheets.Action;

namespace FfxivEchoes.Triggers;

/// <summary>
/// Lumina Action データから AoE 形状（半径と CastType）を解決するヘルパ。
/// AutoTelegraphService と PredictedCastReminderService で共有する。
/// </summary>
public static class AoeResolver
{
    public const float MaxReliableEffectRangeM = 50f;

    public sealed record AoeInfo(
        float Radius,
        int CastType,
        bool FromCaster,
        uint OmenId = 0,
        bool IncludeCasterHitbox = false);

    /// <summary>
    /// Lumina Action から AoE 情報を取得。AoE でない場合や異常値は null。
    /// </summary>
    public static AoeInfo? Resolve(IDataManager dataManager, uint actionId, IPluginLog? log = null)
    {
        if (actionId == 0) return null;
        try
        {
            var sheet = dataManager.GetExcelSheet<LuminaAction>();
            if (!sheet.TryGetRow(actionId, out var row))
            {
                return null;
            }
            var effectRange = (float)row.EffectRange;
            if (effectRange <= 0)
            {
                log?.Debug("[FfxivEchoes] AoE skip non-AoE id={Id:X4} range={R}m castType={Ct}",
                    actionId, effectRange, (int)row.CastType);
                return null;
            }
            // 50m 超は全体攻撃・特殊演出・誤データが混ざりやすく、
            // 自動推測で床範囲として描くと誤誘導になるため明示定義がある場合だけ扱う。
            if (!IsReliableEffectRange(effectRange))
            {
                log?.Debug("[FfxivEchoes] AoE skip oversized id={Id:X4} range={R}m", actionId, effectRange);
                return null;
            }

            var castType = (int)row.CastType;
            // Splatoon の Projection.GuessShapeAndSize / 描画ルールに準拠：
            //   2  = Circle, **target 中心**（地面/プレイヤー指定）
            //   3  = Cone, caster 中心、caster 正面向き、+ HitboxRadius
            //   4  = Rect, caster 中心、caster 正面向き、+ HitboxRadius
            //   5  = Circle (PBAoE), caster 中心、+ HitboxRadius
            //   6  = Donut, caster 中心（特殊：内安置外危）
            //   7  = 特殊 Donut（caster 中心）
            //   10 = Donut
            //   11 = Cross, caster 中心
            //   12 = Rect, **地面（target 中心）**、HitboxRadius 加算なし
            //   13 = Cone, **地面（target 中心）**、HitboxRadius 加算なし
            // 旧実装は 12/13 を caster 中心にしていたため、ground-targeted 技
            // （プレイヤー位置中心の rect/cone）が caster 位置に間違って描画されていた。
            var fromCaster = castType is 3 or 4 or 5 or 6 or 7 or 10 or 11;
            // Omen ID（テレグラフのアセット参照）を取得。失敗時は 0
            uint omenId = 0;
            try
            {
                omenId = row.Omen.RowId;
            }
            catch { /* Omen フィールドが取れない場合は無視 */ }

            log?.Debug("[FfxivEchoes] AoE resolve id={Id:X4} range={R}m castType={Ct} omen={Om}",
                actionId, effectRange, castType, omenId);
            var includeCasterHitbox = castType is 3 or 4 or 5;
            return new AoeInfo(effectRange, castType, fromCaster, omenId, includeCasterHitbox);
        }
        catch (Exception ex)
        {
            log?.Warning(ex, "[FfxivEchoes] AoE resolve 失敗 id={Id}", actionId);
            return null;
        }
    }

    public static bool IsReliableEffectRange(float effectRange)
        => effectRange > 0f && effectRange <= MaxReliableEffectRangeM;

    /// <summary>
    /// 既知の Omen ID から gimmick を推測。
    /// 未知の Omen ID は CastType ベースのフォールバックを呼び出し側で。
    /// </summary>
    public static string? GuessGimmickByOmen(uint omenId)
    {
        // FFXIV の Omen 一覧の代表値（経験的に蓄積したもの）。
        // 完全網羅は不可能だが、よく使われるテレグラフをカバーする。
        return omenId switch
        {
            // Donut（中央安置 / 外周危険）系：複数バリエーションある
            53 or 60 or 61 or 62 or 63 or 64 => "outer_ring",
            // 通常円（中央危険 / 外周安置）系
            1 or 2 or 3 or 4 or 5 => "inner_circle",
            // 標準コーン
            10 or 11 or 12 or 13 or 14 => "cone",
            // 直線
            20 or 21 or 22 or 23 => "cone",
            // 半円（half-plane 的）
            40 or 41 or 42 => "half_plane",
            _ => null, // 未知は呼び出し側で判定
        };
    }

    /// <summary>
    /// CastType / Omen ID から AoE 効果が「ドーナツ形状」かどうかを判定。
    /// Donut は内側安置・外周危険の特殊形状で、通常円とは描画が違う。
    /// </summary>
    public static bool IsDonutShape(int castType, uint omenId)
    {
        // CastType 6, 7, 10 は明確に Donut（公式ゲームデータ準拠）
        if (castType == 6 || castType == 7 || castType == 10) return true;
        // Omen ID にも Donut バリエーションがある
        if (omenId is 53 or 60 or 61 or 62 or 63 or 64) return true;
        return false;
    }

    /// <summary>
    /// Donut の内径比（外径に対する比率）。Omen ID で違うバリエーションがある。
    /// 不明なら 0.30（標準的な値）。
    /// </summary>
    public static float DonutInnerRatio(uint omenId)
    {
        return omenId switch
        {
            53 => 0.30f,    // 標準 Donut（中央 30% 安置）
            60 or 61 => 0.40f, // 広めのリング
            62 or 63 => 0.20f, // 狭い中央安置
            64 => 0.50f,    // 非常に狭いリング
            _ => 0.30f,     // 不明：標準値
        };
    }

    /// <summary>
    /// CastType と EffectRange から arena_view 用の gimmick タイプを推測。
    /// アリーナ図に表示するための形状。
    /// </summary>
    public static string GuessGimmick(int castType, float effectRange)
    {
        return castType switch
        {
            // ターゲット/キャスター中心円: 中央が危険で外周が安置。
            2 => "inner_circle",
            5 => "inner_circle",
            // Donut: 外周が危険で内側が安置。
            6 => "outer_ring",
            7 => "outer_ring",
            10 => "outer_ring",
            // Cone / Line：cone gimmick（demo の扇形コーンと同じ）
            3 => "cone",
            4 => "cone",
            11 => "cone",
            12 => "cone",
            13 => "cone",
            _ => "inner_circle",
        };
    }

    public static float EffectiveRadius(AoeInfo aoe, float casterHitboxRadius)
    {
        if (!aoe.IncludeCasterHitbox)
        {
            return aoe.Radius;
        }

        return aoe.Radius + Math.Max(0f, casterHitboxRadius);
    }

    public static bool TryParseCastId(string spec, out uint id)
    {
        id = 0;
        if (string.IsNullOrEmpty(spec)) return false;
        var s = spec;
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        if (s.StartsWith("#")) s = s[1..];
        return uint.TryParse(s, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out id);
    }
}
