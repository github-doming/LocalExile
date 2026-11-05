using System.Collections.Concurrent;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

static partial class Game
{
    // The loot pet: a pet cosmetic worn in one of the Cosmetics tab's Pet slots (18, 19) takes ground items into the main
    // inventory on the player's tick (RunPlayerTick, PetPickUp), the way a click's pickup puts them there. The mode decides
    // what it takes and the radius how far (cells, centre to centre; wide, like the click's 20, so a fight's drops are all
    // taken; PetPickUp ignores walls and floors between, as a click's pickup does not). Kept per character in the loaded
    // game module, so a module reload starts it over; /loot_pet changes it.
    internal enum LootPetMode
    {
        All,        // every ground item: quest items and gold through the paths a click's pickup takes
        Valuable,   // the orbs worth taking on their own (ValuableCurrency, the first loot pet's list)
        Currency,   // every currency-class item, the cheap ones included
    }

    internal sealed class LootPetSettings
    {
        public bool Enabled = true;
        public LootPetMode Mode = LootPetMode.All;
        public double Radius = LootPetRadius;
        // A full inventory leaves the items where they lie: say so once every few seconds, not on every tick.
        public double NextFullLogAt = double.NegativeInfinity;
        // The ground items this character dropped (LootPetDropping): the pet leaves them where they fell for the rest of
        // the area instance, so that making room is not undone (a click still takes one). Pending covers the instant
        // between publishing a drop and knowing its id.
        public readonly HashSet<uint> Dropped = [];
        public readonly HashSet<WorldItemObject> Pending = new(ReferenceEqualityComparer.Instance);
        public ulong? DroppedIn;
    }

    static readonly ConcurrentDictionary<string, LootPetSettings> LootPets = new(StringComparer.OrdinalIgnoreCase);

    internal static LootPetSettings LootPetOf(ClientState conn) =>
        LootPets.GetOrAdd(CharacterName(conn), _ => new LootPetSettings());

    // /loot_pet's state line.
    internal static string LootPetStatus(ClientState conn)
    {
        var pet = LootPetOf(conn);
        return $"Loot pet: {(pet.Enabled ? "on" : "off")}, mode {pet.Mode.ToString().ToLowerInvariant()}, radius {pet.Radius:0.#} cells. " +
            "Worn pets in the Cosmetics tab's Pet slots (18, 19) pick the ground up while it is on. /loot_pet on|off, " +
            "/loot_pet mode all|valuable|currency, /loot_pet radius <1-200>.";
    }

    internal static void SetLootPetEnabled(ClientState conn, bool enabled) => LootPetOf(conn).Enabled = enabled;

