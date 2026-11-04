using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GameServer.Core.Protocol;
using GameServer.Core.Protocol.Instance;
using GameServer.Data.Dat;
using GameServer.Data.Ggpk;
using GgDump;

// What the servers need from the client as they run, so they never open Client/ (extract-data.ps1 runs both):
//   constants   data/client-constants.json: the HNC mask of Client/Game.exe, the root hash of Client/Content.ggpk
//   pack        data/game-files.zip: the game files GamePack covers
//   stat-descriptions   data/stat-descriptions.txt: the English stat descriptions (StatDescriptions.cs), for tools
//   localize    data/localized/<tag>.json and <tag>-stat-descriptions.txt: the client's translated item classes, base
//               and affix names, rare-name words and modifier wording, which the Gateway's `crafter` mode sends beside
//               the English text, for tools such as the desktop launcher's item crafter
const string Ggpk = "Client/Content.ggpk";

const string Usage =
    """
    game-pack - the client files the servers read as they run

    usage:
      game-pack constants
      game-pack pack
      game-pack stat-descriptions
      game-pack localize <ggpk> <language folder> <language name> <tag> <names.json> <descriptions.txt>
    """;

switch (args.FirstOrDefault())
{
    case "constants":
        byte[] exe = File.ReadAllBytes("Client/Game.exe");
        HncSender.LoadMask(exe); // Checks the mask against its known hash.
        byte[] mask = PeImage.ReadAtVa(exe, HncSender.MaskVa, 64);
        new ClientConstants(Convert.ToHexString(mask), Convert.ToHexString(GgpkMaintenance.RootHash(Ggpk))).Save();
        Console.WriteLine($"wrote {ClientConstants.DefaultFile}");
        return 0;
    case "pack":
        using (var decoder = new NodeOodleDecoder("node", Path.GetFullPath("Tools/ooz/ooz_worker.mjs")))
        using (var fs = GameFileSystem.Open(Ggpk, decoder))
        {
            // In bundle order, so each bundle is read once.
            var paths = fs.EnumeratePaths().Where(GamePack.Covers)
                .Select(p => (Path: p, Location: fs.Index.TryGetFile(p, out var l) ? l : throw new InvalidDataException(p)))
                .OrderBy(f => f.Location.BundleIndex).ThenBy(f => f.Location.Offset).ToList();
            string temp = GamePack.DefaultFile + ".tmp";
            using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
                foreach (var (path, _) in paths)
                {
                    using var entry = zip.CreateEntry(path, CompressionLevel.Optimal).Open();
                    entry.Write(fs.ReadFile(path));
                }
            File.Move(temp, GamePack.DefaultFile, overwrite: true);
            Console.WriteLine($"wrote {GamePack.DefaultFile}: {paths.Count} files, {new FileInfo(GamePack.DefaultFile).Length >> 20} MB");
        }
        return 0;
    case "stat-descriptions":
        using (var decoder = new NodeOodleDecoder("node", Path.GetFullPath("Tools/ooz/ooz_worker.mjs")))
        using (var fs = GameFileSystem.Open(Ggpk, decoder))
        {
            const string source = "metadata/statdescriptions/stat_descriptions.txt", target = "data/stat-descriptions.txt";
            // UTF-16 with every translation after the English lines of a description: a `lang "…"` line starts them, and
            // they run to the next unindented line.
            string text = System.Text.Encoding.Unicode.GetString(fs.ReadFile(source)).TrimStart('﻿');
            var english = new List<string>();
            bool translation = false;
            foreach (string line in text.Split('\n').Select(l => l.TrimEnd('\r')))
            {
                if (line.Length > 0 && !char.IsWhiteSpace(line[0])) translation = false;
                else if (line.TrimStart().StartsWith("lang ", StringComparison.Ordinal)) translation = true;
                if (!translation) english.Add(line);
            }
            File.WriteAllLines(target + ".tmp", english);
            File.Move(target + ".tmp", target, overwrite: true);
            Console.WriteLine($"wrote {target}: {english.Count} lines");
        }
        return 0;
    case "localize":
        return Localize(args);
    default:
        Console.Error.WriteLine(Usage);
        return 2;
}

