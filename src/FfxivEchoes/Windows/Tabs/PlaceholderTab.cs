using Dalamud.Bindings.ImGui;

namespace FfxivEchoes.Windows.Tabs;

/// <summary>後続マイルストーンで実装予定のタブの占位実装。</summary>
public abstract class PlaceholderTab : ITab
{
    public abstract string Title { get; }
    public abstract string Id { get; }
    protected abstract string PlannedFor { get; }
    protected abstract string Description { get; }

    public void Draw()
    {
        ImGui.TextColored(new System.Numerics.Vector4(0.7f, 0.7f, 0.7f, 1f),
            $"未実装（{PlannedFor} で実装予定）");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextWrapped(Description);
    }
}
