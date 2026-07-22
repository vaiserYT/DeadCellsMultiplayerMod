using dc;
using dc.h2d;
using dc.hxd;
using dc.libs.heaps.slib;
using dc.pr;
using dc.shader;
using dc.ui;
using Hashlink.Virtuals;
using HaxeProxy.Runtime;
using ModCore.Events;
using ModCore.Utilities;
using Serilog;
using DeadCellsMultiplayerMod.MultiplayerModUI.Connection.LightingInitializer;
using ModCore.Modules;
using DeadCellsMultiplayerMod.Tools;
using DeadCellsMultiplayerMod.MultiplayerModUI.lifeUI;

namespace DeadCellsMultiplayerMod.MultiplayerModUI.Connection
{
    public class ConnectionUI :
    Process,
    IEventReceiver
    {
        private Flow? rootFlow;
        private UIBox? bg;
        private dc.h2d.Interactive? inter;
        private Flow? spritesflow;
        private Flow? MainTitleflow;
        private readonly List<HSprite> sprites = new();
        private readonly List<dc.ui.Text> connectionLabels = new();
        private readonly List<string> lastConnections = new();
        private Flow? lobbyCodeFlow;
        private dc.ui.Text? lobbyCodeTitleLabel;
        private dc.ui.Text? lobbyIdLabel;
        private string lastLobbyIdLabelText = string.Empty;

        private static ConnectionUI? Instance;
        private HSprite? spriteui;


        public ConnectionUI(Process parent) : base(parent)
        {
            Instance = this;
            this.createRoot(parent.root);
            MainPageLightingInitializer mainPage = new MainPageLightingInitializer(this);
            this.BuildUI();
            EventSystem.AddReceiver(this);
            this.root.visible = set_visible;
        }

        public static bool set_visible
        {
            get => Instance?.root.visible ?? false;
            set { if (Instance != null) Instance.root.visible = value; }
        }

        /// <summary>After gamepad connect/disconnect, window metrics can change; re-run layout to avoid blurred/scaled UI.</summary>
        public static void RefreshLayoutAfterDisconnect()
        {
            try
            {
                if (Instance != null && set_visible)
                    Instance.onResize();
            }
            catch
            {
            }
        }


        private void BuildUI()
        {
            this.clean();

            this.rootFlow = new Flow(null);
            this.rootFlow.set_isVertical(true);
            this.rootFlow.set_verticalAlign(new FlowAlign.Middle());
            this.rootFlow.set_horizontalAlign(new FlowAlign.Right());


            base.root.addChild(this.rootFlow);
            this.onResize();
            List<(string loColor, string hiColor)> colorPairs = new List<(string, string)>
            {
                ("#FF0000", "#0000FF"),
                ("#00FF00", "#FF00FF"),
                ("#FFFF00", "#FF0000"),
                ("#00FFFF", "#FF00FF"),
                ("#FFA500", "#800080"),
                ("#FF69B4", "#4169E1"),
            };


        }

        private List<double> sprx = new List<double> { 0.4, -1.0, -0.2, -0.6 };
        private List<string> animlist = new List<string>
        {
           "idle", "idle","idle","idle"
        };
        private List<string> sprmodu = new List<string>
        {
            "Tick4","PrisonerGold","KingWhite","PrisonerDefault"
        };

        private void loadspr(double x, string sprmuld, int count)
        {
            this.spritesflow = new Flow(null);
            this.spritesflow.set_verticalAlign(new FlowAlign.Top());
            this.spritesflow.set_horizontalAlign(new FlowAlign.Middle());
            this.spritesflow.isVertical = false;



            dc.String idle = "idle".AsHaxeString();
            string skinanim = animlist[count];
            SpriteLib g = Assets.Class.getHeroLib(Cdb.Class.getSkinInfo(sprmuld.AsHaxeString()));
            this.spriteui = new HSprite(g, skinanim.AsHaxeString(), Ref<int>.Null, null);



            SpritePivot pivot = this.spriteui.pivot;
            pivot.centerFactorX = x;
            pivot.centerFactorY = 0.5;
            pivot.usingFactor = true;
            pivot.isUndefined = false;

            initColorMap(sprmuld);


            AnimManager animManager = this.spriteui.get_anim().play(skinanim.AsHaxeString(), null, null).loop(null);
            animManager.genSpeed = 0.4;

            this.spriteui.set_visible(true);
            this.spritesflow.addChild(this.spriteui);
            this.bg?.addChild(this.spritesflow);
            this.sprites.Add(this.spriteui);
        }

        private string GetRandomAnimation(List<string> values)
        {
            Random fallbackRandom = new Random();
            int fallbackIndex = fallbackRandom.Next(values.Count);
            return values[fallbackIndex];
        }


        public void playallanims(HSprite hSprite)
        {
            try
            {
                var groups = hSprite.lib?.groups;
                if (groups == null)
                    return;

                var keysIterator = groups.keys();
                animlist.Clear();

                while (keysIterator.hasNext())
                {
                    string key = keysIterator.next().ToString();
                    if (!key.StartsWith("Atk", StringComparison.OrdinalIgnoreCase))
                        animlist.Add(key);
                }
            }
            catch
            {
            }
        }


        public void loadText()
        {

        }



        public void initColorMap(string colorMap)
        {
            dc.shader.ColorMap shader = (dc.shader.ColorMap)this.spriteui!.getShader(dc.shader.ColorMap.Class);
            if (shader != null)
            {
                this.spriteui.removeShader(shader);
            }

            dc.h3d.mat.Texture texture = Res.Class.load("atlas/beheaded_aladdin_s.png".AsHaxeString()).toTexture();
            dc.h3d.mat.Filter filter = new dc.h3d.mat.Filter.Nearest();
            filter = texture.set_filter(filter);

            virtual_colorMap_consoleCmdId_glowData_group_head_incompatibleHeads_item_model_onlyDefaultHead_scarfBlendMode_scarfs_ skinInfo = Cdb.Class.getSkinInfo(colorMap.AsHaxeString());
            dc.h3d.mat.Texture heroColorMap = Assets.Class.getHeroColorMap(skinInfo);
            dc.shader.ColorMap colorMapp = (ColorMap)this.spriteui.addShader(new dc.shader.ColorMap(heroColorMap));


            DirLighted s2 = new DirLighted();
            s2 = (DirLighted)this.spriteui.addShader(s2);


            dc.h3d.mat.Texture normalMapFromGroup = this.spriteui.lib.getNormalMapFromSprite(this.spriteui);
            dc.shader.NormalMap normal = new dc.shader.NormalMap(normalMapFromGroup);
            this.spriteui.addShader(normal);
        }


        private void clean()
        {
            ClearLobbyCodeUi();
            this.bg?.remove();
            this.rootFlow?.remove();
            this.inter?.remove();
            this.sprites.Clear();
        }


        public override void onResize()
        {
            base.onResize();
            if (this.rootFlow == null || base.root == null)
                return;

            var win = dc.hxd.Window.Class.getInstance();
            double screenWidth = win.get_width();
            double screenHeight = win.get_height();
            var uiScale = UiScale.GetResolutionScale();


            this.rootFlow.set_minWidth((int)(screenWidth * 0.4)); //宽度 40%
            this.rootFlow.set_minHeight((int)(screenHeight * 0.3)); // 高度 30%
            this.rootFlow.reflow();


            double flowW = this.rootFlow.get_innerWidth();
            double flowH = this.rootFlow.get_innerHeight();

            ClearLobbyCodeUi();
            this.bg?.remove();
            this.bg = UIBox.Class.drawBoxValidation(
                (int)flowW,
                (int)flowH,
                Ref<int>.Null,
                Ref<int>.Null,
                null,
                false
            );
            this.root.addChild(this.bg);

            this.bg.set_visible(true);
            this.bg.wid = (int)255;
            this.bg.hei = (int)flowH;


            double posX = screenWidth - flowW - base.get_pixelScale.Invoke() * 200.0; // 离右边 20 像素
            posX = screenWidth - flowW - base.get_pixelScale.Invoke() * 200.0 * uiScale;
            double posY = (screenHeight - flowH) / 1.35;
            this.rootFlow.x = posX;
            this.rootFlow.y = posY;


            this.bg.x = posX;
            this.bg.y = posY;


            this.inter?.remove();
            this.inter = new dc.h2d.Interactive(this.bg.wid, this.bg.hei, this.bg, null);
            this.inter.onClick = new HlAction<Event>(this.OnClick);
            BGtext();
            UpdateLobbyIdLabel(forceRefreshText: true);
        }


        private void BGtext()
        {
            this.MainTitleflow = new Flow(null);
            this.MainTitleflow.isVertical = true;
            var uiScale = UiScale.GetResolutionScale();

            FlowAlign flowAlign = this.MainTitleflow.set_horizontalAlign(new FlowAlign.Middle());
            flowAlign = this.MainTitleflow.set_verticalAlign(new FlowAlign.Top());

            double bgWidth = this.bg!.wid;
            double bgHeight = this.bg.hei;
            this.MainTitleflow.set_minWidth((int)bgWidth);
            this.MainTitleflow.set_minHeight((int)bgHeight);


            this.bg!.addChild(this.MainTitleflow);
            dc.ui.Text title = Assets.Class.makeText(
                GetText.Instance.GetString("Lobby menu").AsHaxeString(),
                Tools.MultiColor.ColorFromHex("#f7fc65"),
                true,
                null
            );

            title.scaleX = 0.6 * uiScale;
            title.scaleY = 0.6 * uiScale;

            this.MainTitleflow.addChild(title);


            Flow titleWrapper = new Flow(null);
            titleWrapper.isVertical = false;
            titleWrapper.set_horizontalAlign(new FlowAlign.Middle());

            titleWrapper.addChild(title);
            this.MainTitleflow.addChild(titleWrapper);

            dc.ui.Text subtitle = Assets.Class.makeText(
                GetText.Instance.GetString("Players' list").AsHaxeString(),
                Tools.MultiColor.ColorFromHex("#919191"),
                false,
                null
            );
            subtitle.scaleX = 0.5 * uiScale;
            subtitle.scaleY = 0.5 * uiScale;


            Flow subtitleWrapper = new Flow(null);
            subtitleWrapper.isVertical = false;
            subtitleWrapper.set_horizontalAlign(new FlowAlign.Middle());

            subtitleWrapper.addChild(subtitle);
            this.MainTitleflow.addChild(subtitleWrapper);

            Flow playersListWrapper = new Flow(null);
            playersListWrapper.isVertical = true;
            playersListWrapper.set_horizontalAlign(new FlowAlign.Middle());
            playersListWrapper.set_verticalSpacing((int)(4 * uiScale));

            this.MainTitleflow.addChild(playersListWrapper);
            updateConnections();
            this.MainTitleflow.reflow();

        }

        public void updateConnections()
        {
            RefreshConnections(null);
        }

        public static void NotifyConnectionsChanged()
        {
            Instance?.updateConnections();
        }

        private void RefreshConnections(List<string>? names)
        {
            if (this.MainTitleflow == null)
                return;

            var uiScale = UiScale.GetResolutionScale();
            for (int i = 0; i < this.connectionLabels.Count; i++)
            {
                var label = this.connectionLabels[i];
                this.MainTitleflow.removeChild(label);
                label.remove();
            }
            this.connectionLabels.Clear();

            List<string> allname = names ?? _ConnectionUI.GetAllPlayerNames();
            foreach (var name in allname)
            {
                bool isSteamLobbyConnecting = string.Equals(name, _ConnectionUI.SteamLobbyConnectingMarker, StringComparison.Ordinal);
                bool isConnecting =
                    isSteamLobbyConnecting
                    || string.Equals(name, "connecting", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "connecting...", StringComparison.OrdinalIgnoreCase);
                string displayName = isSteamLobbyConnecting
                    ? GetText.Instance.GetString("Connecting to Steam lobby...")
                    : isConnecting
                    ? GetText.Instance.GetString("connecting...")
                    : $"{GetText.Instance.GetString("- ")}{name}";
                var nameColor = Tools.MultiColor.ColorFromHex("#c9c9c9");
                dc.ui.Text player2 = Assets.Class.makeText(
                displayName.AsHaxeString(),
                nameColor,
                false,
                null
            );
                player2.customScale = 0.5 * uiScale;
                player2.onResize();
                player2.textColor = nameColor;
                this.MainTitleflow.addChild(player2);
                this.connectionLabels.Add(player2);
            }

            this.lastConnections.Clear();
            this.lastConnections.AddRange(allname);
            UpdateLobbyIdLabel(forceRefreshText: false);
        }

        private void ClearLobbyCodeUi()
        {
            this.lobbyCodeFlow?.remove();
            this.lobbyCodeFlow = null;

            this.lobbyCodeTitleLabel?.remove();
            this.lobbyCodeTitleLabel = null;
            this.lobbyIdLabel?.remove();
            this.lobbyIdLabel = null;
            this.lastLobbyIdLabelText = string.Empty;
        }

        private void EnsureLobbyCodeFlow(double uiScale)
        {
            if (this.bg == null || base.root == null)
                return;

            if (this.lobbyCodeFlow == null)
            {
                this.lobbyCodeFlow = new Flow(null);
                this.lobbyCodeFlow.isVertical = true;
                this.lobbyCodeFlow.set_horizontalAlign(new FlowAlign.Left());
                this.lobbyCodeFlow.set_verticalAlign(new FlowAlign.Bottom());
                this.lobbyCodeFlow.set_verticalSpacing((int)(2 * uiScale));
                this.lobbyCodeFlow.x += 10;
                this.lobbyCodeFlow.y += 80;
                this.bg.addChild(this.lobbyCodeFlow);
            }

            if (this.lobbyCodeTitleLabel == null)
            {
                var titleColor = Tools.MultiColor.ColorFromHex("#9ea8b3");
                this.lobbyCodeTitleLabel = Assets.Class.makeText(
                    GetText.Instance.GetString("Lobby code").AsHaxeString(),
                    titleColor,
                    false,
                    null);
                this.lobbyCodeFlow.addChild(this.lobbyCodeTitleLabel);
                this.lobbyCodeTitleLabel.textColor = titleColor;
            }

            if (this.lobbyIdLabel == null)
            {
                var idColor = Tools.MultiColor.ColorFromHex("#7fd4ff");
                this.lobbyIdLabel = Assets.Class.makeText(
                    string.Empty.AsHaxeString(),
                    idColor,
                    true,
                    null);
                this.lobbyCodeFlow.addChild(this.lobbyIdLabel);
                this.lobbyIdLabel.textColor = idColor;
            }

            var lobbyCodeScale = 0.55 * uiScale;
            this.lobbyCodeTitleLabel.customScale = lobbyCodeScale;
            this.lobbyCodeTitleLabel.onResize();
            this.lobbyCodeTitleLabel.textColor = Tools.MultiColor.ColorFromHex("#9ea8b3");
            this.lobbyIdLabel.customScale = lobbyCodeScale;
            this.lobbyIdLabel.onResize();
            this.lobbyIdLabel.textColor = Tools.MultiColor.ColorFromHex("#7fd4ff");
        }

        private void UpdateLobbyIdLabel(bool forceRefreshText)
        {
            if (this.bg == null)
                return;

            var lobbyCode = GameMenu.GetSteamLobbyCodeForUi();
            if (string.IsNullOrWhiteSpace(lobbyCode))
            {
                if (this.lobbyCodeFlow != null)
                    this.lobbyCodeFlow.set_visible(false);
                this.lastLobbyIdLabelText = string.Empty;
                return;
            }

            var uiScale = UiScale.GetResolutionScale();
            var labelText = lobbyCode.Trim().ToLowerInvariant();
            EnsureLobbyCodeFlow(uiScale);
            if (this.lobbyCodeFlow == null || this.lobbyIdLabel == null || this.lobbyCodeTitleLabel == null)
                return;

            if (forceRefreshText || !string.Equals(this.lastLobbyIdLabelText, labelText, StringComparison.Ordinal))
            {
                this.lobbyIdLabel.set_text(labelText.AsHaxeString());
                this.lastLobbyIdLabelText = labelText;
            }

            var leftPadding = 10.0 * uiScale;
            var bottomPadding = 8.0 * uiScale;
            this.lobbyCodeFlow.reflow();
            var flowHeight = this.lobbyCodeFlow.get_innerHeight();
            this.lobbyCodeFlow.x = this.bg.x + leftPadding;
            this.lobbyCodeFlow.y = this.bg.y + this.bg.hei - flowHeight - bottomPadding;
            this.lobbyCodeFlow.set_visible(true);
        }

        private bool NeedsConnectionsRefresh(List<string> names)
        {
            if (names.Count != this.lastConnections.Count)
                return true;

            for (int i = 0; i < names.Count; i++)
            {
                if (!string.Equals(names[i], this.lastConnections[i], StringComparison.Ordinal))
                    return true;
            }

            return false;
        }


        public override void update()
        {
            base.update();
            var names = _ConnectionUI.GetAllPlayerNames();
            if (NeedsConnectionsRefresh(names))
                RefreshConnections(names);
            else
                UpdateLobbyIdLabel(forceRefreshText: false);

            if (dc.hxd.Key.Class.isPressed(80))
            {
                clean();
                Log.Debug("destory ui");


            }
        }

        public override void postUpdate()
        {
            base.postUpdate();

        }

        private void OnClick(Event e)
        {
            if (this.lobbyCodeFlow == null || !this.lobbyCodeFlow.visible || this.bg == null)
                return;

            var x = e.relX;
            var y = e.relY;
            var width = this.lobbyCodeFlow.get_innerWidth();
            var height = this.lobbyCodeFlow.get_innerHeight();
            var minX = this.lobbyCodeFlow.x - this.bg.x;
            var minY = this.lobbyCodeFlow.y - this.bg.y;
            var maxX = minX + width;
            var maxY = minY + height;

            if (x < minX || x > maxX || y < minY || y > maxY)
                return;

            if (GameMenu.TryCopySteamLobbyCodeFromUi())
                MultiplayerUI.PushSystemMessage("Lobby id copied to clipboard");

        }


        public static void Initialize(ModEntry entry)
        {
            entry.Logger.Information("\x1b[32m[[ModEntry.ConnectionUI] Initializing ConnectionUI...]\x1b[0m ");
        }

        /// <summary>
        /// Ensures ConnectionUI exists on the given TitleScreen. Called from mainMenu hook
        /// to avoid Hashlink marshaling crash in TitleScreen constructor (bool? titleLib).
        /// </summary>
        public static void EnsureCreated(TitleScreen screen)
        {
            if (Instance != null && ReferenceEquals(Instance.parent, screen))
                return;
            Instance = null;
            var connectionUI = new ConnectionUI(screen);
            screen.addChild(connectionUI);
            connectionUI.root.set_visible(false);
        }



    }
}
