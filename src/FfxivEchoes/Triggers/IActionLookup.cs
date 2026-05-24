using System.Collections.Generic;

namespace FfxivEchoes.Triggers;

/// <summary>
/// Lumina Action sheet を抽象化したルックアップ。
/// 本番は <see cref="LuminaActionLookup"/> が <c>IDataManager</c> 経由で実装し、
/// headless replay（<c>FfxivEchoes.Replay</c>）では JSON dump を読み込むモック実装が使う。
/// </summary>
/// <remarks>
/// 設計書 <c>docs/superpowers/specs/2026-05-24-aoe-fix-and-browser-replay-design.md §5</c> 参照。
/// 抽象化の目的は、Dalamud SDK 非依存で AoE 形状解決ロジックをテスト可能にすること。
/// </remarks>
public interface IActionLookup
{
    /// <summary>
    /// 指定 <paramref name="actionId"/> の形状情報を返す。Action が存在しない場合は null。
    /// </summary>
    ActionGeometry? TryGet(uint actionId);

    /// <summary>
    /// 既知の全 Action を列挙する（dump 用途）。実装によっては空列挙でも可。
    /// </summary>
    IEnumerable<ActionGeometry> ListAll();
}

/// <summary>
/// Action の AoE 形状を表す純 POCO。Lumina 型に依存しない。
/// </summary>
/// <param name="Id">Action ID (Lumina row id)。</param>
/// <param name="Name">Action 名（日本語クライアント前提）。</param>
/// <param name="CastType">Lumina CastType。2=Circle, 3=Cone, 4=Rect, 5=PBAoE, 6/7=Donut(特殊), 10=Donut, 11=Cross, 12=Rect(地面), 13=Cone(地面)。</param>
/// <param name="EffectRangeM">効果範囲（メートル）。Lumina の EffectRange そのまま。</param>
/// <param name="XAxisModifierM">幅方向修飾子（メートル）。rect/cone の半幅相当。</param>
/// <param name="OmenId">Omen の row id（テレグラフのアセット参照）。無ければ 0。</param>
/// <param name="CastTimeMs">キャスト時間（ミリ秒）。インスタント = 0。</param>
/// <param name="IsPlayerAction">プレイヤースキルか（Lumina の IsPlayerAction）。学習対象 / フィルタ判定で使う。</param>
public sealed record ActionGeometry(
    uint Id,
    string Name,
    int CastType,
    float EffectRangeM,
    float XAxisModifierM,
    uint OmenId,
    int CastTimeMs,
    bool IsPlayerAction = false);
