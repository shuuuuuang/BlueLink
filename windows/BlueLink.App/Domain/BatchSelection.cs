namespace BlueLink.Domain;

/// <summary>Stable-ID selection in display order, shared by chat, search and file lists.</summary>
public static class BatchSelection
{
    public static bool Toggle(HashSet<Guid> selected, IReadOnlyList<Guid> displayed, Guid? anchor, Guid target,
        bool extend, int maximum = int.MaxValue)
    {
        var last = displayed.IndexOf(target);
        if (last < 0) return false;
        var first = anchor is { } id ? displayed.IndexOf(id) : -1;
        var next = selected.ToHashSet();
        if (extend && first >= 0)
            next.UnionWith(displayed.Skip(Math.Min(first, last)).Take(Math.Abs(last - first) + 1));
        else if (!next.Add(target)) next.Remove(target);
        if (next.Count > maximum) return false;
        selected.Clear(); selected.UnionWith(next);
        return true;
    }

    public static Guid? ResolveAnchor(IReadOnlySet<Guid> selected, IReadOnlyList<Guid> displayed, Guid? anchor)
    {
        var origin = anchor is { } id ? displayed.IndexOf(id) : -1;
        if (origin >= 0 && selected.Contains(anchor!.Value)) return anchor;
        // A deselected/deleted anchor must not reverse range direction. Prefer the nearest remaining selected row.
        return displayed.Select((id, index) => (Id: id, Index: index))
            .Where(item => selected.Contains(item.Id))
            .OrderBy(item => origin < 0 ? item.Index : Math.Abs(item.Index - origin))
            .Select(item => (Guid?)item.Id).FirstOrDefault();
    }

    private static int IndexOf(this IReadOnlyList<Guid> ids, Guid id)
    {
        for (var i = 0; i < ids.Count; i++) if (ids[i] == id) return i;
        return -1;
    }
}
