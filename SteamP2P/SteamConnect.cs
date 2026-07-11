using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using ModCore.Utilities;
using Newtonsoft.Json;
using Steamworks;

namespace DeadCellsMultiplayerMod
{
    internal static class SteamConnect
    {
        private const int DeadCellsAppId = 588650;
        private const int WorkerTimeoutMs = 25000;
        private const int SteamCallTimeoutMs = 15000;
        private const int JoinWorkerRetryCount = 3;
        private const int JoinWorkerRetryDelayMs = 400;
        private const string HostIpLobbyKey = "dccm_host_ip";
        private const string HostPortLobbyKey = "dccm_host_port";
        private const string ModMarkerLobbyKey = "dccm_mod";
        private const string ModMarkerLobbyValue = "DeadCellsMultiplayerMod";
        private const string LobbyCodeLobbyKey = "dccm_code";
        private const string LobbyCodePrefix = "dc";

        private const string EnvRequestPath = "DCCM_STEAM_CONNECT_REQUEST_PATH";
        private const string EnvResponsePath = "DCCM_STEAM_CONNECT_RESPONSE_PATH";

        private static readonly object HostWorkerSync = new();
        private static Process? _hostWorkerProcess;
        private static string? _hostWorkerStopSignalPath;

        private const uint CfUnicodeText = 13;
        private const uint GmemMoveable = 0x0002;

        [DllImport("user32.dll")]
        private static extern bool OpenClipboard(IntPtr hWndNewOwner);

        [DllImport("user32.dll")]
        private static extern bool CloseClipboard();

        [DllImport("user32.dll")]
        private static extern bool EmptyClipboard();

        [DllImport("user32.dll")]
        private static extern IntPtr GetClipboardData(uint uFormat);

        [DllImport("user32.dll")]
        private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

