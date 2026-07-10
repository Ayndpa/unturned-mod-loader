using UnturnedModLoader.Models;

namespace UnturnedModLoader.Services;

public sealed class ModuleDependencyGraph
{
    private readonly Dictionary<string, InstalledMod> _modulesByName;
    private readonly List<InstalledMod> _modules;

    private ModuleDependencyGraph(List<InstalledMod> modules, Dictionary<string, InstalledMod> modulesByName)
    {
        _modules = modules;
        _modulesByName = modulesByName;
    }

    public static ModuleDependencyGraph Build(IReadOnlyList<InstalledMod> mods)
    {
        var modules = mods
            .Where(mod => mod.Kind == LocalModKind.Module && !string.IsNullOrWhiteSpace(mod.ModuleName))
            .ToList();

        var byName = new Dictionary<string, InstalledMod>(StringComparer.OrdinalIgnoreCase);
        foreach (var module in modules)
        {
            if (!byName.ContainsKey(module.ModuleName))
                byName[module.ModuleName] = module;
        }

        return new ModuleDependencyGraph(modules, byName);
    }

    public IReadOnlyList<InstalledMod> GetEnabledDependents(InstalledMod module)
    {
        if (string.IsNullOrWhiteSpace(module.ModuleName))
            return [];

        var results = new List<InstalledMod>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        queue.Enqueue(module.ModuleName);

        while (queue.Count > 0)
        {
            var name = queue.Dequeue();
            foreach (var candidate in _modules)
            {
                if (!candidate.IsEnabled)
                    continue;

                if (!DependsOn(candidate, name))
                    continue;

                if (!visited.Add(candidate.RelativePath))
                    continue;

                results.Add(candidate);

                if (!string.IsNullOrWhiteSpace(candidate.ModuleName))
                    queue.Enqueue(candidate.ModuleName);
            }
        }

        return results;
    }

    public IReadOnlyList<InstalledMod> GetDisabledDependencies(InstalledMod module)
    {
        if (module.Kind != LocalModKind.Module || module.DependencyNames.Count == 0)
            return [];

        var results = new List<InstalledMod>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dependencyName in module.DependencyNames)
        {
            if (!_modulesByName.TryGetValue(dependencyName, out var dependency))
                continue;

            if (dependency.IsEnabled)
                continue;

            if (!seen.Add(dependency.RelativePath))
                continue;

            results.Add(dependency);
        }

        return results;
    }

    public IReadOnlyList<InstalledMod> ExpandDependenciesForEnable(
        InstalledMod module,
        IReadOnlyCollection<string> alreadyPlanned)
    {
        if (module.Kind != LocalModKind.Module)
            return [];

        var results = new List<InstalledMod>();
        var visited = new HashSet<string>(alreadyPlanned, StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<InstalledMod>();
        queue.Enqueue(module);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var dependencyName in current.DependencyNames)
            {
                if (!_modulesByName.TryGetValue(dependencyName, out var dependency))
                    continue;

                if (!visited.Add(dependency.RelativePath))
                    continue;

                if (!dependency.IsEnabled)
                    results.Add(dependency);

                queue.Enqueue(dependency);
            }
        }

        return results;
    }

    private static bool DependsOn(InstalledMod module, string dependencyName) =>
        module.DependencyNames.Any(name =>
            string.Equals(name, dependencyName, StringComparison.OrdinalIgnoreCase));
}