    internal static void SetLootPetMode(ClientState conn, string mode)
    {
        if (!Enum.TryParse<LootPetMode>(mode, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
            throw new ArgumentException("mode all, valuable or currency");
        LootPetOf(conn).Mode = parsed;
    }

    internal static void SetLootPetRadius(ClientState conn, double cells)
    {
        if (!double.IsFinite(cells) || cells is < 1 or > 200) throw new ArgumentException("radius in 1 to 200 cells");
        LootPetOf(conn).Radius = cells;
    }

    // The character is about to drop a ground item here (Items.DropItem): recorded before the item is published, so that
    // a tick which sees it in between still leaves it alone; Items.LootPetDropped finishes the record with the object's id.
    internal static void LootPetDropping(ClientState conn, WorldItemObject ground)
    {
        var pet = LootPetOf(conn);
        lock (pet)
        {
            if (pet.DroppedIn != conn.InstanceNumber) { ClearDropped(pet, conn.InstanceNumber); }
            pet.Pending.Add(ground);
        }
    }

    internal static void LootPetDropped(ClientState conn, uint id, WorldItemObject ground)
    {
        var pet = LootPetOf(conn);
        lock (pet)
        {
            pet.Pending.Remove(ground);
            pet.Dropped.Add(id);
        }
    }

    static void ClearDropped(LootPetSettings pet, ulong instance)
    {
        pet.Dropped.Clear();
        pet.Pending.Clear();
        pet.DroppedIn = instance;
    }

    // Whether the character dropped this ground item here: the pet leaves it where it fell for the rest of the area
    // instance (a click still takes it).
    internal static bool LootPetDroppedHere(ClientState conn, uint id, WorldItemObject ground)
    {
        var pet = LootPetOf(conn);
        lock (pet) return pet.DroppedIn == conn.InstanceNumber && (pet.Dropped.Contains(id) || pet.Pending.Contains(ground));
    }

    /// <summary>Whether the loot pet takes this type: every type in <see cref="LootPetMode.All"/>, the valuable orbs only
    /// in <see cref="LootPetMode.Valuable"/>, any currency in <see cref="LootPetMode.Currency"/>.</summary>
    internal static bool LootPetTakes(ItemCatalog.BaseType? type, LootPetMode mode) => type is not null && mode switch
    {
        LootPetMode.All => true,
        LootPetMode.Valuable => ValuableCurrency.Contains(type.Id),
        LootPetMode.Currency => type.Class is "Currency" or "StackableCurrency" or "DelveSocketableCurrency" or "DelveStackableSocketableCurrency",
        _ => false,
    };

    // The orbs worth taking on their own and, by request, Chromatic Orbs and Gemcutter's Prisms. Cheap currency,
    // fragments, divination cards on the ground and equipment are left alone. Ids are BaseItemTypes' (the comment names
    // each one).
    static readonly HashSet<string> ValuableCurrency =
    [
        "Metadata/Items/Currency/CurrencyRerollRare",            // Chaos Orb
        "Metadata/Items/Currency/CurrencyRerollSocketColours",   // Chromatic Orb
        "Metadata/Items/Currency/CurrencyGemQuality",            // Gemcutter's Prism
        "Metadata/Items/Currency/CurrencyAddModToRare",          // Exalted Orb
        "Metadata/Items/Currency/CurrencyRemoveMod",             // Orb of Annulment
        "Metadata/Items/Currency/CurrencyModValues",             // Divine Orb
        "Metadata/Items/Currency/CurrencyDuplicate",             // Mirror of Kalandra
        "Metadata/Items/Currency/CurrencyFractureRare",          // Fracturing Orb
        "Metadata/Items/Currency/CurrencyRerollUnique",          // Ancient Orb
        "Metadata/Items/Currency/CurrencyHinekorasLock",         // Hinekora's Lock
        "Metadata/Items/Currency/CurrencyMutatedAddModToRare",   // Foulborn Exalted Orb
        "Metadata/Items/Currency/CurrencyHellscapeModValues",    // Volatile Vaal Orb
        "Metadata/Items/Currency/CurrencyHellscapeRerollRare",   // Tainted Chaos Orb
        "Metadata/Items/Currency/CurrencyHellscapeAddModToRare", // Tainted Exalted Orb
        "Metadata/Items/Currency/CurrencyEldritchRerollRare",    // Eldritch Chaos Orb
        "Metadata/Items/Currency/CurrencyEldritchAddModToRare",  // Eldritch Exalted Orb
        "Metadata/Items/Currency/CurrencyEldritchRemoveMod",     // Eldritch Orb of Annulment
        "Metadata/Items/Currency/CurrencyRerollRareVeiled",      // Veiled Exalted Orb
        "Metadata/Items/Currency/CurrencyRerollRareVeiledChaos", // Veiled Chaos Orb
        "Metadata/Items/Currency/CurrencyRerollDefences",        // Sacred Orb
        "Metadata/Items/Currency/CurrencyAtlasPassiveRefund",    // Orb of Unmaking
        "Metadata/Items/Currency/CurrencyUpgradeInfluenceMod",   // Orb of Dominance
        "Metadata/Items/Currency/CurrencyUpgradeMapTier",        // Harbinger's Orb
        "Metadata/Items/Currency/CurrencyRerollMapType",         // Orb of Horizons
        "Metadata/Items/Currency/CurrencyAddAtlasModHigh",       // Awakened Sextant
        "Metadata/Items/Currency/CurrencyAddAtlasModMaven",      // Elevated Sextant
        "Metadata/Items/AtlasExiles/ApplyInfluence",             // Awakener's Orb
        "Metadata/Items/DivinationCards/DivinationCardDeck",     // Stacked Deck
    ];

    // The loot pet's pickup (RunPlayerTick): a click's collection without the walk or the cursor (CollectGroundItem), so a
    // quest item, the tutorial's start weapon, gold, a stack and the inventory messages go the same way. A full inventory
    // leaves the item on the ground, with the news once every few seconds rather than on every tick; gold needs no room
    // (PickUpGold adds it to the stash's gold).
    internal static async Task PetPickUp(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id, WorldItemObject ground)
    {
        if (await RemoveDoneQuestItem(remote, stream, ciphers, conn, id, ground)) return;
        if (!IsGold(ground) && !ItemsOf(conn).Fits(PlayerInventories.MainInventory, ground.Item, ShapeOf))
        {
            double now = conn.Clock.Elapsed.TotalSeconds;
            var pet = LootPetOf(conn);
            if (now < pet.NextFullLogAt) return;
            pet.NextFullLogAt = now + 5;
            Console.WriteLine($"[gateway] {remote} loot pet: no room for {ground.Item.StackSize} of 0x{ground.Item.TypeHash:x8}; it stays on the ground");
            return;
        }
        await CollectGroundItem(remote, stream, ciphers, conn, id, ground, toCursor: false, "picked up by the loot pet");
    }
}
