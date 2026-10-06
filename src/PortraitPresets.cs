using Godot;

namespace STS2Portrait;

/// <summary>
/// Data-only source of the portrait window sizes this mod will offer.
/// </summary>
/// <remarks>
/// Card A13 scope, on purpose: this type holds numbers and nothing else.
/// It does not patch anything, does not touch <c>DisplayServer</c>, does not read or write
/// <c>SettingsSave</c>, and is not referenced by <see cref="Entry"/> yet - so building it
/// into the assembly changes no game behaviour. Wiring it in is card A14/A15's job.
///
/// Naming note: reports/A04.md class_contract C3 sketched this type as
/// "PortraitResolutionPresets"; card A13 fixes the file name to
/// "mods/STS2Portrait/src/PortraitPresets.cs", so the class is named after the file
/// (<c>PortraitPresets</c>) and A04's C3 is amended accordingly.
///
/// Values come from reports/A12.md (approved_presets): both sizes were measured to fit this
/// display's work area 2560x1600 including the window frame overhead of +22x+56, and both
/// stay above the game's reported minimum window size of 64x64.
/// </remarks>
internal static class PortraitPresets
{
    /// <summary>
    /// The portrait sizes, in physical pixels, as the game's own whitelist expresses them
    /// (<c>Godot.Vector2I</c>, width first). Ordered smallest to largest.
    /// </summary>
    /// <remarks>
    /// Deliberately private: exposing the array itself would hand out a mutable reference to
    /// shared state, since <see cref="Array"/> is not read-only. Callers that need to append
    /// into the game's resolution list must go through <see cref="Snapshot"/> instead.
    /// </remarks>
    private static readonly Vector2I[] _presets =
    {
        new Vector2I(450, 800),
        new Vector2I(540, 960),
    };

    /// <summary>
    /// How many presets exist. Constant-time, allocation free.
    /// </summary>
    internal static int Count => _presets.Length;

    /// <summary>
    /// One preset by index. <see cref="Vector2I"/> is a struct, so this returns a copy and
    /// the caller cannot corrupt the stored value.
    /// </summary>
    /// <param name="index">Zero-based index, must be less than <see cref="Count"/>.</param>
    internal static Vector2I At(int index) => _presets[index];

    /// <summary>
    /// A fresh array containing the presets, safe for a caller to mutate.
    /// </summary>
    /// <remarks>
    /// This is the accessor card A14's postfix uses: it appends the returned array into the
    /// <c>List&lt;Vector2I&gt;</c> that <c>NResolutionDropdown.GetResolutionWhiteList()</c>
    /// returns. Because the array is a copy, repeated menu rebuilds can never accumulate
    /// changes into our stored values (that would be the "shared mutable list" bug A13 step 2
    /// warns about, and it would break card A22's "no duplicate injection" check).
    /// </remarks>
    internal static Vector2I[] Snapshot() => (Vector2I[])_presets.Clone();
}
