using DeadCellsMultiplayerMod.Interface.ModuleInitializing;
using ModCore.Events;
using dc;
using dc.h2d;
using dc.libs;
using dc.level;
using dc.pr;
using dc.tool;
using dc.critter;
using dc.light;
using dc.shader;
using dc.haxe.ds;
using dc.hl.types;
using ModCore.Mods;
using dc.libs.misc;
using dc.level.disp;
using dc.tool.signals;
using Math = dc.Math;
using dc.libs.heaps.slib;
using HaxeProxy.Runtime;
using ModCore.Utilities;
using dc.tool.quadTree;
using Hashlink.Virtuals;
using System.Reflection;


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
    }

    private void Levelinit_OnDispose(Hook_Level.orig_onDispose orig, Level self)
    {
        orig(self);
    }



    // Compatibility path for Dead Cells v35.9+ (June 2026).
    // Do not replace the game's complete Level.init implementation: the game now
    // owns render/UI initialization details that this legacy copied routine cannot
    // safely reproduce. MobSync attaches later through entitiesPostCreate.
    private void Levelinit_Main(Hook_Level.orig_init orig, Level self)
    {
        ModEntry.Instance?.Logger.Information(
            "[NetMod][Compat] using vanilla Level.init for level={LevelId}",
            self.map?.id?.ToString() ?? string.Empty);
        orig(self);
    }

    // Kept only as reference for older game builds. It is no longer hooked.
    private void LegacyLevelinit_Main(Hook_Level.orig_init orig, Level self)
    {
        initprocess(self);

        self.permanentTW = new Tweenie(self.getDefaultFrameRate());
        self.levelSignals = new LevelSignals();


        virtual_baseLootLevel_biome_bonusTripleScrollAfterBC_cellBonus_dlc_doubleUps_eliteRoomChance_eliteWanderChance_flagsProps_group_icon_id_index_loreDescriptions_mapDepth_minGold_mobDensity_mobs_name_nextLevels_parallax_props_quarterUpsBC3_quarterUpsBC4_specificLoots_specificSubBiome_transitionTo_tripleUps_worldDepth_
        Levelvirtual_ = new virtual_baseLootLevel_biome_bonusTripleScrollAfterBC_cellBonus_dlc_doubleUps_eliteRoomChance_eliteWanderChance_flagsProps_group_icon_id_index_loreDescriptions_mapDepth_minGold_mobDensity_mobs_name_nextLevels_parallax_props_quarterUpsBC3_quarterUpsBC4_specificLoots_specificSubBiome_transitionTo_tripleUps_worldDepth_();

        object? mapRaw = Data.Class.level.byId.get(self.map.id);
        Levelvirtual_ = ((HaxeDynObj)mapRaw!).ToVirtual<virtual_baseLootLevel_biome_bonusTripleScrollAfterBC_cellBonus_dlc_doubleUps_eliteRoomChance_eliteWanderChance_flagsProps_group_icon_id_index_loreDescriptions_mapDepth_minGold_mobDensity_mobs_name_nextLevels_parallax_props_quarterUpsBC3_quarterUpsBC4_specificLoots_specificSubBiome_transitionTo_tripleUps_worldDepth_>();


        if (self.viewport == null)
        {
            self.viewport = new Viewport(self);
        }

        self.entitiesByClass = new IntMap();



        int Index = 0;
        ArrayBytes_Int arrayBytes_Int;
        if (Level.Class.ENTITIES_CLIDS != null)
        {
            arrayBytes_Int = Level.Class.ENTITIES_CLIDS;
        }
        else
        {
            object? hlTypeObj = dc.haxe.rtti.Meta.Class.getType(Level.Class);
            var entitiesProp = hlTypeObj?.GetType().GetProperty("entitiesByClassUsed", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (entitiesProp?.GetValue(hlTypeObj) is not ArrayDyn arrayDyn)
                throw new InvalidOperationException("Level hl.Type.entitiesByClassUsed unavailable");
            arrayBytes_Int = ArrayUtils.CreateInt();

            int len = arrayDyn.get_length();
            for (; Index < len; Index++)
            {
                int dyn = arrayDyn.getDyn(Index);
                arrayBytes_Int.push(dyn);
            }

            Level.Class.ENTITIES_CLIDS = arrayBytes_Int;
            Index = 0;
        }



        ArrayObj arrayObj;
        while (Index < arrayBytes_Int.length)
        {
            int clid = arrayBytes_Int.getDyn(Index);
            Index++;
            IntMap entitiesByClassMap = self.entitiesByClass;
            arrayObj = (ArrayObj)ArrayUtils.CreateDyn().array;
            entitiesByClassMap.set(clid, arrayObj);
        }




        Index = 0;
        arrayObj = self.entities;
        ArrayObj obj;
        int entitiesCount = arrayObj.length;
        for (; Index < entitiesCount; Index++)
        {
            var arrayobj = arrayObj.getDyn(Index);
            if (arrayobj == null) continue;

            Entity entity = (Entity)arrayobj;
            arrayBytes_Int = entity.getEntityCLIDS();

            int clidsCount = arrayBytes_Int.length;
            for (int Index3 = 0; Index3 < clidsCount; Index3++)
            {
                int clid = arrayBytes_Int.getDyn(Index3);
                obj = (ArrayObj)self.entitiesByClass.get(clid);
                if (obj != null)
                {
                    obj.push(entity);
                }
            }
        }

        self.splatters = (ArrayObj)ArrayUtils.CreateDyn().array;
        self.entityLights = (ArrayObj)ArrayUtils.CreateDyn().array;
        self.uiProcesses = (ArrayObj)ArrayUtils.CreateDyn().array;
        self.entitiesGC = (ArrayObj)ArrayUtils.CreateDyn().array;
        self.accu = 0.0;



        Index = Level.Class.cirColBufferMaxCount;
        arrayObj = (ArrayObj)ArrayUtils.CreateDyn().array;
        if (Index > 0)
        {
            int i = Index - 1;
            Entity entity = null!;

            if (i >= arrayObj.length)
            {
                arrayObj.__expand(i);
            }
            arrayObj.array[i] = entity;
        }

        self.cirColEntitiesBuffer = arrayObj;
        Boot.Class.tryRender();


        Layers root = new Layers(null);
        self.root = root;


        FlashLight.Class.alloc(self, 64);


        int? generationFlagIndex = null;
        int? lootFlagIndex = null;
        int? gameplayFlagIndex = null;
        int? metaFlagIndex = null;
        int? visualFlagIndex = 0;

        virtual_gameplayFlags_genFlags_lootFlags_metaFlags_visualFlags_ flagsProps = Levelvirtual_.flagsProps;
        bool hasFlag = true;

        if (generationFlagIndex.HasValue)
        {
            hasFlag = CheckFlag(flagsProps.genFlags, generationFlagIndex.Value);
        }
        else if (lootFlagIndex.HasValue)
        {
            hasFlag = CheckFlag(flagsProps.lootFlags, lootFlagIndex.Value);
        }
        else if (gameplayFlagIndex.HasValue)
        {
            hasFlag = CheckFlag(flagsProps.gameplayFlags, gameplayFlagIndex.Value);
        }
        else if (metaFlagIndex.HasValue)
        {
            hasFlag = CheckFlag(flagsProps.metaFlags, metaFlagIndex.Value);
        }
        else if (visualFlagIndex.HasValue)
        {
            hasFlag = CheckFlag(flagsProps.visualFlags, visualFlagIndex.Value);
        }

        self.scroller = new LightedLayers(self, Ref<bool>.From(ref hasFlag));

        int backgroundColorAlpha = (int)255.0 << 24;
        int backgroundColor = backgroundColorAlpha | 11265535;
        self.scroller.backgroundColor = backgroundColor;

        self.root.addChildAt(self.scroller, Const.Class.ROOT_DP_MAIN);


        OnionSkin.Class.alloc(self, 128);
        Boot.Class.tryRender();


        self.controller = Boot.Class.ME.controller.createAccess("level".AsHaxeString(), null);

        self.cm = new Cinematic((int)self.getDefaultFrameRate());


        dc.h2d.Object @object = new dc.h2d.Object(null);
        LightedLayers scroller = self.scroller;
        self.scroller.addChildAt(@object, Const.Class.DP_ROOM_BACK_FX);


        dc.h2d.Object object2 = new dc.h2d.Object(null);
        self.scroller.addChildAt(object2, Const.Class.DP_ROOM_MAIN_FX);


        TopFx topFx = new TopFx(null);
        self.scroller.addChildAt(topFx, Const.Class.DP_FOREGROUND_FX);


        self.fx = new Fx(self, @object, object2, topFx);
        Boot.Class.tryRender();
        self.map.init();
        Boot.Class.tryRender();
        self.map.buildBlurredCols();
        Boot.Class.tryRender();

        dc.String id = self.map.biome.id;
        var hasatlasname = (HaxeDynObj)Data.Class.biome.byId.get(id);
        dc.String atlasname = hasatlasname.ToVirtual<virtual_ambient_ambientScale_atlasName_camEffects_celShadow_cloud_commonAtlas_floorJunkDensity_floorStamps_fog_fogScale_glowData_id_lavaBg_lavaSurface_layers_lightColors_oneWayOpaque_reverbKind_reverbMix_scatterConf_smoke_smokeShader_uiProps_vegetation_vegetationRandScale_wallJunkDensity_water_waterLight_>().atlasName;


        virtual_norm_slib_used_ level = Assets.Class.lib.getLevel(atlasname, new HlAction(self.onLevelAssetsReloaded));
        self.slib = level.slib;
        self.norm = level.norm;
        self.lAudio = new LevelAudio(self);

        Boot.Class.tryRender();


        LevelDisp lDisp;
        Dictionary<string, Func<LevelDisp>> levelDispMappings = new Dictionary<string, Func<LevelDisp>>()
        {
            ["AncientTemple"] = () => new AncientTemple(self, self.map, id),
            ["CastleAlchemy"] = () => new CastleAlchemy(self, self.map, id),
            ["CastleTorture"] = () => new CastleTorture(self, self.map, id),
            ["GardenerStage"] = () => new GardenerStage(self, self.map, id, "Gardener_outside".AsHaxeString()),
            ["LighthouseTop"] = () => new Lighthouse(self, self.map, id, "LighthouseTop".AsHaxeString()),
            ["PrisonCorrupt"] = () => new Prison(self, self.map, id),
            ["RichterCastle"] = () => new RichterCastle(self, self.map),
            ["SkinningBiome"] = () => new Prison(self, self.map, id),
            ["TopClockTower"] = () => new TopClockTower(self, self.map, id),
            ["Astrolab"] = () => new Astrolab(self, self.map),
            ["Cemetery"] = () => new Cemetery(self, self.map, id),
            ["SewerOld"] = () =>
            {
                bool flag2 = true;
                return new Sewer(self, self.map, Ref<bool>.From(ref flag2));
            },
            ["Bank"] = () => new Bank(self, self.map),
            ["BeholderPit"] = () => new BeholderPit(self, self.map, id),
            ["CastleVegan"] = () => new CastleVegan(self, self.map, id),
            ["CemeteryInt"] = () => new Cemetery(self, self.map, id),
            ["DookuCastle"] = () => new DookuCastle(self, self.map),
            ["Observatory"] = () => new Observatory(self, self.map),
            ["PrisonStart"] = () => new Prison(self, self.map, id),
            ["SecretRooms"] = () => new SecretRooms(self, self.map),
            ["BossRushZone"] = () => new BossRushZone(self, self.map),
            ["PrisonDepths"] = () => new Prison(self, self.map, id),
            ["PurpleGarden"] = () => new PurpleGarden(self, self.map),
            ["StiltVillage"] = () => new StiltVillage(self, self.map),
            ["Bridge"] = () => new Bridge(self, self.map),
            ["Castle"] = () => new Castle(self, self.map, id),
            ["Cavern"] = () => new Cavern(self, self.map, id),
            ["Throne"] = () => new Throne(self, self.map),
            ["BridgeBoatDock"] = () => new Docks(self, self.map),
            ["Cliff"] = () => new Cliff(self, self.map, id, "Cliff_outside".AsHaxeString()),
            ["Crypt"] = () => new Crypt(self, self.map),
            ["Giant"] = () => new Cavern(self, self.map, id),
            ["Sewer"] = () => new Sewer(self, self.map, Ref<bool>.Null),
            ["Swamp"] = () => new Swamp(self, self.map),
            ["ClockTower"] = () => new ClockTower(self, self.map, id),
            ["DeathArena"] = () => new DeathArena(self, self.map),
            ["Distillery"] = () => new Distillery(self, self.map),
            ["DookuArena"] = () => new DookuArena(self, self.map, id, "DookuBeastArena".AsHaxeString()),
            ["Greenhouse"] = () => new Greenhouse(self, self.map, id, "Greenhouse_underground".AsHaxeString()),
            ["PrisonRoof"] = () => new PrisonRoof(self, self.map),
            ["QueenArena"] = () => new QueenArena(self, self.map, id),
            ["SwampHeart"] = () => new SwampHeart(self, self.map),
            ["LighthouseBottom"] = () => new Lighthouse(self, self.map, id, "LighthouseTop".AsHaxeString()),
            ["PrisonCourtyard2"] = () => new PrisonCourtyard(self, self.map, id),
            ["Ossuary"] = () => new Ossuary(self, self.map),
            ["Tumulus"] = () => new Tumulus(self, self.map, id),
            ["PhotoRoom"] = () => new PhotoRoom(self, self.map, null),
            ["PrisonHub"] = () => new Prison(self, self.map, id),
            ["Shipwreck"] = () => new Shipwreck(self, self.map, id, "Shipwreck_underground".AsHaxeString()),
            ["PrisonCourtyard"] = () => new PrisonCourtyard(self, self.map, id),
            ["StiltVillageInt"] = () => new StiltVillage(self, self.map),
            ["PrisonRoofCorrupt"] = () => new PrisonRoof(self, self.map),
            ["Shipwreck_underground"] = () => new Shipwreck(self, self.map, id, "Shipwreck_underground".AsHaxeString()),
            ["Template"] = () => new Template(self, self.map),
            ["TumulusInt"] = () => new Tumulus(self, self.map, id),

        };

        if (levelDispMappings.TryGetValue(id.ToString(), out var createLevelDispFunc))
        {
            lDisp = createLevelDispFunc();
            self.lDisp = lDisp;
            Boot.Class.tryRender();
        }

        QtRectangle boundary = new QtRectangle(0, 0, (int)Math.Class.max((double)self.map.wid, (double)self.map.hei), (int)Math.Class.max((double)self.map.wid, (double)self.map.hei));
        self.qTree = new QuadTree(boundary, 4, 1, self.lDisp.debug);


        SpriteLib gameElements = Assets.Class.gameElements;
        Tile tile = (Tile)gameElements.pages.getDyn(0)!;


        self.sbUi = self.createStandardBatch(tile, Const.Class.DP_CTX_UI);
        self.sbCritters = self.createStandardBatch(tile, Const.Class.DP_ROOM_BACK_FX);
        self.sbCritters.blendMode = new BlendMode.Alpha();
        self.lDisp.applyLayerConf(self.sbCritters, "MainFrontWalls".AsHaxeString(), Ref<bool>.Null, Ref<double>.Null);

        SplatterCont splatterCont = new SplatterCont(null);
        self.scroller.addChildAt(splatterCont, Const.Class.DP_ROOM_BACK_FX);


        self.sbSplatters = new HSpriteBatch(tile, null);
        self.sbSplatters.hasRotationScale = true;
        splatterCont.addChild(self.sbSplatters);


        self.sbBodyPart = new HSpriteBatch(tile, null);
        self.sbBodyPart.hasRotationScale = true;
        self.scroller.addChildAt(self.sbBodyPart, Const.Class.DP_ROOM_BACK);



        self.sbBodyPartFront = new HSpriteBatch(tile, null);
        self.sbBodyPartFront.hasRotationScale = true;
        self.scroller.addChildAt(self.sbBodyPartFront, Const.Class.DP_ROOM_FRONT_HERO);



        self.sbPendulum_ChainBack = self.createStandardBatch(tile, Const.Class.DP_ROOM_BACK);
        NormalMap normalMap = (NormalMap)self.sbPendulum_ChainBack.addShader(new NormalMap(self.norm));
        self.sbPendulum_ChainBack.blendMode = new BlendMode.Alpha();
        self.lDisp.applyLayerConf(self.sbPendulum_ChainBack, "MainAction".AsHaxeString(), Ref<bool>.Null, Ref<double>.Null);


        gameElements = self.slib;
        tile = (Tile)gameElements.pages.getDyn(0)!;
        self.sbPendulum_ChainFront = self.createStandardBatch(tile, Const.Class.DP_ROOM_FRONT);
        normalMap = (NormalMap)self.sbPendulum_ChainFront.addShader(new NormalMap(self.norm));
        self.sbPendulum_ChainFront.blendMode = new BlendMode.Alpha();
        self.lDisp.applyLayerConf(self.sbPendulum_ChainBack, "MainBackProps".AsHaxeString(), Ref<bool>.Null, Ref<double>.Null);




        obj = CdbTypeConverter.Class.getGlowData(self.map.biome);
        if (0 < obj.length)
        {
            GlowKey s = new GlowKey(obj);
            GlowKey glowKey = (GlowKey)self.sbPendulum_ChainBack.addShader(s);
            glowKey = (GlowKey)self.sbPendulum_ChainFront.addShader(s);
        }


        self.mask = new Bitmap(Tile.Class.fromColor(0, 1, 1, null, null), null);

        double tileWidth = (double)self.mask.tile.width;
        int halfTileWidth = -(int)(0.5 * tileWidth);
        tile.dx = halfTileWidth;

        double tileHeight = (double)tile.height;
        int halfTileHeight = -(int)(0.5 * tileHeight);
        tile.dy = halfTileHeight;


        int backgroundDarkenerColor = Main.Class.ME.options.backgroundDarkenerColor;
        double? backgroundDarkenerAlpha = Main.Class.ME.options.backgroundDarkenerAlpha;
        self.bgDarkener = new Bitmap(Tile.Class.fromColor(backgroundDarkenerColor, 1, 1, backgroundDarkenerAlpha, null), null);


        tile = self.bgDarkener.tile;
        tileWidth = (double)tile.width;
        halfTileWidth = -(int)(0.5 * tileWidth);
        tile.dx = halfTileWidth;

        tileHeight = (double)tile.height;
        halfTileHeight = -(int)(0.5 * tileHeight);
        tile.dy = halfTileHeight;

        self.scroller.addChildAt(self.bgDarkener, Const.Class.DP_ROOM_BACK_DECO);

        self.critters = (ArrayObj)ArrayUtils.CreateDyn().array;

        virtual_gameplayFlags_genFlags_lootFlags_metaFlags_visualFlags_ flagsProps2 = self.map.infos.flagsProps;
        hasFlag = ((flagsProps2.visualFlags & 1 << 1) != 0);



        dc.libs.Rand rand;
        if (!hasFlag)
        {
            CritterGen critterGen = new CritterGen(self);
            Boot.Class.tryRender();
        }
        rand = new dc.libs.Rand(self.map.seed);
        obj = self.map.rooms;


        int roomIndex = 0;
        int markerIndex = 0;
        while (true)
        {
            int roomsCount = obj.length;
            if (roomIndex >= roomsCount)
            {
                break;
            }

            roomsCount = obj.length;
            Room room;
            ArrayObj markers;

            if (roomIndex >= roomsCount)
            {
                room = null!;
                roomIndex++;
                markerIndex = 0;
                markers = room!.markers;
            }
            else
            {
                var roommakers = obj.getDyn(roomIndex);
                room = (Room)roommakers!;
                roomIndex++;
                markerIndex = 0;
                markers = room.markers;
            }

            while (true)
            {
                int markersCount = markers.length;
                if (markerIndex >= markersCount)
                {
                    break;
                }

                markersCount = markers.length;
                Marker marker;
                if (markerIndex >= markersCount)
                {
                    marker = null!;
                }
                else
                {
                    var markerdy = markers.getDyn(markerIndex);
                    marker = (Marker)markerdy!;
                }
                markerIndex++;

                if (marker!.kind == "Critters".AsHaxeString())
                {
                    dc.String customId = marker.customId;
                    if (customId != null)
                    {

                        if (customId.ToString() == "bats")
                        {
                            GenerateBats(rand, room, marker, self);
                            continue;
                        }

                        if (customId.ToString() == "crow")
                        {
                            GenerateCrows(rand, room, marker, self);
                        }

                    }
                }
            }
            Boot.Class.tryRender();
        }
        self.onResize();
        self.onApplyOptions();
        virtual_xMax_xMin_yMax_yMin_ viewportBounds = new virtual_xMax_xMin_yMax_yMin_();
        viewportBounds.xMin = 0;
        viewportBounds.yMin = 0;
        viewportBounds.xMax = 99999;
        viewportBounds.yMax = 99999;
        self.newViewportRect = viewportBounds;
    }


    private bool CheckFlag(int flags, int flagIndex)
    {
        return (flags & (1 << flagIndex)) != 0;
    }

    private int RoundUp(double value)
    {
        int rounded = (int)value;
        return (double)rounded < value ? rounded + 1 : rounded;
    }

    private int RoundDown(double value)
    {
        int rounded = (int)value;
        return (double)rounded > value ? rounded - 1 : rounded;
    }

    private void GenerateBats(Rand rand, Room room, Marker marker, dc.pr.Level level)
    {
        double batCountDouble = (double)marker.width / 10.0;
        double zero = 0.0;
        int batCountRounded;

        if (zero < batCountDouble)
        {
            batCountRounded = RoundUp(batCountDouble + 0.5);
        }
        else if (batCountDouble < 0.0)
        {
            batCountRounded = RoundDown(batCountDouble - 0.5);
        }
        else
        {
            batCountRounded = 0;
        }

        for (int i = 0; i < batCountRounded; i++)
        {
            int widthRange = marker.width - 2 - 1;


            double seedResult = rand.seed * 16807.0 % 2147483647.0;
            rand.seed = seedResult;
            int batX = room.x + marker.cx;
            int randomValue = (int)seedResult & 1073741823;
            int range = widthRange - 2 + 1;
            randomValue %= range;
            int xOffset = 2 + randomValue;
            batX += xOffset;


            int clusterCount = 0;
            seedResult = rand.seed * 16807.0 % 2147483647.0;
            rand.seed = seedResult;
            range = ((int)seedResult & 1073741823) % 5;
            clusterCount = 3 + range;


            for (int j = 0; j < clusterCount; j++)
            {
                bool useRandomDirection = true;
                bool? directionFlag = useRandomDirection;

                if (directionFlag == null)
                {
                    useRandomDirection = false;
                    directionFlag = useRandomDirection;
                }


                seedResult = rand.seed * 16807.0 % 2147483647.0;
                rand.seed = seedResult;
                int horizontalOffset = ((int)seedResult & 1073741823) % 3;

                if (directionFlag != null)
                {

                    seedResult = rand.seed * 16807.0 % 2147483647.0;
                    rand.seed = seedResult;
                    int direction = ((int)seedResult & 1073741823) % 2 * 2 - 1;
                    int xAdjustment = horizontalOffset * direction;
                    int finalX = batX + xAdjustment;
                    int finalY = room.y + marker.cy + 1;


                    dc.critter.Bat bat = new dc.critter.Bat(level, finalX, finalY);
                }
                else
                {

                    int xAdjustment = horizontalOffset;
                    int finalX = batX + xAdjustment;
                    int finalY = room.y + marker.cy + 1;


                    dc.critter.Bat bat = new dc.critter.Bat(level, finalX, finalY);
                }
            }
        }
    }


    private void GenerateCrows(Rand rand, Room room, Marker marker, dc.pr.Level level)
    {
        double crowCountDouble = (double)marker.width / 10.0;
        double zero = 0.0;
        int crowCountRounded;

        if (zero < crowCountDouble)
        {
            crowCountRounded = RoundUp(crowCountDouble + 0.5);
        }
        else if (crowCountDouble < 0.0)
        {
            crowCountRounded = RoundDown(crowCountDouble - 0.5);
        }
        else
        {
            crowCountRounded = 0;
        }

        for (int i = 0; i < crowCountRounded; i++)
        {
            int widthRange = marker.width - 2 - 1;


            double seedResult = rand.seed * 16807.0 % 2147483647.0;
            rand.seed = seedResult;
            int crowX = room.x + marker.cx;
            int randomValue = (int)seedResult & 1073741823;
            int range = widthRange - 2 + 1;
            randomValue %= range;
            int xOffset = 2 + randomValue;
            crowX += xOffset;

            int clusterCount = 0;
            seedResult = rand.seed * 16807.0 % 2147483647.0;
            rand.seed = seedResult;
            range = ((int)seedResult & 1073741823) % 5;
            clusterCount = 3 + range;


            for (int j = 0; j < clusterCount; j++)
            {
                bool useRandomDirection = true;
                bool? directionFlag = useRandomDirection;

                if (directionFlag == null)
                {
                    useRandomDirection = false;
                    directionFlag = useRandomDirection;
                }


                seedResult = rand.seed * 16807.0 % 2147483647.0;
                rand.seed = seedResult;
                int horizontalOffset = ((int)seedResult & 1073741823) % 3;

                if (directionFlag != null)
                {

                    seedResult = rand.seed * 16807.0 % 2147483647.0;
                    rand.seed = seedResult;
                    int direction = ((int)seedResult & 1073741823) % 2 * 2 - 1;
                    int xAdjustment = horizontalOffset * direction;
                    int finalX = crowX + xAdjustment;
                    int finalY = room.y + marker.cy;


                    Crow crow = new Crow(level, finalX, finalY);
                }
                else
                {

                    int xAdjustment = horizontalOffset;
                    int finalX = crowX + xAdjustment;
                    int finalY = room.y + marker.cy;


                    Crow crow = new Crow(level, finalX, finalY);
                }
            }
        }
    }


    public void initprocess(dc.pr.Level level)
    {
        level.name = "process".AsHaxeString();

        dc.libs._Process @class = dc.libs.Process.Class;
        level.uniqId = @class.UNIQ_ID++;

        level.children = (ArrayObj)ArrayUtils.CreateDyn().array;
        level.paused = false;
        level.destroyed = false;
        level.ftime = 0.0;
        level.tmod = 1.0;
        level.speedMod = 1.0;

        double frameRate = level.getDefaultFrameRate();
        level.delayer = new Delayer(frameRate);
        level.cd = new dc.libs.Cooldown(frameRate);
        level.tw = new Tweenie(frameRate);
    }


}
