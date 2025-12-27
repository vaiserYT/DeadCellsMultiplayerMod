using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Globalization;
using DeadCellsMultiplayerMod;
using HaxeProxy.Runtime;
using Serilog;
using dc;

public enum NetRole { None, Host, Client }

public sealed class NetNode : IDisposable
{
    private readonly ILogger _log;
    private readonly NetRole _role;

    private TcpListener? _listener;   // host
    private TcpClient?   _client;     // client OR accepted
    private NetworkStream? _stream;

    private readonly IPEndPoint _bindEp;   // host bind
    private readonly IPEndPoint _destEp;   // client connect

    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private Task? _recvTask;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private bool _disposed;

    private readonly object _sync = new();
    private double _rx, _ry;
    private bool _hasRemote;
    private string? _remoteLevelText;
    private string? _remoteAnim;
    private int? _remoteAnimQueue;
    private bool? _remoteAnimG;
    private bool _hasRemoteAnim;

    public bool HasRemote { get { lock (_sync) return _hasRemote; } }
    public bool IsAlive =>
        (_role == NetRole.Host && _listener != null) ||
        (_role == NetRole.Client && _client   != null);
    public bool IsHost => _role == NetRole.Host;

    // Новое свойство для реального адреса хоста
    public IPEndPoint? ListenerEndpoint =>
        _listener != null ? (IPEndPoint?)_listener.LocalEndpoint : null;

    public static NetNode CreateHost(ILogger log, IPEndPoint ep)  => new(log, NetRole.Host,  ep);
    public static NetNode CreateClient(ILogger log, IPEndPoint ep)=> new(log, NetRole.Client, ep);

    private NetNode(ILogger log, NetRole role, IPEndPoint ep)
    {
        _log  = log;
        _role = role;

        if (role == NetRole.Host)
        {
            // только loopback
            _bindEp = ep;
            _destEp = new IPEndPoint(IPAddress.None, 0);
            StartHost();
        }
        else
        {
            _destEp = ep; // 127.0.0.1:XXXX из server.txt
            _bindEp = new IPEndPoint(IPAddress.None, 0);
            StartClient();
        }
    }

