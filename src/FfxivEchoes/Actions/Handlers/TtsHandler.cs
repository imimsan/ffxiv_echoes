using System;
using System.Collections.Generic;
using System.IO;
using System.Speech.Synthesis;
using System.Threading;
using Dalamud.Plugin.Services;
using FfxivEchoes.Events;
using FfxivEchoes.Triggers.Models;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace FfxivEchoes.Actions.Handlers;

/// <summary>
/// SAPI5（System.Speech.Synthesis）でテキスト読み上げを行う。
/// SAPI の音量上限（100%）を超えてさらに増幅したい場合は NAudio 経由で再生する。
/// </summary>
/// <remarks>
/// 発話は単一のワーカースレッドで <b>順次</b> 再生する（FIFO キュー）。
/// 旧実装は新規発話が来るたび <c>SpeakAsyncCancelAll</c>/<c>previous.Stop()</c> で
/// 直前の発話を無条件に打ち切っていたため、2 体同時詠唱や連続詠唱で「最後の発話だけが鳴り、
/// 先に出た重要コール（軽減/安置）が途中で消える」事故が起きていた。これを廃し、
/// 再生中は次をキューに積んで読み終えてから再生する。陳腐化した発話（積まれてから
/// <see cref="MaxStaleSeconds"/> 超過）は実戦で既に手遅れなのでドロップし、重要コールの遅延を防ぐ。
/// 加えて入口で短窓 dedup を行い、direction_call / proximity_feedback がハンドラを直呼びして
/// dispatcher の dedup を素通りする経路でも「北—北—北」のスタッターを抑える。
/// </remarks>
public sealed class TtsHandler : IActionHandler, IDisposable
{
    public string Type => "tts";

    /// <summary>同一テキストの連続発話を抑える dedup 窓（秒）。dispatcher の 3 秒より短く、正当な二度読みは通す。</summary>
    public const double DedupWindowSeconds = 0.5;

    /// <summary>キューに積まれてからこの秒数を超えた発話はドロップする（手遅れの読み上げで重要コールを遅らせない）。</summary>
    public const double MaxStaleSeconds = 2.5;

    /// <summary>キュー上限。超過時は最古を捨てる。</summary>
    private const int MaxQueue = 6;

    private readonly Configuration _configuration;
    private readonly IPluginLog _log;
    private readonly SpeechSynthesizer _synthesizer;
    private readonly object _gate = new();

    // 発話キュー（単一ワーカーで順次処理）。優先度付き挿入のため LinkedList を使う。
    private readonly object _queueGate = new();
    private readonly LinkedList<SpeechRequest> _queue = new();
    private readonly Thread _worker;
    private volatile bool _disposed;

    // 入口 dedup 履歴（短窓）。窓が極短なので戦闘跨ぎのクリアは不要。
    private readonly object _dedupGate = new();
    private readonly Dictionary<string, DateTimeOffset> _recentSpeech = new(StringComparer.Ordinal);

    // 現在再生中の NAudio プレイヤー。Dispose で確実に停止するために追跡する。
    private WaveOutEvent? _currentNAudioPlayer;

