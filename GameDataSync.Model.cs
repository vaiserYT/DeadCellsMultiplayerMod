using System.Collections.Generic;

namespace DeadCellsMultiplayerMod
{
    public class GameDataSync
    {
        public int Seed { get; set; }
        public string StartLevel { get; set; } = string.Empty;

        public bool IsCustom { get; set; }
        public int BossRune { get; set; }
        public int RunNum { get; set; }
        public string EndKind { get; set; } = string.Empty;
        public bool HasMods { get; set; }

        public List<int> Forge { get; set; } = new();
        public List<string> Meta { get; set; } = new();

        public List<HistoryEntry> History { get; set; } = new();

        public class HistoryEntry
        {
            public string Level { get; set; } = string.Empty;
            public int Brut { get; set; }
            public int Surv { get; set; }
            public int Tact { get; set; }
            public int CellsEarned { get; set; }
            public float Time { get; set; }
        }
    }
}
