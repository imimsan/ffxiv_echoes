using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace FfxivEchoes.Triggers.Models;

/// <summary>
/// 1 コンテンツぶんのトリガー定義ファイル全体（trigger-schema.json のルート）。
/// </summary>
public sealed class TriggerFile
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.0";

    [JsonPropertyName("zone")]
    public string Zone { get; set; } = string.Empty;

    [JsonPropertyName("auto_settings")]
    public AutoSettings AutoSettings { get; set; } = new();

    [JsonPropertyName("metadata")]
    public TriggerFileMetadata? Metadata { get; set; }

    [JsonPropertyName("active_strategy_profile_id")]
    public string? ActiveStrategyProfileId { get; set; }

    [JsonPropertyName("strategy_profiles")]
    public List<StrategyProfile> StrategyProfiles { get; set; } = new();

    [JsonPropertyName("sync_points")]
    public List<SyncPoint> SyncPoints { get; set; } = new();

    /// <summary>
    /// このコンテンツ内だけで「全体攻撃」として扱う cast / action。
    /// グローバル辞書に置くと同じ action id を使う別コンテンツまで AoE が消えるため、
    /// raid-wide 抑止は原則としてここを参照する。
    /// </summary>
    [JsonPropertyName("raid_wide_markers")]
    public List<RaidWideMarker> RaidWideMarkers { get; set; } = new();

    [JsonPropertyName("triggers")]
    public List<TriggerDefinition> Triggers { get; set; } = new();

    [JsonPropertyName("ignored_events")]
    public List<IgnoredEvent> IgnoredEvents { get; set; } = new();

    /// <summary>
    /// プレイヤーが事前に書いておくタイムラインノート（軽減タイミング、LB 確認、
    /// 移動指示など）。trigger とは独立に時刻指定で表示・通知する。
    /// </summary>
    [JsonPropertyName("notes")]
    public List<TimelineNote> Notes { get; set; } = new();

    /// <summary>
    /// タイムライン分岐定義。例：滅エヌオーで「最初のキャストでパターン1とパターン2に分岐」。
    /// 戦闘開始から <see cref="BranchCondition.WindowSec"/> 以内に判定キャストを観測した
    /// branch が active となり、他は rejected。
    /// 空（既定）なら分岐無し（全 mechanic が共通として扱われる）。
    /// </summary>
    [JsonPropertyName("branches")]
    public List<TimelineBranch> Branches { get; set; } = new();
}

public sealed class RaidWideMarker
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("callout")]
    public string? Callout { get; set; }

    [JsonPropertyName("tts")]
    public string? Tts { get; set; }

    [JsonPropertyName("confidence")]
    public double? Confidence { get; set; }
}

public sealed class StrategyProfile
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("arena_radius")]
    public double? ArenaRadius { get; set; }

    /// <summary>
    /// アリーナの形状。"circle"（既定）/ "square" / "rect"。
    /// 矩形系は (ArenaWidth, ArenaDepth) を使い、circle は ArenaRadius のみ。
    /// </summary>
    [JsonPropertyName("arena_shape")]
    public string ArenaShape { get; set; } = "circle";

    /// <summary>square / rect の場合の東西全長（メートル）。中心からは半分が有効範囲。</summary>
    [JsonPropertyName("arena_width")]
    public double? ArenaWidth { get; set; }

    /// <summary>square / rect の場合の南北全長（メートル）。中心からは半分が有効範囲。</summary>
    [JsonPropertyName("arena_depth")]
    public double? ArenaDepth { get; set; }

    /// <summary>アリーナ中心の世界座標 X。null なら戦闘開始時のボス位置を使う。</summary>
    [JsonPropertyName("arena_center_x")]
    public double? ArenaCenterX { get; set; }

    /// <summary>アリーナ中心の世界座標 Z。null なら戦闘開始時のボス位置を使う。</summary>
    [JsonPropertyName("arena_center_z")]
    public double? ArenaCenterZ { get; set; }

    [JsonPropertyName("spread_positions")]
    public List<StrategyPosition> SpreadPositions { get; set; } = new();

    /// <summary>
    /// フェーズ単位のアリーナ寸法既定。フェーズ名 → 形状 dict。
    /// メカニクス側の寸法 override が無い場合、所属フェーズの設定がプロファイル既定より優先される。
    /// </summary>
    [JsonPropertyName("phase_arena_shapes")]
    public Dictionary<string, PhaseArenaSpec> PhaseArenaShapes { get; set; } = new();

    /// <summary>
    /// ランダム配置されるオブジェクト起点 AoE の形状ルール。
    /// パラデイグマのように「出現位置は毎回違うが、オブジェクト種別で AoE が決まる」
    /// ギミックを、cast id ではなく object_name / data_id で管理する。
    /// </summary>
    [JsonPropertyName("object_aoe_rules")]
    public List<ObjectAoeRule> ObjectAoeRules { get; set; } = new();

    /// <summary>
    /// 「特定の cast が起きると、N 秒後に Object が出現して即時 AoE 発動」のパターンを録画から
    /// 学習し、cast 検知時点で先取り予告描画するためのデータ。
    /// 月の底のパラデイグマ → ケツアクアトル 4 体出現のような、Dalamud ObjectTable 登録遅延が
    /// 大きいギミックを対象に、ObjectTable を待たずに事前描画する。
    /// docs/predicted-object-spawn-design.md 参照。
    /// </summary>
    [JsonPropertyName("predicted_object_spawns")]
    public List<PredictedObjectSpawn> PredictedObjectSpawns { get; set; } = new();

    [JsonPropertyName("mechanics")]
    public List<MechanicStrategy> Mechanics { get; set; } = new();
}

