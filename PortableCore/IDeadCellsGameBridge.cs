namespace DeadCellsMultiplayerMod.PortableCore;

/// <summary>
/// Boundary between portable multiplayer policy and Dead Cells-specific code.
/// The current DCCM implementation and a future official source implementation
/// should satisfy the same conceptual contract.
/// </summary>
internal interface IDeadCellsGameBridge
{
    bool ValidateLaunch(RunLaunchDescriptor descriptor, out string error);
    void CommitLaunch(RunLaunchDescriptor descriptor);
    void LoadCommittedLevel(RunLaunchDescriptor descriptor, int levelGeneration);
    void ApplyAuthoritativeSpawn(NetEntityId entityId, string payload);
    void ApplyAuthoritativeEvent(string eventType, long sequence, string payload);
    void EndRun(string reason);
}
