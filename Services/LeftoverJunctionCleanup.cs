using System.Runtime.InteropServices;

namespace UnturnedModLoader.Services;

/// <summary>
/// Idempotent startup cleanup of junctions the legacy overlay left inside a real
/// game install. This is a <b>safe cleanup</b>, not a migration: it only removes a
/// directory junction when (a) it is one of the loader's known owned roots and
/// (b) its target resolves into the loader's profiles directory. Everything else
/// is left untouched. Run once at startup; harmless to re-run.
/// </summary>
internal static class LeftoverJunctionCleanup
{
    // Roots the old overlay owned and applied as junctions. Inlined here because
    // OverlayOwnedRoots was removed with the junction machinery.
    private static readonly string[] OwnedRoots = ["Modules", "BepInEx", "doorstop_libs"];

    /// <summary>
    /// Removes leftover loader junctions under <paramref name="gamePath"/>. Best-effort:
    /// any failure on a single root is swallowed so startup never blocks on it.
    /// </summary>
    public static void Run(string? gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath) || !Directory.Exists(gamePath))
            return;

        foreach (var root in OwnedRoots)
        {
            var path = Path.Combine(gamePath, root);
            try
            {
                if (!JunctionHelper.IsJunction(path))
                    continue;

                if (!JunctionHelper.TryGetJunctionTarget(path, out var target) || target is null)
                    continue;

                // Only touch junctions that point back into our own profiles tree.
                var profilesRoot = Path.GetFullPath(AppPaths.ProfilesRoot)
                    .TrimEnd(Path.DirectorySeparatorChar);
                var targetFull = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar);
                if (!targetFull.StartsWith(profilesRoot + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                    continue;

                JunctionHelper.DeleteJunction(path);
            }
            catch
            {
                // best-effort: leave the junction in place rather than risk deleting real data
            }
        }
    }
}