/// <summary>
/// The client's translated game data for tools (the Gateway's `crafter` mode): the item class, base and affix names and
/// the rare-name words of `data/&lt;language folder&gt;/&lt;table&gt;.datc64`, and the modifier stat descriptions of the
/// `lang "&lt;language name&gt;"` blocks of `Metadata/StatDescriptions/stat_descriptions.txt`, written as one JSON file
/// and one file in the English stat descriptions' own format. Names and descriptions the client leaves untranslated are
/// left out, so a tool falls back to English for them. An updated or otherwise changed client regenerates this with the
/// rest of the extraction (extract-data.ps1's `localized` entry), and the files say which language they hold: a tool
/// shows them only in an interface of that language.
/// </summary>
static int Localize(string[] args)
{
    if (args.Length < 7)
    {
        Console.Error.WriteLine(Usage);
        return 2;
    }
    string ggpk = args[1], folder = args[2], language = args[3], tag = args[4], namesOut = args[5], descriptionsOut = args[6];
    using var oodle = new NodeOodleDecoder("node", Path.GetFullPath("Tools/ooz/ooz_worker.mjs"));
    using var fs = GameFileSystem.Open(ggpk, oodle);
    var schema = DatSchema.LoadFile("data/schema/schema.min.json");
    var classes = Table(fs, schema, folder, "itemclasses", "Id", "Name");
    var bases = Table(fs, schema, folder, "baseitemtypes", "Id", "Name");
    var mods = Table(fs, schema, folder, "mods", "Id", "Name");
    // The rare-name words are translated in Text2; Text stays English there (`Words.Text2` is the display column: it also
    // corrects the English typos Word.Text has).
    var words = Table(fs, schema, folder, "words", "HASH32", "Text2");
    var blocks = Descriptions(fs.ReadFile("metadata/statdescriptions/stat_descriptions.txt"), language);
    // The client keeps its Chinese pack in Simplified Chinese (metadata/ChineseSimplifiedToTraditional.txt converts it
    // for a Traditional display, which tools do not use), so the text is written as the client has it.
    var localized = new SortedDictionary<string, string>[]
    {
        Names(classes), Names(bases), Names(mods), Names(words),
    };
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(namesOut))!);
    var root = new JsonObject
    {
        // The language of the text below, for a tool to match against its own interface language; empty when the
        // client has no translated text for this language.
        ["tag"] = localized.Any(l => l.Count > 0) ? tag : "",
        ["classes"] = JsonSerializer.SerializeToNode(localized[0]),
        ["bases"] = JsonSerializer.SerializeToNode(localized[1]),
        ["mods"] = JsonSerializer.SerializeToNode(localized[2]),
        ["words"] = JsonSerializer.SerializeToNode(localized[3]),
    };
    File.WriteAllText(namesOut + ".tmp", root.ToJsonString());
    File.Move(namesOut + ".tmp", namesOut, overwrite: true);
    int described = WriteDescriptions(blocks, descriptionsOut);
    Console.WriteLine($"wrote {namesOut}: {localized[0].Count} classes, {localized[1].Count} bases, {localized[2].Count} mods, {localized[3].Count} words, tag '{root["tag"]}'");
    Console.WriteLine($"wrote {descriptionsOut}: {described} descriptions");
    return 0;
}

