
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Text;
using System.Threading.Channels;
using ModCore.Utilities;
using Newtonsoft.Json;
using Steamworks;

namespace DeadCellsMultiplayerMod
{
    internal static class SteamP2PWorkerEnvironment
    {
        public const string EnvWorkerMode = "DCCM_STEAM_WORKER_MODE";
        public const string WorkerModeP2P = "p2p";
        public const string EnvRole = "DCCM_STEAM_P2P_ROLE";
        public const string EnvHostSteamId = "DCCM_STEAM_P2P_HOST_STEAM_ID";
        public const string EnvCommandPipe = "DCCM_STEAM_P2P_COMMAND_PIPE";
        public const string EnvEventPipe = "DCCM_STEAM_P2P_EVENT_PIPE";
        public const string EnvHostPort = "DCCM_STEAM_P2P_HOST_PORT";
        public const string EnvHostIp = "DCCM_STEAM_P2P_HOST_IP";
        public const string EnvDebugP2P = "DCCM_STEAM_P2P_DEBUG";

        // Reuse SteamConnect bootstrap error path to capture startup failures before pipes are ready.
        public const string EnvBootstrapResponsePath = "DCCM_STEAM_CONNECT_RESPONSE_PATH";
    }

    internal readonly struct SteamP2PWorkerPacket
    {
        public readonly ulong RemoteSteamId;
        public readonly int Channel;
        public readonly string Payload;

        public SteamP2PWorkerPacket(ulong remoteSteamId, int channel, string payload)
        {
            RemoteSteamId = remoteSteamId;
            Channel = channel;
            Payload = payload ?? string.Empty;
        }
    }

    internal interface ISteamP2PBridge : IDisposable
    {
        bool IsRunning { get; }
        SteamConnect.HostLobbyResult? HostLobbyResult { get; }
        ulong LocalSteamId { get; }
        bool TrySend(ulong steamId, EP2PSend sendType, int channel, byte[] payload, out string error);
        bool TryClosePeer(ulong steamId);
        bool TrySetRichPresence(string key, string value, out string error);
        bool TryClearRichPresence(out string error);
        bool TryReadPacket(out SteamP2PWorkerPacket packet);
        bool TryReadWarning(out string warning);
        bool TryReadSessionFail(out ulong steamId);
    }

    internal sealed class SteamP2PWorkerBridge : ISteamP2PBridge
    {
        private const int StartupTimeoutMs = 15000;
        private const int PipeConnectPollMs = 25;

        private readonly Process _process;
        private readonly NamedPipeServerStream _commandPipe;
        private readonly NamedPipeServerStream _eventPipe;
        private readonly StreamWriter _commandWriter;
        private readonly StreamReader _eventReader;
        private readonly string _bootstrapResponsePath;
        private readonly object _commandSync = new();
        private readonly Channel<SteamP2PWorkerPacket> _packets = Channel.CreateBounded<SteamP2PWorkerPacket>(
            new BoundedChannelOptions(4096)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false
            });
        private readonly ConcurrentQueue<string> _warnings = new();
        private readonly ConcurrentQueue<ulong> _sessionFailSteamIds = new();
        private readonly CancellationTokenSource _readerCts = new();
        private readonly Task _readerTask;
        private volatile bool _disposed;

        public SteamConnect.HostLobbyResult? HostLobbyResult { get; }
        public ulong LocalSteamId { get; }

        private SteamP2PWorkerBridge(
            Process process,
            NamedPipeServerStream commandPipe,
            NamedPipeServerStream eventPipe,
            StreamWriter commandWriter,
            StreamReader eventReader,
            string bootstrapResponsePath,
            SteamConnect.HostLobbyResult? hostLobbyResult,
            ulong localSteamId)
        {
            _process = process;
            _commandPipe = commandPipe;
            _eventPipe = eventPipe;
            _commandWriter = commandWriter;
            _eventReader = eventReader;
            _bootstrapResponsePath = bootstrapResponsePath;
            HostLobbyResult = hostLobbyResult;
            LocalSteamId = localSteamId;
            _readerTask = Task.Run(() => EventReaderLoop(_readerCts.Token));
        }

        public bool IsRunning
        {
            get
            {
                if (_disposed)
                    return false;

                try
                {
                    return !_process.HasExited;
                }
                catch
                {
                    return false;
                }
            }
        }

