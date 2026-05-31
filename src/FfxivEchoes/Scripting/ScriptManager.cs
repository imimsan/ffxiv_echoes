using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FfxivEchoes.Events;

namespace FfxivEchoes.Scripting;

/// <summary>
/// {ConfigDirectory}/scripts/*.dll をスキャンして <see cref="ICustomScript"/> を
/// ロードする（P5 スタブ実装）。
/// </summary>
/// <remarks>
/// 安全性検討項目：
/// - 任意 .dll の動的ロードは UAC を無視できる経路になりうる。<see cref="LoadAll"/> は
///   Assembly.LoadFrom で既定の AssemblyLoadContext にロードするため isolation も
///   サンドボックスも無い（以前のコメントの「ALC を分離し isolation を確保」は事実誤認だった）。
///   現状の安全担保は「既定では LoadAll を一切呼ばず休眠させる」オプトイン設計のみ。
///   将来サンドボックス化するなら collectible な分離 ALC、または
///   System.Reflection.MetadataLoadContext での走査検証（ロードせず型だけ検査）を導入する。
/// - .csx (Roslyn スクリプト) ロードは未対応。動的コンパイルが必要なら
///   Microsoft.CodeAnalysis.CSharp.Scripting NuGet を導入予定。
///
/// したがって P5 スタブとしては：
/// - scripts/ ディレクトリは作るが、起動時は何もロードしない（dryRun）
/// - LoadAll() を明示呼び出しした場合のみ DLL ロードを試みる
/// - ScriptManager 自体は IDisposable で安全に Unload を呼ぶ枠だけ提供
/// </remarks>
public sealed class ScriptManager : IDisposable
{
    public const string ScriptsDirName = "scripts";

    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly ScriptContext _context;
    private readonly IPluginLog _log;
    private readonly List<LoadedScript> _scripts = new();
    private readonly IDisposable _eventSub;

    public ScriptManager(IDalamudPluginInterface pluginInterface, ScriptContext context, IPluginLog log)
    {
        _pluginInterface = pluginInterface;
        _context = context;
        _log = log;
        _eventSub = context.Bus.SubscribeAll(OnEvent);

        // ディレクトリだけ事前作成（ユーザーが DLL を配置できるように）
        try
        {
            Directory.CreateDirectory(ScriptsDirectory);
        }
        catch
        {
            // 無視
        }
    }

    public string ScriptsDirectory => Path.Combine(_pluginInterface.ConfigDirectory.FullName, ScriptsDirName);
    public IReadOnlyList<LoadedScript> Loaded => _scripts;

    /// <summary>明示的にスクリプトをロードする（既定では呼ばれない安全側設計）。</summary>
    public void LoadAll()
    {
        if (!Directory.Exists(ScriptsDirectory))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(ScriptsDirectory, "*.dll"))
        {
            try
            {
                var asm = Assembly.LoadFrom(path);
                foreach (var type in asm.GetTypes())
                {
                    if (!typeof(ICustomScript).IsAssignableFrom(type) || type.IsAbstract)
                    {
                        continue;
                    }
                    var instance = (ICustomScript?)Activator.CreateInstance(type);
                    if (instance is null)
                    {
                        continue;
                    }
                    instance.Initialize(_context);
                    _scripts.Add(new LoadedScript(path, instance));
                    _log.Information("[FfxivEchoes] スクリプト読込：{Path}::{Type}", path, type.FullName ?? type.Name);
                }
            }
            catch (Exception ex)
            {
                _log.Error(ex, "[FfxivEchoes] スクリプト読込失敗：{Path}", path);
            }
        }
    }

    public void UnloadAll()
    {
        foreach (var s in _scripts.ToList())
        {
            try
            {
                s.Instance.Dispose();
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[FfxivEchoes] スクリプト Dispose 失敗：{Id}", s.Instance.Id);
            }
        }
        _scripts.Clear();
    }

    private void OnEvent(IGameEvent ev)
    {
        foreach (var s in _scripts)
        {
            try
            {
                s.Instance.OnEvent(ev);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "[FfxivEchoes] スクリプト '{Id}' でイベント処理例外", s.Instance.Id);
            }
        }
    }

    public void Dispose()
    {
        _eventSub.Dispose();
        UnloadAll();
    }

    public sealed record LoadedScript(string Path, ICustomScript Instance);
}
