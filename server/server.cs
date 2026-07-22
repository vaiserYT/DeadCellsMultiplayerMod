using System.Net;
using System.Net.Sockets;
using System.Text;
using DeadCellsMultiplayerMod;
using DeadCellsMultiplayerMod.Interaction;
using Serilog;
using Steamworks;

public enum NetRole { None, Host, Client }
public enum RemoteAttackAction { Attack, Interrupt }

public sealed partial class NetNode : IDisposable
{
    private readonly ILogger _log;
    private readonly NetRole _role;

    private sealed class ClientConnection : IDisposable
    {
        public TcpClient Client { get; }
        public NetworkStream Stream { get; }
        public SemaphoreSlim SendLock { get; } = new(1, 1);
        public int AssignedId { get; }
        public EndPoint? RemoteEndPoint => Client.Client?.RemoteEndPoint;
        private int _handshakeComplete;
        public bool HandshakeComplete => Volatile.Read(ref _handshakeComplete) != 0;

        public ClientConnection(TcpClient client, int assignedId)
        {
            Client = client;
            Stream = client.GetStream();
            AssignedId = assignedId;
        }

        public bool TryCompleteHandshake() => Interlocked.Exchange(ref _handshakeComplete, 1) == 0;

        public void Dispose()
        {
            try { Stream.Close(); } catch { }
            try { Client.Close(); } catch { }
            try { SendLock.Dispose(); } catch { }
        }
    }

    private sealed class SteamClientConnection : IDisposable
    {
        public CSteamID SteamId { get; }
        public SemaphoreSlim SendLock { get; } = new(1, 1);
        public int AssignedId { get; }
        private int _handshakeComplete;
        public bool HandshakeComplete => Volatile.Read(ref _handshakeComplete) != 0;
        private readonly object _initialStateSync = new();
        private DateTime _lastInitialStateSentUtc = DateTime.MinValue;

        public SteamClientConnection(CSteamID steamId, int assignedId)
        {
            SteamId = steamId;
            AssignedId = assignedId;
        }

        public bool TryCompleteHandshake() => Interlocked.Exchange(ref _handshakeComplete, 1) == 0;

        public bool TryReserveInitialStateSend(TimeSpan minInterval, bool force = false)
        {
            var now = DateTime.UtcNow;
            lock (_initialStateSync)
            {
                if (!force &&
                    _lastInitialStateSentUtc != DateTime.MinValue &&
                    now - _lastInitialStateSentUtc < minInterval)
                {
                    return false;
                }

                _lastInitialStateSentUtc = now;
                return true;
            }
        }

        public void Dispose()
        {
            try { SendLock.Dispose(); } catch { }
        }
    }

    private sealed class RemoteState
    {
        public int Id { get; }
        public double X;
        public double Y;
        public int Dir = 1;
        public bool HasPosition;
        public bool HasRemote;
        public string? LevelId;
        public string? RoomLevelId;
        public int? RoomId;
        public bool HasRoom;
        public string? Anim;
        public int? AnimQueue;
        public bool? AnimG;
        public bool HasAnim;
        public int Life;
        public int MaxLife;
        public int Lif;
        public int BonusLife;
        public int Recover;
        public string? Username;
        public string? Skin;
        public string? Head;

        public string HeadAnim;
        public bool HasHeadAnim;

        public string? WeaponKind;
        public int WeaponSlot;
        public int WeaponPermanentId;
        public int WeaponAmmo = int.MinValue;
        public bool HasWeaponUpdate;

        public RemoteState(int id)
        {
            Id = id;
            HeadAnim = string.Empty;
        }
    }

