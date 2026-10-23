namespace DocumentRelationsGenerator.Scenarios;

public static class DependencyOrder
{
    /// <summary>
    /// Topological order (parents before children). Ties keep declaration order, so the plan and the
    /// random stream are stable across runs.
    /// </summary>
    public static IReadOnlyList<ScenarioStep> Sort(IReadOnlyList<ScenarioStep> steps)
    {
        var byKey = new Dictionary<string, ScenarioStep>(StringComparer.Ordinal);
        foreach (var step in steps)
            if (!byKey.TryAdd(step.Key, step))
                throw new InvalidOperationException($"Duplicate step key '{step.Key}'.");

        foreach (var step in steps)
        foreach (var dependency in step.Dependencies)
            if (!byKey.ContainsKey(dependency))
                throw new InvalidOperationException($"Step '{step.Key}' depends on unknown step '{dependency}'.");

        var ordered = new List<ScenarioStep>(steps.Count);
        var done = new HashSet<string>(StringComparer.Ordinal);
        while (ordered.Count < steps.Count)
        {
            var next = steps.FirstOrDefault(step => !done.Contains(step.Key) && step.Dependencies.All(done.Contains))
                       ?? throw new InvalidOperationException(
                           "Dependency cycle between steps: " +
                           string.Join(", ", steps.Where(step => !done.Contains(step.Key)).Select(step => step.Key)));
            ordered.Add(next);
            done.Add(next.Key);
        }

        return ordered;
    }

    /// <summary>All steps that transitively depend on any of <paramref name="brokenKeys"/>.</summary>
    public static IReadOnlySet<string> Descendants(IReadOnlyList<ScenarioStep> steps, IEnumerable<string> brokenKeys)
    {
        var blocked = new HashSet<string>(brokenKeys, StringComparer.Ordinal);
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var step in steps)
            {
                if (blocked.Contains(step.Key) || !step.Dependencies.Any(blocked.Contains)) continue;
                blocked.Add(step.Key);
                changed = true;
            }
        }

        blocked.ExceptWith(brokenKeys);
        return blocked;
    }
}
