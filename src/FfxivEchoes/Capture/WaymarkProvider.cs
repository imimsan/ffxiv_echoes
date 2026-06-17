using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace FfxivEchoes.Capture;

/// <summary>
/// FFXIV のフィールドマーカー（A/B/C/D / 1/2/3/4）の世界座標を読み取るヘルパ。
/// 攻略登録の StrategyObjectMarker.Waymark を当日の実位置に紐付けるのに使う。
/// </summary>
public static class WaymarkProvider
{
    /// <summary>
    /// 指定文字（"A".."D" / "1".."4"）の現在のウェイマーク位置を返す。未設置なら null。
    /// </summary>
    public static Vector3? TryGetPosition(string? letter)
    {
        if (string.IsNullOrEmpty(letter)) return null;
        var idx = letter.Trim().ToUpperInvariant() switch
        {
            "A" => 0,
            "B" => 1,
            "C" => 2,
            "D" => 3,
            "1" => 4,
            "2" => 5,
            "3" => 6,
            "4" => 7,
            _ => -1,
        };
        if (idx < 0) return null;

        unsafe
        {
            try
            {
                var ctrl = MarkingController.Instance();
                if (ctrl is null) return null;
                var marker = ctrl->FieldMarkers[idx];
                if (!marker.Active) return null;
                // FFXIVClientStructs の FieldMarker は座標を 1/1000 m 単位（int）で持つ
                return new Vector3(
                    marker.X / 1000f,
                    marker.Y / 1000f,
                    marker.Z / 1000f);
            }
            catch
            {
                return null;
            }
        }
    }
}
