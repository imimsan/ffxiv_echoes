using System.IO;

namespace FfxivEchoes.Recording;

internal static class RecordingFileIO
{
    public static FileStream OpenReadShared(string path)
    {
        return new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
    }
}
