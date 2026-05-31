using System;
using System.IO;
using System.Text;

namespace FfxivEchoes.Utils;

/// <summary>
/// ファイルをアトミックに上書き保存するユーティリティ。
/// </summary>
/// <remarks>
/// 一時ファイルへ書き込んでから rename（<see cref="File.Replace(string,string,string?)"/> /
/// <see cref="File.Move(string,string)"/>）で差し替えることで、書き込み途中の失敗
/// （ディスクフル・I/O エラー・クラッシュ・OneDrive 同期ロック・ウイルス対策のファイルロック）で
/// 既存ファイルが truncate されて全データを失う事故を防ぐ。トリガーファイル（119KB 級）や
/// プロファイル・辞書の上書き保存に使う。失敗時も元ファイルは無傷で残る。
/// </remarks>
public static class AtomicFileWriter
{
    public static void WriteAllText(string path, string contents, Encoding encoding)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, contents, encoding);
            if (File.Exists(path))
            {
                // temp → path の原子的差し替え（バックアップ無し）。
                File.Replace(temp, path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temp, path);
            }
        }
        finally
        {
            // 置換成功後は temp は存在しない。失敗時に残った一時ファイルを掃除する。
            if (File.Exists(temp))
            {
                try
                {
                    File.Delete(temp);
                }
                catch (IOException)
                {
                    // 残っても次回の保存で上書きされるため致命的ではない。
                }
            }
        }
    }
}
