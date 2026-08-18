public sealed partial class NetNode
{
    /// <summary>Network-layer state for one remote player, separate from its in-world GhostKing.</summary>
    private sealed class RemotePlayerState
    {
        public int Id { get; }
        public double X;
        public double Y;
        public int Dir = 1;
        public bool HasPosition;
        public long LastPositionSequence;
        public bool HasRemote;
        public string? LevelId;
        public string? RoomLevelId;
        public int? RoomId;
        public bool HasRoom;
        public string? Anim;
        public int? AnimQueue;
        public bool? AnimG;
        public bool HasAnim;
        public int Life;
        public int MaxLife;
        public int Lif;
        public int BonusLife;
        public int Recover;
        public string? Username;
        public bool Ready;
        public string? CoopId;
        public bool HasContinueSave;
        public string? Skin;
        public string? Head;
        public string HeadAnim = string.Empty;
        public bool HasHeadAnim;
        public string? WeaponKind;
        public int WeaponSlot;
        public int WeaponPermanentId;
        public int WeaponAmmo = int.MinValue;
        public bool HasWeaponUpdate;

        public RemotePlayerState(int id)
        {
            Id = id;
        }
    }
}