public sealed class ObjectAoeRule
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>対象オブジェクト名。空なら DataId のみで一致させる。</summary>
    [JsonPropertyName("object_name")]
    public string? ObjectName { get; set; }

    /// <summary>"contains" / "exact" / "startswith"。既定 contains。</summary>
    [JsonPropertyName("name_match")]
    public string NameMatch { get; set; } = "contains";

    /// <summary>対象オブジェクト DataId。null なら名前のみで一致させる。</summary>
    [JsonPropertyName("data_id")]
    public uint? DataId { get; set; }

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    /// <summary>"manual" / "recording" / "dictionary" / "builtin" など。</summary>
    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("shape")]
    public string Shape { get; set; } = "circle";

    [JsonPropertyName("radius_m")]
    public double RadiusM { get; set; } = 5.0;

    [JsonPropertyName("inner_radius_m")]
    public double? InnerRadiusM { get; set; }

    [JsonPropertyName("fan_deg")]
    public double? FanDeg { get; set; }

    [JsonPropertyName("half_width_m")]
    public double? HalfWidthM { get; set; }

    /// <summary>表示時間（秒）。null なら自動 object AoE の既定表示時間を使う。</summary>
    [JsonPropertyName("duration_sec")]
    public double? DurationSec { get; set; }

    [JsonPropertyName("color")]
    public string? Color { get; set; }

    [JsonPropertyName("live_floor_paint")]
    public bool LiveFloorPaint { get; set; } = true;
}

/// <summary>
/// フェーズ単位のアリーナ形状既定。StrategyProfile.PhaseArenaShapes の値型。
/// </summary>
public sealed class PhaseArenaSpec
{
    [JsonPropertyName("shape")]
    public string? Shape { get; set; }

    [JsonPropertyName("radius")]
    public double? Radius { get; set; }

    [JsonPropertyName("width")]
    public double? Width { get; set; }

    [JsonPropertyName("depth")]
    public double? Depth { get; set; }

    [JsonPropertyName("center_x")]
    public double? CenterX { get; set; }

    [JsonPropertyName("center_z")]
    public double? CenterZ { get; set; }
}

