namespace FfxivEchoes.Recording;

/// <summary>
/// 録画モード（SPEC.md §9.1）。
/// </summary>
/// <remarks>
/// プラグインリロード／ゲーム再起動時は <see cref="Auto"/> にリセットされる。
/// 永続化はしない（コンテンツ単位の <c>auto_record</c> は将来 M5/M8 でトリガー定義側に持つ）。
/// </remarks>
public enum RecordingMode
{
    /// <summary>コンテンツ別の auto_record 設定に従う（M4 では常に false にフォールバック）。</summary>
    Auto,

    /// <summary>記録 ON を強制。</summary>
    ForceOn,

    /// <summary>記録 OFF を強制。</summary>
    ForceOff,
}
