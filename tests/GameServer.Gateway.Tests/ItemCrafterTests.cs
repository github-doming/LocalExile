using System.Text.Json.Nodes;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class ItemCrafterTests
{
    [Fact]
    public void Build_makes_the_composed_item_and_refuses_what_does_not_exist()
    {
        var items = new ItemCatalog("data");
        var rolls = new ItemRolls(items);
        var ring = items.All.First(t => t.Name == "Iron Ring");
        var words = rolls.Words.Where(w => w.List is 1 or 2).GroupBy(w => w.List).Select(g => g.First().Key).ToList();
        string json = $$"""
            {"base":"{{ring.Id}}","rarity":"rare","itemLevel":84,"states":["corrupted","shaper","Shaper"],"fractured":1,
             "name":[{{words[0]}},{{words[1]}}],
             "mods":[{"id":"{{ring.Implicits![0].Id}}","values":[3]},{"id":"IncreasedLife7","values":[114]},
                     {"id":"FireResist1","section":"explicit"}]}
            """;
        var item = ItemCrafter.Build(items, rolls, json, 10, new Random(3), out string? error);
        Assert.Null(error);
        Assert.NotNull(item);
        Assert.Equal(ring.Hash, item.TypeHash);
        Assert.Equal((ItemRarity.Rare, (byte)84, true, ItemInfluence.Shaper, (byte)1), (item.Rarity, item.ItemLevel, item.Corrupted, item.Influences, item.FracturedCount));
        Assert.Equal(words, item.NameWords);
        Assert.Equal([ModSection.Implicit, ModSection.Explicit, ModSection.Explicit], item.Mods.Select(m => m.Section));
        // The implicit adds physical damage, a minimum and a maximum: the missing maximum is rolled.
        Assert.Equal((3, ring.Implicits[0].Stats.Count), (item.Mods[0].Values[0], item.Mods[0].Values.Count));
        Assert.Equal([114], item.Mods[1].Values);
        var fire = items.Mods.ById("FireResist1")!;
        Assert.InRange(Assert.Single(item.Mods[2].Values), fire.RangeAt(0).Min, fire.RangeAt(0).Max);

        var armour = items.All.First(t => t.Class == "Body Armour" && ItemCrafter.Craftable(items, t));
        var linked = ItemCrafter.Build(items, rolls, $$"""{"base":"{{armour.Id}}","sockets":"RRG B"}""", 70, new Random(1), out _)!;
        Assert.Equal([SocketColour.Red, SocketColour.Red, SocketColour.Green, SocketColour.Blue], linked.Sockets.Select(s => s.Colour));
        Assert.Equal([3, 1], linked.Links!);
        Assert.Equal((ItemRarity.Normal, (byte)70), (linked.Rarity, linked.ItemLevel));
        Assert.Empty(ItemCrafter.Build(items, rolls, $$"""{"base":"{{armour.Id}}","sockets":""}""", 70, new Random(1), out _)!.Sockets);

        foreach (var (bad, reason) in new[]
        {
            ("""{"base":"No Such Ring"}""", "No item type"),
            ($$"""{"base":"{{ring.Id}}","mods":[{"id":"NoSuchMod"}]}""", "No mod"),
            ($$"""{"base":"{{ring.Id}}","states":["clear"]}""", "no state"),
            ($$"""{"base":"{{ring.Id}}","rarity":"legendary"}""", "no rarity"),
            ("not json", "not an item"),
        })
        {
            Assert.Null(ItemCrafter.Build(items, rolls, bad, 10, new Random(1), out error));
            Assert.StartsWith(reason, error);
        }
    }

    [Fact]
    public void Catalog_lists_a_bases_pools_and_the_descriptions_of_their_stats()
    {
        var descriptions = StatDescriptions.Parse("""
            description
            	1 base_maximum_life
            	1
            		# "{0:+d} to maximum Life"
            description
            	2 attack_minimum_added_physical_damage attack_maximum_added_physical_damage
            	2
            		1|# # "Adds {0} to {1} [Physical] Damage to Attacks" reminderstring ReminderTextX canonical_line
            		#|-1 # "Removes [Physical|Physical Damage] {0}" negate 1
            no_description level
            """.Split('\n'));
        var life = descriptions.For("base_maximum_life")!;
        Assert.Equal(["#"], Assert.Single(life.Variants).Conditions);
        var added = descriptions.For("attack_maximum_added_physical_damage")!;
        Assert.Same(added, descriptions.For("attack_minimum_added_physical_damage"));
        Assert.Equal(("Adds {0} to {1} Physical Damage to Attacks", 0), (added.Variants[0].Text, added.Variants[0].Handlers.Count));
        Assert.Equal(["#|-1", "#"], added.Variants[1].Conditions);
        Assert.Equal(("Removes Physical Damage {0}", ("negate", 1)), (added.Variants[1].Text, Assert.Single(added.Variants[1].Handlers)));

        var items = new ItemCatalog("data");
        var rolls = new ItemRolls(items);
        var lines = ItemCrafter.Catalog(items, rolls, new BenchCrafting(items, rolls), descriptions).ToList();
        var pools = lines.Where(l => (string?)l["type"] == "pool").ToDictionary(l => (int)l["id"]!, l => l["mods"]!.AsArray().Select(m => (string)m!).ToList());
        var ring = lines.Single(l => (string?)l["type"] == "base" && (string?)l["name"] == "Iron Ring");
        Assert.Contains("IncreasedLife7", pools[(int)ring["pool"]!]);
        Assert.DoesNotContain(pools[(int)ring["pool"]!], id => pools[(int)ring["influence"]!["shaper"]!].Contains(id));
        Assert.Contains(lines, l => (string?)l["type"] == "mod" && (string?)l["id"] == "IncreasedLife7" && (int)l["level"]! == 44);
        var desc = Assert.Single(lines, l => (string?)l["type"] == "desc" && l["stats"]!.AsArray().Select(s => (string)s!).SequenceEqual(["base_maximum_life"]));
        Assert.Equal("{0:+d} to maximum Life", (string?)desc["variants"]![0]!["text"]);
        Assert.Contains(lines, l => (string?)l["type"] == "word" && (int)l["list"]! == 1);
        Assert.DoesNotContain(lines, l => (string?)l["type"] == "base" && items.Find((string)l["id"]!) is { } t && items.Gem(t) is not null);
    }

    [Fact]
    public void Catalog_offers_the_clients_translated_names_beside_the_English_ones()
    {
        var items = new ItemCatalog("data");
        var rolls = new ItemRolls(items);
        string folder = Path.Combine(Path.GetTempPath(), $"localexile-localized-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(folder, "localized"));
        var belt = items.All.First(t => t.Name == "Vanguard Belt");
        File.WriteAllText(Path.Combine(folder, "localized", "zh-CN.json"), $$$"""
            {"tag":"zh-CN","classes":{"{{{belt.Class}}}":"腰带"},"bases":{"{{{belt.Id}}}":"先锋腰带"},
             "mods":{"IncreasedLife7":"丰饶的"},"words":{"4053401100":"不祥"}}
            """);
        File.WriteAllText(Path.Combine(folder, "localized", "zh-CN-stat-descriptions.txt"), """
            description
            	1 base_maximum_life
            	1
            		# "{0:+d} 最大生命"
            """);
        // The checkout's English descriptions, paired with the translated ones the way the Gateway's `crafter` mode does.
        var descriptions = StatDescriptions.Parse("""
            description
            	1 base_maximum_life
            	1
            		# "{0:+d} to maximum Life"
            """.Split('\n'));
        var localization = CrafterLocalization.Load(folder)!;
        descriptions.AddTranslations(localization.Descriptions);

        var lines = ItemCrafter.Catalog(items, rolls, new BenchCrafting(items, rolls), descriptions, "data", localization).ToList();
        Assert.Contains(lines, l => (string?)l["type"] == "locale" && (string?)l["tag"] == "zh-CN");
        Assert.Contains(lines, l => (string?)l["type"] == "class" && (string?)l["id"] == belt.Class && (string?)l["nameLocalized"] == "腰带");
        var baseLine = lines.Single(l => (string?)l["type"] == "base" && (string?)l["id"] == belt.Id);
        Assert.Equal(("Vanguard Belt", "先锋腰带"), ((string?)baseLine["name"], (string?)baseLine["nameLocalized"]));
        Assert.Contains(lines, l => (string?)l["type"] == "mod" && (string?)l["id"] == "IncreasedLife7" && (string?)l["nameLocalized"] == "丰饶的");
        Assert.Contains(lines, l => (string?)l["type"] == "word" && (string?)l["textLocalized"] == "不祥");
        var desc = lines.Single(l => (string?)l["type"] == "desc" && l["stats"]!.AsArray().Select(s => (string)s!).SequenceEqual(["base_maximum_life"]));
        var variant = desc["variants"]![0]!;
        Assert.Equal(("{0:+d} to maximum Life", "{0:+d} 最大生命"), ((string?)variant["text"], (string?)variant["textLocalized"]));

        // A checkout without the client's translated files — an older one, or one that has not run the extraction — keeps
        // the English catalog, and so does a client whose pack has no text for a name: the fields are optional.
        string plain = Path.Combine(folder, "plain");
        Directory.CreateDirectory(plain);
        var english = ItemCrafter.Catalog(items, rolls, new BenchCrafting(items, rolls), descriptions, plain).ToList();
        Assert.DoesNotContain(english, l => (string?)l["type"] == "locale");
        Assert.DoesNotContain(english, l => l["nameLocalized"] is not null || l["textLocalized"] is not null);
        Directory.Delete(folder, recursive: true);
    }

    [Fact]
    public void A_translated_description_keeps_its_conditions_and_loses_the_clients_markup()
    {
        var descriptions = StatDescriptions.Parse("""
            description
            	1 base_maximum_life
            	2
            		1|# "{0:+d} to maximum Life"
            		#|-1 "<DA1>{{{0:+d}}} reduced maximum Life" negate 1
            """.Split('\n'));
        string folder = Path.Combine(Path.GetTempPath(), $"localexile-localized-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "zh-CN-stat-descriptions.txt"), """
            description
            	1 base_maximum_life
            	2
            		1|# "{0:+d} 最大生命"
            		#|-1 "<DA1>{{{0:+d}}} 降低最大生命" negate 1
            """);
        descriptions.AddTranslations(Path.Combine(folder, "zh-CN-stat-descriptions.txt"));
        var life = descriptions.For("base_maximum_life")!;
        // The client's display markup is drawn, not read: "[A|B]" is B, "<DF1>"/ "<DA1>" pick colours, "{{" and "}}" wrap
        // the value the client fills in.
        Assert.Equal(["{0:+d} to maximum Life", "{0:+d} reduced maximum Life"], life.Variants.Select(v => v.Text));
        Assert.Equal(["{0:+d} 最大生命", "{0:+d} 降低最大生命"], descriptions.Translations(life));
        Directory.Delete(folder, recursive: true);
    }
}
