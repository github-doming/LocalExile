/// <summary>
/// The stat descriptions (data/stat-descriptions.txt: Metadata/StatDescriptions/stat_descriptions.txt without its
/// translations, `game-pack stat-descriptions`): how the client words a mod's stats ("{0:+d} to maximum Life"). A
/// description covers one or more stats; each variant applies when every stat's value meets its condition ("#" any,
/// "5" exactly, "1|#" at least 1, "#|-1" at most -1, "!0" not 0), and its handlers ("negate 1") change a value before it
/// is shown. Tools read them through the Gateway's `crafter` mode; the server itself words nothing with them. A checkout
/// whose client has a Chinese pack also has their translations (extract-data.ps1's `localized` entry,
/// <see cref="AddTranslations"/>): the same descriptions and variants, worded in Chinese.
/// </summary>
sealed class StatDescriptions
{
    public const string DefaultFile = "data/stat-descriptions.txt";

    /// <param name="Conditions">One per stat, as written ("#", "1|#", "!0").</param>
    /// <param name="Handlers">(name, 1-based stat index), in order.</param>
    public sealed record Variant(IReadOnlyList<string> Conditions, string Text, IReadOnlyList<(string Name, int Stat)> Handlers);

    public sealed record Description(IReadOnlyList<string> Stats, IReadOnlyList<Variant> Variants);

    readonly Dictionary<string, Description> _byStat = new(StringComparer.Ordinal);

    /// <summary>The translated descriptions by their stats, when the checkout has them.</summary>
    readonly Dictionary<string, Description> _translations = new(StringComparer.Ordinal);

    public IReadOnlyList<Description> All { get; }

    StatDescriptions(List<Description> all)
    {
        All = all;
        foreach (var d in all)
            foreach (string stat in d.Stats) _byStat.TryAdd(stat, d);
    }

    /// <summary>The first description that covers the stat, or null.</summary>
    public Description? For(string stat) => _byStat.GetValueOrDefault(stat);

    /// <summary>
    /// Loads the translated wordings of the same descriptions from a file in this one's format (extract-data.ps1's
    /// `localized` entry). A missing file, a description it lacks or one whose variant count differs stays English, so a
    /// client version that changed its descriptions reads correctly.
    /// </summary>
    public void AddTranslations(string path)
    {
        if (!File.Exists(path)) return;
        foreach (var d in Parse(File.ReadLines(path)).All) _translations[string.Join('\n', d.Stats)] = d;
    }

    /// <summary>
    /// The translated text of each variant, in the English variants' order, or null when this checkout has no translation
    /// of the description. A Chinese variant and its English one word the same description: they share their conditions
    /// and handlers, so a variant whose conditions differ stays English.
    /// </summary>
    public IReadOnlyList<string?>? Translations(Description description)
    {
        if (!_translations.TryGetValue(string.Join('\n', description.Stats), out var translated)) return null;
        if (translated.Variants.Count != description.Variants.Count) return null;
        var texts = new string?[description.Variants.Count];
        for (int i = 0; i < texts.Length; i++)
            if (description.Variants[i].Conditions.SequenceEqual(translated.Variants[i].Conditions)
                && translated.Variants[i].Text.Length > 0)
                texts[i] = translated.Variants[i].Text;
        return texts;
    }

    public static StatDescriptions Load(string path = DefaultFile) =>
        File.Exists(path) ? Parse(File.ReadLines(path)) : new([]);

    public static StatDescriptions Parse(IEnumerable<string> lines)
    {
        var all = new List<Description>();
        using var e = lines.GetEnumerator();
        bool more = e.MoveNext();
        while (more)
        {
            if (e.Current.Trim() != "description" && !e.Current.StartsWith("description ", StringComparison.Ordinal)) { more = e.MoveNext(); continue; }
            // "\t2 stat_a stat_b", "\tN" variants, then N variant lines.
            if (!(more = e.MoveNext())) break;
            string[] head = e.Current.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (head.Length < 2 || !int.TryParse(head[0], out int statCount)) continue;
            var stats = head.Skip(1).Take(statCount).ToList();
            if (!(more = e.MoveNext())) break;
            if (!int.TryParse(e.Current.Trim(), out int count)) continue;
            var variants = new List<Variant>();
            for (int i = 0; i < count && (more = e.MoveNext()); i++)
                if (ParseVariant(e.Current, stats.Count) is { } v) variants.Add(v);
            all.Add(new(stats, variants));
            more = e.MoveNext();
        }
        return new(all);
    }

    /// <summary>`1|# "text" negate 1 reminderstring ReminderTextX canonical_line`: conditions, text, handlers.</summary>
    static Variant? ParseVariant(string line, int stats)
    {
        int open = line.IndexOf('"'), close = line.LastIndexOf('"');
        if (open < 0 || close <= open) return null;
        var conditions = line[..open].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Take(stats).ToList();
        while (conditions.Count < stats) conditions.Add("#");
        var handlers = new List<(string, int)>();
        string[] tail = line[(close + 1)..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < tail.Length; i++)
        {
            if (tail[i] == "reminderstring") { i++; continue; }
            if (i + 1 < tail.Length && int.TryParse(tail[i + 1], out int index)) { handlers.Add((tail[i], index)); i++; }
        }
        return new(conditions, Markup(line[(open + 1)..close]), handlers);
    }

    /// <summary>
    /// The text as the client shows it: without its link markup ("[ContainsAbyss|Abyss]" is "Abyss", "[Toughness]"
    /// "Toughness") and its display markup ("&lt;DF1&gt;" picks a value's colour and "{{", "}}" wrap a value the client
    /// fills in; the client draws the value, not the markers).
    /// </summary>
    static string Markup(string text) => System.Text.RegularExpressions.Regex.Replace(
        System.Text.RegularExpressions.Regex.Replace(text, @"\[([^\]|]*)(?:\|([^\]]*))?\]", m => m.Groups[2].Success ? m.Groups[2].Value : m.Groups[1].Value),
        @"<[A-Za-z][A-Za-z0-9]*>|\{\{|\}\}", "");
}
