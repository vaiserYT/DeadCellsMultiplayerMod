using System.Globalization;
using Newtonsoft.Json;
using IOFile = System.IO.File;
using IODirectory = System.IO.Directory;
using IOPath = System.IO.Path;

namespace DeadCellsMultiplayerMod
{
    internal static class MUser
    {
        private const int CurrentVersion = 1;
        private const int MaxCoopIdLength = 128;
        private const string MultiplayerSaveFolderName = "MSave";
        private const string MetadataExtension = ".coop.json";
        private static readonly object Sync = new();
        private static int _cachedSlot = -1;
        private static bool _cachedPathExists;
        private static DateTime _cachedWriteUtc;
        private static CoopMetadata? _cachedMetadata;

        public static string? GetCurrentCoopId()
        {
            return GetCoopIdForSlot(null);
        }

        public static string? GetCoopIdForSlot(int? slot)
        {
            return TryLoad(slot, out var metadata)
                ? NormalizeCoopId(metadata.CoopId)
                : null;
        }

        public static string EnsureCoopIdForNewCoopWorld(string? lastHostId = null, int? lastSeed = null)
        {
            var coopId = Guid.NewGuid().ToString("N");
            SetCoopId(coopId, lastHostId, lastSeed);
            return coopId;
        }

        public static bool UpdateCoopRunSeed(int? lastSeed, string? lastHostId = null)
        {
            var coopId = GetCurrentCoopId();
            return !string.IsNullOrWhiteSpace(coopId) && SetCoopId(coopId, lastHostId, lastSeed);
        }

