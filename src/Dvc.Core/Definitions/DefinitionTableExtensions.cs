namespace Dvc.Core.Definitions;

public static class DefinitionTableExtensions
{
    /// <summary>
    /// The table's own spelling of a key matched case-insensitively (e.g.
    /// "SLOW" -> "slow"), or null when there's no match.
    /// </summary>
    public static string? CanonicalKey<TValue>(this IReadOnlyDictionary<string, TValue> table, string key) =>
        table.Keys.FirstOrDefault(candidate => string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase));
}
