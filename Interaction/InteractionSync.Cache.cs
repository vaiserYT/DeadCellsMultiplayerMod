using dc.en;
using dc.en.inter;

namespace DeadCellsMultiplayerMod.Interaction;

public partial class InteractionSync
{
    private sealed class LevelInteractionCache
    {
        public readonly List<Door> Doors = new();
        public readonly List<Elevator> Elevators = new();
        public readonly List<VineLadder> VineLadders = new();
        public readonly List<Teleport> Teleports = new();
        public readonly List<Portal> Portals = new();
        public readonly List<PressurePlate> PressurePlates = new();
        public readonly List<dc.en.inter.button.Button> Buttons = new();
        public readonly List<TreasureChest> TreasureChests = new();
        public readonly List<SwitchBossRune> SwitchBossRunes = new();
        public readonly List<Elevator> TriggerElevators = new();
        public readonly List<Teleport> TriggerTeleports = new();
        public readonly List<Portal> TriggerPortals = new();
        public readonly List<dc.en.inter.button.Button> TriggerButtons = new();

        public void Clear()
        {
            Doors.Clear();
            Elevators.Clear();
            VineLadders.Clear();
            Teleports.Clear();
            Portals.Clear();
            PressurePlates.Clear();
            Buttons.Clear();
            TreasureChests.Clear();
            SwitchBossRunes.Clear();
            TriggerElevators.Clear();
            TriggerTeleports.Clear();
            TriggerPortals.Clear();
            TriggerButtons.Clear();
        }
    }
}
