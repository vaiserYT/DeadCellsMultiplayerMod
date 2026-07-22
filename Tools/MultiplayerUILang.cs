using DeadCellsMultiplayerMod.Interface.ModuleInitializing;
using ModCore.Events;
using ModCore.Events.Interfaces.Game;
using ModCore.Modules;
using ModCore.Utilities;
using Serilog;

namespace DeadCellsMultiplayerMod.Tools.ModLang
{
    public class MultiplayerModLang :
    IEventReceiver,
    IOnAdvancedModuleInitializing,
    IOnGameEndInit
    {
        private ModEntry? Entry;
        public MultiplayerModLang(ModEntry entry)
        {
            Entry = entry;
            EventSystem.AddReceiver(this);
            GetText.Instance.RegisterMod("DeadCellsMultiplayerModLang");
        }

        void IOnAdvancedModuleInitializing.OnAdvancedModuleInitializing(ModEntry entry)
        {
            entry.Logger.Information("\x1b[32m[[ModEntry.MultiplayerModLang] Initializing MultiplayerModLang...]\x1b[0m ");
        }

        void IOnGameEndInit.OnGameEndInit()
        {
            var res = Entry!.Info.ModRoot!.GetFilePath("res.pak");
            FsPak.Instance.FileSystem.loadPak(res.AsHaxeString());
        }
    }
}