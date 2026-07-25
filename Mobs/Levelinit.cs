using DeadCellsMultiplayerMod.Interface.ModuleInitializing;
using ModCore.Events;
using dc;
using dc.pr;
using ModCore.Mods;

namespace DeadCellsMultiplayerMod.Mobs.Levelinit;

public class Levelinit : ModBase, IEventReceiver, IOnAdvancedModuleInitializing
{
    public Levelinit(ModInfo info) : base(info)
    {
        EventSystem.AddReceiver(this);
    }

    void IOnAdvancedModuleInitializing.OnAdvancedModuleInitializing(ModEntry entry)
    {
        entry.Logger.Information("\x1b[32m[[ModEntry.Levelinit] Initializing Levelinit...]\x1b[0m ");
    }

    public override void Initialize()
    {
        base.Initialize();
        dc.pr.Hook_Level.init += Levelinit_Main;
        dc.pr.Hook_Level.entitiesPostCreate += Levelinit_EntitiesPostCreate;
        dc.pr.Hook_Level.onDispose += Levelinit_OnDispose;
    }

    private void Levelinit_EntitiesPostCreate(Hook_Level.orig_entitiesPostCreate orig, Level self)
    {
        orig(self);
        try
        {
            var purged = GhostHero.PurgeGhostKingsFromLevel(self);
            if (purged > 0)
                ModEntry.Instance?.Logger.Information("[NetMod] Purged {Count} GhostKing(s) after level create", purged);
        }
        catch (Exception ex)
        {
            ModEntry.Instance?.Logger.Warning("[NetMod] GhostKing purge after level create failed: {Message}", ex.Message);
        }
    }

    private void Levelinit_OnDispose(Hook_Level.orig_onDispose orig, Level self)
    {
        orig(self);
    }

    // Compatibility path for Dead Cells v35.9+ (June 2026).
    // Do not replace the game's complete Level.init implementation: the game now
    // owns render/UI initialization details that a legacy copied routine cannot
    // safely reproduce. MobSync attaches later through entitiesPostCreate.
    private void Levelinit_Main(Hook_Level.orig_init orig, Level self)
    {
        ModEntry.Instance?.Logger.Information(
            "[NetMod][Compat] using vanilla Level.init for level={LevelId}",
            self.map?.id?.ToString() ?? string.Empty);
        orig(self);
    }
}
