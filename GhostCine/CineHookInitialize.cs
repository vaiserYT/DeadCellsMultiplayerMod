using DeadCellsMultiplayerMod;
using DeadCellsMultiplayerMod.Interface.ModuleInitializing;
using ModCore.Events;

namespace CineHookInitialize
{
    public class CineHooks : IEventReceiver, IOnAdvancedModuleInitializing
    {
        public CineHooks() => EventSystem.AddReceiver(this);

        void IOnAdvancedModuleInitializing.OnAdvancedModuleInitializing(ModEntry entry)
        {
            entry.Logger.Information("\x1b[32m[[ModEntry.CineHooks] Initializing CineHooks...]\x1b[0m ");
        }
    }
}