/// <summary>
/// メカニクス発動条件。type に応じて使うフィールドが異なる：
///  - "cast"          : 敵キャスト（既定。<see cref="Match"/> に cast_id 等）
///  - "action_used"   : 瞬間アクション
///  - "status_gain"   : 特定バフ／デバフが付与された
///  - "status_lose"   : 特定バフ／デバフが消えた
///  - "rotation"      : 特定アクターが特定方向を向いた（<see cref="ActorName"/> + <see cref="FacingDeg"/> ± <see cref="FacingToleranceDeg"/>）
///  - "hp_pct"        : 特定アクターの HP% が閾値を跨いだ（フェーズ遷移検知）
///  - "object_appear" : 特定オブジェクトが出現した
///  - "object_group"  : 特定オブジェクトが短時間に指定数出現した
/// </summary>
public sealed class MechanicTrigger
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "cast";

    /// <summary>cast / action_used / status_gain / status_lose 用のマッチ条件。</summary>
    [JsonPropertyName("match")]
    public MatchCondition? Match { get; set; }

    // ── rotation / hp_pct 用 ─────────────────────────
    /// <summary>監視するアクター名（部分一致）。</summary>
    [JsonPropertyName("actor_name")]
    public string? ActorName { get; set; }

    /// <summary>監視するアクターの DataId（任意）。</summary>
    [JsonPropertyName("actor_data_id")]
    public uint? ActorDataId { get; set; }

    /// <summary>object_group 用。短時間に出現してほしい最小個数。</summary>
    [JsonPropertyName("object_count_min")]
    public int? ObjectCountMin { get; set; }

    /// <summary>object_group 用。分岐を厳密にしたい場合の最大個数。</summary>
    [JsonPropertyName("object_count_max")]
    public int? ObjectCountMax { get; set; }

    /// <summary>object_group 用。同時出現とみなす秒数。</summary>
    [JsonPropertyName("object_window_sec")]
    public double? ObjectWindowSec { get; set; } = 1.5;

    // ── rotation 用 ─────────────────────────────────
    /// <summary>
    /// 検出する向き（度数法）。FFXIV 慣習：0=南、π/2=東、π=北、-π/2=西。
    /// ここでは角度（度）で指定：S=0, E=90, N=180, W=-90。
    /// </summary>
    [JsonPropertyName("facing_deg")]
    public double? FacingDeg { get; set; }

    /// <summary>許容誤差（度）。例：30 で ±30° 以内なら一致。</summary>
    [JsonPropertyName("facing_tolerance_deg")]
    public double? FacingToleranceDeg { get; set; } = 30.0;

    // ── hp_pct 用 ───────────────────────────────────
    /// <summary>HP% がこの値を下回ったら発動（割合 0〜100）。例：80 で 80% 未満。</summary>
    [JsonPropertyName("hp_pct_below")]
    public double? HpPctBelow { get; set; }

    /// <summary>HP% がこの値を上回ったら発動（割合 0〜100）。閾値超え検知。</summary>
    [JsonPropertyName("hp_pct_above")]
    public double? HpPctAbove { get; set; }

    /// <summary>
    /// hp_pct 用：この閾値を下回って跨いだときに開始するフェーズ名（<see cref="MechanicStrategy.Phase"/> と対応）。
    /// 設定すると crossedBelow 時に PhaseTransitionedEvent が発行される。sync_point を使わず HP% で
    /// フェーズ管理するコンテンツ向け（従系統）。null（既定）ならフェーズ境界としては扱わない。
    /// </summary>
    [JsonPropertyName("phase")]
    public string? Phase { get; set; }

    /// <summary>同じ rotation / hp_pct 条件の連続発火を抑制する秒数（既定 5 秒）。</summary>
    [JsonPropertyName("dedup_sec")]
    public double? DedupSec { get; set; } = 5.0;
}

/// <summary>
/// 「特定ステータスを持っている PT メンバーだけ強調色／追加ラベル」の指定。
/// </summary>
public sealed class StatusHighlightSpec
{
    [JsonPropertyName("status_id")]
    public uint? StatusId { get; set; }

    [JsonPropertyName("status_name")]
    public string? StatusName { get; set; }

    /// <summary>該当者のドット色を上書き（"#RRGGBB"）。</summary>
    [JsonPropertyName("color")]
    public string? Color { get; set; }

    /// <summary>該当者のドット脇に出すバッジ文字（絵文字 OK）。</summary>
    [JsonPropertyName("badge")]
    public string? Badge { get; set; }
}

