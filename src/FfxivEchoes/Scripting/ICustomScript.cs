using FfxivEchoes.Events;

namespace FfxivEchoes.Scripting;

/// <summary>
/// カスタムスクリプトが実装するインターフェイス。
/// 外部 .cs / .dll を動的ロードして拡張ポイントとして使うための土台（P5 スタブ）。
/// </summary>
/// <remarks>
/// 本格的な動的読み込み（Roslyn コンパイル / アセンブリロード）は安全性検討が
/// 必要なため P5 スタブでは未実装。本インターフェイスを満たす型を将来
/// アセンブリ参照経由で組み込めるよう、表面 API のみを定義する。
/// </remarks>
public interface ICustomScript
{
    /// <summary>スクリプトの一意 ID（ログ・設定参照用）。</summary>
    string Id { get; }

    /// <summary>プラグインロード時に 1 回だけ呼ばれる。</summary>
    void Initialize(ScriptContext context);

    /// <summary>イベントバスから受信した各イベントが流れてくる。重い処理は禁止。</summary>
    void OnEvent(IGameEvent ev);

    /// <summary>プラグインアンロード時に 1 回だけ呼ばれる。</summary>
    void Dispose();
}
