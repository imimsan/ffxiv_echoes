using System;
using System.Collections.Concurrent;
using Dalamud.Plugin.Services;

namespace FfxivEchoes.Diagnostics;

/// <summary>
/// <see cref="IFramework.Update"/> など毎フレーム実行されるハンドラで発生した例外を、
/// ログを氾濫させずに記録するためのスロットル。
/// </summary>
/// <remarks>
/// 例外が毎フレーム連続発生しても、コンテキストごとに一定間隔で 1 回だけログを出し、
/// その間に抑制した件数を集計して付記する。Dalamud のフレームループに例外が素通りすると
/// Dalamud 側が毎フレーム error ログを吐き、dalamud.log が肥大化してプラグイン全体が重く
/// なる（絶コンテンツでの体感悪化の主因）。各ハンドラをこのスロットル付き try/catch で
/// 囲むことで「クラッシュ素通り」と「ログ氾濫による重さ」を同時に防ぐ。
/// 状態はコンテキスト文字列をキーに静的に保持するため、各ハンドラ側にフィールドを増やさず
/// 1 行で導入できる。フレームスレッドと Reloaded ワーカースレッドからの同時呼び出しに備えて
/// 状態更新は <see cref="State"/> 単位で lock する（happy path には影響しない）。
/// </remarks>
public static class FrameErrorThrottle
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
    private static readonly ConcurrentDictionary<string, State> States = new();

    /// <summary>
    /// 例外を抑制付きで記録する。直近 <see cref="Interval"/> 内の同一コンテキストの追加例外は
    /// ログを出さず件数のみ数える。
    /// </summary>
    public static void Report(IPluginLog log, Exception ex, string context)
    {
        var state = States.GetOrAdd(context, static _ => new State());
        lock (state)
        {
            var now = DateTimeOffset.UtcNow;
            if (now < state.NextLogAt)
            {
                state.Suppressed++;
                return;
            }

            int suppressed = state.Suppressed;
            state.Suppressed = 0;
            state.NextLogAt = now + Interval;

            if (suppressed > 0)
            {
                log.Error(
                    ex,
                    "[FfxivEchoes] {Context} で例外（直近{Sec:0}秒で追加{Count}件を抑制）",
                    context, Interval.TotalSeconds, suppressed);
            }
            else
            {
                log.Error(ex, "[FfxivEchoes] {Context} で例外", context);
            }
        }
    }

    private sealed class State
    {
        public DateTimeOffset NextLogAt = DateTimeOffset.MinValue;
        public int Suppressed;
    }
}
