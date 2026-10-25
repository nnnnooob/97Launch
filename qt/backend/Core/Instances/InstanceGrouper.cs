namespace Launcher.Core.Instances;

/// <summary>
/// The cards PCL places instances into (<c>McInstanceCardType</c>).
/// </summary>
public enum InstanceCardType
{
    /// <summary>Starred instances, always first.</summary>
    Star,

    /// <summary>Moddable instances (Forge / NeoForge / LiteLoader / Fabric).</summary>
    Api,

    /// <summary>Ordinary vanilla and OptiFine instances.</summary>
    OriginalLike,

    /// <summary>Old versions and leftover snapshots.</summary>
    Rubbish,

    /// <summary>April Fools versions.</summary>
    Fool,

    /// <summary>Unlaunchable instances.</summary>
    Error,
}

/// <summary>
/// Distributes instances into PCL's card groups.
/// </summary>
/// <remarks>
/// <para>
/// A direct port of PCL's <c>InitMcInstanceListWithoutCache</c> grouping
/// (<c>ModMinecraft.vb:1169-1254</c>). Each step mirrors an <c>McInstanceFilter</c>
/// call: take the matching instances out of the pool and append them to a group,
/// never revisiting them.
/// </para>
/// <para>
/// The non-obvious part is 常规实例. PCL does not list every vanilla and OptiFine
/// install; it collapses each major game version down to at most one OptiFine and
/// one vanilla entry. That is what stops a folder holding five different 1.20.1
/// builds from flooding the page, and it is why the grouping cannot be expressed
/// as a simple filter.
/// </para>
/// <para>
/// Pure and stateless: the input is a snapshot and the output is a new list, so
/// every rule here is directly testable without a filesystem.
/// </para>
/// </remarks>
public static class InstanceGrouper
{
    /// <summary>
    /// Splits instances into the card groups, in PCL's fixed card order.
    /// </summary>
    public static List<(InstanceCardType Type, List<InstanceInfo> Items)> Group(
        IEnumerable<InstanceInfo> instances)
    {
        ArgumentNullException.ThrowIfNull(instances);

        // PCL mutates the pool in place; index tracking reproduces "remove what we
        // just took" exactly, which HashSet.Except cannot guarantee for records
        // that compare by value and may therefore be identical.
        var pool = instances.ToList();
        var groups = new List<(InstanceCardType, List<InstanceInfo>)>();

        // ---- 1. Starred instances (PCL: InstanceStar) ----
        var stars = TakeWhere(pool, i => i.IsStar);
        if (stars.Count > 0)
        {
            groups.Add((InstanceCardType.Star, stars));
        }

        // ---- 2. Errors, then April Fools versions ----
        TakeInto(pool, groups, i => i.Kind == InstanceKind.Error, InstanceCardType.Error);
        TakeInto(pool, groups, i => i.Kind == InstanceKind.Fool, InstanceCardType.Fool);

        // ---- 3. Moddable instances: Forge / NeoForge / LiteLoader / Fabric ----
        TakeInto(pool, groups, i => i.Kind is
            InstanceKind.Forge or InstanceKind.NeoForge
            or InstanceKind.LiteLoader or InstanceKind.Fabric,
            InstanceCardType.Api);

        // ---- 4. Old versions go straight to the rarely-used pile ----
        var useful = new List<InstanceInfo>();
        var rubbish = TakeWhere(pool, i => i.Kind == InstanceKind.Old);

        // ---- 5. The newest vanilla/snapshot is useful only if it is a snapshot ----
        var largest = pool
            .Where(i => i.Kind is InstanceKind.Original or InstanceKind.Snapshot)
            .OrderByDescending(i => i.ReleaseTime)
            .FirstOrDefault();

        if (largest is not null && largest.Kind == InstanceKind.Snapshot)
        {
            useful.Add(largest);
            pool.Remove(largest);
        }

        // ---- 6. Every remaining snapshot is rarely used ----
        rubbish.AddRange(TakeWhere(pool, i => i.Kind == InstanceKind.Snapshot));

        // ---- 7. Per major version, keep the newest vanilla and OptiFine ----
        // PCL only considers instances with a valid Drop; ones without a
        // parseable game version stay in the pool and land in the rubbish pile.
        var byDrop = new Dictionary<string, (InstanceInfo? Vanilla, InstanceInfo? OptiFine)>();

        foreach (var instance in pool.ToList())
        {
            var drop = DropOf(instance.VanillaName);
            if (drop.Length == 0)
            {
                continue;
            }

            byDrop.TryGetValue(drop, out var current);

            if (instance.Kind == InstanceKind.OptiFine)
            {
                // PCL compares OptiFine builds by version code, not by date.
                if (current.OptiFine is null || CompareOptiFine(instance, current.OptiFine) > 0)
                {
                    current.OptiFine = instance;
                }
            }
            else if (current.Vanilla is null || instance.ReleaseTime > current.Vanilla.ReleaseTime)
            {
                current.Vanilla = instance;
            }

            byDrop[drop] = current;
        }

        // Add the chosen representatives and drop them from the pool.
        foreach (var pair in byDrop.Values)
        {
            if (pair.OptiFine is not null && pair.Vanilla is not null)
            {
                // Both exist for this version: the vanilla is kept only when it is
                // newer than the OptiFine build, otherwise OptiFine represents it.
                if (CompareDrop(pair.Vanilla.VanillaName, pair.OptiFine.VanillaName) > 0)
                {
                    useful.Add(pair.Vanilla);
                    pool.Remove(pair.Vanilla);
                }

                useful.Add(pair.OptiFine);
                pool.Remove(pair.OptiFine);
            }
            else if (pair.OptiFine is not null)
            {
                useful.Add(pair.OptiFine);
                pool.Remove(pair.OptiFine);
            }
            else if (pair.Vanilla is not null)
            {
                useful.Add(pair.Vanilla);
                pool.Remove(pair.Vanilla);
            }
        }

        // ---- 8. Whatever is left is rarely used ----
        rubbish.AddRange(pool);

        if (useful.Count > 0)
        {
            groups.Add((InstanceCardType.OriginalLike, useful));
        }

        if (rubbish.Count > 0)
        {
            groups.Add((InstanceCardType.Rubbish, rubbish));
        }

        // ---- 9. Emit in PCL's card order, skipping empty groups ----
        var order = new[]
        {
            InstanceCardType.Star,
            InstanceCardType.Api,
            InstanceCardType.OriginalLike,
            InstanceCardType.Rubbish,
            InstanceCardType.Fool,
            InstanceCardType.Error,
        };

        var result = new List<(InstanceCardType, List<InstanceInfo>)>();
        foreach (var type in order)
        {
            foreach (var group in groups)
            {
                if (group.Item1 == type && group.Item2.Count > 0)
                {
                    result.Add((type, Sort(group.Item2)));
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Removes every matching instance from the pool and returns them.
    /// </summary>
    private static List<InstanceInfo> TakeWhere(List<InstanceInfo> pool, Func<InstanceInfo, bool> predicate)
    {
        var taken = pool.Where(predicate).ToList();
        pool.RemoveAll(item => predicate(item));
        return taken;
    }

    /// <summary>
    /// Removes matching instances from the pool and appends them as a group.
    /// </summary>
    private static void TakeInto(
        List<InstanceInfo> pool,
        List<(InstanceCardType, List<InstanceInfo>)> groups,
        Func<InstanceInfo, bool> predicate,
        InstanceCardType type)
    {
        var taken = TakeWhere(pool, predicate);
        if (taken.Count > 0)
        {
            groups.Add((type, taken));
        }
    }

    /// <summary>
    /// PCL's per-card ordering (<c>ModMinecraft.vb:1288-1303</c>): release date
    /// first, then which loader is present, then the loader version, then name.
    /// </summary>
    private static List<InstanceInfo> Sort(List<InstanceInfo> items)
        => items
            .OrderByDescending(i => i.ReleaseTime)
            .ThenByDescending(i => i.FabricVersion is not null)
            .ThenByDescending(i => i.NeoForgeVersion is not null)
            .ThenByDescending(i => i.ForgeVersion is not null)
            .ThenByDescending(i => i.HasOptiFine)
            .ThenByDescending(i => i.HasLiteLoader)
            .ThenByDescending(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// The major version a game version belongs to, as a comparable key.
    /// </summary>
    /// <remarks>
    /// <c>1.20.1</c> and <c>1.20.4</c> are different keys because the patch
    /// number is part of it, matching PCL's <c>Drop</c>: two builds of the same
    /// exact version are interchangeable, 1.20.1 and 1.20.4 are not.
    /// </remarks>
    internal static string DropOf(string vanillaName)
    {
        ArgumentNullException.ThrowIfNull(vanillaName);

        var parts = vanillaName.Split('.');
        if (parts.Length < 2
            || !int.TryParse(parts[0], out var major)
            || !int.TryParse(parts[1], out var minor))
        {
            return "";
        }

        var patch = parts.Length > 2 && int.TryParse(parts[2], out var p) ? p : 0;
        return $"{major}.{minor}.{patch}";
    }

    private static int CompareDrop(string left, string right)
    {
        var a = DropOf(left).Split('.');
        var b = DropOf(right).Split('.');
        if (a.Length < 3 || b.Length < 3)
        {
            return 0;
        }

        for (var i = 0; i < 3; i++)
        {
            var cmp = int.Parse(a[i]).CompareTo(int.Parse(b[i]));
            if (cmp != 0)
            {
                return cmp;
            }
        }

        return 0;
    }

    /// <summary>
    /// Orders two OptiFine builds by their trailing build number.
    /// </summary>
    private static int CompareOptiFine(InstanceInfo left, InstanceInfo right)
        => TrailingNumber(left.OptiFineVersion).CompareTo(TrailingNumber(right.OptiFineVersion));

    /// <summary>
    /// The trailing digits of an OptiFine version, e.g. <c>5</c> for
    /// <c>1.20.1_HD_U_I5</c>.
    /// </summary>
    private static int TrailingNumber(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var digits = text.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray();
        return digits.Length > 0 && int.TryParse(new string(digits), out var value) ? value : 0;
    }
}