    public readonly struct RemoteSnapshot
    {
        public readonly int Id;
        public readonly double X;
        public readonly double Y;
        public readonly int Dir;
        public readonly string? LevelId;
        public readonly string? RoomLevelId;
        public readonly int? RoomId;
        public readonly bool HasRoom;
        public readonly string? Anim;
        public readonly int? AnimQueue;
        public readonly bool? AnimG;
        public readonly bool HasAnim;
        public readonly string? Username;
        public readonly string? HeadAnim;
        public readonly bool HasHeadAnim;

        public RemoteSnapshot(
            int id,
            double x,
            double y,
            int dir,
            string? levelId,
            string? roomLevelId,
            int? roomId,
            bool hasRoom,
            string? anim,
            int? animQueue,
            bool? animG,
            bool hasAnim,
            string? username,
            string? headAnim,
            bool hasHeadAnim)
        {
            Id = id;
            X = x;
            Y = y;
            Dir = dir;
            LevelId = levelId;
            RoomLevelId = roomLevelId;
            RoomId = roomId;
            HasRoom = hasRoom;
            Anim = anim;
            AnimQueue = animQueue;
            AnimG = animG;
            HasAnim = hasAnim;
            Username = username;
            HeadAnim = headAnim;
            HasHeadAnim = hasHeadAnim;
        }
    }

    public readonly struct RemoteWeaponSnapshot
    {
        public readonly int Id;
        public readonly string? Kind;
        public readonly int Slot;
        public readonly int PermanentId;
        public readonly int? Ammo;

        public RemoteWeaponSnapshot(int id, string? kind, int slot, int permanentId, int? ammo)
        {
            Id = id;
            Kind = kind;
            Slot = slot;
            PermanentId = permanentId;
            Ammo = ammo;
        }
    }

    public readonly struct RemoteAttack
    {
        public readonly int Id;
        public readonly string? Kind;
        public readonly int Slot;
        public readonly int PermanentId;
        public readonly int? Ammo;
        public readonly RemoteAttackAction Action;

        public RemoteAttack(int id, string? kind, int slot, int permanentId, int? ammo, RemoteAttackAction action)
        {
            Id = id;
            Kind = kind;
            Slot = slot;
            PermanentId = permanentId;
            Ammo = ammo;
            Action = action;
        }
    }

    public readonly struct RemoteHpSnapshot
    {
        public readonly int Id;
        public readonly int Life;
        public readonly int MaxLife;
        public readonly int Lif;
        public readonly int BonusLife;
        public readonly int Recover;
        public readonly string? Username;

        public RemoteHpSnapshot(int id, int life, int maxLife, int lif, int bonusLife, int recover, string? username)
        {
            Id = id;
            Life = life;
            MaxLife = maxLife;
            Lif = lif;
            BonusLife = bonusLife;
            Recover = recover;
            Username = username;
        }
    }

    public readonly struct RemoteUserSnapshot
    {
        public readonly int Id;
        public readonly string? Username;

        public RemoteUserSnapshot(int id, string? username)
        {
            Id = id;
            Username = username;
        }
    }

    public readonly struct RemoteChatMessage
    {
        public readonly int Id;
        public readonly string? Username;
        public readonly string Message;

        public RemoteChatMessage(int id, string? username, string message)
        {
            Id = id;
            Username = username;
            Message = message ?? string.Empty;
        }
    }

    public readonly struct MobStateSnapshot
    {
        public readonly int Index;
        public readonly int Generation;
        public readonly double X;
        public readonly double Y;
        public readonly int Dir;
        public readonly int Life;
        public readonly int MaxLife;
        public readonly string AnimPayload;
        public readonly string Type;
        public readonly string StatePayload;
        public readonly double Time;
        public readonly double Dx;
        public readonly double Dy;

        public MobStateSnapshot(int index, double x, double y, int dir, int life, int maxLife, string animPayload, string type, string statePayload = "", int generation = 0, double time = 0.0, double dx = 0.0, double dy = 0.0)
        {
            Index = index;
            Generation = generation;
            X = x;
            Y = y;
            Dir = dir;
            Life = life;
            MaxLife = maxLife;
            AnimPayload = animPayload ?? string.Empty;
            Type = type ?? string.Empty;
            StatePayload = statePayload ?? string.Empty;
            Time = time;
            Dx = dx;
            Dy = dy;
        }
    }

