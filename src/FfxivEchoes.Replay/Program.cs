using System;
using System.Collections.Generic;
using System.IO;
using FfxivEchoes.Events;
using FfxivEchoes.Replay;
using FfxivEchoes.Replay.Capture;
using FfxivEchoes.Replay.MockServices;

namespace FfxivEchoes.Replay;

/// <summary>
/// FfxivEchoes プラグインの AoE 描画ロジックを Dalamud 非依存で再生するための
/// console エントリポイント。
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            var opts = Cli.Parse(args);
            if (opts is null)
            {
                Cli.PrintUsage();
                return 2;
            }

            return Run(opts);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Replay] fatal: {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    private static int Run(Cli.Options opts)
    {
        var log = new MockPluginLog(MockPluginLog.LogLevel.Information);
        log.Information("[Replay] starting recording={Rec} zone={Zone} output={Out}",
            opts.Recording, opts.Zone ?? "(auto)", opts.OutputPath);

        var eventBus = new InMemoryEventBus(log);
        var framework = new MockFramework();
        var objectTable = new MockObjectTable();

        var traceRecorder = new TraceRecorder
        {
            Zone = opts.Zone,
            RecordingPath = opts.Recording,
        };
        traceRecorder.Subscribe(eventBus);

        var replayer = new JsonlReplayer(eventBus, framework, objectTable, log, traceRecorder);
        replayer.OnMetaParsed = meta =>
        {
            if (string.IsNullOrEmpty(traceRecorder.Zone) && !string.IsNullOrEmpty(meta.Zone))
            {
                traceRecorder.Zone = meta.Zone;
            }
        };

        // 設計書 §10 リスク対策：サービス起動時の NRE で全体停止しないように
        // 各サービス構築を try/catch で隔離する。Phase 1 では実サービスを wire しないが、
        // 将来 wiring されたとき同じパターンで保護される。
        TryConstructServices(opts, eventBus, framework, objectTable, traceRecorder, log);

        replayer.Play(opts.Recording);

        traceRecorder.WriteTo(opts.OutputPath);
        log.Information("[Replay] wrote trace: {Out} events={N} cast_start={C}",
            opts.OutputPath, traceRecorder.EventCount, traceRecorder.CountKind("cast_start"));

        return 0;
    }

    /// <summary>
    /// AoE 解決サービスを構築する。Phase 1 ではいくつかの依存（TriggerStore / RecordingScanner /
    /// WorldOverlayWindow など）の重さから wire 自体を保留し、TraceRecorder が
    /// IEventBus 経由で source イベントだけを記録する形に留める。
    /// </summary>
    private static void TryConstructServices(
        Cli.Options opts,
        InMemoryEventBus bus,
        MockFramework framework,
        MockObjectTable objectTable,
        TraceRecorder trace,
        MockPluginLog log)
    {
        // 雛形：将来 wire するときの構造を残しておく。
        // var triggerStore = ...;
        // try
        // {
        //     var auto = new AutoTelegraphService(bus, actionLookup, mockObjectTable,
        //         mockWorldOverlay, trace, triggerStore, log);
        // }
        // catch (Exception ex) { log.Warning(ex, "[Replay] AutoTelegraph wire skipped"); }

        if (!string.IsNullOrEmpty(opts.TriggerFile))
        {
            log.Warning("[Replay] --trigger-file 指定ありだが Phase 1 では未 wire。後続 agent でサービス組み立て予定。");
        }
    }
}

internal static class Cli
{
    public sealed record Options(
        string Recording,
        string? Zone,
        string? TriggerFile,
        string OutputPath);

    public static Options? Parse(string[] args)
    {
        string? recording = null;
        string? zone = null;
        string? triggerFile = null;
        string outputPath = "out/trace.json";

        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "--recording":
                case "-r":
                    if (++i >= args.Length) return null;
                    recording = args[i];
                    break;
                case "--zone":
                case "-z":
                    if (++i >= args.Length) return null;
                    zone = args[i];
                    break;
                case "--trigger-file":
                case "-t":
                    if (++i >= args.Length) return null;
                    triggerFile = args[i];
                    break;
                case "--output":
                case "-o":
                    if (++i >= args.Length) return null;
                    outputPath = args[i];
                    break;
                case "--help":
                case "-h":
                case "/?":
                    return null;
            }
        }

        if (string.IsNullOrEmpty(recording))
        {
            Console.Error.WriteLine("[Replay] --recording <path> は必須です");
            return null;
        }
        return new Options(recording, zone, triggerFile, outputPath);
    }

    public static void PrintUsage()
    {
        Console.WriteLine("FfxivEchoes.Replay — headless replay harness");
        Console.WriteLine();
        Console.WriteLine("USAGE:");
        Console.WriteLine("  dotnet run --project src/FfxivEchoes.Replay -- \\");
        Console.WriteLine("    --recording <path> \\");
        Console.WriteLine("    [--zone <name>] \\");
        Console.WriteLine("    [--trigger-file <path>] \\");
        Console.WriteLine("    --output <trace.json>");
        Console.WriteLine();
        Console.WriteLine("  -r/--recording   入力 jsonl ファイル（必須）");
        Console.WriteLine("  -z/--zone        ゾーン名（未指定なら meta 行から推定）");
        Console.WriteLine("  -t/--trigger-file トリガー定義 JSON（Phase 1 では未使用）");
        Console.WriteLine("  -o/--output      trace.json 出力先（既定: out/trace.json）");
    }
}
