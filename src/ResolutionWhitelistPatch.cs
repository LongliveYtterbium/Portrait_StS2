using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;
using STS2RitsuLib.Patching.Models;

namespace STS2Portrait.Patches;

/// <summary>
/// Appends this mod's portrait sizes to the game's own resolution whitelist.
/// </summary>
/// <remarks>
/// Card A14 declared the patch and its postfix; card A15 registered it, so this type is live:
/// <see cref="Entry"/> creates the patcher and registers exactly this patch. Appending here
/// reuses the whole native selection chain (filter -> item text -> click ->
/// ApplyDisplaySettings -> saved settings) and needs no UI code of its own.
///
/// Card A19c replaced the postfix's null branch (previously Log.Error + return) with
/// <see cref="ArgumentNullException.ThrowIfNull(object?)"/>, so a broken target contract is
/// reported to the caller instead of being swallowed.
///
/// Target and shape come from reports/A11.md: <c>NResolutionDropdown.GetResolutionWhiteList()</c>
/// (private static, no parameters, returns <c>List&lt;Vector2I&gt;</c>) has exactly one caller,
/// <c>PopulateDropdownItems</c> (L142), which feeds every entry through <c>DoesResolutionFit</c>
/// and then builds the dropdown items.
///
/// The method-body shape mirrors RitsuLib's own patch classes: static <c>GetTargets()</c> plus a
/// by-convention <c>Postfix</c> (no [HarmonyPatch] attribute - RitsuLib supplies the target).
/// </remarks>
internal sealed class ResolutionWhitelistPatch : IPatchMethod
{
    /// <summary>
    /// Stable id for this patch; also what patch diagnostics group under.
    /// </summary>
    public static string PatchId => "sts2portrait_resolution_whitelist";

    /// <summary>
    /// Human readable purpose, shown by the patcher's diagnostics.
    /// </summary>
    public static string Description => "Append the portrait presets to the native windowed resolution whitelist";

    /// <summary>
    /// Without this patch the mod does nothing at all, so a failed application is critical:
    /// RitsuLib will call the disable callback instead of leaving a half-working mod loaded.
    /// </summary>
    public static bool IsCritical => true;

    /// <summary>
    /// The single vanilla method this patch extends.
    /// </summary>
    public static ModPatchTarget[] GetTargets() => new ModPatchTarget[]
    {
        PatchTarget.Method(typeof(NResolutionDropdown), "GetResolutionWhiteList"),
    };

    /// <summary>
    /// Runs after the vanilla whitelist has been built and adds only the presets that are
    /// missing from it.
    /// </summary>
    /// <param name="__result">
    /// The vanilla list, injected by the patcher. This patch never removes or reorders what is
    /// already in it, and never replaces the list instance.
    /// </param>
    /// <remarks>
    /// Idempotent on purpose (acceptance rule "the same preset may appear at most once"): the
    /// list is rebuilt from scratch every time the settings screen repopulates the dropdown, and
    /// appending blindly would add duplicates if this ever runs against a list that already
    /// contains our values. A22 later checks that reopening the settings screen does not
    /// accumulate entries, and this guard is what makes that hold.
    ///
    /// Our own values are read through <see cref="PortraitPresets.Snapshot"/>, a copy, so a
    /// caller that mutates the returned array cannot corrupt the stored data.
    /// </remarks>
    public static void Postfix(ref List<Vector2I> __result)
    {
        // Patch-boundary contract: the vanilla method builds a fresh list and never returns
        // null, so a null here means the target changed shape underneath this patch. That is a
        // broken contract, not a recoverable condition, and silently returning would leave the
        // caller with null while this mod quietly did nothing.
        //
        // Card A19c removed the previous Log.Error + return. Logging and continuing hid the
        // failure from every caller and from any test, which the project's "fail loudly" rule
        // forbids; the argument name is carried into the exception message so the failing
        // boundary is identifiable from the crash report alone.
        ArgumentNullException.ThrowIfNull(__result);

        Vector2I[] presets = PortraitPresets.Snapshot();
        for (int i = 0; i < presets.Length; i++)
        {
            Vector2I preset = presets[i];
            if (!__result.Contains(preset))
            {
                __result.Add(preset);
            }
        }
    }
}