    public readonly struct MobMoveSnapshot
    {
        public readonly int Index;
        public readonly int Generation;
        public readonly double X;
        public readonly double Y;
        public readonly int Dir;
        public readonly string AnimPayload;
        public readonly double Time;
        public readonly double Dx;
        public readonly double Dy;

        public MobMoveSnapshot(int index, double x, double y, int dir, string animPayload, int generation = 0, double time = 0.0, double dx = 0.0, double dy = 0.0)
        {
            Index = index;
            Generation = generation;
            X = x;
            Y = y;
            Dir = dir;
            AnimPayload = animPayload ?? string.Empty;
            Time = time;
            Dx = dx;
            Dy = dy;
        }
    }

    public readonly struct MobChargeSnapshot
    {
        public readonly int Index;
        public readonly int Generation;
        public readonly string SkillId;
        public readonly double Ratio;

        public MobChargeSnapshot(int index, string skillId, double ratio, int generation = 0)
        {
            Index = index;
            Generation = generation;
            SkillId = skillId ?? string.Empty;
            Ratio = ratio;
        }
    }

    public readonly struct MobHit
    {
        public readonly int UserId;
        public readonly int MobIndex;
        public readonly int Generation;
        public readonly int Hp;
        public readonly double X;
        public readonly double Y;
        /// <summary>Mob type signature from MOBEVENT row (for host hit routing when syncId was rebound to a nearby same-type mob).</summary>
        public readonly string Type;
        /// <summary>Best-effort client damage intent. Used only when local client state rejected the hit and HP did not decrease.</summary>
        public readonly double DamageHint;

        public MobHit(int userId, int mobIndex, int hp, double x, double y, string type = "", int generation = 0, double damageHint = 0.0)
        {
            UserId = userId;
            MobIndex = mobIndex;
            Generation = generation;
            Hp = hp;
            X = x;
            Y = y;
            Type = type ?? string.Empty;
            DamageHint = double.IsFinite(damageHint) && damageHint > 0.0 ? damageHint : 0.0;
        }
    }

    public readonly struct MobDie
    {
        public readonly int UserId;
        public readonly int MobIndex;
        public readonly int Generation;
        public readonly double X;
        public readonly double Y;
        /// <summary>Optional MOBEVENT type signature used to recover a rebuilt boss mapping.</summary>
        public readonly string Type;

        public MobDie(int userId, int mobIndex, double x, double y, int generation = 0, string type = "")
        {
            UserId = userId;
            MobIndex = mobIndex;
            Generation = generation;
            X = x;
            Y = y;
            Type = type ?? string.Empty;
        }
    }

    /// <summary>
    /// Host-authoritative confirmation that the current boss encounter is over.  This is
    /// deliberately separate from MOBDIE: multipart and phase-replacing bosses can destroy one
    /// mob wrapper without completing the encounter.
    /// </summary>
    public readonly struct BossVictoryState
    {
        public readonly int Generation;
        public readonly int EncounterId;

        public BossVictoryState(int generation, int encounterId)
        {
            Generation = generation;
            EncounterId = encounterId;
        }
    }

    /// <summary>
    /// Client acknowledgement that its native boss introduction reached the real combat handoff.
    /// The host derives <see cref="UserId"/> from the authenticated connection rather than trusting
    /// any id supplied by the peer.
    /// </summary>
    public readonly struct BossIntroReadyState
    {
        public readonly int UserId;
        public readonly string Payload;

        public BossIntroReadyState(int userId, string payload)
        {
            UserId = userId;
            Payload = payload ?? string.Empty;
        }
    }

