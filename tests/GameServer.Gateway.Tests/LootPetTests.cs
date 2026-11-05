using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

// The loot pet (LootPet.cs): what each mode takes, /loot_pet's per-character settings with their bounds, and the
// dropped-item exemption belonging to the area instance.
public sealed class LootPetTests
{
    static ItemCatalog.BaseType Type(string name) => Program.Items.Find(name) ?? throw new InvalidOperationException($"no item type {name}");

    static bool Takes(string name, Game.LootPetMode mode) => Game.LootPetTakes(Type(name), mode);

    [Fact]
    public void Modes_take_their_items()
    {
        Assert.True(Takes("Chaos Orb", Game.LootPetMode.Valuable));
        Assert.False(Takes("Scroll of Wisdom", Game.LootPetMode.Valuable));
        Assert.True(Takes("Scroll of Wisdom", Game.LootPetMode.Currency));
        Assert.True(Takes("Rusted Sword", Game.LootPetMode.All));
        Assert.False(Takes("Rusted Sword", Game.LootPetMode.Currency));
        Assert.False(Game.LootPetTakes(null, Game.LootPetMode.All));
    }

    [Fact]
    public void Settings_are_per_character_and_bounded()
    {
        var conn = new ClientState(new(1, "loot-pet", 1000, 1000, 100, 100, null, []), 1, 1) { Character = $"LootPet{Guid.NewGuid():N}" };
        Assert.Equal(Game.LootPetMode.All, Game.LootPetOf(conn).Mode);
        Assert.Contains("mode all, radius 60", Game.LootPetStatus(conn));

        Game.SetLootPetEnabled(conn, false);
        Game.SetLootPetMode(conn, "CURRENCY");
        Game.SetLootPetRadius(conn, 25.5);
        var again = new ClientState(new(1, "loot-pet", 1000, 1000, 100, 100, null, []), 1, 1) { Character = conn.Character };
        Assert.False(Game.LootPetOf(again).Enabled);
        Assert.Equal(Game.LootPetMode.Currency, Game.LootPetOf(again).Mode);
        Assert.Equal(25.5, Game.LootPetOf(again).Radius);
        Assert.Contains("Loot pet: off, mode currency, radius 25.5", Game.LootPetStatus(again));

        Assert.Throws<ArgumentException>(() => Game.SetLootPetMode(conn, "everything"));
        Assert.Throws<ArgumentException>(() => Game.SetLootPetMode(conn, "3"));
        Assert.Throws<ArgumentException>(() => Game.SetLootPetRadius(conn, 0));
        Assert.Throws<ArgumentException>(() => Game.SetLootPetRadius(conn, 201));
        Assert.Throws<ArgumentException>(() => Game.SetLootPetRadius(conn, double.NaN));

        // A drop is exempt for the pet here until the area instance changes; its id may be unknown for one tick.
        var here = new ClientState(new(1, "loot-pet", 1000, 1000, 100, 100, null, []), 1, 1) { Character = conn.Character, InstanceNumber = 7 };
        var ground = new WorldItemObject(3, 4, ItemCatalog.Create(Type("Chaos Orb")));
        Assert.False(Game.LootPetDroppedHere(here, 100, ground));
        Game.LootPetDropping(here, ground);
        Assert.True(Game.LootPetDroppedHere(here, 100, ground));
        Game.LootPetDropped(here, 100, ground);
        Assert.True(Game.LootPetDroppedHere(here, 100, ground));
        Assert.False(Game.LootPetDroppedHere(here, 101, ground));
        var elsewhere = new ClientState(new(1, "loot-pet", 1000, 1000, 100, 100, null, []), 1, 1) { Character = conn.Character, InstanceNumber = 8 };
        Assert.False(Game.LootPetDroppedHere(elsewhere, 100, ground));
        Game.LootPetDropping(elsewhere, ground);
        Game.LootPetDropped(elsewhere, 200, ground);
        Assert.True(Game.LootPetDroppedHere(elsewhere, 200, ground));
    }
}
