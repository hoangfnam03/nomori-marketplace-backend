using Nomori.Marketplace.Core.Catalog;

namespace Nomori.Marketplace.Services.Catalog;

/// <summary>In-memory helpers over the full category list. Categories are few, so the whole tree is loaded.</summary>
internal sealed class CategoryHierarchy(IReadOnlyList<Category> all)
{
    private readonly Dictionary<int, Category> byId = all.ToDictionary(c => c.Id);

    public Category? Find(int id) => byId.GetValueOrDefault(id);

    /// <summary>The category and every ancestor, nearest first. Stops at a missing parent or a loop.</summary>
    public IEnumerable<Category> SelfAndAncestors(Category category)
    {
        var seen = new HashSet<int>();
        var current = category;
        while (seen.Add(current.Id))
        {
            yield return current;
            if (current.ParentCategoryId == 0 || !byId.TryGetValue(current.ParentCategoryId, out var parent)) yield break;
            current = parent;
        }
    }

    /// <summary>True when the category and all of its ancestors exist and are published (a missing ancestor counts as hidden).</summary>
    public bool IsPublishedChain(Category category)
    {
        var seen = new HashSet<int>();
        var current = category;
        while (true)
        {
            if (!current.Published || !seen.Add(current.Id)) return false;
            if (current.ParentCategoryId == 0) return true;
            if (!byId.TryGetValue(current.ParentCategoryId, out var parent)) return false;
            current = parent;
        }
    }

    public string Path(Category category) =>
        string.Join(" > ", SelfAndAncestors(category).Reverse().Select(c => c.Name));

    public HashSet<int> DescendantIds(int id)
    {
        var result = new HashSet<int>();
        var pending = new Queue<int>([id]);
        while (pending.Count > 0)
        {
            var parent = pending.Dequeue();
            foreach (var child in all.Where(c => c.ParentCategoryId == parent && result.Add(c.Id)))
                pending.Enqueue(child.Id);
        }

        return result;
    }
}
