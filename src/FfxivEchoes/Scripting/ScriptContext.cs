using Dalamud.Plugin.Services;
using FfxivEchoes.Events;
using FfxivEchoes.Triggers;
using FfxivEchoes.Variables;

namespace FfxivEchoes.Scripting;

/// <summary>
/// カスタムスクリプトに渡される依存集。プラグイン内部の主要サービスへの
/// 参照を集約し、不要な依存にスクリプト側がアクセスできないように制限する。
/// </summary>
public sealed class ScriptContext
{
    public IEventBus Bus { get; }
    public TriggerStore TriggerStore { get; }
    public VariableStore Variables { get; }
    public IPluginLog Log { get; }

    public ScriptContext(IEventBus bus, TriggerStore triggerStore, VariableStore variables, IPluginLog log)
    {
        Bus = bus;
        TriggerStore = triggerStore;
        Variables = variables;
        Log = log;
    }
}