/// <summary>
/// メカニクス用ミニマップに置く「敵 / オブジェクト」マーカー。
/// 敵 NPC・ボス・誘導目標・印（A〜D マーカー風）など。
/// </summary>
public sealed class StrategyObjectMarker
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("x")]
    public double X { get; set; }

    [JsonPropertyName("z")]
    public double Z { get; set; }

    /// <summary>"#RRGGBB"。未指定なら既定（赤系）。</summary>
    [JsonPropertyName("color")]
    public string? Color { get; set; }

    /// <summary>"circle" / "square" / "triangle" / "diamond"。既定は circle。</summary>
    [JsonPropertyName("shape")]
    public string? Shape { get; set; }

    [JsonPropertyName("note")]
    public string? Note { get; set; }

    /// <summary>
    /// FFXIV のフィールドマーカー参照（A/B/C/D / 1/2/3/4）。指定があるとミニマップ
    /// 表示時に該当ウェイマークの実位置をライブで読み取り、(X, Z) を上書きする。
    /// 「ボスを A マーカーに誘導」のような攻略表現を、PT が当日マーカーを置いたら
    /// 自動で追従する設計。
    /// 値の例："A" / "B" / "C" / "D" / "1" / "2" / "3" / "4"。
    /// </summary>
    [JsonPropertyName("waymark")]
    public string? Waymark { get; set; }
}

/// <summary>
/// 時間差 / 連鎖 AoE のステップ。
/// 1 つの mechanic 発火で「0s に円、2s に円、4s に円」のような順次着弾を表現する。
/// </summary>
public sealed class AoeSequenceStep
{
    /// <summary>mechanic 発火からの遅延秒。</summary>
    [JsonPropertyName("delay_sec")]
    public double DelaySec { get; set; }

    /// <summary>このステップで描画する zones。複数同時着弾も表現可能。</summary>
    [JsonPropertyName("zones")]
    public List<StrategyAoeZone> Zones { get; set; } = new();

    /// <summary>各 zone の表示時間（秒）。null なら 3.0。</summary>
    [JsonPropertyName("duration_sec")]
    public double? DurationSec { get; set; }

    /// <summary>このステップ内 zones に共通する label。診断ログ用。</summary>
    [JsonPropertyName("label")]
    public string? Label { get; set; }
}

/// <summary>
/// 時間差 AoE 群を 1 つの mechanic に紐付けるシーケンス。
/// 例：絶アルテマ エクサフレア（0/2/4/6 秒の順次着弾）。
/// </summary>
public sealed class AoeSequence
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("steps")]
    public List<AoeSequenceStep> Steps { get; set; } = new();

    /// <summary>キャスト cancel 時に未発火ステップを破棄する（既定 true）。</summary>
    [JsonPropertyName("cancel_on_cast_cancel")]
    public bool CancelOnCastCancel { get; set; } = true;
}

