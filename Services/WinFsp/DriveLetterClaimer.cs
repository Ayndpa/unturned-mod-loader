using System.Runtime.InteropServices;

namespace UnturnedModLoader.Services.WinFsp;

/// <summary>
/// Picks a free Windows drive letter for the WinFsp mount point.
/// Prefers letters near the end of the alphabet to avoid clashing with
/// common system/network drives; skips A/B (legacy floppy) and C (system).
/// </summary>
internal static class DriveLetterClaimer
{
    // Preference order: U, V, W, T, S, R, then any free letter D..Z excluding the above.
    private static readonly char[] Preferred =
    {
        'U', 'V', 'W', 'T', 'S', 'R', 'Q', 'P', 'O', 'N', 'M',
        'L', 'K', 'J', 'I', 'H', 'G', 'F', 'E', 'D', 'Y', 'X', 'Z',
    };

    [DllImport("kernel32.dll")]
    private static extern uint GetLogicalDrives();

    /// <summary>Returns the first free drive letter, or null if all are taken.</summary>
    public static char? FindFree()
    {
        var mask = GetLogicalDrives();
        foreach (var c in Preferred)
        {
            var bit = 1u << (c - 'A');
            if ((mask & bit) == 0)
                return c;
        }
        return null;
    }
}
