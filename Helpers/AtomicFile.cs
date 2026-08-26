using System;
using System.IO;
using System.Threading.Tasks;

namespace XrayUI.Helpers
{
    /// <summary>
    /// Write-to-temp + atomic swap. A crash or power cut mid-save can never leave a truncated
    /// file — the previous complete one survives until the replace commits. The temp name is
    /// per-call (Guid-suffixed) so concurrent saves of the same path never write over each
    /// other's temp file or race on the replace.
    /// </summary>
    public static class AtomicFile
    {
        public static async Task WriteAllTextAsync(string path, string contents)
        {
            var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
                await File.WriteAllTextAsync(tmp, contents).ConfigureAwait(false);

                if (File.Exists(path))
                    File.Replace(tmp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
                else
                    File.Move(tmp, path);
            }
            catch
            {
                try { File.Delete(tmp); } catch { }
                throw;
            }
        }
    }
}