/// <summary>
/// メカニクス用ミニマップに描画する AoE 形状。
/// 円 / ドーナツ / 扇形 / 矩形をユーザーが選択して配置。
/// </summary>
public sealed class StrategyAoeZone
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    /// <summary>"circle" / "donut" / "cone" / "rect" / "line"。</summary>
    [JsonPropertyName("shape")]
    public string Shape { get; set; } = "circle";

    /// <summary>中心の世界座標 X（アリーナ中心からの相対 m）。</summary>
    [JsonPropertyName("x")]
    public double X { get; set; }

    /// <summary>中心の世界座標 Z（同上）。</summary>
    [JsonPropertyName("z")]
    public double Z { get; set; }

    /// <summary>円・ドーナツ外径・扇半径・矩形長辺の長さ（m）。</summary>
    [JsonPropertyName("radius_m")]
    public double RadiusM { get; set; } = 5.0;

    /// <summary>ドーナツ内径（m）。shape=donut のときだけ使う。</summary>
    [JsonPropertyName("inner_radius_m")]
    public double? InnerRadiusM { get; set; }

    /// <summary>扇・矩形の中心方向（度数法、0=東、90=南、180=西、-90=北）。</summary>
    [JsonPropertyName("rotation_deg")]
    public double? RotationDeg { get; set; }

    /// <summary>扇の全角（度）。例：120 で 120° の扇形。</summary>
    [JsonPropertyName("fan_deg")]
    public double? FanDeg { get; set; }

    /// <summary>矩形の半幅（m）。中心から左右に伸びる。</summary>
    [JsonPropertyName("half_width_m")]
    public double? HalfWidthM { get; set; }

    /// <summary>
    /// anchor actor の hitbox 半径を outer radius/line length に足す。
    /// Splatoon の includeHitbox 相当。大型ボス中心 AoE の短すぎる表示を防ぐ。
    /// </summary>
    [JsonPropertyName("include_hitbox")]
    public bool IncludeHitbox { get; set; }

    /// <summary>
    /// rotation_source / rotation_deg の結果に加算する描画角度（度）。
    /// 0=東、90=南のミニマップ/描画系で指定する。
    /// </summary>
    [JsonPropertyName("rotation_offset_deg")]
    public double? RotationOffsetDeg { get; set; }

    /// <summary>"#RRGGBB"。未指定なら危険色（赤）。</summary>
    [JsonPropertyName("color")]
    public string? Color { get; set; }

    /// <summary>true=危険ゾーン（赤）、false=安置（緑）。既定 true。</summary>
    [JsonPropertyName("is_danger")]
    public bool IsDanger { get; set; } = true;

    /// <summary>
    /// 動的配置の基準。"static"（既定）/ "source_actor" / "matched_object" /
    /// "each_matched_object" / "waymark"。
    /// </summary>
    [JsonPropertyName("anchor")]
    public string? Anchor { get; set; }

    /// <summary>anchor=waymark のとき参照する A/B/C/D/1/2/3/4。</summary>
    [JsonPropertyName("anchor_waymark")]
    public string? AnchorWaymark { get; set; }

    /// <summary>
    /// anchor=matched_object / each_matched_object 用のアクターマッチャー。
    /// NPC ID / 名前 / DataId 等で対象アクターを絞り込む。
    /// 未指定なら trigger の SourceId 直指定として扱う。
    /// </summary>
    [JsonPropertyName("actor_matcher")]
    public ActorMatcher? ActorMatcher { get; set; }

    /// <summary>
    /// 表示するかどうかのリアルタイム判定（HP / 距離 / cast 中等）。
    /// 未指定なら常に true。
    /// </summary>
    [JsonPropertyName("state_filter")]
    public StateFilter? StateFilter { get; set; }

    /// <summary>
    /// AoE の向き（rotation）をどこから取るか。
    /// 未指定なら shape に応じた既定（cone/rect は CastSnapshot、その他は Live）。
    /// </summary>
    [JsonPropertyName("rotation_source")]
    public RotationSourceSpec? RotationSource { get; set; }

    /// <summary>
    /// 床塗りライブ描画する（ActorTrackedAoeService 経由）。既定 true。
    /// false の場合はミニマップのみで床塗りスキップ（俯瞰だけで十分なギミック用）。
    /// </summary>
    [JsonPropertyName("live_floor_paint")]
    public bool LiveFloorPaint { get; set; } = true;

    /// <summary>表示時間（秒）。null = ActionDefinition.Duration を継承。</summary>
    [JsonPropertyName("duration_sec")]
    public double? DurationSec { get; set; }

    /// <summary>
    /// true：この zone と同じ cast id の Lumina 自動推測 AoE を抑制する。
    /// 「Lumina の 90° cone がずれているので攻略タブで上書きしたい」用途。既定 false。
    /// </summary>
    [JsonPropertyName("suppress_auto_aoe")]
    public bool SuppressAutoAoe { get; set; } = false;
}

public sealed class StrategyPosition
{
    [JsonPropertyName("slot")]
    public string Slot { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("role")]
    public string? Role { get; set; }

    [JsonPropertyName("job")]
    public string? Job { get; set; }

    [JsonPropertyName("x")]
    public double X { get; set; }

    [JsonPropertyName("z")]
    public double Z { get; set; }

    [JsonPropertyName("color")]
    public string? Color { get; set; }

    [JsonPropertyName("note")]
    public string? Note { get; set; }
}

