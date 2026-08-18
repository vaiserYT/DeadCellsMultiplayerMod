using System;
using DeadCellsMultiplayerMod;

public sealed partial class NetNode
{
    private enum FastPathRole
    {
        Host,
        Client
    }

    private enum FastPathKind
    {
        LevelSeedRequest,
        LevelGraphRequest,
        SerializerSync,
        LevelSeed,
        LevelGraph
    }

    private readonly record struct FastPathRegistration(
        string Prefix,
        FastPathRole Role,
        FastPathKind Kind);

    private static readonly FastPathRegistration[] FastPathRegistrations =
    [
        new("LSEEDREQ|", FastPathRole.Host, FastPathKind.LevelSeedRequest),
        new("LGRAPHREQ|", FastPathRole.Host, FastPathKind.LevelGraphRequest),
        new("HXSYNC|", FastPathRole.Client, FastPathKind.SerializerSync),
        new("LSEED|", FastPathRole.Client, FastPathKind.LevelSeed),
        new("LGRAPH|", FastPathRole.Client, FastPathKind.LevelGraph)
    ];

    private static bool TryGetFastPathRegistration(
        string line,
        FastPathRole role,
        out FastPathRegistration registration)
    {
        for (var i = 0; i < FastPathRegistrations.Length; i++)
        {
            var candidate = FastPathRegistrations[i];
            if (candidate.Role == role && line.StartsWith(candidate.Prefix, StringComparison.Ordinal))
            {
                registration = candidate;
                return true;
            }
        }

        registration = default;
        return false;
    }

    private bool TryHandleHostFastPathLine(string line)
    {
        if (!TryGetFastPathRegistration(line, FastPathRole.Host, out var registration))
            return false;

        switch (registration.Kind)
        {
            case FastPathKind.LevelSeedRequest:
            {
                var levelId = ClampProtocolText(line[registration.Prefix.Length..], MaxIdentityFieldChars);
                if (!string.IsNullOrWhiteSpace(levelId))
                    ResendCachedLevelSeed(levelId);
                return true;
            }

            case FastPathKind.LevelGraphRequest:
            {
                var levelId = ClampProtocolText(line[registration.Prefix.Length..], MaxIdentityFieldChars);
                if (!string.IsNullOrWhiteSpace(levelId))
                    ResendCachedLevelGraph(levelId);
                return true;
            }
        }

        return false;
    }

    private bool TryHandleClientFastPathLine(string line)
    {
        try
        {
            if (!TryGetFastPathRegistration(line, FastPathRole.Client, out var registration))
                return false;

            var payload = line[registration.Prefix.Length..];
            switch (registration.Kind)
            {
                case FastPathKind.SerializerSync:
                    lock (_sync) _hasRemote = true;
                    GameDataSync.ReceiveSerializerSync(payload);
                    return true;

                case FastPathKind.LevelSeed:
                    lock (_sync) _hasRemote = true;
                    GameDataSync.ReceiveLevelSeed(payload);
                    return true;

                case FastPathKind.LevelGraph:
                    lock (_sync) _hasRemote = true;
                    GameDataSync.ReceiveLevelGraph(payload);
                    return true;
            }
        }
        catch (Exception ex)
        {
            _log.Warning("[NetNode] Client fast-path line handling failed: {msg}", ex.Message);
        }

        return false;
    }
}
