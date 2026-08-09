namespace DeadCellsMultiplayerMod;

/// <summary>
/// One authoritative identity for packaging, log diagnostics, and the network handshake.
/// Bump <see cref="NetworkProtocolVersion"/> whenever peers would interpret a packet differently.
/// </summary>
internal static class BuildInfo
{
    public const string Version = "0.8.91";
    public const string SourceMarker = "v0.8.91-runtime-registry+attack-retarget";
    // Protocol 18: launch convergence and level-boundary identity.
    //   * The host re-publishes the full launch prerequisite set (GEN/CGDATA/RUNCOMMIT/SEED/
    //     RUNEXEC) until the client confirms, and GEN is host-cached and replayed to late joiners.
    //     A protocol 17 peer never sends or expects that repetition.
    //   * RUNREADY is now actually emitted, so the session phase reaches Playing and the save guard
    //     disarms. A peer that never sends it would leave the other side's launch unconfirmed.
    //   * EXITREADY carries the reporting peer's level id; a protocol 17 peer omits it and its
    //     readiness would be accepted for the wrong level.
    //   * EXITCOMMIT: the host now AUTHORS level transitions (sequence + door + destination). A
    //     peer that neither sends nor waits for it would transition on its own timing.
    //   * SPAWNANCHOR: host-approved safe spawn for mid-run joiners.
    //   * Boss snapshots may carry Conjunctivius vulnerability/arena-platform fields; a peer
    //     without them computes vulnerability locally and can disagree with the host.
    // These change how peers interpret the same exchange, so cross-version pairing is rejected.
    public const int NetworkProtocolVersion = 18;
}
