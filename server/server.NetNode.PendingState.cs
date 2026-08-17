using System.Collections.Generic;
using DeadCellsMultiplayerMod.AdvancedCoop;
using DeadCellsMultiplayerMod.Interaction;

public sealed partial class NetNode
{
    private List<RemoteAttack> _pendingAttacks = new();
    private List<MobStateSnapshot> _pendingMobStates = new();
    private List<MobMoveSnapshot> _pendingMobMoves = new();
    private readonly Dictionary<(int Generation, int SyncId), int> _pendingMobMoveSlots = new();
    private List<MobHit> _pendingMobHits = new();
    private List<MobDie> _pendingMobDies = new();
    private List<MobAttack> _pendingMobAttacks = new();
    private List<MobDraw> _pendingMobDraws = new();
    private List<MobRegistryEntry> _pendingMobRegistry = new();
    private List<ExitReadyState> _pendingExitReadyStates = new();
    private List<ExitTransitionCommit> _pendingExitTransitionCommits = new();
    private HostSpawnAnchor? _latestHostSpawnAnchor;
    private List<PlayerDownState> _pendingPlayerDownStates = new();
    private List<PlayerReviveRequest> _pendingPlayerReviveRequests = new();
    private List<string> _pendingBossCineLevelIds = new();
    private List<BossHeroTeleportEvent> _pendingBossHeroTeleports = new();
    private List<InterDoorEvent> _pendingInterDoorEvents = new();
    private List<InterElevatorEvent> _pendingInterElevatorEvents = new();
    private List<InterElevatorStateEvent> _pendingInterElevatorStateEvents = new();
    private List<InterPressurePlateEvent> _pendingInterPressurePlateEvents = new();
    private List<InterTreasureChestEvent> _pendingInterTreasureChestEvents = new();
    private List<InterVineLadderEvent> _pendingInterVineLadderEvents = new();
    private List<InterTeleportEvent> _pendingInterTeleportEvents = new();
    private List<InterBreakableGroundEvent> _pendingInterBreakableGroundEvents = new();
    private List<InterBossRuneUpdateCellsEvent> _pendingBossRuneUpdateCells = new();
    private List<InterPortalEvent> _pendingInterPortalEvents = new();
    private int _primaryRemoteId;
}