        public static bool TryStart(NetRole role, CSteamID hostSteamId, int hostPort, string? hostIp, out SteamP2PWorkerBridge? bridge, out string error)
        {
            bridge = null;
            error = "Steam P2P worker failed to start";

            var commandPipeName = $"dccm_steam_p2p_cmd_{Guid.NewGuid():N}";
            var eventPipeName = $"dccm_steam_p2p_evt_{Guid.NewGuid():N}";
            var responsePath = Path.Combine(Path.GetTempPath(), $"dccm_steam_p2p_resp_{Guid.NewGuid():N}.json");

            NamedPipeServerStream? commandPipe = null;
            NamedPipeServerStream? eventPipe = null;
            Process? process = null;
            StreamWriter? commandWriter = null;
            StreamReader? eventReader = null;

            try
            {
                commandPipe = new NamedPipeServerStream(
                    commandPipeName,
                    PipeDirection.Out,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                eventPipe = new NamedPipeServerStream(
                    eventPipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                var startInfo = SteamConnect.BuildWorkerStartInfoForRuntime();
                startInfo.Environment[SteamP2PWorkerEnvironment.EnvWorkerMode] = SteamP2PWorkerEnvironment.WorkerModeP2P;
                startInfo.Environment[SteamP2PWorkerEnvironment.EnvRole] = role == NetRole.Host ? "host" : "client";
                startInfo.Environment[SteamP2PWorkerEnvironment.EnvHostSteamId] = hostSteamId.m_SteamID.ToString(CultureInfo.InvariantCulture);
                startInfo.Environment[SteamP2PWorkerEnvironment.EnvCommandPipe] = commandPipeName;
                startInfo.Environment[SteamP2PWorkerEnvironment.EnvEventPipe] = eventPipeName;
                startInfo.Environment[SteamP2PWorkerEnvironment.EnvBootstrapResponsePath] = responsePath;
                startInfo.Environment["SteamAppId"] = "588650";
                startInfo.Environment["SteamGameId"] = "588650";
                if (role == NetRole.Host && hostPort > 0)
                {
                    startInfo.Environment[SteamP2PWorkerEnvironment.EnvHostPort] = hostPort.ToString(CultureInfo.InvariantCulture);
                    startInfo.Environment[SteamP2PWorkerEnvironment.EnvHostIp] = hostIp ?? string.Empty;
                }

                var assemblyPath = typeof(SteamP2PWorkerBridge).Assembly.Location;
                if (string.IsNullOrWhiteSpace(assemblyPath))
                {
                    error = "Steam P2P worker assembly path is unavailable";
                    return false;
                }

                var loadAssemblies = SteamConnect.BuildWorkerLoadAssembliesForRuntime(assemblyPath);
                process = WorkerProcessUtils.StartWorkerProcess(
                    typeof(SteamWorkerBootstrap).AssemblyQualifiedName!,
                    nameof(SteamWorkerBootstrap.WorkerEntry),
                    startInfo,
                    loadAssemblies);

                if (process == null)
                {
                    error = "Steam P2P worker process was not started";
                    return false;
                }

                var cmdConnectTask = commandPipe.WaitForConnectionAsync();
                var evtConnectTask = eventPipe.WaitForConnectionAsync();
                if (!WaitForPipeConnections(cmdConnectTask, evtConnectTask, process, StartupTimeoutMs))
                {
                    error = BuildStartupError("Steam P2P worker pipe connection timeout", process, responsePath);
                    return false;
                }

                commandWriter = new StreamWriter(commandPipe, new UTF8Encoding(false), 4096, leaveOpen: true)
                {
                    AutoFlush = true
                };
                eventReader = new StreamReader(eventPipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);

                var ready = ReadReadyEvent(eventReader, process, responsePath, StartupTimeoutMs, out var readyError);
                if (ready == null || !ready.Success)
                {
                    error = string.IsNullOrWhiteSpace(readyError)
                        ? "Steam P2P worker startup did not return ready state"
                        : readyError;
                    return false;
                }

                SteamConnect.HostLobbyResult? hostLobbyResult = null;
                if (role == NetRole.Host && ready.LobbyId != 0UL)
                {
                    hostLobbyResult = new SteamConnect.HostLobbyResult
                    {
                        Success = true,
                        LobbyId = ready.LobbyId,
                        HostIp = ready.HostIp ?? string.Empty,
                        HostPort = ready.HostPort,
                        PersonaName = ready.PersonaName ?? string.Empty
                    };
                }

                bridge = new SteamP2PWorkerBridge(process, commandPipe, eventPipe, commandWriter, eventReader, responsePath, hostLobbyResult, ready.SteamId);
                commandPipe = null;
                eventPipe = null;
                commandWriter = null;
                eventReader = null;
                process = null;
                error = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.ToString();
                return false;
            }
            finally
            {
                if (process != null)
                {
                    try
                    {
                        if (!process.HasExited)
                            process.Kill(true);
                    }
                    catch
                    {
                    }
                    finally
                    {
                        try { process.Dispose(); } catch { }
                    }
                }

                try { commandWriter?.Dispose(); } catch { }
                try { eventReader?.Dispose(); } catch { }
                try { commandPipe?.Dispose(); } catch { }
                try { eventPipe?.Dispose(); } catch { }

                if (bridge == null)
                {
                    try
                    {
                        if (File.Exists(responsePath))
                            File.Delete(responsePath);
                    }
                    catch
                    {
                    }
                }
            }
        }

        public bool TrySend(ulong steamId, EP2PSend sendType, int channel, byte[] payload, out string error)
        {
            error = string.Empty;

            if (_disposed || payload == null || payload.Length == 0)
            {
                error = "Steam P2P worker is not available";
                return false;
            }

            var command = new WorkerCommand
            {
                Type = WorkerCommandTypes.Send,
                SteamId = steamId,
                Channel = channel,
                SendType = sendType.ToString(),
                Payload = Convert.ToBase64String(payload)
            };

            return TryWriteCommand(command, out error);
        }

        public bool TryClosePeer(ulong steamId)
        {
            var command = new WorkerCommand
            {
                Type = WorkerCommandTypes.ClosePeer,
                SteamId = steamId
            };

            return TryWriteCommand(command, out _);
        }

        public bool TrySetRichPresence(string key, string value, out string error)
        {
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(key))
            {
                error = "Steam rich presence key is empty";
                return false;
            }

            var command = new WorkerCommand
            {
                Type = WorkerCommandTypes.SetRichPresence,
                SendType = key,
                Payload = value ?? string.Empty
            };

            return TryWriteCommand(command, out error);
        }

        public bool TryClearRichPresence(out string error)
        {
            var command = new WorkerCommand
            {
                Type = WorkerCommandTypes.ClearRichPresence
            };

            return TryWriteCommand(command, out error);
        }

        public bool TryReadPacket(out SteamP2PWorkerPacket packet)
        {
            return _packets.Reader.TryRead(out packet);
        }

        public bool TryReadWarning(out string warning)
        {
            return _warnings.TryDequeue(out warning!);
        }

        public bool TryReadSessionFail(out ulong steamId)
        {
            return _sessionFailSteamIds.TryDequeue(out steamId);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            try
            {
                TryWriteCommand(new WorkerCommand { Type = WorkerCommandTypes.Stop }, out _);
            }
            catch
            {
            }

            try { _readerCts.Cancel(); } catch { }
            try { _commandWriter.Dispose(); } catch { }
            try { _eventReader.Dispose(); } catch { }
            try { _commandPipe.Dispose(); } catch { }
            try { _eventPipe.Dispose(); } catch { }
            try { _readerTask.Wait(500); } catch { }

            try
            {
                if (!_process.HasExited && !_process.WaitForExit(1000))
                {
                    try { _process.Kill(true); } catch { }
                    try { _process.WaitForExit(1000); } catch { }
                }
            }
            catch
            {
            }
            finally
            {
                try { _process.Dispose(); } catch { }
            }

            try
            {
                if (File.Exists(_bootstrapResponsePath))
                    File.Delete(_bootstrapResponsePath);
            }
            catch
            {
            }
        }

        private async Task EventReaderLoop(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    string? line;
                    try
                    {
                        line = await _eventReader.ReadLineAsync().ConfigureAwait(false);
                    }
                    catch
                    {
                        break;
                    }

                    if (line == null)
                        break;
                    if (line.Length == 0)
                        continue;

                    WorkerEvent? evt = null;
                    try
                    {
                        evt = JsonConvert.DeserializeObject<WorkerEvent>(line);
                    }
                    catch
                    {
                    }

                    if (evt == null || string.IsNullOrWhiteSpace(evt.Type))
                        continue;

                    if (string.Equals(evt.Type, WorkerEventTypes.Packet, StringComparison.Ordinal))
                    {
                        if (string.IsNullOrWhiteSpace(evt.Payload))
                            continue;

                        byte[] bytes;
                        try
                        {
                            bytes = Convert.FromBase64String(evt.Payload);
                        }
                        catch
                        {
                            continue;
                        }

                        var payload = Encoding.UTF8.GetString(bytes);
                        await _packets.Writer.WriteAsync(
                            new SteamP2PWorkerPacket(evt.SteamId, evt.Channel, payload),
                            ct).ConfigureAwait(false);
                        continue;
                    }

                    if (string.Equals(evt.Type, WorkerEventTypes.SessionFail, StringComparison.Ordinal))
                    {
                        if (evt.SteamId != 0UL)
                            _sessionFailSteamIds.Enqueue(evt.SteamId);
                        continue;
                    }

                    if (string.Equals(evt.Type, WorkerEventTypes.Warning, StringComparison.Ordinal) ||
                        string.Equals(evt.Type, WorkerEventTypes.Error, StringComparison.Ordinal))
                    {
                        if (!string.IsNullOrWhiteSpace(evt.Error))
                            _warnings.Enqueue(evt.Error);
                    }
                }
            }
            finally
            {
                _packets.Writer.TryComplete();
                if (!_disposed)
                {
                    string msg;
                    try
                    {
                        msg = _process.HasExited
                            ? $"Steam P2P worker exited (exit={_process.ExitCode})"
                            : "Steam P2P worker event stream ended";
                    }
                    catch
                    {
                        msg = "Steam P2P worker event stream ended";
                    }

                    _warnings.Enqueue(msg);
                }
            }
        }

        private bool TryWriteCommand(WorkerCommand command, out string error)
        {
            error = string.Empty;

            if (_disposed)
            {
                error = "Steam P2P worker bridge is disposed";
                return false;
            }

            string json;
            try
            {
                json = JsonConvert.SerializeObject(command);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }

            lock (_commandSync)
            {
                try
                {
                    _commandWriter.WriteLine(json);
                    _commandWriter.Flush();
                    return true;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    return false;
                }
            }
        }

        private static WorkerEvent? ReadReadyEvent(StreamReader reader, Process process, string responsePath, int timeoutMs, out string error)
        {
            error = string.Empty;
            var readTask = reader.ReadLineAsync();
            var timeoutAt = Environment.TickCount64 + timeoutMs;
            while (!readTask.IsCompleted)
            {
                if (Environment.TickCount64 >= timeoutAt)
                {
                    error = BuildStartupError("Steam P2P worker startup timeout", process, responsePath);
                    return null;
                }

                try
                {
                    if (process.HasExited)
                    {
                        error = BuildStartupError("Steam P2P worker exited before ready", process, responsePath);
                        return null;
                    }
                }
                catch
                {
                }

                Thread.Sleep(20);
            }

            string? line;
            try
            {
                line = readTask.Result;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                // The child writes the real bootstrap exception to responsePath before exiting.
                // Surface it instead of hiding every failure behind the same empty-event message.
                error = BuildStartupError("Steam P2P worker returned empty startup event", process, responsePath);
                return null;
            }

            try
            {
                var evt = JsonConvert.DeserializeObject<WorkerEvent>(line);
                if (evt == null)
                {
                    error = "Steam P2P worker returned invalid startup event";
                    return null;
                }

                if (!string.Equals(evt.Type, WorkerEventTypes.Ready, StringComparison.Ordinal))
                {
                    error = "Steam P2P worker did not return ready event";
                    return null;
                }

                if (!evt.Success)
                    error = string.IsNullOrWhiteSpace(evt.Error) ? "Steam P2P worker reported startup failure" : evt.Error;
                return evt;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        private static bool WaitForPipeConnections(Task commandTask, Task eventTask, Process process, int timeoutMs)
        {
            var timeoutAt = Environment.TickCount64 + timeoutMs;
            while (Environment.TickCount64 < timeoutAt)
            {
                if (commandTask.IsCompleted && eventTask.IsCompleted)
                    return !(commandTask.IsFaulted || eventTask.IsFaulted || commandTask.IsCanceled || eventTask.IsCanceled);

                try
                {
                    if (process.HasExited)
                        return false;
                }
                catch
                {
                    return false;
                }

                Thread.Sleep(PipeConnectPollMs);
            }

            return false;
        }

        private static string BuildStartupError(string prefix, Process process, string responsePath)
        {
            if (TryReadBootstrapError(responsePath, out var workerError) && !string.IsNullOrWhiteSpace(workerError))
                return $"{prefix}: {workerError}";

            int? exitCode = null;
            try
            {
                if (process.HasExited)
                    exitCode = process.ExitCode;
            }
            catch
            {
            }

            if (exitCode.HasValue)
                return $"{prefix} (exit={exitCode.Value})";

            return prefix;
        }

        private static bool TryReadBootstrapError(string responsePath, out string error)
        {
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(responsePath) || !File.Exists(responsePath))
                return false;

            try
            {
                var json = File.ReadAllText(responsePath);
                if (string.IsNullOrWhiteSpace(json))
                    return false;

                var parsed = JsonConvert.DeserializeObject<BootstrapResponse>(json);
                if (parsed == null)
                    return false;

                error = parsed.Error ?? string.Empty;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private sealed class BootstrapResponse
        {
            public bool Success { get; set; }
            public string Error { get; set; } = string.Empty;
        }

    }

    internal static class SteamP2PWorker
    {
        private const int DeadCellsAppId = 588650;
        private const int PipeConnectTimeoutMs = 15000;
        private const int WorkerTickSleepMs = 5;
        private const int SteamP2PChannelClientToHost = 0;
        private const int SteamP2PChannelHostToClient = 1;
        private const uint SteamMaxPacketSizeBytes = 16u * 1024u * 1024u;
        private const int SteamMinReceiveBufferBytes = 64 * 1024;

        public static void WorkerEntry()
        {
            var bootstrapResponsePath = Environment.GetEnvironmentVariable(SteamP2PWorkerEnvironment.EnvBootstrapResponsePath);

            try
            {
                var roleText = Environment.GetEnvironmentVariable(SteamP2PWorkerEnvironment.EnvRole) ?? string.Empty;
                var commandPipeName = Environment.GetEnvironmentVariable(SteamP2PWorkerEnvironment.EnvCommandPipe) ?? string.Empty;
                var eventPipeName = Environment.GetEnvironmentVariable(SteamP2PWorkerEnvironment.EnvEventPipe) ?? string.Empty;
                var hostSteamIdRaw = Environment.GetEnvironmentVariable(SteamP2PWorkerEnvironment.EnvHostSteamId) ?? "0";

                if (string.IsNullOrWhiteSpace(commandPipeName) || string.IsNullOrWhiteSpace(eventPipeName))
                    throw new InvalidOperationException("Steam P2P worker pipe names are missing");

                if (!ulong.TryParse(hostSteamIdRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hostSteamId))
                    hostSteamId = 0UL;

                SteamConnect.PrepareSteamNativePathForRuntime();
                // This process is intentionally spawned by the already-running modded game. Calling
                // RestartAppIfNecessary here can terminate the worker before it writes its ready
                // event, producing the misleading "empty startup event" error.
                if (!SteamAPI.Init())
                    throw new InvalidOperationException("Steam API init failed in Steam P2P worker");

                var sessionFailQueue = new ConcurrentQueue<WorkerEvent>();
                using var p2pFailCallback = Callback<P2PSessionConnectFail_t>.Create(data =>
                {
                    sessionFailQueue.Enqueue(new WorkerEvent
                    {
                        Type = WorkerEventTypes.SessionFail,
                        Success = false,
                        SteamId = data.m_steamIDRemote.m_SteamID,
                        Error = ((EP2PSessionError)data.m_eP2PSessionError).ToString()
                    });
                });

                var p2pDebugQueue = new ConcurrentQueue<string>();
                var p2pDebug = string.Equals(Environment.GetEnvironmentVariable(SteamP2PWorkerEnvironment.EnvDebugP2P), "1", StringComparison.Ordinal);

                // Must accept P2P session requests before ReadP2PPacket can receive data.
                using var p2pSessionRequestCallback = Callback<P2PSessionRequest_t>.Create(data =>
                {
                    var remote = data.m_steamIDRemote;
                    var isHost = string.Equals(roleText, "host", StringComparison.OrdinalIgnoreCase);
                    var accepted = false;
                    if (isHost)
                    {
                        try { SteamNetworking.AcceptP2PSessionWithUser(remote); accepted = true; } catch { }
                    }
                    else if (hostSteamId != 0UL && remote.m_SteamID == hostSteamId)
                    {
                        try { SteamNetworking.AcceptP2PSessionWithUser(remote); accepted = true; } catch { }
                    }
                    if (accepted && p2pDebug)
                        p2pDebugQueue.Enqueue($"P2P accept: role={roleText} remote={remote.m_SteamID}");
                });

                try
                {
                    using var commandPipe = new NamedPipeClientStream(".", commandPipeName, PipeDirection.In, PipeOptions.Asynchronous);
                    using var eventPipe = new NamedPipeClientStream(".", eventPipeName, PipeDirection.Out, PipeOptions.Asynchronous);
                    commandPipe.Connect(PipeConnectTimeoutMs);
                    eventPipe.Connect(PipeConnectTimeoutMs);

                    using var commandReader = new StreamReader(commandPipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
                    using var eventWriter = new StreamWriter(eventPipe, new UTF8Encoding(false), 4096, leaveOpen: true)
                    {
                        AutoFlush = true
                    };

                    var commands = new ConcurrentQueue<WorkerCommand>();
                    var commandDone = 0;
                    var commandThread = new Thread(() => ReadCommands(commandReader, commands, ref commandDone))
                    {
                        IsBackground = true,
                        Name = "DCCM-SteamP2P-CommandReader"
                    };
                    commandThread.Start();

                    var localSteamId = 0UL;
                    try { localSteamId = SteamUser.GetSteamID().m_SteamID; } catch { }

                    var readyEvt = new WorkerEvent
                    {
                        Type = WorkerEventTypes.Ready,
                        Success = true,
                        Error = string.Empty,
                        SteamId = localSteamId
                    };

                    ulong hostLobbyId = 0UL;
                    if (string.Equals(roleText, "host", StringComparison.OrdinalIgnoreCase))
                    {
                        var hostPortRaw = Environment.GetEnvironmentVariable(SteamP2PWorkerEnvironment.EnvHostPort);
                        var hostIp = Environment.GetEnvironmentVariable(SteamP2PWorkerEnvironment.EnvHostIp) ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(hostPortRaw) && int.TryParse(hostPortRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hostPort) && hostPort > 0)
                        {
                            if (SteamConnect.TryCreateLobbyForP2PHost(hostPort, hostIp, out var lobby))
                            {
                                hostLobbyId = lobby.LobbyId;
                                readyEvt.LobbyId = lobby.LobbyId;
                                readyEvt.LobbyCode = SteamConnect.BuildLobbyCodeFromLobbyId(lobby.LobbyId);
                                readyEvt.HostIp = lobby.HostIp;
                                readyEvt.HostPort = lobby.HostPort;
                                readyEvt.PersonaName = lobby.PersonaName;
                            }
                            else
                            {
                                readyEvt.Success = false;
                                readyEvt.Error = lobby.Error ?? "Lobby creation failed";
                            }
                        }
                    }

                    WriteEvent(eventWriter, readyEvt);

                    if (readyEvt.Success && !string.IsNullOrWhiteSpace(bootstrapResponsePath))
                        TryWriteBootstrapResponse(bootstrapResponsePath, true, string.Empty);

                    var receiveBuffer = readyEvt.Success ? new byte[SteamMinReceiveBufferBytes] : Array.Empty<byte>();
                    var running = readyEvt.Success;
                    while (running)
                    {
                        var hadWork = false;

                        try
                        {
                            SteamAPI.RunCallbacks();
                        }
                        catch (Exception ex)
                        {
                            WriteEvent(eventWriter, new WorkerEvent
                            {
                                Type = WorkerEventTypes.Warning,
                                Error = $"Steam callbacks error: {ex.Message}"
                            });
                        }

                        while (commands.TryDequeue(out var command))
                        {
                            hadWork = true;
                            if (!HandleCommand(command, eventWriter, roleText, hostSteamId, hostLobbyId, ref running))
                                continue;
                        }

                        while (sessionFailQueue.TryDequeue(out var failEvt))
                        {
                            hadWork = true;
                            WriteEvent(eventWriter, failEvt);
                        }

                        while (p2pDebugQueue.TryDequeue(out var dbg))
                        {
                            hadWork = true;
                            WriteEvent(eventWriter, new WorkerEvent { Type = WorkerEventTypes.Warning, Error = dbg });
                        }

                        if (DrainIncomingPackets(eventWriter, ref receiveBuffer, p2pDebug))
                            hadWork = true;

                        try
                        {
                            SteamAPI.RunCallbacks();
                        }
                        catch (Exception ex)
                        {
                            WriteEvent(eventWriter, new WorkerEvent
                            {
                                Type = WorkerEventTypes.Warning,
                                Error = $"Steam callbacks error: {ex.Message}"
                            });
                        }

                        if (!hadWork)
                            Thread.Sleep(WorkerTickSleepMs);

                        if (Volatile.Read(ref commandDone) == 1 && commands.IsEmpty)
                            running = false;
                    }
                }
                finally
                {
                    try { SteamAPI.Shutdown(); } catch { }
                }
            }
            catch (Exception ex)
            {
                if (!string.IsNullOrWhiteSpace(bootstrapResponsePath))
                    TryWriteBootstrapResponse(bootstrapResponsePath, false, ex.ToString());

                Environment.Exit(1);
            }

            Environment.Exit(0);
        }

        private static void ReadCommands(StreamReader reader, ConcurrentQueue<WorkerCommand> commands, ref int doneFlag)
        {
            try
            {
                while (true)
                {
                    string? line;
                    try
                    {
                        line = reader.ReadLine();
                    }
                    catch
                    {
                        break;
                    }

                    if (line == null)
                        break;
                    if (line.Length == 0)
                        continue;

                    WorkerCommand? command = null;
                    try
                    {
                        command = JsonConvert.DeserializeObject<WorkerCommand>(line);
                    }
                    catch
                    {
                    }

                    if (command != null)
                        commands.Enqueue(command);
                }
            }
            finally
            {
                Volatile.Write(ref doneFlag, 1);
            }
        }

        private static bool HandleCommand(
            WorkerCommand command,
            StreamWriter eventWriter,
            string roleText,
            ulong hostSteamId,
            ulong hostLobbyId,
            ref bool running)
        {
            if (command == null || string.IsNullOrWhiteSpace(command.Type))
                return false;

            if (string.Equals(command.Type, WorkerCommandTypes.Stop, StringComparison.Ordinal))
            {
                try { SteamFriends.ClearRichPresence(); } catch { }
                if (hostLobbyId != 0UL)
                {
                    try { SteamMatchmaking.LeaveLobby(new CSteamID(hostLobbyId)); } catch { }
                }
                running = false;
                return true;
            }

            if (string.Equals(command.Type, WorkerCommandTypes.ClosePeer, StringComparison.Ordinal))
            {
                if (command.SteamId != 0UL)
                {
                    try { SteamNetworking.CloseP2PSessionWithUser(new CSteamID(command.SteamId)); } catch { }
                    for (var i = 0; i < 3; i++)
                    {
                        try { SteamAPI.RunCallbacks(); } catch { }
                        Thread.Sleep(10);
                    }
                }
                return true;
            }

            if (string.Equals(command.Type, WorkerCommandTypes.SetRichPresence, StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(command.SendType))
                    return false;

                try
                {
                    SteamFriends.SetRichPresence(command.SendType, command.Payload ?? string.Empty);
                }
                catch (Exception ex)
                {
                    WriteEvent(eventWriter, new WorkerEvent
                    {
                        Type = WorkerEventTypes.Warning,
                        Error = $"SetRichPresence failed: {ex.Message}"
                    });
                }
                return true;
            }

            if (string.Equals(command.Type, WorkerCommandTypes.ClearRichPresence, StringComparison.Ordinal))
            {
                try
                {
                    SteamFriends.ClearRichPresence();
                }
                catch (Exception ex)
                {
                    WriteEvent(eventWriter, new WorkerEvent
                    {
                        Type = WorkerEventTypes.Warning,
                        Error = $"ClearRichPresence failed: {ex.Message}"
                    });
                }
                return true;
            }

            if (!string.Equals(command.Type, WorkerCommandTypes.Send, StringComparison.Ordinal))
                return false;

            if (command.SteamId == 0UL || string.IsNullOrWhiteSpace(command.Payload))
                return false;

            byte[] payloadBytes;
            try
            {
                payloadBytes = Convert.FromBase64String(command.Payload);
            }
            catch
            {
                return false;
            }

            if (!Enum.TryParse(command.SendType, out EP2PSend sendType))
                sendType = EP2PSend.k_EP2PSendReliable;

            var targetSteamId = command.SteamId;
            if (string.Equals(roleText, "client", StringComparison.OrdinalIgnoreCase) &&
                hostSteamId != 0UL &&
                targetSteamId != hostSteamId)
            {
                WriteEvent(eventWriter, new WorkerEvent
                {
                    Type = WorkerEventTypes.Warning,
                    Error = $"Steam client attempted send to unexpected peer {targetSteamId}"
                });
                return true;
            }

            bool sent;
            try
            {
                sent = SteamNetworking.SendP2PPacket(
                    new CSteamID(targetSteamId),
                    payloadBytes,
                    (uint)payloadBytes.Length,
                    sendType,
                    command.Channel);
            }
            catch (Exception ex)
            {
                WriteEvent(eventWriter, new WorkerEvent
                {
                    Type = WorkerEventTypes.Warning,
                    Error = $"Steam send error: {ex.Message}"
                });
                return true;
            }

            if (!sent)
            {
                WriteEvent(eventWriter, new WorkerEvent
                {
                    Type = WorkerEventTypes.Warning,
                    Error = $"Steam send failed to {targetSteamId} ({sendType}, ch={command.Channel})"
                });
            }

            return true;
        }

        private static bool DrainIncomingPackets(StreamWriter eventWriter, ref byte[] receiveBuffer, bool debug = false)
        {
            var hadPackets = false;

            for (var channel = SteamP2PChannelClientToHost; channel <= SteamP2PChannelHostToClient; channel++)
            {
                try
                {
                    while (SteamNetworking.IsP2PPacketAvailable(out var packetSize, channel))
                    {
                        hadPackets = true;
                        if (packetSize == 0 || packetSize > SteamMaxPacketSizeBytes || packetSize > int.MaxValue)
                            continue;

                        var required = (int)Math.Max(packetSize, SteamMinReceiveBufferBytes);
                        if (receiveBuffer.Length < required)
                            Array.Resize(ref receiveBuffer, required);

                        if (!SteamNetworking.ReadP2PPacket(
                                receiveBuffer,
                                (uint)receiveBuffer.Length,
                                out var bytesRead,
                                out var remoteSteamId,
                                channel))
                        {
                            break;
                        }

                        if (bytesRead == 0 || bytesRead > receiveBuffer.Length)
                            continue;

                        try { SteamNetworking.AcceptP2PSessionWithUser(remoteSteamId); } catch { }

                        if (debug)
                            WriteEvent(eventWriter, new WorkerEvent { Type = WorkerEventTypes.Warning, Error = $"P2P packet: ch={channel} size={bytesRead} from={remoteSteamId.m_SteamID}" });

                        var payloadBase64 = Convert.ToBase64String(receiveBuffer, 0, (int)bytesRead);
                        WriteEvent(eventWriter, new WorkerEvent
                        {
                            Type = WorkerEventTypes.Packet,
                            SteamId = remoteSteamId.m_SteamID,
                            Channel = channel,
                            Payload = payloadBase64
                        });
                    }
                }
                catch (Exception ex)
                {
                    WriteEvent(eventWriter, new WorkerEvent
                    {
                        Type = WorkerEventTypes.Warning,
                        Error = $"Steam packet read error: {ex.Message}"
                    });
                }
            }

            return hadPackets;
        }

        private static void WriteEvent(StreamWriter writer, WorkerEvent evt)
        {
            try
            {
                var json = JsonConvert.SerializeObject(evt);
                writer.WriteLine(json);
                writer.Flush();
            }
            catch
            {
            }
        }

        private static void TryWriteBootstrapResponse(string responsePath, bool success, string error)
        {
            try
            {
                var payload = new
                {
                    Success = success,
                    Error = error ?? string.Empty,
                    LobbyId = 0UL,
                    HostIp = string.Empty,
                    HostPort = 0,
                    PersonaName = string.Empty
                };
                File.WriteAllText(responsePath, JsonConvert.SerializeObject(payload));
            }
            catch
            {
            }
        }
    }
}