/// <summary>
/// A table's localized names by the key the servers know it by, each with the English it translates: the English table
/// and the localized one in <c>data/&lt;folder&gt;/</c> are paired by that key. An absent or unreadable localized table
/// gives nothing, and the tool keeps English.
/// </summary>
static List<(string Key, string English, string Localized)> Table(GameFileSystem fs, IReadOnlyDictionary<string, SchemaTable> schema,
    string folder, string table, string keyColumn, string textColumn)
{
    var rows = new List<(string, string, string)>();
    if (!schema.TryGetValue(table, out var definition)) return rows;
    if (!fs.TryReadFile($"data/{table}.datc64", out byte[] english) || !fs.TryReadFile($"data/{folder}/{table}.datc64", out byte[] localized))
        return rows;
    try
    {
        var original = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in DatFile.Parse(english).ReadRows(definition))
            if (Text(row, keyColumn) is { Length: > 0 } key && Text(row, textColumn) is { } text)
                original.TryAdd(key, text);
        foreach (var row in DatFile.Parse(localized).ReadRows(definition))
            if (Text(row, keyColumn) is { Length: > 0 } key && Text(row, textColumn) is { Length: > 0 } text
                && original.TryGetValue(key, out string? source))
                rows.Add((key, source, text));
    }
    catch (DatFormatException)
    {
        // A client whose localized tables have another layout: tools keep English.
        rows.Clear();
    }
    return rows;
}

static string? Text(IReadOnlyDictionary<string, DatValue> row, string column) =>
    row.TryGetValue(column, out var value)
        ? value.Raw switch
        {
            string s => s,
            ulong u => u.ToString(CultureInfo.InvariantCulture),
            long l => l.ToString(CultureInfo.InvariantCulture),
            _ => null,
        }
        : null;

/// <summary>The names a tool shows: only those the translation changes, so "absent" means "English".</summary>
static SortedDictionary<string, string> Names(List<(string Key, string English, string Localized)> rows)
{
    var names = new SortedDictionary<string, string>(StringComparer.Ordinal);
    foreach (var (key, english, localized) in rows)
        if (localized is { Length: > 0 } text && text != english)
            names[key] = text;
    return names;
}
/// <summary>
/// The <c>lang "&lt;language&gt;"</c> blocks of the client's stat descriptions: the description's own stats (the English
/// line the client pairs them with) and the translated variant lines. Blocks whose translated variant count differs from
/// the English one are left out, so every variant keeps its own conditions and handlers.
/// </summary>
static List<(string Head, string Count, List<string> Variants)> Descriptions(byte[] file, string language)
{
    string text = Encoding.Unicode.GetString(file).TrimStart('﻿').Replace("\r\n", "\n");
    string[] lines = text.Split('\n');
    var blocks = new List<(string, string, List<string>)>();
    for (int i = 0; i < lines.Length; i++)
    {
        if (lines[i].Trim() != "description") continue;
        // description / <stat count> <stats> / <variant count> / <variant lines>
        if (i + 2 >= lines.Length || !int.TryParse(lines[i + 2].Trim(), out int englishCount)) continue;
        for (int lang = i + 3 + englishCount; lang + 1 < lines.Length && lines[lang].TrimStart().StartsWith("lang ", StringComparison.Ordinal);)
        {
            if (!int.TryParse(lines[lang + 1].Trim(), out int translatedCount)) break;
            if (translatedCount == englishCount && Regex.Match(lines[lang], "^\\s*lang\\s+\"(?<name>[^\"]*)\"\\s*$").Groups["name"].Value == language)
            {
                blocks.Add((lines[i + 1], lines[lang + 1], [.. lines[(lang + 2)..(lang + 2 + translatedCount)]]));
                break;
            }
            lang += 2 + translatedCount;
        }
    }
    return blocks;
}

/// <summary>Writes the translated descriptions in the English file's format (description, its stats, the variant count,
/// the variants), which the Gateway's StatDescriptions reads.</summary>
static int WriteDescriptions(List<(string Head, string Count, List<string> Variants)> blocks, string output)
{
    var lines = new List<string>();
    foreach (var (head, count, variants) in blocks)
    {
        lines.Add("description");
        lines.Add(head);
        lines.Add(count);
        foreach (string variant in variants) lines.Add(variant);
    }
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
    File.WriteAllLines(output + ".tmp", lines);
    File.Move(output + ".tmp", output, overwrite: true);
    return blocks.Count;
}
