// このファイルは撤去対象。ユーザー要望「キャストバー / 戦闘時間カウント / 横スクロール式
// タイムラインは不要」に伴い機能を完全に外しました。Plugin.cs からの参照も全て削除済み。
// 空クラスとしてのみ残してあるのはファイル削除権限が無いビルド環境のため。
//
// 機能後継：
//   - 縦リスト式タイムライン → UpcomingEventsWindow（cactbot 風縮むバー）
//   - キャスト進行表示 → FFXIV ネイティブの cast bar で代替
//   - 戦闘時間カウント → 不要

using System;

namespace FfxivEchoes.Windows;

[Obsolete("撤去済み。UpcomingEventsWindow に統合")]
internal sealed class LiveTimelineWindow : IDisposable
{
    public void Dispose() { }
}
