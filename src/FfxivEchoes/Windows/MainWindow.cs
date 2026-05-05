using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace FfxivEchoes.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly Plugin _plugin;

    public MainWindow(Plugin plugin)
        : base("FFXIV Echoes##main", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 240),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

        _plugin = plugin;
    }

    public void Dispose() { }

    public override void Draw()
    {
        ImGui.Text("FFXIV Echoes");
        ImGui.Separator();
        ImGui.TextUnformatted($"Version: {Plugin.PluginInterface.Manifest.AssemblyVersion}");
        ImGui.TextUnformatted($"DalamudApiLevel: {Plugin.PluginInterface.Manifest.DalamudApiLevel}");
        ImGui.Spacing();
        ImGui.TextWrapped("M1（開発環境構築）の動作確認用ウィンドウです。設定や録画機能は M2 以降で実装予定。");
        ImGui.Spacing();

        if (ImGui.Button("設定を開く"))
        {
            _plugin.ToggleConfigUi();
        }
    }
}