public sealed class MechanicStrategy
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("time")]
    public double? Time { get; set; }

    [JsonPropertyName("duration")]
    public double? Duration { get; set; }

    [JsonPropertyName("attached_to")]
    public MatchCondition? AttachedTo { get; set; }

    /// <summary>
    /// このメカニクスを発動させる条件のリスト（OR 結合）。複数列挙すれば
    /// どれか 1 つが成立した時点で発動する。空なら <see cref="AttachedTo"/> 単独
    /// にフォールバック。
    /// 例：「ボスが特定キャスト」OR「PT 内にデバフ X」OR「ボスが北を向いた」。
    /// </summary>
    [JsonPropertyName("triggers")]
    public List<MechanicTrigger> Triggers { get; set; } = new();

    /// <summary>
    /// 発動時の PT 散開ポジに対し「特定のバフ／デバフを持っている人だけ
    /// 強調色」をかける指定。例：「Tower 対象には黄色」「散開対象は白」。
    /// </summary>
    [JsonPropertyName("party_status_highlights")]
    public List<StatusHighlightSpec> PartyStatusHighlights { get; set; } = new();

    [JsonPropertyName("advance_warning_sec")]
    public double? AdvanceWarningSec { get; set; }

    [JsonPropertyName("role")]
    public string? Role { get; set; }

    [JsonPropertyName("job")]
    public string? Job { get; set; }

    [JsonPropertyName("warning_text")]
    public string? WarningText { get; set; }

    [JsonPropertyName("callout")]
    public string? Callout { get; set; }

    [JsonPropertyName("gimmick")]
    public string? Gimmick { get; set; }

    [JsonPropertyName("safe_zone")]
    public SafeZoneCalculation? SafeZone { get; set; }

    [JsonPropertyName("positions")]
    public List<string> Positions { get; set; } = new();

    /// <summary>
    /// このメカニクス専用の散開ポジション。指定があるとプロファイル全体ではなく
    /// このリストがミニマップにプロットされる。空ならプロファイル側 SpreadPositions
    /// を継承する。「ホリッドロアの散開」「ピザカット位置」などギミック単位で
    /// 立ち位置が変わる場合に使う。
    /// </summary>
    [JsonPropertyName("spread_positions")]
    public List<StrategyPosition> SpreadPositions { get; set; } = new();

    /// <summary>
    /// このメカニクスの瞬間にミニマップへ表示する「敵 / オブジェクト」マーカー。
    /// ボス位置・add NPC 位置・誘導目標などをユーザーが地図上にドラッグで置く。
    /// </summary>
    [JsonPropertyName("object_markers")]
    public List<StrategyObjectMarker> ObjectMarkers { get; set; } = new();

    /// <summary>
    /// このメカニクスの瞬間にミニマップへ描画する AoE 形状（円・ドーナツ・扇・矩形）。
    /// ユーザーが「ここにこういう範囲が出る」と直接描く。
    /// </summary>
    [JsonPropertyName("aoe_zones")]
    public List<StrategyAoeZone> AoeZones { get; set; } = new();

    /// <summary>
    /// このメカニクスが属する分岐 id（<see cref="TimelineBranch.Id"/> を参照）。
    /// null = どの分岐でも有効（共通ギミック）。null 以外の値が指定された分岐が
    /// <see cref="BranchStatus.Rejected"/> 状態のとき、このメカニクスはタイムライン
    /// 表示と発火対象から除外される。
    /// </summary>
    [JsonPropertyName("branch_id")]
    public string? BranchId { get; set; }

    /// <summary>
    /// 時間差連鎖 AoE。aoe_zones の単発に加え、ステップごとに遅延着弾を表現する。
    /// 例：エクサフレア（0/2/4/6 秒で順次）/ 雷電（複数連続着弾）。
    /// null なら連鎖無し。
    /// </summary>
    [JsonPropertyName("aoe_sequence")]
    public AoeSequence? AoeSequence { get; set; }

    /// <summary>
    /// このメカニクス専用のアリーナ形状。null ならプロファイル既定を継承。
    /// フェーズによって形が変わる（例：Phase1 円形 / Phase2 矩形）場合に上書きする。
    /// "circle" / "square" / "rect"。
    /// </summary>
    [JsonPropertyName("arena_shape")]
    public string? ArenaShape { get; set; }

    [JsonPropertyName("arena_radius")]
    public double? ArenaRadius { get; set; }

    [JsonPropertyName("arena_width")]
    public double? ArenaWidth { get; set; }

    [JsonPropertyName("arena_depth")]
    public double? ArenaDepth { get; set; }

    /// <summary>このメカニクス専用のアリーナ中心 X（世界座標）。null ならフェーズ → プロファイル既定を継承。</summary>
    [JsonPropertyName("arena_center_x")]
    public double? ArenaCenterX { get; set; }

    /// <summary>このメカニクス専用のアリーナ中心 Z（世界座標）。null ならフェーズ → プロファイル既定を継承。</summary>
    [JsonPropertyName("arena_center_z")]
    public double? ArenaCenterZ { get; set; }

    /// <summary>
    /// フェーズ名／フェーズ ID。同じ名前のメカニクスをグルーピングする目的。
    /// 例："P1" / "P2" / "中間フェーズ" / "終焉"。空ならグルーピング無し。
    /// </summary>
    [JsonPropertyName("phase")]
    public string? Phase { get; set; }

    /// <summary>
    /// true：このギミックではミニマップを描画しない（TTS / オーバーレイ通知のみ）。
    /// 「タンクスワップ」「軽減使う」など視覚的補助が要らないギミック向け。既定 false。
    /// </summary>
    [JsonPropertyName("disable_minimap")]
    public bool DisableMinimap { get; set; }

    [JsonPropertyName("color")]
    public string? Color { get; set; }

    [JsonPropertyName("source_event_type")]
    public string? SourceEventType { get; set; }

    [JsonPropertyName("observed_count")]
    public int? ObservedCount { get; set; }

    [JsonPropertyName("occurrence_seen_count")]
    public int? OccurrenceSeenCount { get; set; }

    [JsonPropertyName("confidence")]
    public double? Confidence { get; set; }

    [JsonPropertyName("time_jitter_seconds")]
    public double? TimeJitterSeconds { get; set; }

    [JsonPropertyName("occurrence_index")]
    public int? OccurrenceIndex { get; set; }
}

