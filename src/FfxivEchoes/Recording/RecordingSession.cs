using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Dalamud.Plugin.Services;
using FfxivEchoes.Events;

namespace FfxivEchoes.Recording;

/// <summary>
/// 1 戦闘ぶんの録画セッション。<see cref="StreamWriter"/> をラップして JSON Lines 形式で書き出す。
/// </summary>
/// <remarks>
/// 戦闘終了時に <see cref="Dispose"/> を呼ぶこと。途中でゲーム強制終了した場合の保護のため、
/// <see cref="WriteEvent"/> 毎に AutoFlush を有効にしている。
/// </remarks>
public sealed class RecordingSession : IDisposable
{
    private readonly DateTimeOffset _startTime;
    private readonly string _filePath;
    private readonly IPluginLog _log;
    private readonly Dictionary<uint, string> _partyIdToName;
    private StreamWriter? _writer;
    private int _eventCount;

    public RecordingSession(
        string filePath,
        string zone,
        DateTimeOffset startTime,
        string pluginVersion,
        IReadOnlyList<PartyMemberInfo> party,
        IPluginLog log)
    {
        _filePath = filePath;
        _startTime = startTime;
        _log = log;

        // status events の source 名解決マップ。EventSerializer に渡すことで PC self-buff /
        // PC DoT の source_id → name 解決ができ、aggregation 段階の party filter（名前マッチ）が
        // 確実に PC 由来 status を除外できるようになる。
        _partyIdToName = new Dictionary<uint, string>(party.Count);
        foreach (var m in party)
        {
            if (m.ObjectId is { } id && id != 0 && !string.IsNullOrEmpty(m.Name))
            {
                _partyIdToName[id] = m.Name;
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

        var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read);
        _writer = new StreamWriter(stream, new UTF8Encoding(false))
        {
            AutoFlush = true,
            NewLine = "\n",
        };

        var metaLine = EventSerializer.SerializeMeta(zone, startTime, pluginVersion, party);
        _writer.WriteLine(metaLine);
    }

    public string FilePath => _filePath;
    public int EventCount => _eventCount;
    public DateTimeOffset StartTime => _startTime;

    public void WriteEvent(IGameEvent ev)
    {
        if (_writer is null)
        {
            return;
        }

        try
        {
            var line = EventSerializer.Serialize(ev, _startTime, _partyIdToName);
            _writer.WriteLine(line);
            _eventCount++;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[FfxivEchoes] 録画イベントの書き込みに失敗 ({Type})", ev.GetType().Name);
        }
    }

    public void Dispose()
    {
        var w = _writer;
        if (w is null)
        {
            return;
        }
        _writer = null;

        try
        {
            w.Flush();
            w.Dispose();
            _log.Information("[FfxivEchoes] 録画ファイルをクローズ：{Path} ({Count} events)",
                _filePath, _eventCount);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[FfxivEchoes] 録画ファイルクローズ時に例外");
        }
    }
}
