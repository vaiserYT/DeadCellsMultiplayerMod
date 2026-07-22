namespace DeadCellsMultiplayerMod;

/// <summary>
/// One authoritative identity for packaging, log diagnostics, and the network handshake.
/// Bump <see cref="NetworkProtocolVersion"/> whenever peers would interpret a packet differently.
/// </summary>
internal static class BuildInfo
{
    public const string Version = "0.8.91";
    public const string SourceMarker = "v0.8.91-mobsync-teleport-hardening";
    // Protocol 17: authoritative Boss Rush launch descriptor (extended generation fields),
    // RUNQUEUED client-queued gate, and removal of the client local-seed fallback. Peers on
    // protocol 16 interpret the launch handshake differently, so cross-version pairing is rejected.
    public const int NetworkProtocolVersion = 17;
}