/// <summary>
/// 戦闘相対秒で配置するメモ。LiveTimeline に表示し、必要なら advance_warning_sec
/// 前に TTS / オーバーレイで通知する。
/// </summary>
public sealed class TimelineNote
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>戦闘相対秒。</summary>
    [JsonPropertyName("time")]
    public double Time { get; set; }

    /// <summary>ライブタイムラインで表示する短文。例：「迅速 + 堅実」。</summary>
    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    /// <summary>長尺ノート（例：軽減効果時間）。指定があるとバー風に幅を持たせて描画。</summary>
    [JsonPropertyName("duration")]
    public double? Duration { get; set; }

    /// <summary>このノートを表示／通知するロール。空なら全員。
    /// 値は target_resolver の表記に倣う：tank/mt/st/healer/h1/h2/dps/melee/ranged/caster/any。</summary>
    [JsonPropertyName("role")]
    public string? Role { get; set; }

    /// <summary>このノートを表示／通知するジョブ略称（"PLD" 等）。空なら全ジョブ。</summary>
    [JsonPropertyName("job")]
    public string? Job { get; set; }

    /// <summary>"#RRGGBB"。タイムライン上の色付け。</summary>
    [JsonPropertyName("color")]
    public string? Color { get; set; }

    /// <summary>
    /// 時刻の代わりに、特定イベントに紐付ける場合のマッチ条件。
    /// ここが指定されていれば Time は無視され、対象イベントが過去に観測された
    /// 相対秒（録画から取得）にノートを配置する。
    /// 例：｛type:cast_start, cast_id:0x189E｝でホリッドロアの瞬間にノートを表示。
    /// </summary>
    [JsonPropertyName("attached_to")]
    public MatchCondition? AttachedTo { get; set; }

    /// <summary>表示アイコン。絵文字（"🛡"）または画像 URL を文字列単独 or 配列で指定。
    /// 配列にすると複数アイコンを横並びで描画する（例：ランパート + ブラインド）。</summary>
    [JsonPropertyName("icon")]
    [JsonConverter(typeof(StringOrStringArrayJsonConverter))]
    public List<string> Icons { get; set; } = new();

    /// <summary>このノートの time の何秒前に TTS / overlay で先行通知するか。
    /// 0 や null なら通知しない（タイムライン表示のみ）。</summary>
    [JsonPropertyName("advance_warning_sec")]
    public double? AdvanceWarningSec { get; set; }

    /// <summary>通知時に読み上げるテキスト。空なら label を使う。</summary>
    [JsonPropertyName("warning_text")]
    public string? WarningText { get; set; }

    /// <summary>
    /// AttachedTo で複数回出現するイベントに紐付くとき、何回目の出現に配置するか（0 始まり）。
    /// Time 未指定（=0）の mechanic 由来ノートで「後半フェーズの 2 回目のキャスト」等を正しく選ぶ。
    /// </summary>
    [JsonPropertyName("occurrence_index")]
    public int OccurrenceIndex { get; set; }
}