        [DllImport("user32.dll")]
        private static extern bool IsClipboardFormatAvailable(uint format);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalLock(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalUnlock(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalFree(IntPtr hMem);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetDllDirectory(string lpPathName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryEx(string lpFileName, IntPtr hFile, uint dwFlags);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeLibrary(IntPtr hModule);

        internal sealed class HostLobbyResult
        {
            public bool Success { get; init; }
            public ulong LobbyId { get; init; }
            public string HostIp { get; init; } = string.Empty;
            public int HostPort { get; init; }
            public string PersonaName { get; init; } = string.Empty;
            public string Error { get; init; } = string.Empty;
        }

        internal sealed class JoinLobbyResult
        {
            public bool Success { get; init; }
            public ulong LobbyId { get; init; }
            public ulong HostSteamId { get; init; }
            public string PersonaName { get; init; } = string.Empty;
            public IPEndPoint? Endpoint { get; init; }
            public string Error { get; init; } = string.Empty;
        }

        private sealed class WorkerRequest
        {
            public string Mode { get; set; } = string.Empty;
            public ulong LobbyId { get; set; }
            public string LobbyCode { get; set; } = string.Empty;
            public string HostIp { get; set; } = string.Empty;
            public int HostPort { get; set; }
            public string StopSignalPath { get; set; } = string.Empty;
        }

        private sealed class WorkerResponse
        {
            public bool Success { get; set; }
            public string Error { get; set; } = string.Empty;
            public ulong LobbyId { get; set; }
            public ulong HostSteamId { get; set; }
            public string HostIp { get; set; } = string.Empty;
            public int HostPort { get; set; }
            public string PersonaName { get; set; } = string.Empty;
        }

        internal static bool TryResolveJoinEndpointFromClipboard(out JoinLobbyResult result)
        {
            result = new JoinLobbyResult
            {
                Success = false,
                Error = "Clipboard does not contain a valid Steam lobby id"
            };

            var clipboardText = TryGetClipboardText();
            if (!TryParseLobbyInput(clipboardText, out var lobbyId, out var lobbyCode))
                return false;

            var request = new WorkerRequest
            {
                Mode = "join",
                LobbyId = lobbyId,
                LobbyCode = lobbyCode
            };

            var workerSucceeded = TryRunWorker(request, out var response);
            if (!workerSucceeded && IsTransientJoinWorkerError(response.Error))
            {
                for (var attempt = 0; attempt < JoinWorkerRetryCount; attempt++)
                {
                    Thread.Sleep(JoinWorkerRetryDelayMs);
                    workerSucceeded = TryRunWorker(request, out response);
                    if (workerSucceeded || !IsTransientJoinWorkerError(response.Error))
                        break;
                }
            }

            if (!workerSucceeded)
            {
                result = new JoinLobbyResult
                {
                    Success = false,
                    LobbyId = lobbyId,
                    HostSteamId = response.HostSteamId,
                    PersonaName = response.PersonaName ?? string.Empty,
                    Error = string.IsNullOrWhiteSpace(response.Error)
                        ? "Steam worker process failed"
                        : response.Error
                };
                return false;
            }

            IPEndPoint? endpoint = null;
            if (IPAddress.TryParse(response.HostIp, out var hostIp))
                endpoint = new IPEndPoint(hostIp, NormalizePort(response.HostPort));

            if (endpoint == null && response.HostSteamId == 0UL)
            {
                result = new JoinLobbyResult
                {
                    Success = false,
                    LobbyId = response.LobbyId,
                    HostSteamId = response.HostSteamId,
                    PersonaName = response.PersonaName ?? string.Empty,
                    Error = "Lobby host IP is invalid"
                };
                return false;
            }

            result = new JoinLobbyResult
            {
                Success = true,
                LobbyId = response.LobbyId,
                HostSteamId = response.HostSteamId,
                PersonaName = response.PersonaName ?? string.Empty,
                Endpoint = endpoint,
                Error = string.Empty
            };

            return true;
        }

        internal static bool TryResolveJoinEndpointFromLobbyId(ulong lobbyId, out JoinLobbyResult result)
        {
            ModEntry.Instance?.Logger?.Information("[NetMod][Steam] TryResolveJoinEndpointFromLobbyId start: lobbyId={LobbyId}", lobbyId);

            result = new JoinLobbyResult
            {
                Success = false,
                Error = "Steam lobby id is invalid"
            };

            if (lobbyId == 0UL)
            {
                ModEntry.Instance?.Logger?.Warning("[NetMod][Steam] TryResolveJoinEndpointFromLobbyId failed: invalid lobbyId");
                return false;
            }

            var request = new WorkerRequest
            {
                Mode = "join",
                LobbyId = lobbyId,
                LobbyCode = string.Empty
            };

            var workerSucceeded = TryRunWorker(request, out var response);
            if (!workerSucceeded && IsTransientJoinWorkerError(response.Error))
            {
                for (var attempt = 0; attempt < JoinWorkerRetryCount; attempt++)
                {
                    Thread.Sleep(JoinWorkerRetryDelayMs);
                    workerSucceeded = TryRunWorker(request, out response);
                    if (workerSucceeded || !IsTransientJoinWorkerError(response.Error))
                        break;
                }
            }

            if (!workerSucceeded)
            {
                var err = string.IsNullOrWhiteSpace(response.Error) ? "Steam worker process failed" : response.Error;
                ModEntry.Instance?.Logger?.Warning("[NetMod][Steam] TryResolveJoinEndpointFromLobbyId failed: lobbyId={LobbyId} error={Error}", lobbyId, err);
                result = new JoinLobbyResult
                {
                    Success = false,
                    LobbyId = lobbyId,
                    HostSteamId = response.HostSteamId,
                    PersonaName = response.PersonaName ?? string.Empty,
                    Error = err
                };
                return false;
            }

            IPEndPoint? endpoint = null;
            if (IPAddress.TryParse(response.HostIp, out var hostIp))
                endpoint = new IPEndPoint(hostIp, NormalizePort(response.HostPort));

            if (endpoint == null && response.HostSteamId == 0UL)
            {
                result = new JoinLobbyResult
                {
                    Success = false,
                    LobbyId = response.LobbyId,
                    HostSteamId = response.HostSteamId,
                    PersonaName = response.PersonaName ?? string.Empty,
                    Error = "Lobby host IP is invalid"
                };
                return false;
            }

            result = new JoinLobbyResult
            {
                Success = true,
                LobbyId = response.LobbyId,
                HostSteamId = response.HostSteamId,
                PersonaName = response.PersonaName ?? string.Empty,
                Endpoint = endpoint,
                Error = string.Empty
            };

            ModEntry.Instance?.Logger?.Information("[NetMod][Steam] TryResolveJoinEndpointFromLobbyId success: lobbyId={LobbyId} hostSteamId={HostSteamId}", lobbyId, response.HostSteamId);
            return true;
        }

        private static bool IsTransientJoinWorkerError(string? error)
        {
            if (string.IsNullOrWhiteSpace(error))
                return false;

            return error.IndexOf("host data is unavailable", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   error.IndexOf("data request failed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   error.IndexOf("callback timeout", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static void StopHostLobbyWorker()
        {
            Process? worker;
            string? stopSignalPath;

            lock (HostWorkerSync)
            {
                worker = _hostWorkerProcess;
                stopSignalPath = _hostWorkerStopSignalPath;
                _hostWorkerProcess = null;
                _hostWorkerStopSignalPath = null;
            }

            if (!string.IsNullOrWhiteSpace(stopSignalPath))
            {
                try
                {
                    File.WriteAllText(stopSignalPath, "stop");
                }
                catch
                {
                }
            }

            if (worker != null)
            {
                try
                {
                    if (!worker.HasExited && !worker.WaitForExit(2000))
                    {
                        try { worker.Kill(true); } catch { }
                        try { worker.WaitForExit(1000); } catch { }
                    }
                }
                catch
                {
                }
                finally
                {
                    worker.Dispose();
                }
            }

            if (!string.IsNullOrWhiteSpace(stopSignalPath))
            {
                try
                {
                    if (File.Exists(stopSignalPath))
                        File.Delete(stopSignalPath);
                }
                catch
                {
                }
            }
        }

        internal static void LeaveLobby(ulong lobbyId)
        {
            if (lobbyId == 0UL)
                return;

            try
            {
                SteamMatchmaking.LeaveLobby(new CSteamID(lobbyId));
            }
            catch
            {
            }
        }

        internal static bool TryCopyLobbyIdToClipboard(ulong lobbyId)
        {
            if (lobbyId == 0)
                return false;

            var text = lobbyId.ToString(CultureInfo.InvariantCulture);
            return TrySetClipboardText(text);
        }

        internal static bool TryCopyLobbyCodeToClipboard(string? lobbyCode)
        {
            if (string.IsNullOrWhiteSpace(lobbyCode))
                return false;

            return TrySetClipboardText(lobbyCode.Trim());
        }

        internal static string BuildLobbyCodeFromLobbyId(ulong lobbyId)
        {
            if (lobbyId == 0)
                return string.Empty;

            Span<char> buffer = stackalloc char[32];
            const string alphabet = "0123456789abcdefghijklmnopqrstuvwxyz";
            var value = lobbyId;
            var index = buffer.Length;

            do
            {
                var digit = (int)(value % 36UL);
                buffer[--index] = alphabet[digit];
                value /= 36UL;
            } while (value > 0 && index > 0);

            return string.Concat(LobbyCodePrefix, new string(buffer[index..]));
        }

        private static bool TryParseLobbyInput(string? text, out ulong lobbyId, out string lobbyCode)
        {
            lobbyId = 0;
            lobbyCode = string.Empty;
            if (string.IsNullOrWhiteSpace(text))
                return false;

            var raw = text.Trim();
            if (ulong.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var direct) && direct > 0)
            {
                lobbyId = direct;
                return true;
            }

            var match = Regex.Match(raw, @"joinlobby/\d+/(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (match.Success && ulong.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
            {
                lobbyId = parsed;
                return true;
            }

            var codeMatch = Regex.Match(raw, @"\bdc[a-z0-9]{6,}\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
            if (codeMatch.Success)
            {
                lobbyCode = NormalizeLobbyCode(codeMatch.Value);
                lobbyId = 0;
                return true;
            }

            return false;
        }

        private static string NormalizeLobbyCode(string? rawCode)
        {
            if (string.IsNullOrWhiteSpace(rawCode))
                return string.Empty;

            var normalized = rawCode.Trim().ToLowerInvariant();
            if (!normalized.StartsWith(LobbyCodePrefix, StringComparison.Ordinal))
                return string.Concat(LobbyCodePrefix, normalized);
            return normalized;
        }

        private static bool TryRunHostWorker(WorkerRequest request, out WorkerResponse response)
        {
            response = new WorkerResponse
            {
                Success = false,
                Error = "Unknown worker error"
            };

            StopHostLobbyWorker();

            var requestPath = Path.Combine(Path.GetTempPath(), $"dccm_steam_req_{Guid.NewGuid():N}.json");
            var responsePath = Path.Combine(Path.GetTempPath(), $"dccm_steam_resp_{Guid.NewGuid():N}.json");
            Process? process = null;
            var keepWorker = false;

            try
            {
                File.WriteAllText(requestPath, JsonConvert.SerializeObject(request));

                var startInfo = BuildWorkerStartInfo();
                startInfo.Environment[EnvRequestPath] = requestPath;
                startInfo.Environment[EnvResponsePath] = responsePath;

                var assemblyPath = typeof(SteamConnect).Assembly.Location;
                if (string.IsNullOrWhiteSpace(assemblyPath))
                {
                    response.Error = "Steam worker assembly path is unavailable";
                    return false;
                }
                var loadAssemblies = BuildWorkerLoadAssemblies(assemblyPath);

                process = WorkerProcessUtils.StartWorkerProcess(
                    typeof(SteamWorkerBootstrap).AssemblyQualifiedName!,
                    nameof(SteamWorkerBootstrap.WorkerEntry),
                    startInfo,
                    loadAssemblies);

                if (process == null)
                {
                    response.Error = "Steam worker process was not started";
                    return false;
                }

                if (!TryReadWorkerResponse(responsePath, process, WorkerTimeoutMs, out var parsed, out var readError))
                {
                    response.Error = readError;
                    return false;
                }

                response = parsed;
                if (!response.Success)
                    return false;

                if (process.HasExited)
                {
                    response.Error = "Steam host worker exited unexpectedly";
                    return false;
                }

                lock (HostWorkerSync)
                {
                    _hostWorkerProcess = process;
                    _hostWorkerStopSignalPath = request.StopSignalPath;
                }

                keepWorker = true;
                process = null;
                return true;
            }
            catch (Exception ex)
            {
                response.Error = ex.ToString();
                return false;
            }
            finally
            {
                if (!keepWorker && process != null)
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
                        process.Dispose();
                    }
                }

                try { if (File.Exists(requestPath)) File.Delete(requestPath); } catch { }
                try { if (File.Exists(responsePath)) File.Delete(responsePath); } catch { }

                if (!keepWorker && !string.IsNullOrWhiteSpace(request.StopSignalPath))
                {
                    try
                    {
                        if (File.Exists(request.StopSignalPath))
                            File.Delete(request.StopSignalPath);
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static bool TryRunWorker(WorkerRequest request, out WorkerResponse response)
        {
            response = new WorkerResponse
            {
                Success = false,
                Error = "Unknown worker error"
            };

            var requestPath = Path.Combine(Path.GetTempPath(), $"dccm_steam_req_{Guid.NewGuid():N}.json");
            var responsePath = Path.Combine(Path.GetTempPath(), $"dccm_steam_resp_{Guid.NewGuid():N}.json");

            try
            {
                File.WriteAllText(requestPath, JsonConvert.SerializeObject(request));

                var startInfo = BuildWorkerStartInfo();
                startInfo.Environment[EnvRequestPath] = requestPath;
                startInfo.Environment[EnvResponsePath] = responsePath;

                var assemblyPath = typeof(SteamConnect).Assembly.Location;
                if (string.IsNullOrWhiteSpace(assemblyPath))
                {
                    response.Error = "Steam worker assembly path is unavailable";
                    return false;
                }
                var loadAssemblies = BuildWorkerLoadAssemblies(assemblyPath);

                using var process = WorkerProcessUtils.StartWorkerProcess(
                    typeof(SteamWorkerBootstrap).AssemblyQualifiedName!,
                    nameof(SteamWorkerBootstrap.WorkerEntry),
                    startInfo,
                    loadAssemblies);

                if (process == null)
                {
                    response.Error = "Steam worker process was not started";
                    return false;
                }

                if (!process.WaitForExit(WorkerTimeoutMs))
                {
                    try { process.Kill(true); } catch { }
                    response.Error = "Steam worker timed out";
                    return false;
                }

                if (!TryReadWorkerResponse(responsePath, process, 1000, out var parsed, out var readError))
                {
                    response.Error = readError;
                    return false;
                }

                response = parsed;
                return response.Success;
            }
            catch (Exception ex)
            {
                response.Error = ex.ToString();
                return false;
            }
            finally
            {
                try { if (File.Exists(requestPath)) File.Delete(requestPath); } catch { }
                try { if (File.Exists(responsePath)) File.Delete(responsePath); } catch { }
            }
        }

        private static bool TryReadWorkerResponse(
            string responsePath,
            Process process,
            int timeoutMs,
            out WorkerResponse response,
            out string error)
        {
            response = new WorkerResponse
            {
                Success = false,
                Error = "Steam worker returned no response"
            };
            error = "Steam worker returned no response";

            var timeoutAt = Environment.TickCount64 + timeoutMs;
            while (Environment.TickCount64 < timeoutAt)
            {
                if (File.Exists(responsePath))
                {
                    try
                    {
                        var responseJson = File.ReadAllText(responsePath);
                        var parsed = JsonConvert.DeserializeObject<WorkerResponse>(responseJson);
                        if (parsed == null)
                        {
                            error = "Steam worker response is invalid";
                            return false;
                        }

                        response = parsed;
                        error = string.Empty;
                        return true;
                    }
                    catch
                    {
                    }
                }

                if (process.HasExited)
                {
                    
                    error = BuildWorkerNoResponseError(process, "Steam worker exited without response");
                    return false;
                }

                Thread.Sleep(20);
            }

            if (process.HasExited)
                error = BuildWorkerNoResponseError(process, "Steam worker exited before response");
            else
                error = "Steam worker response timeout";
            return false;
        }

        private static string[] BuildWorkerLoadAssemblies(string mainAssemblyPath)
        {
            var loadAssemblies = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Add(string? path)
            {
                if (string.IsNullOrWhiteSpace(path))
                    return;
                string fullPath;
                try
                {
                    fullPath = Path.GetFullPath(path);
                }
                catch
                {
                    return;
                }

                if (!File.Exists(fullPath))
                    return;
                if (!seen.Add(fullPath))
                    return;

                loadAssemblies.Add(fullPath);
            }

            Add(mainAssemblyPath);
            return loadAssemblies.ToArray();
        }

        private static string BuildWorkerNoResponseError(Process process, string prefix)
        {
            int? exitCode = null;

            try
            {
                if (process.HasExited)
                    exitCode = process.ExitCode;
            }
            catch
            {
            }

            if (!exitCode.HasValue)
                return prefix;

            return string.Create(CultureInfo.InvariantCulture, $"{prefix} (exit={exitCode.Value})");
        }

        private static ProcessStartInfo BuildWorkerStartInfo()
        {
            var executablePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                using var current = Process.GetCurrentProcess();
                executablePath = current.MainModule?.FileName;
            }

            if (string.IsNullOrWhiteSpace(executablePath))
                throw new InvalidOperationException("Could not determine current process path for Steam worker");

            return new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = Environment.CurrentDirectory,
                UseShellExecute = false,
                CreateNoWindow = true
            };
        }

        internal static ProcessStartInfo BuildWorkerStartInfoForRuntime()
        {
            return BuildWorkerStartInfo();
        }

        internal static string[] BuildWorkerLoadAssembliesForRuntime(string mainAssemblyPath)
        {
            return BuildWorkerLoadAssemblies(mainAssemblyPath);
        }

        public static void WorkerEntry()
        {
            var response = new WorkerResponse
            {
                Success = false,
                Error = "Steam worker startup failed"
            };

            var responsePath = Environment.GetEnvironmentVariable(EnvResponsePath);
            var responseWritten = false;
            try
            {
                var requestPath = Environment.GetEnvironmentVariable(EnvRequestPath);
                if (string.IsNullOrWhiteSpace(requestPath) || !File.Exists(requestPath))
                    throw new InvalidOperationException("Steam worker request file is missing");

                var json = File.ReadAllText(requestPath);
                var request = JsonConvert.DeserializeObject<WorkerRequest>(json);
                if (request == null)
                    throw new InvalidOperationException("Steam worker request is invalid");

                response = ExecuteWorkerRequest(request, responsePath, out responseWritten);
            }
            catch (Exception ex)
            {
                response = new WorkerResponse
                {
                    Success = false,
                    Error = ex.ToString()
                };
            }
            finally
            {
                if (!responseWritten)
                {
                    TryWriteWorkerResponse(responsePath, response);
                }

                Environment.Exit(response.Success ? 0 : 1);
            }
        }

        private static WorkerResponse ExecuteWorkerRequest(WorkerRequest request, string? responsePath, out bool responseWritten)
        {
            responseWritten = false;
            PrepareSteamNativePath();

            if (SteamAPI.RestartAppIfNecessary(new AppId_t(DeadCellsAppId)))
            {
                return new WorkerResponse
                {
                    Success = false,
                    Error = "Steam requested app restart"
                };
            }

            if (!SteamAPI.Init())
            {
                WriteSteamInitDiagnostics();
                return new WorkerResponse
                {
                    Success = false,
                    Error = "Steam API init failed. Ensure Steam client is running and the game was launched from Steam."
                };
            }

            try
            {
                if (string.Equals(request.Mode, "host", StringComparison.OrdinalIgnoreCase))
                    return ExecuteHost(request, responsePath, out responseWritten);

                if (string.Equals(request.Mode, "join", StringComparison.OrdinalIgnoreCase))
                    return ExecuteJoin(request);

                return new WorkerResponse
                {
                    Success = false,
                    Error = "Unknown Steam worker mode"
                };
            }
            finally
            {
                try { SteamAPI.Shutdown(); } catch { }
            }
        }

        private static void WriteSteamInitDiagnostics()
        {
            try
            {
                var steamApiName = Environment.Is64BitProcess ? "steam_api64.dll" : "steam_api.dll";
                var lines = new List<string>
                {
                    $"DCCM Steam API init diagnostics - {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z",
                    $"Process: {Environment.Is64BitProcess}",
                    $"CurrentDirectory: {Environment.CurrentDirectory}",
                    $"BaseDirectory: {AppContext.BaseDirectory}",
                    $"ProcessPath: {Environment.ProcessPath ?? "(null)"}",
                    ""
                };

                var steamRunning = Process.GetProcessesByName("steam").Length > 0;
                lines.Add($"Steam process running: {steamRunning}");
                lines.Add("");

                var paths = GetSteamNativeSearchPaths();
                lines.Add("Search paths checked:");
                foreach (var path in paths)
                {
                    var steamApiPath = Path.Combine(path, steamApiName);
                    var exists = File.Exists(steamApiPath);
                    lines.Add($"  {path} -> {steamApiName} exists: {exists}");
                }

                var diagPath = Path.Combine(Path.GetTempPath(), "dccm_steam_init_diag.txt");
                File.WriteAllLines(diagPath, lines);
            }
            catch
            {
                // Best-effort diagnostics
            }
        }

        private static WorkerResponse ExecuteHost(WorkerRequest request, string? responsePath, out bool responseWritten)
        {
            responseWritten = false;
            var hostPort = NormalizePort(request.HostPort);
            var hostIp = string.IsNullOrWhiteSpace(request.HostIp) ? "127.0.0.1" : request.HostIp.Trim();

            var createCall = SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypePublic, 2);
            if (!TryWaitForCallResult(
                    createCall,
                    out LobbyCreated_t created,
                    out var ioFailure,
                    out var waitError))
            {
                return new WorkerResponse
                {
                    Success = false,
                    Error = waitError
                };
            }

            if (ioFailure)
            {
                return new WorkerResponse
                {
                    Success = false,
                    Error = "Steam lobby creation failed (I/O failure)"
                };
            }

            if (created.m_eResult != EResult.k_EResultOK || created.m_ulSteamIDLobby == 0)
            {
                return new WorkerResponse
                {
                    Success = false,
                    Error = $"Steam lobby creation failed ({created.m_eResult})"
                };
            }

            var lobbyId = created.m_ulSteamIDLobby;
            var lobby = new CSteamID(lobbyId);
            var lobbyCode = BuildLobbyCodeFromLobbyId(lobbyId);
            SteamMatchmaking.SetLobbyJoinable(lobby, true);
            SteamMatchmaking.SetLobbyType(lobby, ELobbyType.k_ELobbyTypePublic);
            SteamMatchmaking.SetLobbyData(lobby, HostIpLobbyKey, hostIp);
            SteamMatchmaking.SetLobbyData(lobby, HostPortLobbyKey, hostPort.ToString(CultureInfo.InvariantCulture));
            SteamMatchmaking.SetLobbyData(lobby, ModMarkerLobbyKey, ModMarkerLobbyValue);
            SteamMatchmaking.SetLobbyData(lobby, LobbyCodeLobbyKey, lobbyCode);

            var response = new WorkerResponse
            {
                Success = true,
                LobbyId = lobbyId,
                HostIp = hostIp,
                HostPort = hostPort,
                PersonaName = SafeGetPersonaName()
            };

            if (!string.IsNullOrWhiteSpace(request.StopSignalPath))
            {
                TryWriteWorkerResponse(responsePath, response);
                responseWritten = true;
                KeepHostLobbyAlive(request.StopSignalPath);
                try { SteamMatchmaking.LeaveLobby(lobby); } catch { }
            }

            return response;
        }

        /// <summary>
        /// Creates a Steam lobby for P2P host. Used by the P2P worker when it runs as host.
        /// The caller must keep SteamAPI.RunCallbacks() running to keep the lobby visible.
        /// </summary>
        internal static bool TryCreateLobbyForP2PHost(int hostPort, string hostIp, out HostLobbyResult result)
        {
            result = new HostLobbyResult { Success = false, Error = "Steam lobby creation failed" };
            var port = NormalizePort(hostPort);
            var ip = string.IsNullOrWhiteSpace(hostIp) ? "127.0.0.1" : hostIp.Trim();

            var createCall = SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypePublic, 2);
            if (!TryWaitForCallResult(
                    createCall,
                    out LobbyCreated_t created,
                    out var ioFailure,
                    out var waitError))
            {
                result = new HostLobbyResult { Success = false, Error = waitError };
                return false;
            }

            if (ioFailure)
            {
                result = new HostLobbyResult { Success = false, Error = "Steam lobby creation failed (I/O failure)" };
                return false;
            }

            if (created.m_eResult != EResult.k_EResultOK || created.m_ulSteamIDLobby == 0)
            {
                result = new HostLobbyResult { Success = false, Error = $"Steam lobby creation failed ({created.m_eResult})" };
                return false;
            }

            var lobbyId = created.m_ulSteamIDLobby;
            var lobby = new CSteamID(lobbyId);
            var lobbyCode = BuildLobbyCodeFromLobbyId(lobbyId);
            SteamMatchmaking.SetLobbyJoinable(lobby, true);
            SteamMatchmaking.SetLobbyType(lobby, ELobbyType.k_ELobbyTypePublic);
            SteamMatchmaking.SetLobbyData(lobby, HostIpLobbyKey, ip);
            SteamMatchmaking.SetLobbyData(lobby, HostPortLobbyKey, port.ToString(CultureInfo.InvariantCulture));
            SteamMatchmaking.SetLobbyData(lobby, ModMarkerLobbyKey, ModMarkerLobbyValue);
            SteamMatchmaking.SetLobbyData(lobby, LobbyCodeLobbyKey, lobbyCode);

            result = new HostLobbyResult
            {
                Success = true,
                LobbyId = lobbyId,
                HostIp = ip,
                HostPort = port,
                PersonaName = SafeGetPersonaName()
            };
            return true;
        }

        private static void KeepHostLobbyAlive(string stopSignalPath)
        {
            while (true)
            {
                if (!string.IsNullOrWhiteSpace(stopSignalPath) && File.Exists(stopSignalPath))
                    break;

                try
                {
                    SteamAPI.RunCallbacks();
                }
                catch
                {
                    Thread.Sleep(250);
                    continue;
                }

                Thread.Sleep(50);
            }
        }

        private static void TryWriteWorkerResponse(string? responsePath, WorkerResponse response)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(responsePath))
                    File.WriteAllText(responsePath, JsonConvert.SerializeObject(response));
            }
            catch
            {
            }
        }

        private static WorkerResponse ExecuteJoin(WorkerRequest request)
        {
            var requestedCode = NormalizeLobbyCode(request.LobbyCode);
            ulong targetLobbyId = request.LobbyId;

            if (!string.IsNullOrWhiteSpace(requestedCode))
            {
                if (TryResolveLobbyIdByCode(requestedCode, out var resolvedLobbyId, out _))
                    targetLobbyId = resolvedLobbyId;
            }

            if (targetLobbyId == 0)
            {
                return new WorkerResponse
                {
                    Success = false,
                    Error = string.IsNullOrWhiteSpace(requestedCode)
                        ? "Steam lobby id is invalid"
                        : "Steam lobby code does not exist"
                };
            }

            var lobby = new CSteamID(targetLobbyId);
            TryJoinLobbyForDataFetch(lobby);
            if (!TryReadLobbyHostData(lobby, out var hostSteamId, out var hostIp, out var hostPortRaw, out var readError))
            {
                if (request.LobbyId != 0UL && request.LobbyId != targetLobbyId)
                {
                    lobby = new CSteamID(request.LobbyId);
                    TryJoinLobbyForDataFetch(lobby);
                    if (!TryReadLobbyHostData(lobby, out hostSteamId, out hostIp, out hostPortRaw, out readError))
                    {
                        return new WorkerResponse
                        {
                            Success = false,
                            Error = readError
                        };
                    }

                    targetLobbyId = request.LobbyId;
                }
                else
                {
                    return new WorkerResponse
                    {
                        Success = false,
                        Error = readError
                    };
                }
            }

            var hostPort = 0;
            var hasValidEndpoint = false;
            if (IPAddress.TryParse(hostIp, out _) &&
                int.TryParse(hostPortRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out hostPort))
            {
                hasValidEndpoint = true;
            }

            if (hostSteamId == 0UL && !hasValidEndpoint)
            {
                return new WorkerResponse
                {
                    Success = false,
                    Error = "Steam lobby host data is unavailable"
                };
            }

            return new WorkerResponse
            {
                Success = true,
                LobbyId = targetLobbyId,
                HostSteamId = hostSteamId,
                HostIp = hasValidEndpoint ? hostIp : string.Empty,
                HostPort = hasValidEndpoint ? NormalizePort(hostPort) : 0,
                PersonaName = SafeGetPersonaName()
            };
        }

        private static void TryJoinLobbyForDataFetch(CSteamID lobby)
        {
            if (lobby.m_SteamID == 0UL)
                return;
            try
            {
                SteamMatchmaking.JoinLobby(lobby);
                Thread.Sleep(300);
                for (var i = 0; i < 20; i++)
                {
                    try
                    {
                        SteamAPI.RunCallbacks();
                    }
                    catch { }
                    Thread.Sleep(50);
                }
            }
            catch
            {
            }
        }

        private static bool TryReadLobbyHostData(
            CSteamID lobby,
            out ulong hostSteamId,
            out string hostIp,
            out string hostPortRaw,
            out string error)
        {
            hostSteamId = 0UL;
            hostIp = string.Empty;
            hostPortRaw = string.Empty;
            error = string.Empty;

            var requestSent = false;
            try
            {
                requestSent = SteamMatchmaking.RequestLobbyData(lobby);
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            var start = Stopwatch.GetTimestamp();
            var timeoutTicks = (long)(Stopwatch.Frequency * 8.0);
            while (Stopwatch.GetTimestamp() - start < timeoutTicks)
            {
                try
                {
                    hostSteamId = SteamMatchmaking.GetLobbyOwner(lobby).m_SteamID;
                }
                catch
                {
                    hostSteamId = 0UL;
                }

                hostIp = SteamMatchmaking.GetLobbyData(lobby, HostIpLobbyKey) ?? string.Empty;
                hostPortRaw = SteamMatchmaking.GetLobbyData(lobby, HostPortLobbyKey) ?? string.Empty;
                if (hostSteamId != 0UL &&
                    !string.IsNullOrWhiteSpace(hostIp) &&
                    !string.IsNullOrWhiteSpace(hostPortRaw))
                {
                    return true;
                }

                try
                {
                    SteamAPI.RunCallbacks();
                }
                catch
                {
                }

                Thread.Sleep(20);
            }

            // Steam lobby data propagation can lag, but lobby owner is enough for P2P transport.
            try
            {
                if (hostSteamId == 0UL)
                    hostSteamId = SteamMatchmaking.GetLobbyOwner(lobby).m_SteamID;
            }
            catch
            {
                hostSteamId = 0UL;
            }

            if (hostSteamId != 0UL)
            {
                hostIp = SteamMatchmaking.GetLobbyData(lobby, HostIpLobbyKey) ?? string.Empty;
                hostPortRaw = SteamMatchmaking.GetLobbyData(lobby, HostPortLobbyKey) ?? string.Empty;
                return true;
            }

            if (string.IsNullOrWhiteSpace(error))
            {
                error = requestSent
                    ? "Steam lobby host data is unavailable"
                    : "Steam lobby data request failed";
            }

            return false;
        }

        private static bool TryResolveLobbyIdByCode(string lobbyCode, out ulong lobbyId, out string error)
        {
            lobbyId = 0UL;
            error = string.Empty;

            var normalizedCode = NormalizeLobbyCode(lobbyCode);
            if (string.IsNullOrWhiteSpace(normalizedCode))
            {
                error = "Steam lobby code is invalid";
                return false;
            }

            SteamMatchmaking.AddRequestLobbyListStringFilter(
                ModMarkerLobbyKey,
                ModMarkerLobbyValue,
                ELobbyComparison.k_ELobbyComparisonEqual);
            SteamMatchmaking.AddRequestLobbyListStringFilter(
                LobbyCodeLobbyKey,
                normalizedCode,
                ELobbyComparison.k_ELobbyComparisonEqual);
            SteamMatchmaking.AddRequestLobbyListResultCountFilter(8);

            var listCall = SteamMatchmaking.RequestLobbyList();
            if (!TryWaitForCallResult(
                    listCall,
                    out LobbyMatchList_t matches,
                    out var ioFailure,
                    out var waitError))
            {
                error = waitError;
                return false;
            }

            if (ioFailure)
            {
                error = "Steam lobby list request failed (I/O failure)";
                return false;
            }

            var count = (int)matches.m_nLobbiesMatching;
            for (int i = 0; i < count; i++)
            {
                var lobby = SteamMatchmaking.GetLobbyByIndex(i);
                if (lobby.m_SteamID == 0UL)
                    continue;

                var code = NormalizeLobbyCode(SteamMatchmaking.GetLobbyData(lobby, LobbyCodeLobbyKey));
                if (!string.Equals(code, normalizedCode, StringComparison.Ordinal))
                    continue;

                lobbyId = lobby.m_SteamID;
                return true;
            }

            error = "Steam lobby code does not exist";
            return false;
        }

        private static bool TryWaitForCallResult<T>(
            SteamAPICall_t apiCall,
            out T data,
            out bool ioFailure,
            out string error)
            where T : struct
        {
            data = default;
            ioFailure = false;
            error = string.Empty;

            var completed = false;
            var callbackDataLocal = default(T);
            var callbackIoFailure = false;

            CallResult<T>? callResult = null;
            callResult = CallResult<T>.Create((callbackData, failed) =>
            {
                callbackDataLocal = callbackData;
                callbackIoFailure = failed;
                completed = true;
            });

            callResult.Set(apiCall, (callbackData, failed) =>
            {
                callbackDataLocal = callbackData;
                callbackIoFailure = failed;
                completed = true;
            });

            var start = Stopwatch.GetTimestamp();
            var timeoutTicks = (long)(Stopwatch.Frequency * (SteamCallTimeoutMs / 1000.0));
            while (!completed && Stopwatch.GetTimestamp() - start < timeoutTicks)
            {
                SteamAPI.RunCallbacks();
                Thread.Sleep(15);
            }

            callResult.Dispose();

            if (!completed)
            {
                error = "Steam callback timeout";
                return false;
            }

            data = callbackDataLocal;
            ioFailure = callbackIoFailure;
            return true;
        }

        private static string SafeGetPersonaName()
        {
            try
            {
                return SteamFriends.GetPersonaName() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static int NormalizePort(int port)
        {
            if (port <= 0 || port > 65535)
                return 1234;
            return port;
        }

        internal static bool TryOpenInviteOverlay(ulong lobbyId, out string error)
        {
            error = string.Empty;
            if (lobbyId == 0UL)
            {
                error = "No Steam lobby is active yet";
                return false;
            }

            try
            {
                SteamFriends.ActivateGameOverlayInviteDialog(new CSteamID(lobbyId));
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }


        internal static string ResolveBestHostIp()
        {
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.Connect("1.1.1.1", 65530);
                if (socket.LocalEndPoint is IPEndPoint endPoint &&
                    endPoint.Address != null &&
                    endPoint.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(endPoint.Address))
                {
                    return endPoint.Address.ToString();
                }
            }
            catch
            {
            }

            try
            {
                var host = Dns.GetHostName();
                var addresses = Dns.GetHostAddresses(host);
                for (int i = 0; i < addresses.Length; i++)
                {
                    var address = addresses[i];
                    if (address.AddressFamily != AddressFamily.InterNetwork)
                        continue;
                    if (IPAddress.IsLoopback(address))
                        continue;
                    return address.ToString();
                }
            }
            catch
            {
            }

            return "127.0.0.1";
        }

        internal static void PrepareSteamNativePathForRuntime()
        {
            PrepareSteamNativePath();
        }

        private static void PrepareSteamNativePath()
        {
            var steamApiName = Environment.Is64BitProcess ? "steam_api64.dll" : "steam_api.dll";
            var requiredExport = "SteamAPI_ISteamClient_GetISteamGameSearch";
            string? fallbackPath = null;

            foreach (var path in GetSteamNativeSearchPaths())
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                        continue;

                    var steamApiPath = Path.Combine(path, steamApiName);
                    if (!File.Exists(steamApiPath))
                        continue;

                    fallbackPath ??= path;

                    if (!HasSteamApiExport(steamApiPath, requiredExport))
                        continue;

                    SetDllDirectory(path);
                    return;
                }
                catch
                {
                }
            }

            if (!string.IsNullOrWhiteSpace(fallbackPath))
            {
                try
                {
                    SetDllDirectory(fallbackPath);
                }
                catch
                {
                }
            }
        }

        private static IEnumerable<string> GetSteamNativeSearchPaths()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var searchPaths = new List<string>();
            var preferredNativeDir = Environment.Is64BitProcess ? "win-x64" : "win-x86";

            void Add(string? raw)
            {
                if (string.IsNullOrWhiteSpace(raw))
                    return;
                string fullPath;
                try
                {
                    fullPath = Path.GetFullPath(raw);
                }
                catch
                {
                    return;
                }

                if (!seen.Add(fullPath))
                    return;
                if (Directory.Exists(fullPath))
                    searchPaths.Add(fullPath);
            }

            var hlBootPath = Environment.GetEnvironmentVariable("DCCM_HLBOOT_PATH");
            if (!string.IsNullOrWhiteSpace(hlBootPath))
            {
                var gameRoot = Path.GetDirectoryName(hlBootPath);
                if (!string.IsNullOrWhiteSpace(gameRoot))
                {
                    Add(Path.Combine(gameRoot, "coremod", "core", "native", preferredNativeDir));
                    Add(Path.Combine(gameRoot, "coremod", "core", "native", "win-x64"));
                    Add(Path.Combine(gameRoot, "coremod", "core", "native", "win-x86"));
                    Add(Path.Combine(gameRoot, "coremod", "core", "mdk", "tools"));
                    Add(gameRoot);
                }
            }

            var mdkRoot = Environment.GetEnvironmentVariable("DCCM_MDK_ROOT");
            if (!string.IsNullOrWhiteSpace(mdkRoot))
            {
                Add(Path.Combine(mdkRoot, "..", "native", preferredNativeDir));
                Add(Path.Combine(mdkRoot, "tools"));
            }

            var steamPath = TryGetSteamInstallPath();
            if (!string.IsNullOrWhiteSpace(steamPath))
            {
                Add(Path.Combine(steamPath, "steamapps", "common", "Steamworks SDK Redist", "redist", "bin", preferredNativeDir));
                Add(Path.Combine(steamPath, "steamapps", "common", "Steamworks SDK Redist", "redist", "bin", "win64"));
                Add(Path.Combine(steamPath, "steamapps", "common", "Steamworks SDK Redist", "redist", "bin", "win32"));
                Add(steamPath);
            }

            Add(Environment.CurrentDirectory);
            Add(AppContext.BaseDirectory);

            try
            {
                var processPath = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(processPath))
                    Add(Path.GetDirectoryName(processPath));
            }
            catch
            {
            }

            return searchPaths;
        }

        private static string? TryGetSteamInstallPath()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                if (key != null)
                {
                    var steamPath = key.GetValue("SteamPath") as string;
                    if (string.IsNullOrWhiteSpace(steamPath))
                        steamPath = key.GetValue("InstallPath") as string;
                    if (!string.IsNullOrWhiteSpace(steamPath))
                        return steamPath.Trim();
                }
            }
            catch { }

            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
                if (key != null)
                {
                    var steamPath = key.GetValue("InstallPath") as string;
                    if (!string.IsNullOrWhiteSpace(steamPath))
                        return steamPath.Trim();
                }
            }
            catch { }

