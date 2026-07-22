namespace DeadCellsMultiplayerMod.Interface.ModuleInitializing
{


    [ModCore.Events.Event(true)]
    public interface IOnAdvancedModuleInitializing
    {
        void OnAdvancedModuleInitializing(ModEntry entry);
    }

}

