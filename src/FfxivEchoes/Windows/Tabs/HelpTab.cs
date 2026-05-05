using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace FfxivEchoes.Windows.Tabs;

/// <summary>
/// 「使い方」タブ。基本フローと用語の解説をプラグイン内で完結させる。
/// </summary>
public sealed class HelpTab : ITab
{
    public string Title => "使い方";
    public string Id => "help";

    public void Draw()
    {
        if (ImGui.BeginChild("##help-scroll", Vector2.Zero, true))
        {
            ImGui.PushTextWrapPos(0f);

            Header("FFXIV Echoes — 基本フロー");
            ImGui.TextWrapped(
                "1. /echoes record on で録画モードを ON にする\n" +
                "2. 対象コンテンツに突入してボスを 1 周回す\n" +
                "3. プラグイン UI（/echoes）→「コンテンツ一覧」にゾーンが現れる → 「編集」\n" +
                "4. 「集計（観測イベント）」タブで観測されたキャストの中から欲しいものを選び\n" +
                "    「このイベントからトリガー作成」ボタンで自動生成\n" +
                "5. 必要ならアクション（音声・テキスト・俯瞰図など）を追加して保存\n" +
                "6. 同じコンテンツに再突入 → ボスが該当キャストを始めると発動");
            ImGui.Spacing();

            Header("便利機能（録画さえあれば自動）");
            Bullet("予測アドバンス警告：「ファイル設定」タブで ON にすると、" +
                  "ボスのキャスト N 秒前に「次、〇〇」と TTS で先行通知。");
            Bullet("ライブタイムライン：戦闘中に下部に出る。録画から拾った未来キャストを" +
                  "右側に点線で予測表示。同期オフセット（フェーズずれ）にも追従。");
            Bullet("「次に来るイベント」HUD：戦闘中に画面のどこかに縦並びリストで予測表示。" +
                  "残り 5 秒以内のイベントは黄色く強調。");
            Bullet("自動 AoE テレグラフ：ファイル設定で ON にすると、敵キャストの" +
                  "AoE 範囲を Lumina データから自動推測してフィールドに薄い赤円を描画。");
            ImGui.Spacing();

            Header("ノート機能（軽減・LB 等のメモ）");
            ImGui.TextWrapped(
                "「ノート」タブで「ここで軽減」「ここで LB」などを書いておけます。" +
                "時刻は 2 通りの指定方法：");
            Bullet("📎 特定キャストに紐付け：ノートのラベル横の 📎 ボタンで録画キャストを選ぶと、" +
                  "そのキャスト発生時刻にノートが自動配置されます。時刻を手で書く必要なし。");
            Bullet("時刻直接入力：「30s」など秒数で入力。");
            ImGui.Spacing();
            Bullet("「録画キャストから一括追加」で、複数キャストにまとめて紐付けノートを生成可能。");
            Bullet("advance_warning_sec を設定すると、その秒数前に TTS / オーバーレイで通知。");
            ImGui.Spacing();

            Header("アクション種類（よく使う順）");
            BulletKV("音声読み上げ (TTS)", "指定文字を SAPI5 で読み上げ。最も基本");
            BulletKV("中央テキスト (overlay_text)", "画面中央に大型テキストを N 秒表示");
            BulletKV("タイマーバー (timer_bar)", "画面右下にカウントダウンバー（デバフ残時間など）");
            BulletKV("俯瞰アリーナ図 (arena_view)", "ミニマップ風のギミック表示（外周回避/散開/集合等）");
            BulletKV("方位読み上げ (direction_call)", "計算した安置の方向（北/南）を TTS");
            BulletKV("矢印表示 (screen_arrow)", "画面上に安置への矢印を描画");
            BulletKV("フィールド円 (field_marker)", "実際の地面に円・四角・×印を描画");
            BulletKV("距離音 (proximity_feedback)", "安置との距離を音で表現（ビーコン）");
            BulletKV("別トリガー連鎖発動 (chain_trigger)", "あるトリガーから別のトリガーを発動");
            ImGui.Spacing();

            Header("ファイル設定（ゾーンごと）");
            BulletKV("enable_triggers", "突入時に自動でトリガーを有効化（基本 ON）");
            BulletKV("show_timeline", "戦闘中にライブタイムライン HUD を表示");
            BulletKV("show_predicted_casts", "ライブタイムラインに録画ベース予測を描画");
            BulletKV("show_auto_telegraphs", "敵キャストの AoE 範囲を自動でフィールドに描画");
            BulletKV("予測アドバンス警告", "ボスキャスト N 秒前に「次、〇〇」と TTS 通知");
            BulletKV("auto_record", "突入時に自動で録画を開始");
            ImGui.Spacing();

            Header("全体設定");
            BulletKV("デバッグモード", "全イベントをチャットに出す（cast_id 等を確認したい時）");
            BulletKV("トリガー発火をチャットに表示", "発火した瞬間にチャットへ「Trigger: 名前」を出す。動作確認用");
            BulletKV("TTS のみのトリガーで自動オーバーレイ", "音声だけ設定したトリガーでも、" +
                                                            "画面中央に同じ文字を 3 秒表示する（デフォルト ON）");
            ImGui.Spacing();

            Header("録画と同期について");
            ImGui.TextWrapped(
                "本プラグインは「過去の録画から学んで予測する」モデル。1 周の録画があれば、" +
                "次回以降は cast_id ベースで自動的に未来のキャストを予測・通知します。\n" +
                "討伐タイムやフェーズスキップで時刻がずれても、SyncOffsetTracker が" +
                "自動で +X 秒のオフセットを計算し、すべての予測・ノート・アドバンス警告に反映します。" +
                "ライブタイムライン右上に「SYNC: +1.2s」のように表示されたら、それが現在のオフセットです。");
            ImGui.Spacing();

            Header("コマンド");
            BulletKV("/echoes", "設定画面を開く");
            BulletKV("/echoes record on / off / auto", "録画モードを切替");
            BulletKV("/echoes debug on / off", "デバッグモードを切替");
            BulletKV("/echoes timeline all / configured / hidden", "ライブタイムラインの表示モードを切替");
            BulletKV("/echoes reload", "トリガー定義を再読込");
            BulletKV("/echoes help", "コマンド一覧");
            ImGui.Spacing();

            Header("困ったとき");
            ImGui.TextWrapped(
                "・トリガーが発動しない → 「ライブイベント」タブで該当キャストが流れているか確認\n" +
                "・cast_id が分からない → 録画してから「集計」タブで観察\n" +
                "・オーバーレイが見えない → 「全体設定」の「TTS のみのトリガーで自動オーバーレイ」が ON か確認\n" +
                "・予測がずれる → ライブタイムライン右上に SYNC: +Xs が出ていれば自動補正中。\n" +
                "  オフセットが急に変わるのはフェーズ移行が同期点として機能しているサイン");

            ImGui.PopTextWrapPos();
        }
        ImGui.EndChild();
    }

    private static void Header(string text)
    {
        ImGui.Spacing();
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.85f, 0.4f, 1f));
        ImGui.TextUnformatted("■ " + text);
        ImGui.PopStyleColor();
        ImGui.Separator();
        ImGui.Spacing();
    }

    private static void Bullet(string text)
    {
        ImGui.Bullet();
        ImGui.SameLine(0, 0);
        ImGui.TextWrapped(text);
    }

    private static void BulletKV(string key, string value)
    {
        ImGui.Bullet();
        ImGui.SameLine(0, 0);
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.6f, 0.85f, 1f, 1f));
        ImGui.TextUnformatted(key);
        ImGui.PopStyleColor();
        ImGui.SameLine();
        ImGui.TextWrapped("— " + value);
    }
}