        public static bool SetCoopId(string? coopId, string? lastHostId = null, int? lastSeed = null)
        {
            var normalized = NormalizeCoopId(coopId);
            if (string.IsNullOrWhiteSpace(normalized))
                return false;

            lock (Sync)
            {
                try
                {
                    var slot = ResolveSaveSlotNumber(null);
                    var path = GetMetadataPathForSlot(slot);
                    var normalizedHostId = NormalizeMetadataValue(lastHostId);
                    var now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                    var createdAt = now;

                    if (TryLoad(slot, out var existing))
                    {
                        if (string.Equals(NormalizeCoopId(existing.CoopId), normalized, StringComparison.Ordinal) &&
                            string.Equals(existing.LastHostId, normalizedHostId, StringComparison.Ordinal) &&
                            existing.LastSeed == lastSeed &&
                            !string.IsNullOrWhiteSpace(existing.CreatedAtUtc))
                        {
                            return true;
                        }

                        if (!string.IsNullOrWhiteSpace(existing.CreatedAtUtc))
                            createdAt = existing.CreatedAtUtc;
                    }

                    var metadata = new CoopMetadata
                    {
                        Version = CurrentVersion,
                        CoopId = normalized,
                        LastHostId = normalizedHostId,
                        LastSeed = lastSeed,
                        CreatedAtUtc = createdAt,
                        UpdatedAtUtc = now
                    };

                    IODirectory.CreateDirectory(IOPath.GetDirectoryName(path)!);
                    IOFile.WriteAllText(path, JsonConvert.SerializeObject(metadata, Formatting.Indented));
                    UpdateCacheLocked(slot, path, metadata);
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        public static void ClearCoopId(int? slot = null)
        {
            lock (Sync)
            {
                try
                {
                    var resolvedSlot = ResolveSaveSlotNumber(slot);
                    var path = GetMetadataPathForSlot(resolvedSlot);
                    if (IOFile.Exists(path))
                        IOFile.Delete(path);

                    InvalidateCacheLocked(resolvedSlot);
                }
                catch
                {
                }
            }
        }

        public static bool IsContinueCompatible(string? remoteCoopId, out string reason)
        {
            var localCoopId = GetCurrentCoopId();
            if (string.IsNullOrWhiteSpace(localCoopId))
            {
                reason = "No local coop id";
                return false;
            }

            var normalizedRemote = NormalizeCoopId(remoteCoopId);
            if (string.IsNullOrWhiteSpace(normalizedRemote))
            {
                reason = "Host coop id not received";
                return false;
            }

            if (!string.Equals(localCoopId, normalizedRemote, StringComparison.Ordinal))
            {
                reason = "Coop world mismatch";
                return false;
            }

            reason = "OK";
            return true;
        }

        public static string? NormalizeCoopId(string? coopId)
        {
            if (string.IsNullOrWhiteSpace(coopId))
                return null;

            var trimmed = coopId.Trim();
            if (trimmed.Length > MaxCoopIdLength)
                return null;

            for (var i = 0; i < trimmed.Length; i++)
            {
                var c = trimmed[i];
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_')
                    continue;

                return null;
            }

            return trimmed;
        }

        private static bool TryLoad(int? slot, out CoopMetadata metadata)
        {
            metadata = new CoopMetadata();
            lock (Sync)
            {
                try
                {
                    var resolvedSlot = ResolveSaveSlotNumber(slot);
                    var path = GetMetadataPathForSlot(resolvedSlot);
                    var exists = IOFile.Exists(path);
                    var writeUtc = exists ? IOFile.GetLastWriteTimeUtc(path) : DateTime.MinValue;

                    if (_cachedSlot == resolvedSlot &&
                        _cachedPathExists == exists &&
                        _cachedWriteUtc == writeUtc)
                    {
                        if (_cachedMetadata == null)
                            return false;

                        metadata = _cachedMetadata;
                        return true;
                    }

                    if (!exists)
                    {
                        UpdateCacheLocked(resolvedSlot, path, null);
                        return false;
                    }

                    var loaded = JsonConvert.DeserializeObject<CoopMetadata>(IOFile.ReadAllText(path));
                    if (loaded == null || string.IsNullOrWhiteSpace(NormalizeCoopId(loaded.CoopId)))
                    {
                        UpdateCacheLocked(resolvedSlot, path, null);
                        return false;
                    }

                    metadata = loaded;
                    UpdateCacheLocked(resolvedSlot, path, loaded);
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        private static void UpdateCacheLocked(int slot, string path, CoopMetadata? metadata)
        {
            _cachedSlot = slot;
            _cachedPathExists = metadata != null && IOFile.Exists(path);
            _cachedWriteUtc = _cachedPathExists ? IOFile.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            _cachedMetadata = metadata;
        }

        private static void InvalidateCacheLocked(int slot)
        {
            if (_cachedSlot != slot)
                return;

            _cachedSlot = -1;
            _cachedPathExists = false;
            _cachedWriteUtc = DateTime.MinValue;
            _cachedMetadata = null;
        }

        private static string GetMetadataPathForSlot(int slot)
        {
            var fileName = string.Create(
                CultureInfo.InvariantCulture,
                $"user_{slot}{MetadataExtension}");
            return IOPath.Combine(GetMultiplayerSaveFolderPath(), fileName);
        }

        private static string GetMultiplayerSaveFolderPath()
        {
            return IOPath.Combine(GetSaveRootPath(), MultiplayerSaveFolderName);
        }

        private static int ResolveSaveSlotNumber(int? slot)
        {
            if (slot.HasValue && slot.Value >= 0)
                return slot.Value;

            try
            {
                var current = dc.Main.Class.ME?.options?.curSlot;
                if (current.HasValue && current.Value >= 0)
                    return current.Value;
            }
            catch
            {
            }

            return 0;
        }

        private static string GetSaveRootPath()
        {
            try
            {
                var saveRoot = dc.tool.File.Class.PATH?.ToString();
                if (!string.IsNullOrWhiteSpace(saveRoot))
                    return IOPath.GetFullPath(saveRoot);
            }
            catch
            {
            }

            try
            {
                return IOPath.GetFullPath("save");
            }
            catch
            {
                return IOPath.Combine(Environment.CurrentDirectory, "save");
            }
        }

        private static string? NormalizeMetadataValue(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            var normalized = value.Trim()
                .Replace("|", "/", StringComparison.Ordinal)
                .Replace("\r", string.Empty, StringComparison.Ordinal)
                .Replace("\n", string.Empty, StringComparison.Ordinal);

            return normalized.Length > 128 ? normalized[..128] : normalized;
        }

        private sealed class CoopMetadata
        {
            public int Version { get; set; } = CurrentVersion;
            public string CoopId { get; set; } = string.Empty;
            public string? LastHostId { get; set; }
            public int? LastSeed { get; set; }
            public string CreatedAtUtc { get; set; } = string.Empty;
            public string UpdatedAtUtc { get; set; } = string.Empty;
        }
    }
}
