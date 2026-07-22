namespace DeadCellsMultiplayerMod;

/// <summary>
/// Pointer FX suppression used to write an arbitrary boxed integer into Dead Cells' typed
/// cooldown fastCheck map. A key/type collision can corrupt Hashlink state and crash later in
/// unrelated combat. Duplicate pointer effects are cosmetic, so stable builds deliberately do
/// nothing here rather than touching engine-owned cooldown storage.
/// </summary>
internal static class PointerFxHelper
{
    internal static void SuppressPointerFx(dc.ui.Pointer? pointer, int suppressionKey)
    {
        return;
    }
}