    public readonly struct MobAttack
    {
        public readonly int Index;
        public readonly int Generation;
        public readonly string SkillId;
        public readonly bool RequiresTargetInArea;
        public readonly int? Data;
        public readonly double X;
        public readonly double Y;
        public readonly int TargetUserId;
        public readonly int Dir;
        /// <summary>Block client simulation for this many seconds. From event; 0 = use legacy lookup.</summary>
        public readonly double BlockSeconds;
        /// <summary>Force facing dir for this many seconds. From event; 0 = use legacy.</summary>
        public readonly double ForcedDirSeconds;
        /// <summary>Mob type for rebind when syncId mapping is missing. From MOBEVENT.</summary>
        public readonly string Type;
        /// <summary>
        /// Phase 3: host-assigned per-boss monotonic attack sequence (0 = none / non-boss). Lets the
        /// client drop replayed or out-of-order boss attacks deterministically via a high-water mark.
        /// </summary>
        public readonly int AttackSeq;

        public MobAttack(int index, string skillId, bool requiresTargetInArea, int? data, double x, double y, int targetUserId, int dir = 0, double blockSeconds = 0, double forcedDirSeconds = 0, string type = "", int generation = 0, int attackSeq = 0)
        {
            Index = index;
            Generation = generation;
            SkillId = skillId ?? string.Empty;
            RequiresTargetInArea = requiresTargetInArea;
            Data = data;
            X = x;
            Y = y;
            TargetUserId = targetUserId;
            Dir = dir;
            BlockSeconds = blockSeconds;
            ForcedDirSeconds = forcedDirSeconds;
            Type = type ?? string.Empty;
            AttackSeq = attackSeq;
        }
    }

    /// <summary>Event-based mob update: x, y, dir + events (attack, hit, die, oldSkill). Sent when something changes, not repeatedly.</summary>
    public readonly struct MobEventUpdate
    {
        public readonly int Index;
        public readonly int Generation;
        public readonly double X;
        public readonly double Y;
        public readonly int Dir;
        public readonly IReadOnlyList<string> Events;
        /// <summary>Mob type for rebind when syncId mapping is missing. Optional.</summary>
        public readonly string Type;

        public MobEventUpdate(int index, double x, double y, int dir, IReadOnlyList<string> events, string type = "", int generation = 0)
        {
            Index = index;
            Generation = generation;
            X = x;
            Y = y;
            Dir = dir;
            Events = events ?? Array.Empty<string>();
            Type = type ?? string.Empty;
        }
    }

    public readonly struct MobDraw
    {
        public readonly int UserId;
        public readonly int MobIndex;
        public readonly int Generation;
        public readonly bool IsOutOfGame;
        public readonly bool IsOnScreen;

        public MobDraw(int userId, int mobIndex, bool isOutOfGame, bool isOnScreen, int generation = 0)
        {
            UserId = userId;
            MobIndex = mobIndex;
            Generation = generation;
            IsOutOfGame = isOutOfGame;
            IsOnScreen = isOnScreen;
        }
    }

    public readonly struct ExitReadyState
    {
        public readonly int UserId;
        public readonly int DoorCx;
        public readonly int DoorCy;
        public readonly bool Pressed;
        public readonly bool InsideCircle;
        public readonly bool IsOutOfGame;
        public readonly bool IsOnScreen;

        public ExitReadyState(int userId, int doorCx, int doorCy, bool pressed, bool insideCircle, bool isOutOfGame, bool isOnScreen)
        {
            UserId = userId;
            DoorCx = doorCx;
            DoorCy = doorCy;
            Pressed = pressed;
            InsideCircle = insideCircle;
            IsOutOfGame = isOutOfGame;
            IsOnScreen = isOnScreen;
        }
    }

    public readonly struct PlayerDownState
    {
        public readonly int UserId;
        public readonly bool IsDowned;
        public readonly double X;
        public readonly double Y;
        public readonly string LevelId;
        public readonly bool HasHeadPosition;
        public readonly double HeadX;
        public readonly double HeadY;
        public readonly bool HasHeadAnim;
        public readonly string? HeadAnim;

        public PlayerDownState(int userId, bool isDowned, double x, double y, string levelId, bool hasHeadPosition = false, double headX = 0, double headY = 0, bool hasHeadAnim = false, string? headAnim = null)
        {
            UserId = userId;
            IsDowned = isDowned;
            X = x;
            Y = y;
            LevelId = levelId ?? string.Empty;
            HasHeadPosition = hasHeadPosition;
            HeadX = headX;
            HeadY = headY;
            HasHeadAnim = hasHeadAnim;
            HeadAnim = hasHeadAnim ? (headAnim ?? string.Empty) : null;
        }
    }

