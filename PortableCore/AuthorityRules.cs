namespace DeadCellsMultiplayerMod.PortableCore;

internal enum AuthorityDomain
{
    RunLaunch,
    LevelGeneration,
    EntitySpawn,
    EnemyAi,
    BossAi,
    DamageResolution,
    WorldInteraction,
    EncounterLifecycle,
    LevelTransition,
    Progression,
    LocalInput,
    Camera,
    Presentation,
}

internal enum AuthorityOwner
{
    Host,
    LocalClient,
}

/// <summary>
/// One explicit authority policy shared by every integration layer.
/// </summary>
internal static class AuthorityRules
{
    public static AuthorityOwner OwnerOf(AuthorityDomain domain) => domain switch
    {
        AuthorityDomain.LocalInput => AuthorityOwner.LocalClient,
        AuthorityDomain.Camera => AuthorityOwner.LocalClient,
        AuthorityDomain.Presentation => AuthorityOwner.LocalClient,
        _ => AuthorityOwner.Host,
    };

    public static bool CanMutateAuthoritativeState(AuthorityDomain domain, bool isHost) =>
        OwnerOf(domain) == AuthorityOwner.Host ? isHost : true;
}
