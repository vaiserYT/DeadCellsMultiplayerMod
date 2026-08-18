using System.Net;
using System.Net.Sockets;
using System.Text;
using DeadCellsMultiplayerMod;
using DeadCellsMultiplayerMod.Network;
using DeadCellsMultiplayerMod.Tools;
using Serilog;
using Steamworks;

public enum NetRole { None, Host, Client }
public enum RemoteAttackAction { Attack, Interrupt }

public sealed partial class NetNode : IDisposable
{
    private readonly ILogger _log;
    private readonly NetRole _role;
    private readonly NetPacketBudget _realtimePacketBudget = new(96 * 1024, 16.0);
    private readonly LifecycleTracker _lifecycle = new("NetNode");

    private TcpListener? _listener;   // host
    private TcpClient? _client;     // client
    private NetworkStream? _stream;
    private Task? _steamTransportTask;
    /// <summary>
    /// Keep-alive runs on its own task, never on the transport receive loop.
    /// <see cref="SteamBridgeLoop"/> hands non-fast-path lines to the game main thread and awaits
    /// that hand-off to preserve protocol order. While the peer's main thread is busy (level
    /// generation, a host-authoritative graph wait, a loading screen) that await can park the
    /// receive loop for seconds. When keep-alive lived inside that loop, a peer that was merely
    /// loading looked identical to a peer that had died, and the other side dropped the session at
    /// <see cref="SteamReceiveTimeoutSeconds"/>. That is invisible on localhost and is exactly the
    /// "joined the lobby, then got kicked when the host pressed Start" failure online.
    /// </summary>
    private Task? _steamKeepAliveTask;
    /// <summary>
    /// Number of received lines handed to the main thread but not yet executed. A non-zero value
    /// proves the peer is still delivering data, so the receive timeout must not fire while the
    /// only thing that is slow is our own game thread.
    /// </summary>
    private int _steamMainThreadDispatchBacklog;
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

    internal NetTransportSnapshot ReadTransportSnapshot()
    {
        return new NetTransportSnapshot(
            _useSteamTransport ? NetTransportKind.Steam : NetTransportKind.Tcp,
            _role,
            HasAnyConnection(),
            _disposed);
    }

    internal LifecycleSnapshot ReadLifecycleSnapshot() => _lifecycle.Snapshot;

