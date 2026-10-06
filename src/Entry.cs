using System.Reflection;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using STS2Portrait.Patches;
using STS2RitsuLib;
using STS2RitsuLib.Interop;
using STS2RitsuLib.Patching.Core;

namespace STS2Portrait;

/// <summary>
/// Mod entry point.
/// </summary>
/// <remarks>
/// How the game finds this class - verified by reading the game's own decompiled
/// source (MegaCrit.Sts2.Core.Modding.ModManager, CallModInitializer):
///   1. the loader scans the mod assembly for a type carrying [ModInitializer];
///   2. it resolves the named method using BindingFlags.Static, falling back to
///      Instance - an instance method is rejected with an error, so it must be static;
///   3. it invokes it with no arguments and ignores the return value.
/// If no type carries the attribute, the loader instead calls Harmony.PatchAll
/// on the whole assembly.
///
/// Card A15 wires the whitelist patch into the mod lifecycle. The class still owns no
/// layout, portrait or visual logic of its own: it only creates the RitsuLib patcher,
/// registers the single patch and applies it.
/// </remarks>
[ModInitializer(nameof(Initialize))]
public static class Entry
{
    /// <summary>
    /// Prefix of the marker line used as anchor A4 by the automated run assertion.
    /// That check matches this string literally - do not reword it.
    /// </summary>
    public const string LogTag = "[STS2Portrait]";

    /// <summary>
    /// Mod id used for the loader, the framework and the log channel.
    /// Must stay identical to mod_manifest.json's "id", because the loader derives the
    /// dll filename from it (<c>ModManager</c>: <c>id + ".dll"</c>).
    /// </summary>
    public const string ModId = "STS2Portrait";

    /// <summary>
    /// Framework logger for this mod, created once during <see cref="Initialize"/>.
    /// </summary>
    public static Logger Logger { get; private set; } = null!;

    /// <summary>
    /// True when the required patch could not be applied and the mod disabled itself.
    /// </summary>
    /// <remarks>
    /// Card A15 step 3 forbids a half-working mod: if the whitelist patch fails to apply
    /// there is nothing else this mod does, so it goes inert and says so loudly.
    /// It deliberately performs <b>no</b> fallback that would touch the window itself -
    /// a failed patch must never end up resizing the player's window by other means.
    /// </remarks>
    public static bool IsDisabled { get; private set; }

    /// <summary>
    /// Called once by the mod loader while the game starts up.
    /// </summary>
    public static void Initialize()
    {
        Assembly assembly = Assembly.GetExecutingAssembly();

        // The assembly version is logged on purpose: it makes a stale deployment
        // immediately visible. If the version below does not match the build we just
        // produced, the game loaded an old dll and any later observation is worthless.
        Log.Info($"{LogTag} P1 probe initialized. assembly_version={assembly.GetName().Version}");

        Logger = RitsuLibFramework.CreateLogger(ModId);
        ModTypeDiscoveryHub.RegisterModAssembly(ModId, assembly);

        // Keep the native resolution menu and register the portrait canvas handlers.
        // No gameplay or persistence methods are patched by this mod.
        ModPatcher patcher = RitsuLibFramework.CreatePatcher(ModId, "portrait");
        patcher.RegisterPatch<ResolutionWhitelistPatch>();
        patcher.RegisterPatch<PortraitViewportPatch>();
        patcher.RegisterPatch<PortraitAutoAspectPatch>();
        patcher.RegisterPatch<PortraitMainMenuPatch>();
        patcher.RegisterPatch<PortraitCharacterSelectPatch>();
        patcher.RegisterPatch<PortraitSettingsPatch>();
        // Keep the run's native controls and creature entities while adapting
        // their presentation to the same portrait canvas as the menus.
        patcher.RegisterPatch<PortraitTopBarPatch>();
        patcher.RegisterPatch<PortraitCombatRoomPatch>();
        patcher.RegisterPatch<PortraitHandPatch>();
        patcher.RegisterPatch<PortraitCardReadGatePatch>();
        patcher.RegisterPatch<PortraitCombatDockPatch>();
        patcher.RegisterPatch<PortraitMapPatch>();
        // Keep reward selection native while adding a readable portrait preview.
        patcher.RegisterPatch<PortraitCardRewardPatch>();
        // Keep collection callbacks in the native reward screen.
        patcher.RegisterPatch<PortraitRewardsPatch>();
        // Keep ordinary event choices native while reflowing their presentation.
        patcher.RegisterPatch<PortraitEventPatch>();
        // Preserve the native Ancient intro before placing its settled title.
        patcher.RegisterPatch<PortraitAncientNameIntroPatch>();
        // Keep rest choices and their asynchronous entrance owned by the game.
        patcher.RegisterPatch<PortraitRestSitePatch>();
        patcher.RegisterPatch<PortraitRestSiteEntrancePatch>();
        // Prepare the merchant layout before native stock enters the scene tree.
        patcher.RegisterPatch<PortraitMerchantFactoryPatch>();
        patcher.RegisterPatch<PortraitMerchantPatch>();
        // Keep native grid selection and confirmation while exposing readable cards.
        patcher.RegisterPatch<PortraitDeckSelectPatch>();
        // Keep native inspect models and navigation while making their text touch-readable.
        patcher.RegisterPatch<PortraitInspectPatch>();
        // Keep native treasure ownership while exposing readable reward details.
        patcher.RegisterPatch<PortraitTreasurePatch>();
        // Keep native pause actions and confirmation tasks behind larger touch controls.
        patcher.RegisterPatch<PortraitPopupPatch>();
        // Prepare summary scrolling before native score and badge nodes enter the tree.
        patcher.RegisterPatch<PortraitGameOverFactoryPatch>();
        patcher.RegisterPatch<PortraitGameOverPatch>();

        // ApplyRequiredPatcher logs through the patcher's own logger and calls DisableMod when
        // the patch could not be applied, so the failure path is covered even if the return
        // value were ignored. We still log both outcomes under our own tag: A16 greps for them
        // to tell "patch applied" apart from "patch absent" without reading RitsuLib internals.
        bool applied = RitsuLibFramework.ApplyRequiredPatcher(patcher, DisableMod);
        if (applied)
        {
            Log.Info($"{LogTag} patch applied: patch_id={ResolutionWhitelistPatch.PatchId} patcher={ModId}.portrait");
            Log.Info($"{LogTag} portrait viewport handlers registered.");
        }
    }

    /// <summary>
    /// Called by RitsuLib when a patch declared as critical could not be applied.
    /// </summary>
    private static void DisableMod()
    {
        IsDisabled = true;
        Log.Error($"{LogTag} required portrait patch failed; mod disabled. See the patcher diagnostics for the failing target.");
    }
}
