using System;
using System.IO;
using System.Threading;

namespace Klippy.Services;

/// <summary>The last step of a write-to-temp-then-swap save.</summary>
public static class AtomicFile
{
    private const int Attempts = 5;

    /// <summary>
    /// Moves <paramref name="tmp"/> over <paramref name="path"/>. On Windows the replace is
    /// refused while another process (antivirus, the search indexer, a sync client) briefly
    /// holds the old file open, so a refused move is retried a few times before giving up.
    /// </summary>
    public static void Replace(string tmp, string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(tmp, path, overwrite: true);
                return;
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException && attempt < Attempts)
            {
                Thread.Sleep(20 * attempt);
            }
        }
    }
}