    private static readonly int[] ClientIds = { 2, 3, 4 };
    public static int MaxClientSlots => ClientIds.Length;
    public static int ConnectedClientCount
    {
        get
        {
            var active = LobbySession.NetRef;
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
        return !_disposed && ReferenceEquals(LobbySession.NetRef, this);
    }

    private bool IsSupersededNetworkSession()
    {
        var active = LobbySession.NetRef;
        return _disposed || (active != null && !ReferenceEquals(active, this));
    }

    // Ownership: _clients/_steamClients hold the transport connection objects for this session
    // (mutually exclusive per _useSteamTransport). _steamClientIdsBySteam is the reverse lookup
    // from steam id -> assigned client id (needed for packet routing on the steam transport).
    // _remotes is the network-layer RECEIVE buffer: one RemotePlayerState per remote id, written only
    // on the network thread under _sync from decoded incoming lines, and consumed on the main
    // thread through pooled snapshot builders. Its lifetime is exactly this NetNode session:
    // it is built up by GetOrCreateRemoteLocked, emptied by RemoveRemoteLocked on disconnect,
    // and wholesale-cleared in CleanupClient()/Dispose(). It intentionally does NOT own render
    // state; the ModEntry.clients[] slot arrays are a separate main-thread projection of the
    // same players (slot-keyed, normalized skin/head, applied-state for diffing and GhostKing
    // recreation). Keep-both: the two hold different projections with different lifetimes
    // (session-scoped lock-guarded raw field vs main-thread applied visuals) and different
    // consumers: _remotes feeds forwarding, late-join catch-up, and snapshot building, while
    // ModEntry.clients[] feeds the in-world GhostKing pipeline.
    private readonly object _clientsLock = new();
    private readonly Dictionary<int, ClientConnection> _clients = new();
    private readonly Dictionary<int, SteamClientConnection> _steamClients = new();
    private readonly Dictionary<ulong, int> _steamClientIdsBySteam = new();
    private readonly Dictionary<int, RemotePlayerState> _remotes = new();
    private readonly IPEndPoint _bindEp;   // host bind
    private readonly IPEndPoint _destEp;   // client connect

    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private Task? _recvTask;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private int _disposeState;
    private bool _disposed => Volatile.Read(ref _disposeState) != 0;
    private long _lastSteamPacketReceivedTicks;
    private long _nextPositionSequence;
    private long _lastSteamKeepAliveSentTicks;
    /// <summary>
    /// Must comfortably exceed the worst realistic gap between two keep-alives on a peer that is
    /// alive but busy. Keep-alive is sent from a dedicated task every
    /// <see cref="SteamKeepAliveSeconds"/>, so anything under a few multiples of that is a real
    /// connection loss rather than a loading hitch.
    /// </summary>
    private const double SteamReceiveTimeoutSeconds = 30.0;
    private const double SteamKeepAliveSeconds = 3.0;
    private const int SteamKeepAlivePollMs = 500;
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
    private long _nextHostLevelSeedSequence;
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
    /// <summary>
    /// Host level seed / level graph caches for late-join replay, keyed by level id.
    /// </summary>
    /// <remarks>
    /// These used to be single slots holding "the last thing sent". One run generates several
    /// levels — a biome plus its challenge rooms and sublevels — so by the time anyone joined, the
    /// slot described a CHALLENGE ROOM rather than the level the joiner was about to load. The
    /// client then had no graph for its actual level, which both blocked the auto-start gate (it
    /// waits on a graph for that level id) and, if it started anyway, made it generate its own
    /// layout. Keyed by level id, every generated level stays available and the joiner finds the
    /// one it needs. Bounded so a long run cannot grow them without limit.
    /// </remarks>
    private readonly Dictionary<string, string> _cachedHostLevelSeedsByLevelId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _cachedHostLevelGraphsByLevelId = new(StringComparer.Ordinal);
    private const int MaxCachedHostLevelPayloads = 12;
    private string? _cachedHostHeroSkin;
    private string? _cachedHostHeroHeadSkin;
    /// <summary>
    /// The GEN payload carries the launch action/custom/stream flags the client's auto-start gate
    /// waits on. It was the only launch prerequisite with no host cache, so a client whose
    /// handshake completed after the host pressed Start never received it and stayed in the lobby
    /// forever with no error on either side.
    /// </summary>
    private string? _cachedHostGeneratePayload;
    private string? _cachedHostCustomGameDataPayload;
    // Host-selected permanent progression (runes/mobility unlocks) must be available during
    // the handshake, before the joining client starts LevelGen. Without a cache the first
    // RUNEPROG snapshot was delayed until the periodic heartbeat, making fresh/different saves
    // race the initial world generation even when the run seed itself matched.
    private string? _cachedHostRuneProgressPayload;
    private string? _cachedHostCoopId;
    private bool _cachedHostHasContinueSave;
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
    internal static NetNode CreateSteamHost(
        ILogger log,
        int hostPort,
        SteamConnect.SteamLobbyVisibility visibility) =>
        new(log, NetRole.Host, new CSteamID(0), hostPort, visibility);
    public static NetNode CreateSteamClient(ILogger log, ulong hostSteamId) =>
        new(log, NetRole.Client, new CSteamID(hostSteamId), 0, SteamConnect.SteamLobbyVisibility.FriendsOnly);

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

        _lifecycle.Start();
    }

    private readonly int _steamHostPort;
    private readonly SteamConnect.SteamLobbyVisibility _steamLobbyVisibility;

    private NetNode(
        ILogger log,
        NetRole role,
        CSteamID hostSteamId,
        int steamHostPort,
        SteamConnect.SteamLobbyVisibility steamLobbyVisibility)
    {
        _log = log;
        _role = role;
        _useSteamTransport = true;
        _steamHostId = hostSteamId;
        _steamHostPort = steamHostPort;
        _steamLobbyVisibility = steamLobbyVisibility;
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

        _lifecycle.Start();
    }
}
