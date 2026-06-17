using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;

namespace FfxivEchoes.Windows.Tabs;

/// <summary>
/// JSON 値（英語キー）と UI 表示（日本語）を仲介するヘルパ。
/// 内部値は英語のまま保ち、ドロップダウン等での見た目だけ日本語化する。
/// </summary>
public static class Localization
{
    public static readonly Dictionary<string, string> Gimmick = new(StringComparer.OrdinalIgnoreCase)
    {
        ["outer_ring"]    = "外周回避（中央安置）",
        ["inner_circle"]  = "中央AoE（外周安置）",
        ["scatter"]       = "4方向散開",
        ["stack"]         = "中央集合",
        ["cone"]          = "扇形コーン",
    };

    public static readonly Dictionary<string, string> Direction = new(StringComparer.OrdinalIgnoreCase)
    {
        ["N"]  = "北 (N)",
        ["NE"] = "北東 (NE)",
        ["E"]  = "東 (E)",
        ["SE"] = "南東 (SE)",
        ["S"]  = "南 (S)",
        ["SW"] = "南西 (SW)",
        ["W"]  = "西 (W)",
        ["NW"] = "北西 (NW)",
    };

    public static readonly Dictionary<string, string> DirectionFormat = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cardinal"]    = "英語方位（north/east/...）",
        ["cardinal_jp"] = "日本語方位（北/東/...）",
        ["clock"]       = "時計方位（12時/3時/...）",
        ["relative_jp"] = "相対方向（前/左/右/後）",
        ["degrees"]     = "角度（0-360°）",
    };

    public static readonly Dictionary<string, string> FieldShape = new(StringComparer.OrdinalIgnoreCase)
    {
        ["circle"] = "円",
        ["square"] = "四角",
        ["x_mark"] = "×印",
        ["arrow"]  = "矢印",
    };

    public static readonly Dictionary<string, string> SafeZoneMethod = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fixed"]                       = "固定座標",
        ["boss_relative"]               = "ボスからの相対位置",
        ["marker_relative"]             = "フィールドマーカー基準",
        ["arena_center_relative"]       = "アリーナ中心からの相対位置",
        ["inverse_of_telegraph"]        = "AoE の反対側",
        ["party_member_relative"]       = "PT メンバー基準",
        ["find_actor_with_status"]      = "ステータスを持つ敵基準",
        ["find_actor_without_status"]   = "ステータスを持たない敵基準",
        ["find_actor_not_casting"]      = "キャストしていない敵基準",
        ["find_actor_by_distance"]      = "距離による敵検索（最近/最遠）",
        ["midpoint"]                    = "2 アクターの中点",
        ["between_actors"]              = "2 アクター間の指定割合",
        ["line_perpendicular"]          = "2 点を結ぶ線の垂線",
        ["telegraph_gap"]               = "複数 AoE の隙間",
        ["intersection"]                = "複数計算結果の交差",
        ["stored_position"]             = "以前保存した位置（store_position）",
    };

    public static readonly Dictionary<string, string> EventType = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cast_start"]      = "キャスト開始",
        ["cast_complete"]   = "キャスト完了",
        ["action_used"]     = "アクション実行",
        ["status_gain"]     = "ステータス付与",
        ["hp_change"]       = "HP 変化",
        ["combat_start"]    = "戦闘開始",
        ["combat_end"]      = "戦闘終了",
        ["object_appear"]   = "オブジェクト出現",
        ["object_disappear"]= "オブジェクト消失",
        ["timeline_elapsed"]= "タイムライン経過",
    };

    public static readonly Dictionary<string, string> ActionType = new(StringComparer.OrdinalIgnoreCase)
    {
        ["tts"]                = "音声読み上げ (TTS)",
        ["wav"]                = "WAV 再生",
        ["overlay_text"]       = "中央テキスト",
        ["overlay_corner_text"]= "コーナーテキスト",
        ["timer_bar"]          = "タイマーバー",
        ["chat_echo"]          = "チャット出力",
        ["direction_call"]     = "方位読み上げ",
        ["screen_arrow"]       = "矢印表示（安置へ）",
        ["field_marker"]       = "フィールド円（安置位置）",
        ["proximity_feedback"] = "距離音（安置との距離）",
        ["set_variable"]       = "変数設定",
        ["chain_trigger"]      = "別トリガー連鎖発動",
        ["store_position"]     = "現在位置を保存",
        ["arena_view"]         = "俯瞰アリーナ図",
    };

    /// <summary>
    /// 英語キー配列に対応する日本語ラベル配列を返す（ImGui.Combo 用）。
    /// </summary>
    public static string[] LocalizeAll(IReadOnlyList<string> keys, IReadOnlyDictionary<string, string> map)
    {
        return keys.Select(k => map.TryGetValue(k, out var ja) ? $"{ja}（{k}）" : k).ToArray();
    }

    /// <summary>
    /// 単一値を日本語化（マップに無ければそのまま返す）。
    /// </summary>
    public static string Tr(string key, IReadOnlyDictionary<string, string> map)
    {
        return map.TryGetValue(key, out var ja) ? ja : key;
    }
}
