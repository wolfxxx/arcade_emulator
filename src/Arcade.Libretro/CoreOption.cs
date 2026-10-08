namespace Arcade.Libretro;

/// <summary>A core setting declared via SET_VARIABLES ("Description; default|alt1|alt2").</summary>
public sealed class CoreOption
{
    public required string Key { get; init; }
    public required string Description { get; init; }
    public required IReadOnlyList<string> Values { get; init; }
    public required string Value { get; set; }

    internal static CoreOption Parse(string key, string declaration)
    {
        var sep = declaration.IndexOf("; ", StringComparison.Ordinal);
        var description = sep >= 0 ? declaration[..sep] : key;
        var values = (sep >= 0 ? declaration[(sep + 2)..] : declaration).Split('|');
        return new CoreOption { Key = key, Description = description, Values = values, Value = values[0] };
    }
}