    // ================= HOST =================
    private void StartHost()
    {
        try
        {
            _cts = new CancellationTokenSource();
            _listener = new TcpListener(_bindEp);
            _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _listener.Start();

            var lep = (IPEndPoint)_listener.LocalEndpoint;

            // ВАЖНО: логируем реальный адрес слушателя
            _log.Information("[NetNode] Host started OK. Bound to {0}:{1}", lep.Address, lep.Port);

            _acceptTask = Task.Run(() => AcceptLoop(_cts.Token));
        }
        catch (Exception ex)
        {
            _log.Error("[NetNode] Host start failed: {msg}", ex.Message);
            Dispose();
            throw;
        }
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _listener != null)
            {
                var tcp = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                tcp.NoDelay = true;
                _client = tcp;
                _stream = tcp.GetStream();

                _log.Information("[NetNode] Host accepted {ep}", tcp.Client.RemoteEndPoint);

                await SendLineSafe("WELCOME\n").ConfigureAwait(false);
                if (_role == NetRole.Host && GameMenu.TryGetHostRunSeed(out var hostSeed))
                {
                    SendSeed(hostSeed);
                }

                lock (_sync) _hasRemote = true;
                GameMenu.NotifyRemoteConnected(_role);

                _recvTask = Task.Run(() => RecvLoop(ct));
                break;
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            _log.Warning("[NetNode] AcceptLoop error: {msg}", ex.Message);
        }
    }

    // ================= CLIENT =================
    private void StartClient()
    {
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => ConnectWithRetryAsync(_cts.Token));
    }

    private async Task ConnectWithRetryAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _log.Information("[NetNode] Client connecting to {dest}", _destEp);

                var tcp = new TcpClient(AddressFamily.InterNetwork);
                tcp.NoDelay = true;

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));

                await tcp.ConnectAsync(_destEp.Address, _destEp.Port, timeoutCts.Token).ConfigureAwait(false);
                _client = tcp;
                _stream = tcp.GetStream();

                _log.Information("[NetNode] Client connected to {dest}", _destEp);

                await SendLineSafe("HELLO\n").ConfigureAwait(false);

                lock (_sync) _hasRemote = true;
                GameMenu.NotifyRemoteConnected(_role);

                _recvTask = Task.Run(() => RecvLoop(ct));
                return;
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.Warning("[NetNode] Client connect error: {msg}", ex.Message);
                await Task.Delay(3000, ct).ConfigureAwait(false);
            }
        }
    }

    // ============== COMMON IO ==============
    private async Task RecvLoop(CancellationToken ct)
    {
        var buf = new byte[2048];
        var sb  = new StringBuilder();

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var stream = _stream;
                if (stream == null) break;

                int n = await stream.ReadAsync(buf.AsMemory(0, buf.Length), ct).ConfigureAwait(false);
                if (n <= 0) break;

                sb.Append(Encoding.UTF8.GetString(buf, 0, n));

                while (true)
                {
                    var text = sb.ToString();
                    int idx = text.IndexOf('\n');
                    if (idx < 0) break;

                    var line = text[..idx].Trim();
                    sb.Remove(0, idx + 1);
                    if (line.Length == 0) continue;


                    if (line.StartsWith("WELCOME"))
                    {
                        lock (_sync) _hasRemote = true;
                        continue;
                    }

                    if (line.StartsWith("HELLO"))
                    {
                        lock (_sync) _hasRemote = true;
                        continue;
                    }

                    if (line.StartsWith("SEED|"))
                    {
                        var partsSeed = line.Split('|');
                        if (partsSeed.Length >= 2 && int.TryParse(partsSeed[1], out var hostSeed))
                        {
                            lock (_sync) _hasRemote = true;
                            GameMenu.ReceiveHostRunSeed(hostSeed);
                            _log.Information("[NetNode] Received host run seed {Seed}", hostSeed);
                        }
                        else
                        {
                            _log.Warning("[NetNode] Malformed SEED line: \"{line}\"");
                        }
                        continue;
                    }

                    if (line.StartsWith("RUNPARAMS|"))
                    {
                        var payload = line["RUNPARAMS|".Length..];
                        lock (_sync) _hasRemote = true;
                        GameMenu.ReceiveRunParams(payload);
                        continue;
                    }

                    if (line.StartsWith("USER|"))
                    {
                        var payload = line["USER|".Length..];
                        lock (_sync) _hasRemote = true;
                        GameMenu.ReceiveRemoteUsername(payload);
                        continue;
                    }

                    if (line.StartsWith("LDESC|"))
                    {
                        var payload = line["LDESC|".Length..];
                        lock (_sync) _hasRemote = true;
                        GameMenu.ReceiveLevelDesc(payload);
                        continue;
                    }

                    if (line.StartsWith("GEN|"))
                    {
                        var payload = line["GEN|".Length..];
                        lock (_sync) _hasRemote = true;
                        GameMenu.ReceiveGeneratePayload(payload);
                        continue;
                    }

                    if (line.StartsWith("LEVEL|", StringComparison.OrdinalIgnoreCase))
                    {
                        var payload = line[(line.IndexOf('|') + 1)..];
                        lock (_sync)
                        {
                            _hasRemote = true;
                            _remoteLevelText = payload;
                        }
                        continue;
                    }

                    if (line.StartsWith("ANIM|", StringComparison.OrdinalIgnoreCase))
                    {
                        var payload = line[(line.IndexOf('|') + 1)..];
                        var partsAnim = payload.Split('|');
                        string animName = partsAnim.Length >= 1 ? partsAnim[0] : string.Empty;
                        int? q = null;
                        bool? gFlag = null;
                        if (partsAnim.Length >= 2 && int.TryParse(partsAnim[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedQ))
                            q = parsedQ;
                        if (partsAnim.Length >= 3 && TryParseBool(partsAnim[2], out var parsedBool))
                            gFlag = parsedBool;
                        lock (_sync)
                        {
                            _hasRemote = true;
                            _remoteAnim = animName;
                            _remoteAnimQueue = q;
                            _remoteAnimG = gFlag;
                            _hasRemoteAnim = true;
                        }
                        continue;
                    }

                    if (line.StartsWith("KICK"))
                    {
                        GameMenu.NotifyRemoteDisconnected(_role);
                        break;
                    }

                    var parts = line.Split('|');
                    if (parts.Length == 2 &&
                        double.TryParse(parts[0], out var cx) &&
                        double.TryParse(parts[1], out var cy))
                    {
                        lock (_sync)
                        {
                            _rx = cx; _ry = cy; _hasRemote = true;
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            _log.Warning("[NetNode] RecvLoop error: {msg}", ex.Message);
        }
        finally
        {
            lock (_sync)
            {
                _hasRemote = false;
                _remoteLevelText = null;
                _remoteAnim = null;
                _remoteAnimQueue = null;
                _remoteAnimG = null;
                _hasRemoteAnim = false;
            }
            GameMenu.NotifyRemoteDisconnected(_role);
        }
    }

    private async Task SendLineSafe(string line)
    {
        var stream = _stream;
        if (stream == null) return;

        var bytes = Encoding.UTF8.GetBytes(line);
        bool locked = false;
        try
        {
            await _sendLock.WaitAsync().ConfigureAwait(false);
            locked = true;
            await stream.WriteAsync(bytes.AsMemory(0, bytes.Length), CancellationToken.None).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            _log.Warning("[NetNode] send error: {msg}", ex.Message);
        }
        finally
        {
            if (locked) _sendLock.Release();
        }
    }

    public void TickSend(double cx, double cy)
    {
        if (_stream == null || _client == null || !_client.Connected) return;
        var line = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{cx}|{cy}\n");
        _ = SendLineSafe(line);
    }

    public void LevelSend(string lvl) => SendLevelId(lvl);

    public void SendSeed(int seed)
    {
        if (_stream == null || _client == null || !_client.Connected)
        {
            _log.Information("[NetNode] Skip sending seed {Seed}: no connected client", seed);
            return;
        }
        var line = $"SEED|{seed}\n";
        _ = SendLineSafe(line);
        _log.Information("[NetNode] Sent seed {Seed}", seed);
    }

    public void SendUsername(string username)
    {
        if (_stream == null || _client == null || !_client.Connected)
        {
            _log.Information("[NetNode] Skip sending username: no connected client");
            return;
        }

        var safe = (username ?? "guest").Replace("|", "/").Replace("\r", string.Empty).Replace("\n", string.Empty);
        if (safe.Length == 0) safe = "guest";

        SendRaw("USER|" + safe);
        _log.Information("[NetNode] Sent username {Username}", safe);
    }

    public void SendRunParams(string json)
    {
        if (_stream == null || _client == null || !_client.Connected)
        {
            _log.Information("[NetNode] Skip sending run params: no connected client");
            return;
        }

        SendRaw("RUNPARAMS|" + json);
        _log.Information("[NetNode] Sent run params payload");
    }

    public void SendLevelDesc(string json)
    {
        if (_stream == null || _client == null || !_client.Connected)
        {
            _log.Information("[NetNode] Skip sending level desc: no connected client");
            return;
        }

        SendRaw("LDESC|" + json);
        _log.Information("[NetNode] Sent LevelDesc payload");
    }

    public void SendGeneratePayload(string json)
    {
        if (_stream == null || _client == null || !_client.Connected)
        {
            _log.Information("[NetNode] Skip sending generate payload: no connected client");
            return;
        }

        SendRaw("GEN|" + json);
        _log.Information("[NetNode] Sent Generate payload ({Length} bytes)", json.Length);
    }

    public void SendLevelId(string levelId)
    {
        if (_stream == null || _client == null || !_client.Connected)
        {
            _log.Information("[NetNode] Skip sending level id: no connected client");
            return;
        }

        var safe = levelId.Replace("|", "/").Replace("\r", string.Empty).Replace("\n", string.Empty);
        SendRaw("LEVEL|" + safe);
    }

    public void SendKick()
    {
        if (_stream == null || _client == null || !_client.Connected) return;
        SendRaw("KICK");
    }

    public void SendAnim(string anim, int? queueAnim = null, bool? g = null)
    {
        if (_stream == null || _client == null || !_client.Connected)
        {
            _log.Information("[NetNode] Skip sending anim: no connected client");
            return;
        }

        var safe = (anim ?? string.Empty).Replace("|", "/").Replace("\r", string.Empty).Replace("\n", string.Empty);
        if (safe.Length == 0) safe = "idle";
        var queuePart = queueAnim.HasValue ? queueAnim.Value.ToString(CultureInfo.InvariantCulture) : string.Empty;
        var gPart = g.HasValue ? (g.Value ? "1" : "0") : string.Empty;
        SendRaw($"ANIM|{safe}|{queuePart}|{gPart}");
    }

    private void SendRaw(string payload)
    {
        var line = payload.EndsWith('\n') ? payload : payload + "\n";
        _ = SendLineSafe(line);
    }

    public bool TryGetRemote(out double rx, out double ry)
    {
        lock (_sync)
        {
            rx = _rx; ry = _ry;
            return _hasRemote;
        }
    }

    public bool TryGetRemoteLevelString(out string? levelText)
    {
        lock (_sync)
        {
            levelText = _remoteLevelText;
            return _hasRemote && levelText != null;
        }
    }

    public bool TryGetRemoteAnim(out string? anim, out int? queueAnim, out bool? g)
    {
        lock (_sync)
        {
            if (!_hasRemoteAnim)
            {
                anim = null; queueAnim = null; g = null;
                return false;
            }
            anim = _remoteAnim;
            queueAnim = _remoteAnimQueue;
            g = _remoteAnimG;
            _hasRemoteAnim = false;
            return _hasRemote && anim != null;
        }
    }

    private static bool TryParseBool(string text, out bool value)
    {
        if (string.Equals(text, "1", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "true", StringComparison.OrdinalIgnoreCase))
        {
            value = true;
            return true;
        }
        if (string.Equals(text, "0", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "false", StringComparison.OrdinalIgnoreCase))
        {
            value = false;
            return true;
        }
        value = false;
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts?.Cancel(); } catch { }
        try { _stream?.Close(); } catch { }
        try { _client?.Close(); } catch { }
        try { _listener?.Stop(); } catch { }
        GameDataSync.seed = 0;
        _stream = null; _client = null; _listener = null;
        try { _sendLock.Dispose(); } catch { }
    }
}
