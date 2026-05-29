namespace FfxivEchoes.Triggers;

/// <summary>
/// フェーズ移行判定の純粋ロジック。Dalamud 型に依存しないため単体テスト可能（CS0012 回避）。
/// </summary>
public static class PhaseTransitionPolicy
{
    /// <summary>
    /// ボス HP%（0-100）が <paramref name="threshold"/> を上から下へ跨いだかを判定する。
    /// <paramref name="prev"/> が NaN（初回観測）の場合は false。
    /// MechanicTriggerService.OnHpChanged の crossedBelow と同値。
    /// </summary>
    public static bool CrossedBelow(float prev, float now, float threshold)
    {
        // prev=NaN のとき NaN >= threshold は false になるため、初回観測は自然に false。
        return prev >= threshold && now < threshold;
    }
}
