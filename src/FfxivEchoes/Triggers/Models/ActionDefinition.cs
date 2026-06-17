using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FfxivEchoes.Triggers.Models;

/// <summary>
/// トリガー発動時に実行されるアクション（SPEC.md §5）。
/// </summary>
/// <remarks>
/// type に応じて使用するフィールドが変わる。すべての可能なフィールドをここに列挙し、
/// 不要なものは null のままにする方針（実行側 = M7 の ActionDispatcher が type 別に振り分ける）。
/// </remarks>
public sealed class ActionDefinition
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("delay")]
    public double Delay { get; set; } = 0.0;

    // tts / chat_echo / overlay_text 系
    [JsonPropertyName("text")]
    public string? Text { get; set; }

    // tts
    [JsonPropertyName("voice")]
    public string? Voice { get; set; }

    [JsonPropertyName("rate")]
    public double Rate { get; set; } = 1.0;

    /// <summary>
    /// 読み上げ優先度（tts / direction_call）。0=通常、1 以上=優先。
    /// 優先コール（軽減・安置・HP全快コール等）はキュー内で通常コールより前に挿し、
    /// キュー溢れ時も通常コールより先に守られる。連続詠唱の混雑で重要コールが埋もれるのを防ぐ。
    /// </summary>
    [JsonPropertyName("priority")]
    public int Priority { get; set; } = 0;

    // tts / wav / 共通
    [JsonPropertyName("volume")]
    public double Volume { get; set; } = 1.0;

    // wav
    [JsonPropertyName("file")]
    public string? File { get; set; }

    // overlay_text / overlay_corner_text / timer_bar
    [JsonPropertyName("duration")]
    public double? Duration { get; set; }

    [JsonPropertyName("color")]
    public string? Color { get; set; }

    [JsonPropertyName("size")]
    public string? Size { get; set; }

    // overlay_corner_text / field_marker
    [JsonPropertyName("position")]
    public JsonElement? Position { get; set; } // string or {x,y,z}

    // timer_bar
    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("warn_at")]
    public double? WarnAt { get; set; }

    // direction_call / screen_arrow / field_marker / proximity_feedback
    [JsonPropertyName("safe_zone")]
    public SafeZoneCalculation? SafeZone { get; set; }

    // direction_call
    [JsonPropertyName("format")]
    public string? Format { get; set; }

    [JsonPropertyName("tts")]
    public bool? Tts { get; set; }

    [JsonPropertyName("overlay")]
    public bool? Overlay { get; set; }

    // screen_arrow
    [JsonPropertyName("from")]
    public string? From { get; set; }

    [JsonPropertyName("to")]
    public JsonElement? To { get; set; } // string or SafeZoneCalculation

    // field_marker
    [JsonPropertyName("shape")]
    public string? Shape { get; set; }

    [JsonPropertyName("radius")]
    public double? Radius { get; set; }

    // proximity_feedback
    [JsonPropertyName("tolerance")]
    public double? Tolerance { get; set; }

    [JsonPropertyName("in_sound")]
    public string? InSound { get; set; }

    [JsonPropertyName("out_sound")]
    public string? OutSound { get; set; }

    [JsonPropertyName("show_distance")]
    public bool? ShowDistance { get; set; }

    // chain_trigger
    [JsonPropertyName("trigger_id")]
    public string? TriggerId { get; set; }

    // arena_view: ギミックタイプ（outer_ring / inner_circle / scatter / stack / cone）
    [JsonPropertyName("gimmick")]
    public string? Gimmick { get; set; }

    // arena_view: 安置コール文字列
    [JsonPropertyName("callout")]
    public string? Callout { get; set; }

    // arena_view (cone): 危険コーンの方向（N/NE/E/SE/S/SW/W/NW）
    [JsonPropertyName("direction")]
    public string? Direction { get; set; }

    // arena_view (cone): 扇形の角度（度数法）
    [JsonPropertyName("fan_deg")]
    public double? FanDeg { get; set; }

    // arena_view: アリーナ半径（メートル）。プレイヤー位置プロット用。デフォルト 20m。
    [JsonPropertyName("arena_radius")]
    public double? ArenaRadius { get; set; }

    /// <summary>arena_view: アリーナ形状（"circle" / "square" / "rect"）。未指定なら circle。</summary>
    [JsonPropertyName("arena_shape")]
    public string? ArenaShape { get; set; }

    /// <summary>arena_view: 矩形系の幅（東西 m）。</summary>
    [JsonPropertyName("arena_width")]
    public double? ArenaWidth { get; set; }

    /// <summary>arena_view: 矩形系の奥行（南北 m）。</summary>
    [JsonPropertyName("arena_depth")]
    public double? ArenaDepth { get; set; }

    /// <summary>arena_view: アリーナ中心の世界座標 X（校正済の場合）。</summary>
    [JsonPropertyName("arena_center_x")]
    public double? ArenaCenterX { get; set; }

    /// <summary>arena_view: アリーナ中心の世界座標 Z（校正済の場合）。</summary>
    [JsonPropertyName("arena_center_z")]
    public double? ArenaCenterZ { get; set; }

    [JsonPropertyName("strategy_profile_id")]
    public string? StrategyProfileId { get; set; }

    [JsonPropertyName("mechanic_id")]
    public string? MechanicId { get; set; }

    [JsonPropertyName("strategy_positions")]
    public List<StrategyPosition>? StrategyPositions { get; set; }

    /// <summary>arena_view: ユーザー定義のオブジェクト/敵マーカー（ボス・add 等）。</summary>
    [JsonPropertyName("object_markers")]
    public List<StrategyObjectMarker>? ObjectMarkers { get; set; }

    /// <summary>arena_view: ユーザー定義の AoE 形状（円・ドーナツ・扇・矩形）。</summary>
    [JsonPropertyName("aoe_zones")]
    public List<StrategyAoeZone>? AoeZones { get; set; }

    /// <summary>arena_view: 時間差 / 連鎖 AoE。エクサフレア等の順次着弾用。</summary>
    [JsonPropertyName("aoe_sequence")]
    public AoeSequence? AoeSequence { get; set; }

    /// <summary>arena_view: 「特定バフ／デバフを持っている PT メンバーは色／バッジ強調」の指定。</summary>
    [JsonPropertyName("party_status_highlights")]
    public List<StatusHighlightSpec>? PartyStatusHighlights { get; set; }

    // 想定外フィールドを失わないための受け皿（フォーマットバージョン跨ぎの後方互換用）
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extras { get; set; }
}
