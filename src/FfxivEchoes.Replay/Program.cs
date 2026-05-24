using System;
using System.Collections.Generic;
using System.IO;
using FfxivEchoes.Events;
using FfxivEchoes.Replay;
using FfxivEchoes.Replay.Capture;
using FfxivEchoes.Replay.MockServices;
using FfxivEchoes.Triggers;

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
    /// AoE 解決サービスを構築する。Phase 2 (A-1): PredictedObjectSpawnService を wire。
    /// </summary>
    private static void TryConstructServices(
        Cli.Options opts,
        InMemoryEventBus bus,
        MockFramework framework,
        MockObjectTable objectTable,
        TraceRecorder trace,
        MockPluginLog log)
    {
        if (string.IsNullOrEmpty(opts.ConfigDir))
        {
            log.Information("[Replay] --config-dir 未指定。trigger ファイル無しで source イベントだけ trace 化する。");
            return;
        }

        try
        {
            var loader = new TriggerLoader(opts.ConfigDir!, log);
            var store = new TriggerStore(loader, log);
            store.Reload();
            log.Information("[Replay] trigger ファイル {N} 個ロード（{Dir}）", store.LoadedFileCount, loader.TriggersDirectory);

            // PredictedObjectSpawnService: cast → 予告 draw_aoe の経路を有効化。
            // NowProvider に MockFramework の仮想時刻を渡して deterministic に発火させる。
            var predicted = new PredictedObjectSpawnService(framework, bus, store, log);
            predicted.NowProvider = () => new DateTimeOffset(framework.LastUpdateUTC, TimeSpan.Zero);
            log.Information("[Replay] PredictedObjectSpawnService wire OK");
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[Replay] サービス wire 失敗（trigger ファイル参照？ 続行可能）");
        }
    }
}

internal static class Cli
{
    public sealed record Options(
        string Recording,
        string? Zone,
        string? TriggerFile,
        string? ConfigDir,
        string OutputPath);

    public static Options? Parse(string[] args)
    {
        string? recording = null;
        string? zone = null;
        string? triggerFile = null;
        string? configDir = null;
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
                case "--config-dir":
                case "-c":
                    if (++i >= args.Length) return null;
                    configDir = args[i];
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
        return new Options(recording, zone, triggerFile, configDir, outputPath);
    }

    public static void PrintUsage()
    {
        Console.WriteLine("FfxivEchoes.Replay — headless replay harness");
        Console.WriteLine();
        Console.WriteLine("USAGE:");
        Console.WriteLine("  dotnet run --project src/FfxivEchoes.Replay -- \\");
        Console.WriteLine("    --recording <path> \\");
        Console.WriteLine("    [--zone <name>] \\");
        Console.WriteLine("    [--config-dir <dir>] \\");
        Console.WriteLine("    --output <trace.json>");
        Console.WriteLine();
        Console.WriteLine("  -r/--recording   入力 jsonl ファイル（必須）");
        Console.WriteLine("  -z/--zone        ゾーン名（未指定なら meta 行から推定）");
        Console.WriteLine("  -c/--config-dir  プラグイン config dir（triggers/ を含むディレクトリ）");
        Console.WriteLine("                   指定すると TriggerStore + PredictedObjectSpawnService が wire される");
        Console.WriteLine("                   例: %APPDATA%/XIVLauncher/pluginConfigs/FfxivEchoes");
        Console.WriteLine("  -o/--output      trace.json 出力先（既定: out/trace.json）");
    }
}
