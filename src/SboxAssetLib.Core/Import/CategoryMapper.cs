namespace SboxAssetLib.Core.Import;

/// <summary>
/// Maps a provider's free-form categories/tags onto a small canonical set of folders
/// (wood, metal, stone, …) so the library stays tidy and consistent across providers.
/// </summary>
public static class CategoryMapper
{
    public const string Fallback = "misc";

    // Ordered most-specific first; the first canonical whose keyword is found wins.
    private static readonly (string Canonical, string[] Keywords)[] Rules =
    [
        ("brick", ["brick"]),
        ("wood", ["wood", "plank", "bark", "timber", "log"]),
        ("metal", ["metal", "steel", "iron", "rust", "aluminium", "aluminum", "copper", "bronze"]),
        ("concrete", ["concrete", "cement"]),
        ("plaster", ["plaster", "stucco"]),
        ("tiles", ["tile", "tiles"]),
        ("marble", ["marble"]),
        ("stone", ["stone", "rock", "cliff", "granite", "slate", "cobble", "gravel"]),
        ("ground", ["ground", "dirt", "mud", "soil", "sand", "snow", "grass", "moss", "forest floor"]),
        ("road", ["road", "asphalt", "pavement", "tarmac"]),
        ("fabric", ["fabric", "cloth", "textile", "leather", "carpet", "wool", "denim"]),
        ("plastic", ["plastic", "rubber"]),
        ("paper", ["paper", "cardboard"]),
        ("ceramic", ["ceramic", "porcelain"]),
        ("nature", ["nature", "plant", "tree", "leaf", "foliage", "outdoor", "organic"]),
        ("water", ["water", "liquid"]),
        ("furniture", ["furniture", "chair", "table", "sofa", "shelf", "cabinet"]),
        ("props", ["prop", "container", "barrel", "crate", "box"]),
    ];

    public static string Map(IEnumerable<string> categoriesAndTags)
    {
        var terms = categoriesAndTags
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.ToLowerInvariant())
            .ToList();

        foreach (var (canonical, keywords) in Rules)
            if (terms.Any(term => keywords.Any(kw => term.Contains(kw))))
                return canonical;

        return Fallback;
    }
}
