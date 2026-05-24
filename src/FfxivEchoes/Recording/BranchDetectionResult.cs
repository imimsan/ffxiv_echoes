using System.Collections.Generic;

namespace FfxivEchoes.Recording;

/// <summary>
/// 録画ファイル群を「最初の cast」でクラスタリングした結果。
/// <see cref="RecordingBranchAnalyzer.Analyze"/> の出力。
/// </summary>
/// <remarks>
/// JSON シリアライズしない（UI プレビュー用のオンメモリ表現）。
/// 永続化対象は確定後に <see cref="FfxivEchoes.Triggers.Models.TimelineBranch"/> に変換して
/// <see cref="FfxivEchoes.Triggers.Models.TriggerFile.Branches"/> へ書き込む。
/// </remarks>
public sealed class BranchDetectionResult
{
    /// <summary>解析対象の録画ファイル数（先頭 cast を読めたファイル）。</summary>
    public int TotalFilesScanned { get; init; }

    /// <summary>true: ファイルが 0 / 1 件で分岐検出に必要な量に達していない。</summary>
    public bool NotEnoughData { get; init; }

    /// <summary>true: 全ファイルが同じ最初のキャストで、分岐は無いと判定。</summary>
    public bool NoBranchDetected { get; init; }

    /// <summary>UI に出すユーザー向け診断メッセージ（日本語）。</summary>
    public string? DiagnosticsMessage { get; init; }

    /// <summary>主要な分岐グループ（8 分岐級のギミックまで UI で選択可能）。</summary>
    public IReadOnlyList<BranchGroup> Groups { get; init; } = System.Array.Empty<BranchGroup>();

    /// <summary>1 ファイルのみの「外れ値」グループ。<see cref="Groups"/> には含めない。</summary>
    public IReadOnlyList<BranchGroup> OutlierGroups { get; init; } = System.Array.Empty<BranchGroup>();

    /// <summary>9 種類以上のパターンが検出された場合の、主要グループに含まれない残り。</summary>
    public IReadOnlyList<BranchGroup> AdditionalGroups { get; init; } = System.Array.Empty<BranchGroup>();
}

/// <summary>
/// 1 つの分岐グループ。最初のキャストでクラスタ化されたファイル群。
/// </summary>
public sealed class BranchGroup
{
    /// <summary>このグループの判定キャスト ID（"0xABCD" 表記）。共通初手の後に分岐する場合は、その分岐キャスト。</summary>
    public string FirstCastId { get; init; } = string.Empty;

    /// <summary>判定キャストの表示名（"エアロジャ" 等）。</summary>
    public string FirstCastName { get; init; } = string.Empty;

    /// <summary>所属ファイル数。</summary>
    public int FileCount { get; init; }

    /// <summary>所属ファイルの絶対パス。</summary>
    public IReadOnlyList<string> FilePaths { get; init; } = System.Array.Empty<string>();

    /// <summary>有効ファイル総数に対する比率（0.0–1.0）。UI 表示用。</summary>
    public double Confidence { get; init; }

    /// <summary>
    /// このグループのファイル群の集約結果。Apply 時に branch_id 自動付与の照合元として使う。
    /// 検出時は遅延ロード（null）でも OK。Apply 直前に <see cref="RecordingAggregationReader.AggregateFiles"/>
    /// を呼ぶ呼び元を許容する設計。
    /// </summary>
    public AggregatedEvents? AggregatedEvents { get; init; }
}
