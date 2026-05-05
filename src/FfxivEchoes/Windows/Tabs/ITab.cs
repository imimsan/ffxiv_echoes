namespace FfxivEchoes.Windows.Tabs;

public interface ITab
{
    /// <summary>タブのタイトル（UI 表示）</summary>
    string Title { get; }

    /// <summary>プログラムからフォーカスを当てるための識別子</summary>
    string Id { get; }

    void Draw();
}
