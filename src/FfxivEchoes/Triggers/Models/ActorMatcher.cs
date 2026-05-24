using System.Text.Json.Serialization;

namespace FfxivEchoes.Triggers.Models;

/// <summary>
/// Splatoon の refActor* 系列に相当するアクターマッチャー。
/// NPC ID / 名前 / ModelID / DataID / VFX パス / ObjectEffect 引数で
/// 「対象となる actor」を絞り込むための宣言的フィルタ。
/// 全フィールド null なら「無条件マッチ」。複数指定時は AND 評価。
/// </summary>
/// <remarks>
/// 実 actor の解決と紐付けは <c>ActorTrackedAoeService</c> が担当する。
/// このクラスは JSON シリアライズ用の純粋データ表現に徹する。
/// </remarks>
public sealed class ActorMatcher
{
    /// <summary>表示名。完全一致 or <see cref="NameMatch"/> で挙動を変える。</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>名前マッチ方式：<c>exact</c>（既定）/ <c>contains</c> / <c>startswith</c> / <c>regex</c>。</summary>
    [JsonPropertyName("name_match")]
    public string? NameMatch { get; set; }

    /// <summary>BNpcName (Lumina) 行 ID。種族レベルで識別したい場合に使う。</summary>
    [JsonPropertyName("npc_name_id")]
    public uint? NpcNameId { get; set; }

    /// <summary>BNpcBase (Lumina) 行 ID。同名でもボスと add を区別する用途で。</summary>
    [JsonPropertyName("npc_base_id")]
    public uint? NpcBaseId { get; set; }

    /// <summary>ModelChara ID（外見）。同 NPC で形態違いを切り分ける場合。</summary>
    [JsonPropertyName("model_chara_id")]
    public uint? ModelCharaId { get; set; }

    /// <summary>DataId（インスタンス内の actor type id）。</summary>
    [JsonPropertyName("data_id")]
    public uint? DataId { get; set; }

    /// <summary>ObjectId（生きているインスタンス ID、動的）。1 体直指定したいときのみ。</summary>
    [JsonPropertyName("object_id")]
    public uint? ObjectId { get; set; }

    /// <summary>直近で取得した VFX パス（VfxNew イベント由来）。</summary>
    [JsonPropertyName("vfx_path")]
    public string? VfxPath { get; set; }

    /// <summary>ObjectEffect の引数ペア（Splatoon の ObjectEffectData1/2 と同義）。</summary>
    [JsonPropertyName("object_effect")]
    public ObjectEffectSpec? ObjectEffect { get; set; }

    /// <summary>HP &gt; 0 の actor のみ対象（既定 true）。死体に AoE を貼り続けない安全弁。</summary>
    [JsonPropertyName("alive_only")]
    public bool AliveOnly { get; set; } = true;

    /// <summary>ターゲッタブルな actor のみ対象（既定 false）。</summary>
    [JsonPropertyName("targetable_only")]
    public bool TargetableOnly { get; set; } = false;

    /// <summary>
    /// 該当する全 actor に同じ AoE を貼る（既定 true）。
    /// false なら最初の 1 体のみ。「左右翼ボス両方に同じテレグラフ」用途では true。
    /// </summary>
    [JsonPropertyName("match_all")]
    public bool MatchAll { get; set; } = true;
}

/// <summary>
/// ObjectEffect の data1 / data2 ペア。null フィールドは無条件で通る。
/// </summary>
public sealed class ObjectEffectSpec
{
    [JsonPropertyName("data1")]
    public uint? Data1 { get; set; }

    [JsonPropertyName("data2")]
    public uint? Data2 { get; set; }
}