            return null;
        }

        private static bool HasSteamApiExport(string steamApiPath, string exportName)
        {
            IntPtr module = IntPtr.Zero;
            try
            {
                module = LoadLibraryEx(steamApiPath, IntPtr.Zero, 0);
                if (module == IntPtr.Zero)
                    return false;

                return GetProcAddress(module, exportName) != IntPtr.Zero;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (module != IntPtr.Zero)
                {
                    try
                    {
                        FreeLibrary(module);
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static string? TryGetClipboardText()
        {
            try
            {
                if (!IsClipboardFormatAvailable(CfUnicodeText))
                    return null;
                if (!OpenClipboard(IntPtr.Zero))
                    return null;

                try
                {
                    var handle = GetClipboardData(CfUnicodeText);
                    if (handle == IntPtr.Zero)
                        return null;

                    var ptr = GlobalLock(handle);
                    if (ptr == IntPtr.Zero)
                        return null;

                    try
                    {
                        return Marshal.PtrToStringUni(ptr);
                    }
                    finally
                    {
                        GlobalUnlock(handle);
                    }
                }
                finally
                {
                    CloseClipboard();
                }
            }
            catch
            {
                return null;
            }
        }

        private static bool TrySetClipboardText(string text)
        {
            try
            {
                if (!OpenClipboard(IntPtr.Zero))
                    return false;

                try
                {
                    if (!EmptyClipboard())
                        return false;

                    var value = text ?? string.Empty;
                    var bytes = (value.Length + 1) * 2;
                    var hGlobal = GlobalAlloc(GmemMoveable, (UIntPtr)bytes);
                    if (hGlobal == IntPtr.Zero)
                        return false;

                    var target = GlobalLock(hGlobal);
                    if (target == IntPtr.Zero)
                    {
                        GlobalFree(hGlobal);
                        return false;
                    }

                    try
                    {
                        Marshal.Copy(value.ToCharArray(), 0, target, value.Length);
                        Marshal.WriteInt16(target, value.Length * 2, 0);
                    }
                    finally
                    {
                        GlobalUnlock(hGlobal);
                    }

                    if (SetClipboardData(CfUnicodeText, hGlobal) == IntPtr.Zero)
                    {
                        GlobalFree(hGlobal);
                        return false;
                    }

                    return true;
                }
                finally
                {
                    CloseClipboard();
                }
            }
            catch
            {
                return false;
            }
        }
    }
}

namespace DeadCellsMultiplayerMod
{
    internal static class SteamWorkerBootstrap
    {
        private const string EnvResponsePath = "DCCM_STEAM_CONNECT_RESPONSE_PATH";
        private const string EnvWorkerMode = "DCCM_STEAM_WORKER_MODE";
        private static readonly object ResolveSync = new();
        private static bool _resolverInstalled;

        public static void WorkerEntry()
        {
            try
            {
                InstallAssemblyResolver();
                TryPreloadSteamworksAssembly();

                var workerMode = Environment.GetEnvironmentVariable(EnvWorkerMode);
                if (string.Equals(workerMode, SteamP2PWorkerEnvironment.WorkerModeP2P, StringComparison.OrdinalIgnoreCase))
                {
                    SteamP2PWorker.WorkerEntry();
                    return;
                }

                SteamConnect.WorkerEntry();
            }
            catch (TargetInvocationException ex)
            {
                TryWriteBootstrapErrorResponse(ex.InnerException?.ToString() ?? ex.ToString());
                Environment.Exit(1);
            }
            catch (Exception ex)
            {
                TryWriteBootstrapErrorResponse(ex.ToString());
                Environment.Exit(1);
            }
        }

        private static void InstallAssemblyResolver()
        {
            lock (ResolveSync)
            {
                if (_resolverInstalled)
                    return;

                AppDomain.CurrentDomain.AssemblyResolve += ResolveAssembly;
                _resolverInstalled = true;
            }
        }

        private static Assembly? ResolveAssembly(object? sender, ResolveEventArgs args)
        {
            string? requestedName;
            try
            {
                requestedName = new AssemblyName(args.Name).Name;
            }
            catch
            {
                return null;
            }

            if (!string.Equals(requestedName, "Steamworks.NET", StringComparison.OrdinalIgnoreCase))
                return null;

            if (TryGetLoadedSteamworksAssembly(out var loaded))
                return loaded;

            return TryLoadSteamworksAssembly();
        }

        private static void TryPreloadSteamworksAssembly()
        {
            if (TryGetLoadedSteamworksAssembly(out _))
                return;

            TryLoadSteamworksAssembly();
        }

        private static Assembly? TryLoadSteamworksAssembly()
        {
            var preferredPath = GetPreferredSteamworksAssemblyPath();
            if (string.IsNullOrWhiteSpace(preferredPath) || !File.Exists(preferredPath))
                return null;

            try
            {
                return Assembly.LoadFrom(preferredPath);
            }
            catch (FileLoadException ex) when (
                ex.Message.IndexOf("already loaded", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (TryGetLoadedSteamworksAssembly(out var loaded))
                    return loaded;
                return null;
            }
            catch
            {
                return null;
            }
        }

        private static string? GetPreferredSteamworksAssemblyPath()
        {
            string? candidate;

            var mdkRoot = Environment.GetEnvironmentVariable("DCCM_MDK_ROOT");
            candidate = TryGetFilePath(mdkRoot, "tools", "Steamworks.NET.dll");
            if (!string.IsNullOrWhiteSpace(candidate))
                return candidate;

            var hlBootPath = Environment.GetEnvironmentVariable("DCCM_HLBOOT_PATH");
            if (!string.IsNullOrWhiteSpace(hlBootPath))
            {
                var gameRoot = Path.GetDirectoryName(hlBootPath);
                candidate = TryGetFilePath(gameRoot, "coremod", "core", "mdk", "tools", "Steamworks.NET.dll");
                if (!string.IsNullOrWhiteSpace(candidate))
                    return candidate;
            }

            var dccmRoot = Environment.GetEnvironmentVariable("DCCM_ROOT");
            candidate = TryGetFilePath(dccmRoot, "core", "mdk", "tools", "Steamworks.NET.dll");
            if (!string.IsNullOrWhiteSpace(candidate))
                return candidate;

            var thisAssemblyDir = Path.GetDirectoryName(typeof(SteamWorkerBootstrap).Assembly.Location);
            candidate = TryGetFilePath(thisAssemblyDir, "Steamworks.NET.dll");
            if (!string.IsNullOrWhiteSpace(candidate))
                return candidate;

            return null;
        }

        private static string? TryGetFilePath(string? basePath, params string[] parts)
        {
            if (string.IsNullOrWhiteSpace(basePath))
                return null;

            try
            {
                var path = basePath;
                for (int i = 0; i < parts.Length; i++)
                    path = Path.Combine(path, parts[i]);

                var fullPath = Path.GetFullPath(path);
                return File.Exists(fullPath) ? fullPath : null;
            }
            catch
            {
                return null;
            }
        }

        private static bool TryGetLoadedSteamworksAssembly(out Assembly? assembly)
        {
            var loaded = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < loaded.Length; i++)
            {
                var candidate = loaded[i];
                if (!string.Equals(candidate.GetName().Name, "Steamworks.NET", StringComparison.OrdinalIgnoreCase))
                    continue;

                assembly = candidate;
                return true;
            }

            assembly = null;
            return false;
        }

        private static void TryWriteBootstrapErrorResponse(string error)
        {
            var responsePath = Environment.GetEnvironmentVariable(EnvResponsePath);
            if (string.IsNullOrWhiteSpace(responsePath))
                return;

            try
            {
                var payload = new
                {
                    Success = false,
                    Error = error ?? "Steam worker bootstrap failed",
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
