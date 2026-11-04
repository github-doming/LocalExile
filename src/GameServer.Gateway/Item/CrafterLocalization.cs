using System.Globalization;
using System.Text.Json;

/// <summary>
/// The client's translated names for the item crafter's game data, when the checkout has them (extract-data.ps1's
/// `localized` entry: `data/localized/zh-CN.json` and the stat descriptions beside it). The file says which language it
/// holds, so a tool shows the text only in an interface of that language; a checkout without it — an old one, or one
/// whose client has no Chinese pack — keeps the English fields of the catalog. See Agents/docs/dev/item-crafter.md.
/// </summary>
sealed class CrafterLocalization
{
    /// <summary>The file extract-data.ps1's `localized` entry writes, under the data folder.</summary>
    public const string DefaultFile = "localized/zh-CN.json";

    /// <summary>The language of the text, a BCP 47 tag such as "zh-CN" (empty when the client had no translated text).</summary>
    public string Tag { get; }

    /// <summary>The translated stat descriptions, in <see cref="StatDescriptions"/>' format.</summary>
    public string Descriptions { get; }

    readonly Dictionary<string, string> _classes, _bases, _mods, _words;

    CrafterLocalization(string tag, string descriptions, Dictionary<string, string> classes,
        Dictionary<string, string> bases, Dictionary<string, string> mods, Dictionary<string, string> words)
    {
        Tag = tag;
        Descriptions = descriptions;
        _classes = classes;
        _bases = bases;
        _mods = mods;
        _words = words;
    }

    /// <summary>The checkout's translated names, or null when it has none (the crafter then shows English only).</summary>
    public static CrafterLocalization? Load(string folder = "data")
    {
        string path = Path.Combine(folder, DefaultFile);
        if (!File.Exists(path)) return null;
        try
        {
            using var d = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = d.RootElement;
            if (!root.TryGetProperty("tag", out var tag) || tag.GetString() is not { Length: > 0 } language) return null;
            return new(language, Path.Combine(folder, "localized", language + "-stat-descriptions.txt"),
                Names(root, "classes"), Names(root, "bases"), Names(root, "mods"), Names(root, "words"));
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            return null;
        }
    }

    static Dictionary<string, string> Names(JsonElement root, string name)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!root.TryGetProperty(name, out var map) || map.ValueKind != JsonValueKind.Object) return names;
        foreach (var entry in map.EnumerateObject())
            if (entry.Value.GetString() is { Length: > 0 } text) names[entry.Name] = text;
        return names;
    }

    /// <summary>An item class's translated name (ItemClasses Id), or null.</summary>
    public string? Class(string id) => _classes.GetValueOrDefault(id);

    /// <summary>A base's translated name (BaseItemTypes Id), or null.</summary>
    public string? Base(string id) => _bases.GetValueOrDefault(id);

    /// <summary>A mod's translated affix name (Mods.dat Id), or null.</summary>
    public string? Mod(string id) => _mods.GetValueOrDefault(id);

    /// <summary>A rare-name word's translated text (Words.dat HASH32), or null.</summary>
    public string? Word(uint key) => _words.GetValueOrDefault(key.ToString(CultureInfo.InvariantCulture));
}