    public TtsHandler(Configuration configuration, IPluginLog log)
    {
        _configuration = configuration;
        _log = log;
        _synthesizer = new SpeechSynthesizer();
        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "FfxivEchoes-TTS",
        };
        _worker.Start();
    }

    public void Execute(ActionDefinition action, TriggerFiredEvent context)
    {
        if (string.IsNullOrEmpty(action.Text))
        {
            return;
        }

        var volume = Math.Clamp(
            _configuration.MasterVolume * _configuration.TtsVolume * action.Volume,
            0.0, 1.0);
        if (volume <= 0)
        {
            return;
        }

        // 入口 dedup：同一テキストが極短時間に連発するのを抑制（直呼び経路を含めて一元化）。
        var now = DateTimeOffset.UtcNow;
        bool speak;
        lock (_dedupGate)
        {
            speak = ShouldSpeakByDedup(action.Text, now, _recentSpeech, DedupWindowSeconds);
        }
        if (!speak)
        {
            return;
        }

        // ブースト倍率（1.0〜5.0）。SAPI の上限を超えて NAudio で増幅する。
        var boost = Math.Clamp(_configuration.TtsBoost, 1.0f, 5.0f);
        var useNAudio = boost > 1.001f;

        var request = new SpeechRequest(
            Text: action.Text!,
            Voice: !string.IsNullOrEmpty(action.Voice) ? action.Voice : _configuration.DefaultVoice,
            Rate: ClampRate(action.Rate),
            Volume: volume,
            Boost: boost,
            UseNAudio: useNAudio,
            EnqueuedAt: now,
            Priority: action.Priority);

        Enqueue(request);
    }

    /// <summary>
    /// 同一テキストの連続発話を抑制する（窓は初回起点）。テキスト未指定は常に通す。
    /// dispatcher の dedup（trigger 入口）と異なり、ハンドラ直呼び経路（direction_call 等）も覆う。
    /// </summary>
    public static bool ShouldSpeakByDedup(
        string? text, DateTimeOffset now,
        Dictionary<string, DateTimeOffset> recent, double windowSec)
    {
        if (string.IsNullOrEmpty(text))
        {
            return true;
        }
        if (recent.TryGetValue(text, out var last) && (now - last).TotalSeconds < windowSec)
        {
            return false;
        }
        recent[text] = now;
        // 履歴が肥大しないよう、窓を大きく超えた古いキーは間引く。
        if (recent.Count > 64)
        {
            PruneStale(recent, now, windowSec * 4);
        }
        return true;
    }

    private static void PruneStale(Dictionary<string, DateTimeOffset> recent, DateTimeOffset now, double olderThanSec)
    {
        var stale = new List<string>();
        foreach (var kv in recent)
        {
            if ((now - kv.Value).TotalSeconds > olderThanSec)
            {
                stale.Add(kv.Key);
            }
        }
        foreach (var key in stale)
        {
            recent.Remove(key);
        }
    }

    /// <summary>積まれてから <paramref name="maxStaleSec"/> 超過の発話は手遅れとしてドロップ判定。</summary>
    public static bool IsStale(DateTimeOffset enqueuedAt, DateTimeOffset now, double maxStaleSec)
        => (now - enqueuedAt).TotalSeconds > maxStaleSec;

    private void Enqueue(SpeechRequest request)
    {
        lock (_queueGate)
        {
            EnqueueByPriority(_queue, request, MaxQueue);
            Monitor.Pulse(_queueGate);
        }
    }

    /// <summary>
    /// 優先度を考慮してキューへ挿入する（ワーカーは先頭から取り出す）。
    /// 優先コール（Priority&gt;0）は通常コール群より前・既存優先コール群の後ろ（優先度内 FIFO）に挿す。
    /// 溢れ時（maxQueue 超過）は通常コールの最古から落とし、優先コールは可能な限り守る。
    /// スレッド非依存の純ロジックなのでテスト可能。呼び出し側で _queueGate を保持していること。
    /// </summary>
    public static void EnqueueByPriority(LinkedList<SpeechRequest> queue, SpeechRequest request, int maxQueue)
    {
        if (request.Priority > 0)
        {
            // 先頭から優先ノードを読み飛ばし、最初の通常ノードの前へ挿す。
            var node = queue.First;
            while (node is not null && node.Value.Priority > 0)
            {
                node = node.Next;
            }
            if (node is null)
            {
                queue.AddLast(request);
            }
            else
            {
                queue.AddBefore(node, request);
            }
        }
        else
        {
            queue.AddLast(request);
        }

        // 溢れ処理：最古（先頭側）の通常コールから落とす。新しいコールほど現在の盤面を反映するため
        // 古い通常コールを優先的に捨て、優先コールは可能な限り守る。全て優先なら末尾を落とす。
        while (queue.Count > maxQueue)
        {
            var victim = FirstNormalNode(queue) ?? queue.Last;
            if (victim is null)
            {
                break;
            }
            queue.Remove(victim);
        }
    }

    private static LinkedListNode<SpeechRequest>? FirstNormalNode(LinkedList<SpeechRequest> queue)
    {
        for (var node = queue.First; node is not null; node = node.Next)
        {
            if (node.Value.Priority <= 0)
            {
                return node;
            }
        }
        return null;
    }

    private void WorkerLoop()
    {
        while (!_disposed)
        {
            SpeechRequest request;
            lock (_queueGate)
            {
                while (_queue.Count == 0 && !_disposed)
                {
                    Monitor.Wait(_queueGate);
                }
                if (_disposed)
                {
                    return;
                }
                request = _queue.First!.Value;
                _queue.RemoveFirst();
            }

            if (IsStale(request.EnqueuedAt, DateTimeOffset.UtcNow, MaxStaleSeconds))
            {
                continue;
            }

            try
            {
                if (request.UseNAudio)
                {
                    SpeakViaNAudioBlocking(request);
                }
                else
                {
                    SpeakViaSapiBlocking(request);
                }
            }
            catch (Exception ex)
            {
                _log.Error(ex, "[FfxivEchoes] TTS 発話に失敗（text={Text}）", request.Text);
            }
        }
    }

    /// <summary>ボイス選択と話速設定。呼び出し側は <see cref="_gate"/> を保持していること。</summary>
    private void ApplyVoiceAndRate(string? voice, int rate)
    {
        if (!string.IsNullOrEmpty(voice))
        {
            TrySelectVoice(voice);
        }
        _synthesizer.Rate = rate;
    }

    /// <summary>SAPI 直出し（同期）。読み終えるまでブロックしてキューを直列化する。</summary>
    private void SpeakViaSapiBlocking(SpeechRequest req)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            ApplyVoiceAndRate(req.Voice, req.Rate);
            try { _synthesizer.SetOutputToDefaultAudioDevice(); } catch { /* ignore */ }
            _synthesizer.Volume = (int)Math.Round(req.Volume * 100);
            _synthesizer.Speak(req.Text); // 同期発話：読み終えるまでブロック
        }
    }

    /// <summary>
    /// SAPI 出力を MemoryStream に書き出して NAudio で再生（同期・読み終えるまでブロック）。
    /// VolumeSampleProvider で 1.0 を超える増幅が可能（最大 5.0）。
    /// </summary>
    private void SpeakViaNAudioBlocking(SpeechRequest req)
    {
        var effectiveVolume = (float)(req.Volume * req.Boost);
        using var ms = new MemoryStream();
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            ApplyVoiceAndRate(req.Voice, req.Rate);
            _synthesizer.Volume = 100; // SAPI 側はフル、増幅は NAudio で行う
            _synthesizer.SetOutputToWaveStream(ms);
            _synthesizer.Speak(req.Text); // 同期合成
        }

        if (ms.Length == 0)
        {
            return;
        }
        ms.Position = 0;

        using var reader = new WaveFileReader(ms);
        ISampleProvider sample = reader.ToSampleProvider();
        var amp = new VolumeSampleProvider(sample) { Volume = effectiveVolume };
        using var player = new WaveOutEvent();
        using var done = new ManualResetEventSlim(false);
        player.PlaybackStopped += (_, _) =>
        {
            try { done.Set(); } catch { /* ignore */ }
        };

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _currentNAudioPlayer = player;
        }

        player.Init(amp);
        player.Play();

        // 再生完了（または Dispose）までブロックし、キューを直列化する。
        // セーフティとして発話長 + 余裕の上限でも抜ける（PlaybackStopped 取りこぼし対策）。
        done.Wait(TimeSpan.FromSeconds(30));
        try { player.Stop(); } catch { /* ignore */ }

        lock (_gate)
        {
            if (ReferenceEquals(_currentNAudioPlayer, player))
            {
                _currentNAudioPlayer = null;
            }
        }
    }

    private void TrySelectVoice(string name)
    {
        try
        {
            foreach (var voice in _synthesizer.GetInstalledVoices())
            {
                var info = voice.VoiceInfo;
                if (string.Equals(info.Name, name, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(info.Culture.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    _synthesizer.SelectVoice(info.Name);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[FfxivEchoes] TTS ボイス '{Voice}' の選択に失敗（既定を使用）", name);
        }
    }

    private static int ClampRate(double rate)
    {
        var mapped = (int)Math.Round((rate - 1.0) * 10.0);
        return Math.Clamp(mapped, -10, 10);
    }

    public void Dispose()
    {
        _disposed = true;
        lock (_queueGate)
        {
            _queue.Clear();
            Monitor.PulseAll(_queueGate);
        }

        try
        {
            WaveOutEvent? player;
            lock (_gate)
            {
                player = _currentNAudioPlayer;
                _currentNAudioPlayer = null;
            }
            try { player?.Stop(); } catch { /* ignore */ }

            // ワーカーが現在の発話を抜けるのを待つ（最大 1 秒）。
            try { _worker.Join(TimeSpan.FromSeconds(1)); } catch { /* ignore */ }

            // バックグラウンド合成（_gate 保持中）と競合して ObjectDisposedException を
            // 撒かないよう、_synthesizer の破棄も _gate 内で直列化する。
            lock (_gate)
            {
                try { _synthesizer.SpeakAsyncCancelAll(); } catch { /* ignore */ }
                _synthesizer.Dispose();
            }
        }
        catch
        {
            // dispose 中の例外は飲み込む
        }
    }

    public readonly record struct SpeechRequest(
        string Text,
        string? Voice,
        int Rate,
        double Volume,
        float Boost,
        bool UseNAudio,
        DateTimeOffset EnqueuedAt,
        int Priority = 0);
}
