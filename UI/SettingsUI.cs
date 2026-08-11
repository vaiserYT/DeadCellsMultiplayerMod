using DeadCellsMultiplayerMod.Interface.ModuleInitializing;
using DeadCellsMultiplayerMod.Mobs.MobsSynchronization;
using dc.ui;
using Hashlink.Virtuals;
using HaxeProxy.Runtime;
using ModCore.Events;
using ModCore.Utilities;

namespace DeadCellsMultiplayerMod.UI;

public class SettingsUI :
    IEventReceiver,
    IOnAdvancedModuleInitializing
{
    private static bool _hooksAttached;
    private static bool _isMultiplayerSettingsOpen;
    private static int _multiplayerSettingsOptionsId = -1;
    private static readonly string[] DebugPerkFallbackChoices =
    {
        "P_Yolo",
        "P_DeadInside",
        "P_Necromancy",
        "P_Recovery",
        "P_Vengeance"
    };

    private static readonly DebugModuleId[] DebugModuleOrder =
    {
        DebugModuleId.MultiplayerModLang,
        DebugModuleId.CineHooks,
        DebugModuleId.MultiplayerUI,
        DebugModuleId.LevelInit,
        DebugModuleId.MobsSynchronization,
        DebugModuleId.MinimapReveal,
        DebugModuleId.LevelExitSync,
        DebugModuleId.InteractionSync,
        DebugModuleId.ConnectionUI
    };

    private ModEntry mod { get; set; }

    public SettingsUI(ModEntry entry)
    {
        mod = entry;
        EventSystem.AddReceiver(this);
    }

    void IOnAdvancedModuleInitializing.OnAdvancedModuleInitializing(ModEntry entry)
    {
        entry.Logger.Information("\x1b[32m[[ModEntry.SettingsUI] Initializing SettingsUI...]\x1b[0m ");

        if (_hooksAttached)
            return;

        Hook_Options.showMain += Hook_Options_showMain;
        Hook_Options.showCredits += Hook_Options_showCredits;
        Hook_Options.onDispose += Hook_Options_onDispose;
        _hooksAttached = true;
    }

    private void Hook_Options_showMain(Hook_Options.orig_showMain orig, Options self)
    {
        if (self == null)
        {
            orig(self);
            return;
        }

        if (IsMultiplayerSettingsContext(self))
        {
            MultiplayerSettingsStorage.Save();
            ResetMenuState();
        }

        try
        {
            int leftPadding = 5;
            HlAction onSelect = new HlAction(() =>
            {
                OpenMultiplayerSettingsMenu(self);
            });

            // Insert before vanilla entries so it appears at the top.
            self.addSimpleWidget(
                LobbySession.Localize("Multiplayer settings").AsHaxeString(),
                null,
                onSelect,
                Ref<int>.From(ref leftPadding),
                null);
        }
        catch (Exception ex)
        {
            mod.Logger.Warning(ex, "[NetMod] Failed to add Multiplayer settings button");
        }

        orig(self);
    }

    private void Hook_Options_showCredits(Hook_Options.orig_showCredits orig, Options self)
    {
        if (!IsMultiplayerSettingsContext(self))
        {
            orig(self);
            return;
        }

        BuildMultiplayerSettingsSection(self);
    }

    private void Hook_Options_onDispose(Hook_Options.orig_onDispose orig, Options self)
    {
        bool wasMultiplayerSettings = IsMultiplayerSettingsContext(self);
        orig(self);

        if (wasMultiplayerSettings)
        {
            MultiplayerSettingsStorage.Save();
            ResetMenuState();
        }
    }

    private void OpenMultiplayerSettingsMenu(Options self)
    {
        try
        {
            if (self == null || self.destroyed)
                return;

            _isMultiplayerSettingsOpen = true;
            _multiplayerSettingsOptionsId = self.uniqId;
            self.setSection(new OptionsSection.S_Credits());
        }
        catch (Exception ex)
        {
            ResetMenuState();
            mod.Logger.Warning(ex, "[NetMod] Failed to open multiplayer settings menu");
        }
    }

    private static bool IsMultiplayerSettingsContext(Options self)
    {
        return _isMultiplayerSettingsOpen
            && self != null
            && !self.destroyed
            && self.uniqId == _multiplayerSettingsOptionsId;
    }

    private static void ResetMenuState()
    {
        _isMultiplayerSettingsOpen = false;
        _multiplayerSettingsOptionsId = -1;
    }

    private void BuildMultiplayerSettingsSection(Options self)
    {
        try
        {
            if (!IsMultiplayerSettingsContext(self))
                return;

            self.title?.set_text(LobbySession.Localize("Multiplayer settings").AsHaxeString());
            self.createScroller(0.0);

            var widgetParent = self.scrollerFlow;
            if (widgetParent == null)
                return;

            AddMobsSettingsWidgets(self, widgetParent);
            AddDebugSettingsWidgets(self, widgetParent);

            int leftPadding = 5;
            HlAction onBack = new HlAction(() =>
            {
                if (self != null && !self.destroyed)
                    self.onQuit();
            });

            self.addSimpleWidget(
                LobbySession.Localize("Back").AsHaxeString(),
                null,
                onBack,
                Ref<int>.From(ref leftPadding),
                widgetParent);

            self.updateScroller();
        }
        catch (Exception ex)
        {
            mod.Logger.Warning(ex, "[NetMod] Failed to build multiplayer settings menu");
        }
    }

    private void AddMobsSettingsWidgets(Options self, dc.h2d.Flow widgetParent)
    {
        if (self == null || widgetParent == null)
            return;

        AddSectionLabel(self, widgetParent, "Mobs settings");

        bool enabledNow = MultiplayerSettingsStorage.EnableMobsSync;
        self.addToggleWidget(
            LobbySession.Localize("Enable mobs sync").AsHaxeString(),
            null,
            new HlFunc<bool>(ToggleMobsSyncSetting),
            Ref<bool>.From(ref enabledNow),
            widgetParent);

        double mobsHpValue = MultiplayerSettingsStorage.MobsHpMultiplier;
        double mobsHpStep = 0.10;
        bool mobsHpPercentDisplay = false;
        bool mobsHpRawDisplay = true;
        double mobsHpMin = 0.25;
        double mobsHpMax = 8.00;

        self.addSliderWidget(
            LobbySession.Localize("Mobs HP multiplier").AsHaxeString(),
            new HlAction<double>(OnMobsHpSliderChanged),
            mobsHpValue,
            Ref<double>.From(ref mobsHpStep),
            widgetParent,
            Ref<bool>.From(ref mobsHpPercentDisplay),
            Ref<bool>.From(ref mobsHpRawDisplay),
            Ref<double>.From(ref mobsHpMin),
            Ref<double>.From(ref mobsHpMax),
            null,
            Ref<int>.Null);

        double bossesHpValue = MultiplayerSettingsStorage.BossesHpMultiplier;
        double bossesHpStep = 0.10;
        bool bossesHpPercentDisplay = false;
        bool bossesHpRawDisplay = true;
        double bossesHpMin = 0.25;
        double bossesHpMax = 8.00;

        self.addSliderWidget(
            LobbySession.Localize("Bosses HP multiplier").AsHaxeString(),
            new HlAction<double>(OnBossesHpSliderChanged),
            bossesHpValue,
            Ref<double>.From(ref bossesHpStep),
            widgetParent,
            Ref<bool>.From(ref bossesHpPercentDisplay),
            Ref<bool>.From(ref bossesHpRawDisplay),
            Ref<double>.From(ref bossesHpMin),
            Ref<double>.From(ref bossesHpMax),
            null,
            Ref<int>.Null);

        bool verticalSyncNow = MultiplayerSettingsStorage.SyncVerticalPosition;
        self.addToggleWidget(
            LobbySession.Localize("Sync vertical position").AsHaxeString(),
            LobbySession.Localize(
                    "Applies host vertical position to gravity mobs. Off: X-only for walkers; flying mobs still sync Y. On: can reduce desync but may snap ground mobs.")
                .AsHaxeString(),
            new HlFunc<bool>(ToggleVerticalSyncSetting),
            Ref<bool>.From(ref verticalSyncNow),
            widgetParent);
    }

    private static string GetDebugModuleToggleLabel(DebugModuleId id)
    {
        return id switch
        {
            DebugModuleId.MultiplayerModLang => LobbySession.Localize("Module: language"),
            DebugModuleId.CineHooks => LobbySession.Localize("Module: cinematics hooks"),
            DebugModuleId.MultiplayerUI => LobbySession.Localize("Module: multiplayer UI"),
            DebugModuleId.LevelInit => LobbySession.Localize("Module: level init"),
            DebugModuleId.MobsSynchronization => LobbySession.Localize("Module: mobs sync"),
            DebugModuleId.MinimapReveal => LobbySession.Localize("Module: minimap reveal"),
            DebugModuleId.LevelExitSync => LobbySession.Localize("Module: level exit sync"),
            DebugModuleId.InteractionSync => LobbySession.Localize("Module: interaction sync"),
            DebugModuleId.ConnectionUI => LobbySession.Localize("Module: connection UI"),
            _ => LobbySession.Localize("Module")
        };
    }

    private void AddDebugSettingsWidgets(Options self, dc.h2d.Flow widgetParent)
    {
        if (!MultiplayerSettingsStorage.IsDebugSectionEnabled || self == null || widgetParent == null)
            return;

        AddSectionLabel(self, widgetParent, "Debug");

        foreach (var moduleId in DebugModuleOrder)
        {
            bool enabledNow = MultiplayerSettingsStorage.IsModuleEnabled(moduleId);
            self.addToggleWidget(
                GetDebugModuleToggleLabel(moduleId).AsHaxeString(),
                null,
                new HlFunc<bool>(() => ToggleModuleSetting(moduleId)),
                Ref<bool>.From(ref enabledNow),
                widgetParent);
        }

        bool immortalNow = MultiplayerSettingsStorage.DebugPlayerImmortal;
        self.addToggleWidget(
            LobbySession.Localize("Player immortal").AsHaxeString(),
            null,
            new HlFunc<bool>(ToggleDebugImmortalSetting),
            Ref<bool>.From(ref immortalNow),
            widgetParent);

        bool explorersRuneNow = MultiplayerSettingsStorage.DebugUseExplorersRune;
        self.addToggleWidget(
            LobbySession.Localize("Use Explorer's Rune").AsHaxeString(),
            null,
            new HlFunc<bool>(ToggleDebugUseExplorersRuneSetting),
            Ref<bool>.From(ref explorersRuneNow),
            widgetParent);

        bool mobsSyncTraceNow = MultiplayerSettingsStorage.DebugMobsSyncTrace;
        self.addToggleWidget(
            LobbySession.Localize("Mobs sync trace logging").AsHaxeString(),
            null,
            new HlFunc<bool>(ToggleDebugMobsSyncTraceSetting),
            Ref<bool>.From(ref mobsSyncTraceNow),
            widgetParent);

        bool bossSyncTraceNow = MultiplayerSettingsStorage.DebugBossSyncTrace;
        self.addToggleWidget(
            LobbySession.Localize("Boss sync trace logging").AsHaxeString(),
            null,
            new HlFunc<bool>(ToggleDebugBossSyncTraceSetting),
            Ref<bool>.From(ref bossSyncTraceNow),
            widgetParent);

        bool showPerfLogsNow = MultiplayerSettingsStorage.ShowPerfLogs;
        self.addToggleWidget(
            LobbySession.Localize("Show perf logs").AsHaxeString(),
            LobbySession.Localize("Controls threshold-based [Perf] hitch and slowdown logging.").AsHaxeString(),
            new HlFunc<bool>(ToggleShowPerfLogsSetting),
            Ref<bool>.From(ref showPerfLogsNow),
            widgetParent);

        var perkChoices = BuildDebugPerkChoices();
        var selectedPerkIndex = ResolveCurrentDebugPerkIndex(perkChoices);
        var selectedPerk = perkChoices[selectedPerkIndex];

        int leftPadding = 5;
        self.addSimpleWidget(
            LobbySession.Localize("Start perk").AsHaxeString(),
            selectedPerk.AsHaxeString(),
            new HlAction(() => { }),
            Ref<int>.From(ref leftPadding),
            widgetParent);

        self.addSimpleWidget(
            LobbySession.Localize("Previous perk").AsHaxeString(),
            null,
            new HlAction(() => CycleDebugStartPerk(self, -1)),
            Ref<int>.From(ref leftPadding),
            widgetParent);

        self.addSimpleWidget(
            LobbySession.Localize("Next perk").AsHaxeString(),
            null,
            new HlAction(() => CycleDebugStartPerk(self, +1)),
            Ref<int>.From(ref leftPadding),
            widgetParent);
    }

    private static void AddSectionLabel(Options self, dc.h2d.Flow widgetParent, string label)
    {
        if (self == null || widgetParent == null || string.IsNullOrWhiteSpace(label))
            return;

        var localized = LobbySession.Localize(label).AsHaxeString();
        try
        {
            // Native game header widget (centered title + separator line).
            self.addSeparator(localized, widgetParent);
            return;
        }
        catch
        {
            // Fallback keeps menu usable if separator fails in some contexts.
            int leftPadding = 0;
            self.addSimpleWidget(
                localized,
                null,
                new HlAction(() => { }),
                Ref<int>.From(ref leftPadding),
                widgetParent);
        }
    }

    private void CycleDebugStartPerk(Options self, int delta)
    {
        if (self == null || self.destroyed)
            return;

        try
        {
            var perkChoices = BuildDebugPerkChoices();
            if (perkChoices.Count == 0)
                return;

            var current = ResolveCurrentDebugPerkIndex(perkChoices);
            var next = current + delta;
            while (next < 0)
                next += perkChoices.Count;
            while (next >= perkChoices.Count)
                next -= perkChoices.Count;

            MultiplayerSettingsStorage.DebugStartPerkId = perkChoices[next];
            self.setSection(new OptionsSection.S_Credits());
        }
        catch (Exception ex)
        {
            mod.Logger.Warning(ex, "[NetMod] Failed to change debug start perk");
        }
    }

    private static List<string> BuildDebugPerkChoices()
    {
        var list = new List<string> { MultiplayerSettingsStorage.NoStartPerkValue };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            MultiplayerSettingsStorage.NoStartPerkValue
        };

        try
        {
            var byIndex = dc.Data.Class.item?.byIndex;
            if (byIndex != null)
            {
                var len = byIndex.get_length();
                for (var i = 0; i < len; i++)
                {
                    var rowObj = byIndex.getDyn(i);
                    if (rowObj is not HaxeDynObj ho)
                        continue;

                    string? id = null;
                    try
                    {
                        id = ho.ToVirtual<virtual_ambiantDesc_castCD_cellCost_commonProps_dlc_droppable_gameplayDesc_group_icon_id_legendAffixes_moneyCost_name_props_synergy_tags_tier1_tier2_>()?.id?.ToString();
                    }
                    catch
                    {
                    }
                    if (string.IsNullOrWhiteSpace(id))
                        continue;

                    var trimmed = id.Trim();
                    if (!trimmed.StartsWith("P_", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (seen.Add(trimmed))
                        list.Add(trimmed);
                }
            }
        }
        catch
        {
        }

        if (list.Count == 1)
        {
            for (int i = 0; i < DebugPerkFallbackChoices.Length; i++)
            {
                var fallback = DebugPerkFallbackChoices[i];
                if (seen.Add(fallback))
                    list.Add(fallback);
            }
        }

        if (list.Count > 2)
            list.Sort(1, list.Count - 1, StringComparer.OrdinalIgnoreCase);

        return list;
    }

    private static int ResolveCurrentDebugPerkIndex(List<string> perkChoices)
    {
        if (perkChoices.Count == 0)
            return 0;

        var selected = MultiplayerSettingsStorage.DebugStartPerkId;
        for (int i = 0; i < perkChoices.Count; i++)
        {
            if (string.Equals(perkChoices[i], selected, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return 0;
    }

    private bool ToggleMobsSyncSetting()
    {
        bool enabled = !MultiplayerSettingsStorage.EnableMobsSync;
        MultiplayerSettingsStorage.EnableMobsSync = enabled;

        if (!enabled)
        {
            MobsSynchronization.ClearTrackingForLevelChange();
            try { LobbySession.NetRef?.ClearMobSyncQueues(); } catch { }
        }

        return enabled;
    }

    private static bool ToggleVerticalSyncSetting()
    {
        bool enabled = !MultiplayerSettingsStorage.SyncVerticalPosition;
        MultiplayerSettingsStorage.SyncVerticalPosition = enabled;
        return enabled;
    }

    private static bool ToggleModuleSetting(DebugModuleId moduleId)
    {
        var enabled = !MultiplayerSettingsStorage.IsModuleEnabled(moduleId);
        MultiplayerSettingsStorage.SetModuleEnabled(moduleId, enabled);
        return enabled;
    }

    private static bool ToggleDebugImmortalSetting()
    {
        var enabled = !MultiplayerSettingsStorage.DebugPlayerImmortal;
        MultiplayerSettingsStorage.DebugPlayerImmortal = enabled;
        return enabled;
    }

    private static bool ToggleDebugUseExplorersRuneSetting()
    {
        var enabled = !MultiplayerSettingsStorage.DebugUseExplorersRune;
        MultiplayerSettingsStorage.DebugUseExplorersRune = enabled;
        return enabled;
    }

    private static bool ToggleDebugMobsSyncTraceSetting()
    {
        var enabled = !MultiplayerSettingsStorage.DebugMobsSyncTrace;
        MultiplayerSettingsStorage.DebugMobsSyncTrace = enabled;
        return enabled;
    }

    private static bool ToggleDebugBossSyncTraceSetting()
    {
        var enabled = !MultiplayerSettingsStorage.DebugBossSyncTrace;
        MultiplayerSettingsStorage.DebugBossSyncTrace = enabled;
        return enabled;
    }

    private static bool ToggleShowPerfLogsSetting()
    {
        var enabled = !MultiplayerSettingsStorage.ShowPerfLogs;
        MultiplayerSettingsStorage.ShowPerfLogs = enabled;
        return enabled;
    }

    private static void OnMobsHpSliderChanged(double value)
    {
        MultiplayerSettingsStorage.MobsHpMultiplier = value;
        ModEntry._net?.SendHpMultipliers();
    }

    private static void OnBossesHpSliderChanged(double value)
    {
        MultiplayerSettingsStorage.BossesHpMultiplier = value;
        ModEntry._net?.SendHpMultipliers();
    }
}
