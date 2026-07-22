using dc.en;
using dc.hl.types;
using dc.pr;
using dc.tool;
using dc.tool.hero;
using ModCore.Storage;

namespace DeadCellsMultiplayerMod.Ghost.GhostBase
{
    public class KingActiveSkillsManager : HeroActiveSkillsManager, IHxbitSerializable<object>
    {
        private static Hero? lastKnownHero;
        private static readonly Random rng = new();

        private Hero? me;
        private GhostKing? king;
        private Level? lvl;

        public InventItem? equippedWeapon;

        public KingActiveSkillsManager() : base(GetFallbackHero())
        {
            me = lastKnownHero;
        }

        public KingActiveSkillsManager(Hero hero, GhostKing kingSkin, Level level) : base(hero)
        {
            me = hero;
            king = kingSkin;
            lvl = level;
            lastKnownHero = hero;
        }


        public override void init()
        {
            if (this.activeSkills == null)
                this.activeSkills = new ArrayObj();
            if (this.passivePowers == null)
                this.passivePowers = new ArrayObj();
            base.init();
        }


        object IHxbitSerializable<object>.GetData()
        {
            return new();
        }

        void IHxbitSerializable<object>.SetData(object data)
        {
        }

        private static Hero GetFallbackHero()
        {
            var hero = ModEntry.me ?? dc.pr.Game.Class.ME?.hero;
            if (hero != null)
                return hero;

            if (lastKnownHero != null)
                return lastKnownHero;

            throw new InvalidOperationException("KingActiveSkillsManager deserialization requires a Hero.");
        }
    }
}
