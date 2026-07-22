using dc.en;
using DeadCellsMultiplayerMod.Ghost.GhostBase;
 
namespace DeadCellsMultiplayerMod
{
    public static class _DeadBase
    {
        public static DeadBase deadBase = null!;
        public static Hero owen = null!;
        public static GhostKing? k = null;

        public static void EnterGhostDead(DeadBase @base, Hero hero, GhostKing? king)
        {
            deadBase = @base;
            owen = hero;
            k = king;

            @base.disableShowHUD = false;
            @base.cancellable = false;
        }
    }
}