    public readonly struct PlayerReviveRequest
    {
        public readonly int ReviverId;
        public readonly int TargetId;

        public PlayerReviveRequest(int reviverId, int targetId)
        {
            ReviverId = reviverId;
            TargetId = targetId;
        }
    }

    private TcpListener? _listener;   // host
    private TcpClient? _client;     // client
    private NetworkStream? _stream;
    private Task? _steamTransportTask;
    private readonly bool _useSteamTransport;
    private readonly CSteamID _steamHostId;
    private ISteamP2PBridge? _steamBridge;
    private SteamConnect.HostLobbyResult? _steamHostStartupResult;
    private const int SteamP2PChannelClientToHost = 0;
    private const int SteamP2PChannelHostToClient = 1;
    private const uint SteamMaxPacketSizeBytes = 16u * 1024u * 1024u;
    private const int SteamMinReceiveBufferBytes = 64 * 1024;
    private int _connectedClientCount;

    private int ID;

    public int id => ID;

    private static readonly int[] ClientIds = { 2, 3, 4 };
    public static int MaxClientSlots => ClientIds.Length;
    public static int ConnectedClientCount
    {
        get
        {
            var active = GameMenu.NetRef;
            return active == null || active._disposed
                ? 0
                : Volatile.Read(ref active._connectedClientCount);
        }
    }

    // Client ids belong to one NetNode session. Keeping this static allowed a disposing old host
    // to release ids owned by a newly-created host during a fast reconnect.
    private readonly HashSet<int> _usedClientIds = new();

    private bool TryTakeNextUnusedClientId(out int assignedId)
    {
        lock (_usedClientIds)
        {
            for (var i = 0; i < ClientIds.Length; i++)
            {
                var id = ClientIds[i];
                if (!_usedClientIds.Contains(id))
                {
                    _usedClientIds.Add(id);
                    assignedId = id;
                    return true;
                }
            }

            assignedId = 0;
            return false;
        }
    }

    private bool IsCurrentNetworkSession()
    {
        return !_disposed && ReferenceEquals(GameMenu.NetRef, this);
    }

    private bool IsSupersededNetworkSession()
    {
        var active = GameMenu.NetRef;
        return _disposed || (active != null && !ReferenceEquals(active, this));
    }

    private readonly object _clientsLock = new();
    private readonly Dictionary<int, ClientConnection> _clients = new();
    private readonly Dictionary<int, SteamClientConnection> _steamClients = new();
    private readonly Dictionary<ulong, int> _steamClientIdsBySteam = new();
    private readonly Dictionary<int, RemoteState> _remotes = new();
    private List<RemoteAttack> _pendingAttacks = new();
    private List<RemoteChatMessage> _pendingChatMessages = new();
    private List<MobStateSnapshot> _pendingMobStates = new();
    private List<MobMoveSnapshot> _pendingMobMoves = new();
    private List<MobChargeSnapshot> _pendingMobCharges = new();
    private List<MobHit> _pendingMobHits = new();
    private List<MobDie> _pendingMobDies = new();
    private List<BossVictoryState> _pendingBossVictories = new();
    private List<MobAttack> _pendingMobAttacks = new();
    private List<MobDraw> _pendingMobDraws = new();
    private List<ExitReadyState> _pendingExitReadyStates = new();
    private List<PlayerDownState> _pendingPlayerDownStates = new();
    private List<PlayerReviveRequest> _pendingPlayerReviveRequests = new();
    private List<string> _pendingBossCineLevelIds = new();
    private List<string> _pendingBossIntroEnds = new();
    private List<BossIntroReadyState> _pendingBossIntroReadyStates = new();
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