public sealed class AutoSettings
{
    public const double DefaultPredictAdvanceWarningSec = 12.0;

    [JsonPropertyName("enable_triggers")]
    public bool EnableTriggers { get; set; } = false;

    [JsonPropertyName("auto_record")]
    public bool AutoRecord { get; set; } = false;

    [JsonPropertyName("show_timeline")]
    public bool ShowTimeline { get; set; } = false;

    /// <summary>
    /// 録画から予測した未来キャストに対して、何秒前に TTS / overlay で
    /// 自動 advance warning を出すか。0 や null なら無効（デフォルト無効）。
    /// 例: 5 にすると「キャスト開始の 5 秒前に『次：〇〇』と読み上げ」する。
    /// </summary>
    [JsonPropertyName("predict_advance_warning_sec")]
    public double? PredictAdvanceWarningSec { get; set; } = DefaultPredictAdvanceWarningSec;

    /// <summary>
    /// 予測された未来キャストをライブタイムラインに描画するかどうか（デフォルト true）。
    /// false にすると HUD 描画コストを減らせる。
    /// </summary>
    [JsonPropertyName("show_predicted_casts")]
    public bool ShowPredictedCasts { get; set; } = true;

    /// <summary>
    /// 敵のキャストに対し、Lumina Action の EffectRange / CastType から推測した
    /// AoE 形状をミニマップに自動描画する。**既定 ON**：プラグインの主目的が「自動表示」なので。
    /// </summary>
    [JsonPropertyName("show_auto_telegraphs")]
    public bool ShowAutoTelegraphs { get; set; } = true;

    /// <summary>
    /// Lumina に AoE 情報が無い敵キャストをデバッグ用に拾うための設定。
    /// 不正確な 10m 円フォールバックは出さない。
    /// </summary>
    [JsonPropertyName("show_all_enemy_casts")]
    public bool ShowAllEnemyCasts { get; set; } = false;

    [JsonPropertyName("show_auto_attacks")]
    public bool ShowAutoAttacks { get; set; } = true;

    /// <summary>
    /// 床面に貼り付く Splatoon 風 AoE 床塗り（<see cref="FfxivEchoes.Triggers.ActorTrackedAoeService"/>
    /// 経由）を表示するかどうか。**既定 ON**。
    /// このフラグが false でもミニマップ表示（<see cref="ShowAutoTelegraphs"/>）は独立に動作する。
    /// 「画面が AoE で埋まりすぎる」ユーザー向けに分離。
    /// </summary>
    [JsonPropertyName("show_floor_paint")]
    public bool ShowFloorPaint { get; set; } = true;
}

public sealed class TriggerFileMetadata
{
    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAt { get; set; }

    [JsonPropertyName("last_modified")]
    public DateTimeOffset? LastModified { get; set; }

    [JsonPropertyName("author")]
    public string? Author { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
}

public sealed class SyncPoint
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("cast_id")]
    public string? CastId { get; set; }

    [JsonPropertyName("status_id")]
    public uint? StatusId { get; set; }

    [JsonPropertyName("action_id")]
    public string? ActionId { get; set; }

    [JsonPropertyName("expected_time")]
    public double ExpectedTime { get; set; }

    [JsonPropertyName("tolerance")]
    public double Tolerance { get; set; } = 5.0;

    /// <summary>
    /// この sync_point を観測したら開始するフェーズ名（<see cref="MechanicStrategy.Phase"/> と対応）。
    /// 設定すると、このキャスト観測時に PhaseTransitionedEvent が発行され、以降は前フェーズの
    /// ギミックがタイムライン / 読み上げから除外される。null（既定）ならフェーズ境界としては扱わない。
    /// </summary>
    [JsonPropertyName("phase")]
    public string? Phase { get; set; }
}

public sealed class IgnoredEvent
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("cast_id")]
    public string? CastId { get; set; }

    [JsonPropertyName("status_id")]
    public uint? StatusId { get; set; }

    [JsonPropertyName("action_id")]
    public string? ActionId { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}
