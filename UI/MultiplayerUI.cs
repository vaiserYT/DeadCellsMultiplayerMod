using dc;
using dc.en;
using dc.en.mob;
using dc.h2d;
using dc.hxd;
using dc.libs._Cooldown;
using dc.pr;
using dc.tool;
using dc.ui;
using dc.ui.hud;
using DeadCellsMultiplayerMod.Interface.ModuleInitializing;
using HaxeProxy.Runtime;
using ModCore.Events;
using ModCore.Utilities;
using Serilog;
using Cooldown = CooldownHelper.Cooldown;
using DeadCellsMultiplayerMod.MultiplayerModUI.Connection;
using ModCore.Events.Interfaces.Game.Hero;

namespace DeadCellsMultiplayerMod.MultiplayerModUI.lifeUI
{

    public class MultiplayerUI :
        IEventReceiver,
        IOnAdvancedModuleInitializing,
        IOnHeroUpdate
    {
        private sealed class LifeSlot
        {
            public int SlotIndex { get; }
            public dc.ui.hud.LifeBar LifeBar { get; }
            public FlowBox Container { get; }
            public dc.h2d.Object LabelBox { get; }
            public Graphics? ChipGraphics { get; set; }
            public dc.ui.Text? LabelText { get; set; }
            public dc.ui.Text? StatusText { get; set; }
            public string? LastLabel { get; set; }
            public string? LastStatus { get; set; }
            public int LastLife { get; set; } = int.MinValue;
            public int LastMaxLife { get; set; } = int.MinValue;
            public int LastLif { get; set; } = int.MinValue;
            public int LastBonusLife { get; set; } = int.MinValue;
            public int LastRecover { get; set; } = int.MinValue;

            public LifeSlot(int slotIndex, dc.ui.hud.LifeBar lifeBar, FlowBox container, dc.h2d.Object labelBox)
            {
                SlotIndex = slotIndex;
                LifeBar = lifeBar;
                Container = container;
                LabelBox = labelBox;
            }
        }

        private ModEntry mod { get; set; }
        private dc.h2d.Flow toplib { get; set; } = null!;
        public static dc.h2d.Flow flowContainer = null!;
        private static NetNode? _net;
        private NetNode? _boundNet;
        public int SlotIndex { get; set; }

        private LifeSlot?[] _slots = System.Array.Empty<LifeSlot?>();
        private bool[] _slotActive = System.Array.Empty<bool>();
        private HUD? _hud;
        private long _hudReadyAfterTicks;

        private int lastLife = 0;
        private int lastMaxLife = 0;
        private int _lastHostConnectedClientCount = -1;

        private static MultiplayerUI? _instance;

        // Party plate layout (panel-local units), matching the mockup: bronze name plate on the
        // left, long dark bar plate with a segmented HP fill extending to the right.
        private const double PartyChipX = 18.0;
        private const double PartyChipY = 42.0;
        private const double PartyChipGapY = 52.0;
        private const double PlateTotalW = 300.0;
        private const double NamePlateW = 82.0;
        private const double NamePlateH = 42.0;
        private const double BarPlateX = 76.0;
        private const double BarPlateH = 26.0;
        private const double BarPlateY = (NamePlateH - BarPlateH) / 2.0;
        private const double PartyBarX = BarPlateX + 7.0;
        private const double PartyBarY = BarPlateY + 7.0;
        private const double PartyBarW = PlateTotalW - 7.0 - PartyBarX;
        private const double PartyBarH = BarPlateH - 14.0;
        private const int PartyBarSegments = 6;


        public MultiplayerUI(ModEntry Entry, int slotIndex = 0)
        {
            mod = Entry;
            SlotIndex = slotIndex;
            _instance = this;
            EventSystem.AddReceiver(this);
        }

        void IOnAdvancedModuleInitializing.OnAdvancedModuleInitializing(ModEntry entry)
        {

            entry.Logger.Information("\x1b[32m[[ModEntry.MultiplayerUI] Initializing MultiplayerUI...]\x1b[0m ");
            Hook_HUD.initHero += Hook_HUD_initking;
            Hook_Hero.updateLifeBar += Hook_Hero_kinglifupdate;
        }

        private void Hook_HUD_initking(Hook_HUD.orig_initHero orig, HUD self)
        {
            // The custom plate is owned by a vanilla FlowBox, so old HUD disposal owns its
            // render lifecycle. Drop our references before initializing the replacement HUD.
            try { ClearSlots(); } catch { }

            orig(self);

            _hud = self;
            int slotCount = NetNode.MaxClientSlots;
            _slots = new LifeSlot?[slotCount];
            _slotActive = new bool[slotCount];

            // Remote HP is already cached during a level transition. Do not create font-backed
            // custom labels from inside Game.loadMainLevel; wait until the new HUD has settled.
            _hudReadyAfterTicks = System.Diagnostics.Stopwatch.GetTimestamp()
                + (long)(System.Diagnostics.Stopwatch.Frequency * 1.5);
        }
        public bool CanUseJumpHit()
        {
            try
            {
                var fastCheck = ModEntry.me?.cd?.fastCheck;
                if (fastCheck == null)
                    return false;

                int key = Cooldown.Encode(Cooldown.Keys.JUMP_HIT);
                return !fastCheck.exists(key);
            }
            catch { return false; }
        }
        public void Debugkeys()
        {
            // Disabled for stability. The old debug hotkeys spawned mobs and wrote Dead Cells cooldown maps
            // during normal play, which can corrupt Hashlink runtime state.
            return;
#pragma warning disable CS0162

            if (Key.Class.isPressed(97))//num1
            {
                //LevelTransition.Class.@goto("Custom".AsHaxeString());
                Log.Debug("KeyPress");
                ConnectionUI connectionUI = new ConnectionUI(HUD.Class.ME);

            }
            if (Key.Class.isPressed(98))//num2
            {
                var hero = ModCore.Modules.Game.Instance.HeroInstance!;
                Zombie zombie = new Zombie(hero._level, hero.cx, hero.cy, 0, 100);
                zombie.init();
                var fastCheck = ModEntry.me?.cd?.fastCheck;
                if (fastCheck != null)
                {
                    int key = Cooldown.Encode(Cooldown.Keys.JUMP_HIT);
                    fastCheck.set(key, new CdInst(key, 3.0));
                }
            }
            if (Key.Class.isPressed(99))//num3
            {
                var me = ModEntry.me;
                var fastCheck = me?.cd?.fastCheck;
                if (fastCheck != null)
                {
                    int key = Cooldown.Encode(Cooldown.Keys.JUMP_HIT);
                    fastCheck.remove(key);
                }

                if (me != null)
                {
                    InventItem inventItem = new InventItem(new InventItemKind.Perk("P_Yolo".AsHaxeString()));
                    me.applyItemPickEffect(me, inventItem);
                    inventItem.clone(true, "P_Yolo".AsHaxeString());

                    me.tryToApplyYoloPerk();
                    me.removeTemporaryItems();
                }
            }
            if (!CanUseJumpHit())
            {
                return;
            }
#pragma warning restore CS0162

        }
        private void Hook_Hero_kinglifupdate(Hook_Hero.orig_updateLifeBar orig, Hero self)
        {
            orig(self);
            KingLifeUpdate(self);
        }

        private dc.libs.Process process()
        {
            bool? titleLib = null;
            return new TitleScreen(titleLib);
        }

        public void KingLifeUpdate(Hero self)
        {
            _net = ModEntry._net;
            var net = _net;
            if (net == null)
            {
                if (_boundNet != null)
                {
                    _boundNet = null;
                    lastLife = int.MinValue;
                    lastMaxLife = int.MinValue;
                    _lastHostConnectedClientCount = -1;
                    ClearSlots();
                }
                return;
            }

            if (!ReferenceEquals(_boundNet, net))
            {
                _boundNet = net;
                lastLife = int.MinValue;
                lastMaxLife = int.MinValue;
                _lastHostConnectedClientCount = -1;
                ClearSlots();
            }

            if (net.IsHost)
            {
                var connectedClients = NetNode.ConnectedClientCount;
                if (connectedClients != _lastHostConnectedClientCount)
                {
                    _lastHostConnectedClientCount = connectedClients;
                    lastLife = int.MinValue;
                    lastMaxLife = int.MinValue;
                }
            }
            else
            {
                _lastHostConnectedClientCount = -1;
            }


            if (lastLife != self.life || lastMaxLife != self.maxLife)
            {
                net.SendHP(self.life, self.maxLife, self.life, self.bonusLife, self.radius);
                lastLife = self.life;
                lastMaxLife = self.maxLife;
            }

            if (_slots.Length == 0)
                return;

            if (!net.TryGetRemoteHpSnapshots(out var snapshots) || snapshots.Count == 0)
            {
                ClearSlots();
                return;
            }

            try
            {
                System.Array.Clear(_slotActive, 0, _slotActive.Length);
                var localId = net.id;
                foreach (var remote in snapshots)
                {
                    if (!ModEntry.TryGetClientIndex(localId, remote.Id, out var slotIndex))
                        continue;
                    if (slotIndex < 0 || slotIndex >= _slots.Length)
                        continue;

                    // Position/name packets can create a remote snapshot before the first HP packet arrives.
                    // Do not render a broken 0 / 0 party frame; wait for real HP data.
                    if (remote.MaxLife <= 0)
                        continue;

                    var slot = _slots[slotIndex];
                    if (slot == null)
                    {
                        if (System.Diagnostics.Stopwatch.GetTimestamp() < _hudReadyAfterTicks)
                            continue;
                        var hud = _hud;
                        if (hud == null)
                            continue;
                        var lifeBar = new dc.ui.hud.LifeBar(new LifeBarColorMode.Normal(), null);
                        slot = initkingLife(hud, slotIndex, lifeBar);
                        _slots[slotIndex] = slot;
                        Log.Information("[NetMod][PartyHUD] created plate slot={Slot}", slotIndex);
                    }

                    var displayName = ModEntry.GetClientLabel(slotIndex);
                    if (string.IsNullOrWhiteSpace(displayName) ||
                        string.Equals(displayName, "Guest", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(displayName, GameMenu.RemoteUsername, StringComparison.Ordinal))
                    {
                        if (!string.IsNullOrWhiteSpace(remote.Username))
                            displayName = remote.Username.Trim();
                    }
                    if (string.IsNullOrWhiteSpace(displayName))
                        displayName = "Guest";
                    UpdateSlotLabel(slot, displayName);
                    UpdateLifeBar(slot, remote.Life, remote.MaxLife, remote.Lif, remote.BonusLife, remote.Recover);
                    _slotActive[slotIndex] = true;
                }
            }
            finally
            {
                NetNode.ReleaseConsumedList(snapshots);
            }

            RemoveInactiveSlots();
        }


        private LifeSlot initkingLife(HUD self, int slotIndex, dc.ui.hud.LifeBar kinglifeui)
        {
            this.toplib = self.topRightFlowT;

            // The OUTER owner is a vanilla-managed FlowBox. The inner panel is a plain Object so
            // its graphics/text keep exact positions, but it cannot outlive the owning HUD.
            FlowBox owner = FlowBox.Class.createBoxValidation(
                null, Ref<double>.Null, Ref<double>.Null, Ref<bool>.Null, null);
            owner.isVertical = false;
            owner.box.alpha = 0;
            owner.set_horizontalAlign(new FlowAlign.Middle());
            owner.set_verticalAlign(new FlowAlign.Middle());

            var panel = new dc.h2d.Object(null);
            owner.addChild(panel);

            var chip = new Graphics(panel);
            chip.visible = true;

            var displayName = ModEntry.GetClientLabel(slotIndex);
            if (string.IsNullOrWhiteSpace(displayName))
                displayName = "Guest";
            displayName = FitDisplayName(displayName);

            dc.ui.Text nameText = Assets.Class.makeText(
                displayName.AsHaxeString(),
                dc.ui.Text.Class.COLORS.get("WO".AsHaxeString()),
                false,
                panel);
            nameText.y = 11;
            nameText.textColor = 0xF2D98C;
            nameText.customScale = 0.8;
            nameText.onResize();
            CenterText(nameText, displayName, 0.0, NamePlateW);

            dc.ui.Text statusText = Assets.Class.makeText(
                "0%".AsHaxeString(),
                dc.ui.Text.Class.COLORS.get("WO".AsHaxeString()),
                false,
                panel);
            statusText.y = BarPlateY + 5.0;
            statusText.textColor = 0xF4F8FF;
            statusText.customScale = 0.6;
            statusText.onResize();
            CenterText(statusText, "0%", PartyBarX, PartyBarW);

            try { kinglifeui.visible = false; } catch { }

            this.toplib.addChild(owner);
            this.toplib.isVertical = true;
            this.toplib.set_verticalAlign(new FlowAlign.Top());
            this.toplib.set_horizontalAlign(new FlowAlign.Right());

            var slot = new LifeSlot(slotIndex, kinglifeui, owner, panel)
            {
                ChipGraphics = chip,
                LabelText = nameText,
                StatusText = statusText,
                LastLabel = displayName,
                LastStatus = "0%"
            };
            DrawPartyChip(slot, 0, 1, 0);
            return slot;
        }

        /// <summary>Centers a dc.ui.Text horizontally inside [regionX, regionX + regionW].</summary>
        private static void CenterText(dc.ui.Text? text, string label, double regionX, double regionW)
        {
            if (text == null)
                return;

            double textWidth;
            try
            {
                textWidth = text.textWidth * text.scaleX;
            }
            catch
            {
                textWidth = label.Length * 10.0;
            }

            if (textWidth <= 0)
                textWidth = label.Length * 10.0;

            text.x = System.Math.Max(regionX + 4.0, regionX + (regionW - textWidth) * 0.5);
        }

        /// <summary>The name plate is small; trim long Steam names so they stay inside it.</summary>
        private static string FitDisplayName(string displayName)
        {
            const int maxChars = 9;
            if (string.IsNullOrWhiteSpace(displayName))
                return "Guest";
            displayName = displayName.Trim();
            return displayName.Length <= maxChars ? displayName : displayName[..(maxChars - 1)] + "…";
        }

        private static void UpdateSlotLabel(LifeSlot slot, string displayName)
        {
            if (string.IsNullOrWhiteSpace(displayName))
                displayName = "Guest";
            displayName = FitDisplayName(displayName);

            if (slot.LabelText != null && slot.LastLabel != displayName)
            {
                slot.LabelText.set_text(displayName.AsHaxeString());
                slot.LabelText.onResize();
                CenterText(slot.LabelText, displayName, 0.0, NamePlateW);
                slot.LastLabel = displayName;
            }
        }

        private static void UpdateLifeBar(LifeSlot slot, int life, int maxLife, int lif, int bonusLife, int recover)
        {
            var safeMaxLife = System.Math.Max(1, maxLife);
            var safeLife = System.Math.Max(0, System.Math.Min(life, safeMaxLife));
            var safeLif = System.Math.Max(0, System.Math.Min(lif <= 0 ? safeLife : lif, safeMaxLife));
            var percent = safeMaxLife <= 0 ? 0 : (int)System.Math.Round((safeLife * 100.0) / safeMaxLife);
            var status = $"{percent}%";

            if (slot.LastLife == safeLife &&
                slot.LastMaxLife == safeMaxLife &&
                slot.LastLif == safeLif &&
                slot.LastBonusLife == bonusLife &&
                slot.LastRecover == recover &&
                string.Equals(slot.LastStatus, status, StringComparison.Ordinal))
            {
                return;
            }

            DrawPartyChip(slot, safeLife, safeMaxLife, percent);

            if (slot.StatusText != null && !string.Equals(slot.LastStatus, status, StringComparison.Ordinal))
            {
                slot.StatusText.set_text(status.AsHaxeString());
                slot.StatusText.textColor = percent <= 25 ? 0xFF9B8A : percent <= 50 ? 0xFFE0A6 : 0xF4F8FF;
                slot.StatusText.onResize();
                CenterText(slot.StatusText, status, PartyBarX, PartyBarW);
                slot.LastStatus = status;
            }

            slot.LastLife = safeLife;
            slot.LastMaxLife = safeMaxLife;
            slot.LastLif = safeLif;
            slot.LastBonusLife = bonusLife;
            slot.LastRecover = recover;
        }

        private static void DrawPartyChip(LifeSlot slot, int life, int maxLife, int percent)
        {
            var g = slot.ChipGraphics;
            if (g == null)
                return;

            try
            {
                g.clear();

                double fullAlpha = 1.0;
                int shadowColor = 0x000000;

                // --- Soft drop shadow under both plates. ---
                double shadowAlpha = 0.38;
                g.beginFill(Ref<int>.From(ref shadowColor), Ref<double>.From(ref shadowAlpha));
                g.drawRect(3.0, BarPlateY + 3.0, PlateTotalW, BarPlateH);
                g.drawRect(3.0, 3.0, NamePlateW, NamePlateH);
                g.endFill();

                // --- Bar plate (drawn first so the name plate overlaps its left edge). ---
                // Thin gold/bronze outline around a near-black panel, like the mockup.
                int barOutline = 0xC98A4B;
                g.beginFill(Ref<int>.From(ref barOutline), Ref<double>.From(ref fullAlpha));
                g.drawRect(BarPlateX, BarPlateY, PlateTotalW - BarPlateX, BarPlateH);
                g.endFill();

                int barPanel = 0x14161F;
                g.beginFill(Ref<int>.From(ref barPanel), Ref<double>.From(ref fullAlpha));
                g.drawRect(BarPlateX + 2.0, BarPlateY + 2.0, PlateTotalW - BarPlateX - 4.0, BarPlateH - 4.0);
                g.endFill();

                // HP bar shell (dark inset).
                int barBorder = 0x07090F;
                g.beginFill(Ref<int>.From(ref barBorder), Ref<double>.From(ref fullAlpha));
                g.drawRect(PartyBarX - 2.0, PartyBarY - 2.0, PartyBarW + 4.0, PartyBarH + 4.0);
                g.endFill();

                int barBackColor = 0x11202A;
                g.beginFill(Ref<int>.From(ref barBackColor), Ref<double>.From(ref fullAlpha));
                g.drawRect(PartyBarX, PartyBarY, PartyBarW, PartyBarH);
                g.endFill();

                // HP fill with pixel-art highlight/shade bands.
                var safeMax = System.Math.Max(1, maxLife);
                var safeLife = System.Math.Max(0, System.Math.Min(life, safeMax));
                var fillW = PartyBarW * safeLife / safeMax;

                if (fillW > 0)
                {
                    int fillColor = percent <= 25 ? 0xC94040 : percent <= 50 ? 0xD89036 : 0x4CBB5E;
                    int fillHighlight = percent <= 25 ? 0xE87B6E : percent <= 50 ? 0xF0BC6E : 0x7FDD82;
                    int fillShade = percent <= 25 ? 0x8C2A2A : percent <= 50 ? 0x9C6420 : 0x2E8F45;

                    g.beginFill(Ref<int>.From(ref fillColor), Ref<double>.From(ref fullAlpha));
                    g.drawRect(PartyBarX, PartyBarY, fillW, PartyBarH);
                    g.endFill();

                    g.beginFill(Ref<int>.From(ref fillHighlight), Ref<double>.From(ref fullAlpha));
                    g.drawRect(PartyBarX, PartyBarY, fillW, 2.0);
                    g.endFill();

                    g.beginFill(Ref<int>.From(ref fillShade), Ref<double>.From(ref fullAlpha));
                    g.drawRect(PartyBarX, PartyBarY + PartyBarH - 2.0, fillW, 2.0);
                    g.endFill();
                }

                // Segment dividers across the whole bar (visible over the fill, near-invisible
                // over the dark empty part), like the notched bar in the mockup.
                for (int i = 1; i < PartyBarSegments; i++)
                {
                    var dividerX = PartyBarX + PartyBarW * i / PartyBarSegments - 1.0;
                    g.beginFill(Ref<int>.From(ref barBorder), Ref<double>.From(ref fullAlpha));
                    g.drawRect(dividerX, PartyBarY, 2.0, PartyBarH);
                    g.endFill();
                }

                // --- Name plate (bronze frame, chamfered pixel-art corners, navy inner). ---
                int plateDark = 0x3A1B12;
                g.beginFill(Ref<int>.From(ref plateDark), Ref<double>.From(ref fullAlpha));
                g.drawRect(4.0, 0.0, NamePlateW - 8.0, NamePlateH);
                g.drawRect(0.0, 4.0, NamePlateW, NamePlateH - 8.0);
                g.drawRect(2.0, 2.0, NamePlateW - 4.0, NamePlateH - 4.0);
                g.endFill();

                int plateBronze = 0xA44E32;
                g.beginFill(Ref<int>.From(ref plateBronze), Ref<double>.From(ref fullAlpha));
                g.drawRect(6.0, 2.0, NamePlateW - 12.0, NamePlateH - 4.0);
                g.drawRect(2.0, 6.0, NamePlateW - 4.0, NamePlateH - 12.0);
                g.drawRect(4.0, 4.0, NamePlateW - 8.0, NamePlateH - 8.0);
                g.endFill();

                // Lighter bronze top edge for that lit-from-above look.
                int plateBronzeLight = 0xC66B42;
                g.beginFill(Ref<int>.From(ref plateBronzeLight), Ref<double>.From(ref fullAlpha));
                g.drawRect(6.0, 2.0, NamePlateW - 12.0, 2.0);
                g.endFill();

                // Navy inner window.
                int plateInnerEdge = 0x2A3A5E;
                g.beginFill(Ref<int>.From(ref plateInnerEdge), Ref<double>.From(ref fullAlpha));
                g.drawRect(7.0, 7.0, NamePlateW - 14.0, NamePlateH - 14.0);
                g.endFill();

                int plateInner = 0x1A2340;
                g.beginFill(Ref<int>.From(ref plateInner), Ref<double>.From(ref fullAlpha));
                g.drawRect(8.0, 8.0, NamePlateW - 16.0, NamePlateH - 16.0);
                g.endFill();
            }
            catch
            {
            }
        }

        private void ClearSlots()
        {
            if (_slots.Length == 0)
                return;

            var removed = 0;
            for (int i = 0; i < _slots.Length; i++)
            {
                var slot = _slots[i];
                if (slot == null)
                    continue;
                try
                {
                    toplib?.removeChild(slot.Container);
                    slot.Container.remove();
                    removed++;
                }
                catch { }
                _slots[i] = null;
            }

            if (removed > 0)
                Log.Information("[NetMod][PartyHUD] cleared {Count} plate(s)", removed);
        }

        private void RemoveInactiveSlots()
        {
            if (_slots.Length == 0)
                return;

            for (int i = 0; i < _slots.Length; i++)
            {
                if (_slotActive[i])
                    continue;
                var slot = _slots[i];
                if (slot == null)
                    continue;
                try
                {
                    toplib?.removeChild(slot.Container);
                    slot.Container.remove();
                }
                catch { }
                _slots[i] = null;
            }
        }


        private sealed class SystemMessageEntry
        {
            public dc.h2d.Text Text = null!;
            public double LifetimeSeconds;
            public double FadeSeconds;
            public double ElapsedSeconds;
        }

        private sealed class PendingSystemMessage
        {
            public string Text = string.Empty;
            public double LifetimeSeconds;
            public double FadeSeconds;
        }

        private static readonly object SystemMsgSync = new();
        private static readonly Queue<PendingSystemMessage> PendingSystemMessages = new();
        private static readonly List<SystemMessageEntry> ActiveSystemMessages = new();

        private const int MaxSystemMessages = 8;
        private const double DefaultSystemMsgLifetimeSeconds = 10.0;
        private const double DefaultSystemMsgFadeSeconds = 2.5;
        private const double SystemMsgXOffsetPx = 20.0;
        private const double SystemMsgYOffsetPx = 250.0;
        private const double SystemMsgScale = 1.2;

        public static void PushSystemMessage(string message, double lifetimeSeconds = DefaultSystemMsgLifetimeSeconds, double fadeSeconds = DefaultSystemMsgFadeSeconds)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            var normalizedLifetime = System.Math.Max(0.25, lifetimeSeconds);
            var normalizedFade = System.Math.Max(0.15, System.Math.Min(fadeSeconds, normalizedLifetime));
            lock (SystemMsgSync)
            {
                PendingSystemMessages.Enqueue(new PendingSystemMessage
                {
                    Text = message.Trim(),
                    LifetimeSeconds = normalizedLifetime,
                    FadeSeconds = normalizedFade
                });
            }
        }

        public void DebugUI(string @string)
        {
            PushSystemMessage(@string, 5.0, 1.5);
        }

        private static bool EnsureSystemMessageFlow()
        {
            var hud = HUD.Class.ME;
            var root = hud?.root;
            if (root == null)
                return false;
            if (hud == null)
                return false;

            if (flowContainer == null || flowContainer.parent == null || !ReferenceEquals(flowContainer.parent, root))
            {
                try { flowContainer?.remove(); } catch { }
                flowContainer = new dc.h2d.Flow(root);
                flowContainer.multiline = true;
                flowContainer.isVertical = true;
                flowContainer.set_verticalAlign(new FlowAlign.Top());
                flowContainer.set_horizontalAlign(new FlowAlign.Left());
            }

            var pixelScale = hud.get_pixelScale.Invoke();
            flowContainer.x = SystemMsgXOffsetPx * pixelScale;
            flowContainer.y = SystemMsgYOffsetPx * pixelScale;
            flowContainer.alpha = 1;
            flowContainer.visible = true;
            root.addChild(flowContainer);
            return true;
        }

        private static void RemoveSystemMessageAt(int index)
        {
            if (index < 0 || index >= ActiveSystemMessages.Count)
                return;

            var entry = ActiveSystemMessages[index];
            try
            {
                flowContainer?.removeChild(entry.Text);
                entry.Text.remove();
            }
            catch
            {
            }
            ActiveSystemMessages.RemoveAt(index);
        }

        private static void EnqueueSystemMessageInternal(PendingSystemMessage pending)
        {
            if (flowContainer == null || pending == null)
                return;

            var text = Assets.Class.makeText(
                pending.Text.AsHaxeString(),
                dc.ui.Text.Class.COLORS.get("WO".AsHaxeString()),
                false,
                flowContainer);
            text.customScale = SystemMsgScale;
            text.onResize();
            text.textColor = 0xFFFFFF;
            text.alpha = 1;

            ActiveSystemMessages.Add(new SystemMessageEntry
            {
                Text = text,
                LifetimeSeconds = pending.LifetimeSeconds,
                FadeSeconds = pending.FadeSeconds,
                ElapsedSeconds = 0
            });

            while (ActiveSystemMessages.Count > MaxSystemMessages)
                RemoveSystemMessageAt(0);
        }

        private void UpdateSystemMessages(double dt)
        {
            if (!EnsureSystemMessageFlow())
                return;

            lock (SystemMsgSync)
            {
                while (PendingSystemMessages.Count > 0)
                    EnqueueSystemMessageInternal(PendingSystemMessages.Dequeue());
            }

            if (ActiveSystemMessages.Count == 0)
                return;

            for (int i = ActiveSystemMessages.Count - 1; i >= 0; i--)
            {
                var msg = ActiveSystemMessages[i];
                msg.ElapsedSeconds += dt;

                var fadeStart = System.Math.Max(0.0, msg.LifetimeSeconds - msg.FadeSeconds);
                if (msg.ElapsedSeconds >= fadeStart)
                {
                    var fadeT = (msg.ElapsedSeconds - fadeStart) / System.Math.Max(0.01, msg.FadeSeconds);
                    var alpha = 1.0 - fadeT;
                    if (alpha < 0) alpha = 0;
                    msg.Text.alpha = alpha;
                }

                if (msg.ElapsedSeconds >= msg.LifetimeSeconds)
                    RemoveSystemMessageAt(i);
            }
        }

        void IOnHeroUpdate.OnHeroUpdate(double dt)
        {
            UpdateSystemMessages(dt);
            var hero = ModEntry.me;
            if (hero != null)
                KingLifeUpdate(hero);
            Debugkeys();
        }
    }
}