    private readonly IPEndPoint _bindEp;   // host bind
    private readonly IPEndPoint _destEp;   // client connect

    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private Task? _recvTask;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private bool _disposed;
    private long _lastSteamPacketReceivedTicks;
    private long _lastSteamKeepAliveSentTicks;
    private const double SteamReceiveTimeoutSeconds = 18.0;
    private const double SteamKeepAliveSeconds = 6.0;
    private static readonly byte[] SteamKeepAliveBytes = Encoding.UTF8.GetBytes("PING\n");

    private readonly object _sync = new();
    private bool _hasRemote;
    private bool _hasLocalHpSnapshot;
    private int _localHpLife;
    private int _localHpMaxLife;
    private int _localHpLif;
    private int _localHpBonusLife;
    private int _localHpRecover;
    private readonly object _hostCacheSync = new();
    private int? _cachedHostSeed;
    private int? _cachedHostRunSeedSequence;
    private string? _cachedHostLaunchKind;
    private string? _cachedHostRunCommitPayload;
    private string? _cachedHostRunExecutePayload;
    private string? _cachedHostRunReadyPayload;
    private int? _cachedHostRunLaunchSequence;
    private int? _cachedHostBossRune;
    private int? _cachedHostSerializerSeq;
    private int? _cachedHostSerializerUid;
    private string? _cachedHostLevelDescPayload;
    private string? _cachedHostLevelSeedPayload;
    private string? _cachedHostHeroSkin;
    private string? _cachedHostHeroHeadSkin;
    private string? _cachedHostLevelGraphPayload;
    private double? _cachedHostMobsHpMult;
    private double? _cachedHostBossesHpMult;

    public bool HasRemote
    {
        get
        {
            if (_role == NetRole.Host)
            {
                lock (_clientsLock)
                {
                    if (_useSteamTransport)
                        return _steamClients.Count > 0;
                    return _clients.Count > 0;
                }
            }
            lock (_sync) return _hasRemote;
        }
    }
    public bool IsAlive =>
        _useSteamTransport
            ? _cts != null && !_cts.IsCancellationRequested
            : (_role == NetRole.Host && _listener != null) ||
              (_role == NetRole.Client && _client != null);
    public bool IsHost => _role == NetRole.Host;

    public IPEndPoint? ListenerEndpoint =>
        _useSteamTransport
            ? null
            : _listener != null ? (IPEndPoint?)_listener.LocalEndpoint : null;

    public static NetNode CreateHost(ILogger log, IPEndPoint ep)  => new(log, NetRole.Host,  ep);
    public static NetNode CreateClient(ILogger log, IPEndPoint ep)=> new(log, NetRole.Client, ep);
    public static NetNode CreateSteamHost(ILogger log, int hostPort) => new(log, NetRole.Host, new CSteamID(0), hostPort);
    public static NetNode CreateSteamClient(ILogger log, ulong hostSteamId) => new(log, NetRole.Client, new CSteamID(hostSteamId), 0);

    internal SteamConnect.HostLobbyResult? HostLobbyResult =>
        _steamBridge?.HostLobbyResult ?? _steamHostStartupResult;
    private NetNode(ILogger log, NetRole role, IPEndPoint ep)
    {
        _log  = log;
        _role = role;
        _useSteamTransport = false;
        _steamHostId = new CSteamID(0);

        if (role == NetRole.Host)
        {
            _bindEp = ep;
            _destEp = new IPEndPoint(IPAddress.None, 0);
            StartHost();
            ID = 1;
        }
        else
        {
            _destEp = ep;
            _bindEp = new IPEndPoint(IPAddress.None, 0);
            StartClient();
            ID = 0;
        }
    }

    private readonly int _steamHostPort;

    private NetNode(ILogger log, NetRole role, CSteamID hostSteamId, int steamHostPort)
    {
        _log = log;
        _role = role;
        _useSteamTransport = true;
        _steamHostId = hostSteamId;
        _steamHostPort = steamHostPort;
        _bindEp = new IPEndPoint(IPAddress.None, 0);
        _destEp = new IPEndPoint(IPAddress.None, 0);

        if (role == NetRole.Host)
        {
            ID = 1;
            StartSteamHost();
        }
        else
        {
            ID = 0;
            StartSteamClient();
        }
    }
}
