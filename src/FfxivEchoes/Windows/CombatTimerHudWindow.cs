// このファイルは撤去対象。ユーザー要望「戦闘時間カウントは不要」に伴い機能停止。

using System;

namespace FfxivEchoes.Windows;

[Obsolete("撤去済み。経過時間表示は FFXIV ネイティブ UI で代替")]
internal sealed class CombatTimerHudWindow : IDisposable
{
    public void Dispose() { }
}
