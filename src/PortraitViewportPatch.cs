using System;
using Godot;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Settings;
using STS2RitsuLib.Patching.Models;

namespace STS2Portrait.Patches;

/// <summary>
/// Keeps a phone-sized logical canvas without changing display preferences or saves.
/// The native display settings still own the physical window and fullscreen mode.
/// </summary>
internal sealed class PortraitViewportPatch : IPatchMethod
{
    public static string PatchId => "sts2portrait_viewport";
    public static string Description => "Use a portrait logical canvas for portrait windows";
    public static bool IsCritical => true;
    public static ModPatchTarget[] GetTargets() => new[]
    {
        PatchTarget.Method(typeof(NGame), nameof(NGame.ApplyDisplaySettings)),
    };

    private static bool _applying;
    private static bool _queued;
    private static bool _active;
    private static Vector2I _landscapeSize;
    private static Window.ContentScaleAspectEnum _landscapeAspect;
    private static Window.ContentScaleModeEnum _landscapeMode;

    internal static bool IsPortrait => ((SceneTree)Engine.GetMainLoop()).Root.Size.Y
        > ((SceneTree)Engine.GetMainLoop()).Root.Size.X;

    public static void Postfix(bool __runOriginal)
    {
        if (Entry.IsDisabled)
            return;

        // Fixed display settings have just produced a fresh native canvas.
        // Auto is captured after its native window handler has calculated it.
        if (__runOriginal && SaveManager.Instance.SettingsSave.AspectRatioSetting != AspectRatioSetting.Auto)
            CaptureNativeLandscape();
        RequestApply();
    }

    internal static void CaptureNativeLandscape()
    {
        // Ignore our nested size signals and calls outside portrait ownership.
        if (!_active || _applying)
            return;
        Window window = ((SceneTree)Engine.GetMainLoop()).Root;
        _landscapeSize = window.ContentScaleSize;
        _landscapeAspect = window.ContentScaleAspect;
        // Preserve the original mode: the current CanvasItems mode can be ours.
    }

    internal static void RequestApply()
    {
        // Window._update_viewport_size attaches its precomputed screen rectangle
        // AFTER emitting size_changed. A synchronous nested update is overwritten
        // by that outer call, leaving correct properties but a distorted image.
        if (_queued || _applying)
            return;

        _queued = true;
        Callable.From(() =>
        {
            _queued = false;
            Apply();
        }).CallDeferred();
    }

    internal static void Apply()
    {
        // Setting the canvas can emit another size-change signal synchronously.
        // This guard prevents re-entry; it never suppresses an exception.
        if (_applying || Entry.IsDisabled)
            return;

        Window window = ((SceneTree)Engine.GetMainLoop()).Root;
        _applying = true;
        try
        {
            if (window.Size.Y > window.Size.X)
            {
                if (!_active)
                {
                    _landscapeSize = window.ContentScaleSize;
                    _landscapeAspect = window.ContentScaleAspect;
                    _landscapeMode = window.ContentScaleMode;
                    _active = true;
                }

                // Match the actual aspect so the phone's height is usable UI space.
                Vector2I canvas = new(1080, (int)Math.Round(1080.0 * window.Size.Y / window.Size.X));
                bool changed = window.ContentScaleSize != canvas;
                window.ContentScaleMode = Window.ContentScaleModeEnum.CanvasItems;
                // Expand keeps physical aspect changes observable by SizeChanged.
                // Keep can add letterboxing without changing the viewport at all.
                window.ContentScaleAspect = Window.ContentScaleAspectEnum.Expand;
                window.ContentScaleSize = canvas;
                if (changed)
                    Log.Info($"{Entry.LogTag} portrait canvas={canvas} window={window.Size}");
            }
            else if (_active)
            {
                _active = false;
                window.ContentScaleMode = _landscapeMode;
                window.ContentScaleAspect = _landscapeAspect;
                window.ContentScaleSize = _landscapeSize;
                Log.Info($"{Entry.LogTag} restored landscape canvas={_landscapeSize}");
            }
        }
        finally
        {
            _applying = false;
        }
    }
}

/// <summary>
/// The menu and the run both clamp Auto to a landscape aspect. Bypass that clamp
/// only in portrait; every native landscape handler still runs normally.
/// </summary>
internal sealed class PortraitAutoAspectPatch : IPatchMethod
{
    public static string PatchId => "sts2portrait_auto_aspect";
    public static string Description => "Preserve the portrait canvas across native window changes";
    public static bool IsCritical => true;
    public static ModPatchTarget[] GetTargets() => new[]
    {
        PatchTarget.Method(typeof(NMainMenu), "OnWindowChange"),
        PatchTarget.Method(typeof(NGlobalUi), "OnWindowChange"),
    };

    public static bool Prefix()
    {
        if (Entry.IsDisabled)
            return true;

        PortraitViewportPatch.RequestApply();
        return !PortraitViewportPatch.IsPortrait;
    }

    public static void Postfix(bool __runOriginal)
    {
        if (Entry.IsDisabled || !__runOriginal || PortraitViewportPatch.IsPortrait)
            return;

        // Capture the native result before deferred landscape restoration runs.
        if (SaveManager.Instance.SettingsSave.AspectRatioSetting == AspectRatioSetting.Auto)
            PortraitViewportPatch.CaptureNativeLandscape();
    }
}
