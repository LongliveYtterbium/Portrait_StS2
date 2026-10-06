using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Helpers;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using OpCodes = System.Reflection.Emit.OpCodes;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.addons.mega_text;
using STS2RitsuLib.Patching.Models;

namespace STS2Portrait.Patches;

/// <summary>Reflows native event text, dialogue, and choices for touch reading.</summary>
internal sealed class PortraitEventPatch : IPatchMethod
{
    public static string PatchId => "sts2portrait_event";
    public static string Description => "Readable scrolling native event choices in portrait";
    public static bool IsCritical => true;
    public static ModPatchTarget[] GetTargets() => new[]
    {
        // Preserve the original board, tool selection and completion callbacks.
        PatchTarget.Method(typeof(NCrystalSphereScreen), "_Ready"),
        PatchTarget.Method(typeof(NCrystalSphereScreen), "OnMinigameFinished"),
        PatchTarget.Method(typeof(NCrystalSphereDialogue), "Play"),
        PatchTarget.Method(typeof(NProceedButton), "OnEnable"),
        PatchTarget.Method(typeof(NProceedButton), "OnDisable"),
        PatchTarget.Method(typeof(NEventLayout), "_Ready"),
        PatchTarget.Method(typeof(NAncientBgContainer), "OnWindowChange"),
        PatchTarget.Method(typeof(NAncientEventLayout), "_Ready"),
        PatchTarget.Method(typeof(NAncientEventLayout), "SetDialogue"),
        PatchTarget.Method(typeof(NAncientEventLayout), "ClearDialogue"),
        PatchTarget.Method(typeof(NAncientEventLayout), "SetDialogueLineAndAnimate"),
        PatchTarget.Method(typeof(NCombatEventLayout), "_Ready"),
        PatchTarget.Method(typeof(NCombatEventLayout), "HideEventVisuals"),
        PatchTarget.Method(typeof(TheArchitect), "ShowSpeechBubble"),
        // Native portrait swaps and PhobiaMode keep the same background owner.
        PatchTarget.Method(typeof(NEventLayout), "SetPortrait"),
        PatchTarget.Method(typeof(NEventLayout), "UpdatePhobiaMode"),
        PatchTarget.Method(typeof(NEventLayout), "SetTitle"),
        PatchTarget.Method(typeof(NEventLayout), "SetDescription"),
        PatchTarget.Method(typeof(NEventLayout), "AddOptions"),
        PatchTarget.Method(typeof(NEventOptionButton), "AnimateIn"),
    };

    // Only existing default-event art states use this batch; custom/combat owners stay native.
    private static readonly Dictionary<string, string> PortraitEventResources = new(StringComparer.Ordinal)
    {
        ["res://images/events/abyssal_baths.png"] = "res://STS2Portrait/portrait/events/abyssal_baths-portrait-v1.png",
        ["res://images/events/amalgamator.png"] = "res://STS2Portrait/portrait/events/amalgamator-portrait-v1.png",
        ["res://images/events/aroma_of_chaos.png"] = "res://STS2Portrait/portrait/events/aroma_of_chaos-portrait-v1.png",
        ["res://images/events/battleworn_dummy.png"] = "res://STS2Portrait/portrait/events/battleworn_dummy-portrait-v1.png",
        ["res://images/events/brain_leech.png"] = "res://STS2Portrait/portrait/events/brain_leech-portrait-v1.png",
        ["res://images/events/bugslayer.png"] = "res://STS2Portrait/portrait/events/bugslayer-portrait-v1.png",
        ["res://images/events/byrdonis_nest.png"] = "res://STS2Portrait/portrait/events/byrdonis_nest-portrait-v1.png",
        ["res://images/events/colorful_philosophers.png"] = "res://STS2Portrait/portrait/events/colorful_philosophers-portrait-v1.png",
        ["res://images/events/colossal_flower.png"] = "res://STS2Portrait/portrait/events/colossal_flower-portrait-v1.png",
        ["res://images/events/crystal_sphere.png"] = "res://STS2Portrait/portrait/events/crystal_sphere-portrait-v1.png",
        ["res://images/events/dense_vegetation.png"] = "res://STS2Portrait/portrait/events/dense_vegetation-portrait-v1.png",
        ["res://images/events/doll_room.png"] = "res://STS2Portrait/portrait/events/doll_room-portrait-v1.png",
        ["res://images/events/doors_of_light_and_dark.png"] = "res://STS2Portrait/portrait/events/doors_of_light_and_dark-portrait-v1.png",
        ["res://images/events/drowning_beacon.png"] = "res://STS2Portrait/portrait/events/drowning_beacon-portrait-v1.png",
        ["res://images/events/endless_conveyor.png"] = "res://STS2Portrait/portrait/events/endless_conveyor-portrait-v1.png",
        ["res://images/events/field_of_man_sized_holes.png"] = "res://STS2Portrait/portrait/events/field_of_man_sized_holes-portrait-v1.png",
        ["res://images/events/grave_of_the_forgotten.png"] = "res://STS2Portrait/portrait/events/grave_of_the_forgotten-portrait-v1.png",
        ["res://images/events/hungry_for_mushrooms.png"] = "res://STS2Portrait/portrait/events/hungry_for_mushrooms-portrait-v1.png",
        ["res://images/events/infested_automaton.png"] = "res://STS2Portrait/portrait/events/infested_automaton-portrait-v1.png",
        ["res://images/events/jungle_maze_adventure.png"] = "res://STS2Portrait/portrait/events/jungle_maze_adventure-portrait-v1.png",
        ["res://images/events/lost_wisp.png"] = "res://STS2Portrait/portrait/events/lost_wisp-portrait-v1.png",
        ["res://images/events/luminous_choir.png"] = "res://STS2Portrait/portrait/events/luminous_choir-portrait-v1.png",
        ["res://images/events/morphic_grove.png"] = "res://STS2Portrait/portrait/events/morphic_grove-portrait-v1.png",
        ["res://images/events/potion_courier.png"] = "res://STS2Portrait/portrait/events/potion_courier-portrait-v1.png",
        ["res://images/events/ranwid_the_elder.png"] = "res://STS2Portrait/portrait/events/ranwid_the_elder-portrait-v1.png",
        ["res://images/events/reflections.png"] = "res://STS2Portrait/portrait/events/reflections-portrait-v1.png",
        ["res://images/events/relic_trader.png"] = "res://STS2Portrait/portrait/events/relic_trader-portrait-v1.png",
        ["res://images/events/room_full_of_cheese.png"] = "res://STS2Portrait/portrait/events/room_full_of_cheese-portrait-v1.png",
        ["res://images/events/round_tea_party.png"] = "res://STS2Portrait/portrait/events/round_tea_party-portrait-v1.png",
        ["res://images/events/sapphire_seed.png"] = "res://STS2Portrait/portrait/events/sapphire_seed-portrait-v1.png",
        ["res://images/events/self_help_book.png"] = "res://STS2Portrait/portrait/events/self_help_book-portrait-v1.png",
        ["res://images/events/slippery_bridge.png"] = "res://STS2Portrait/portrait/events/slippery_bridge-portrait-v1.png",
        ["res://images/events/spiraling_whirlpool.png"] = "res://STS2Portrait/portrait/events/spiraling_whirlpool-portrait-v1.png",
        ["res://images/events/spirit_grafter.png"] = "res://STS2Portrait/portrait/events/spirit_grafter-portrait-v1.png",
        ["res://images/events/stone_of_all_time.png"] = "res://STS2Portrait/portrait/events/stone_of_all_time-portrait-v1.png",
        ["res://images/events/sunken_statue.png"] = "res://STS2Portrait/portrait/events/sunken_statue-portrait-v1.png",
        ["res://images/events/sunken_treasury.png"] = "res://STS2Portrait/portrait/events/sunken_treasury-portrait-v1.png",
        ["res://images/events/symbiote.png"] = "res://STS2Portrait/portrait/events/symbiote-portrait-v1.png",
        ["res://images/events/tablet_of_truth.png"] = "res://STS2Portrait/portrait/events/tablet_of_truth-portrait-v1.png",
        ["res://images/events/tea_master.png"] = "res://STS2Portrait/portrait/events/tea_master-portrait-v1.png",
        ["res://images/events/the_future_of_potions.png"] = "res://STS2Portrait/portrait/events/the_future_of_potions-portrait-v1.png",
        ["res://images/events/the_legends_were_true.png"] = "res://STS2Portrait/portrait/events/the_legends_were_true-portrait-v1.png",
        ["res://images/events/this_or_that.png"] = "res://STS2Portrait/portrait/events/this_or_that-portrait-v1.png",
        ["res://images/events/tinker_time.png"] = "res://STS2Portrait/portrait/events/tinker_time-portrait-v1.png",
        ["res://images/events/trash_heap.png"] = "res://STS2Portrait/portrait/events/trash_heap-portrait-v1.png",
        ["res://images/events/trial.png"] = "res://STS2Portrait/portrait/events/trial-portrait-v1.png",
        ["res://images/events/trial_started.png"] = "res://STS2Portrait/portrait/events/trial_started-portrait-v1.png",
        ["res://images/events/unrest_site.png"] = "res://STS2Portrait/portrait/events/unrest_site-portrait-v1.png",
        ["res://images/events/war_historian_repy.png"] = "res://STS2Portrait/portrait/events/war_historian_repy-portrait-v1.png",
        ["res://images/events/waterlogged_scriptorium.png"] = "res://STS2Portrait/portrait/events/waterlogged_scriptorium-portrait-v1.png",
        ["res://images/events/welcome_to_wongos.png"] = "res://STS2Portrait/portrait/events/welcome_to_wongos-portrait-v1.png",
        ["res://images/events/wellspring.png"] = "res://STS2Portrait/portrait/events/wellspring-portrait-v1.png",
        ["res://images/events/whispering_hollow.png"] = "res://STS2Portrait/portrait/events/whispering_hollow-portrait-v1.png",
        ["res://images/events/wood_carvings.png"] = "res://STS2Portrait/portrait/events/wood_carvings-portrait-v1.png",
        ["res://images/events/zen_weaver.png"] = "res://STS2Portrait/portrait/events/zen_weaver-portrait-v1.png",
        ["res://images/events/zen_weaver_phobia_mode.png"] = "res://STS2Portrait/portrait/events/zen_weaver_phobia_mode-portrait-v1.png",
    };

    private static readonly ConditionalWeakTable<NEventLayout, LayoutState> States = new();
    private static readonly ConditionalWeakTable<NCrystalSphereScreen, CrystalLayoutState> CrystalStates = new();

    private static CrystalLayoutState? CrystalOwner(Node node)
    {
        for (Node? owner = node; owner != null; owner = owner.GetParent())
            if (owner is NCrystalSphereScreen screen && CrystalStates.TryGetValue(screen, out CrystalLayoutState? state))
                return state;
        return null;
    }

    private static LayoutState? Owner(Node node)
    {
        for (Node? current = node; current != null; current = current.GetParent())
            if (current is NEventLayout layout && States.TryGetValue(layout, out LayoutState? state))
                return state;
        return null;
    }

    public static void Postfix(object __instance, MethodBase __originalMethod)
    {
        if (Entry.IsDisabled)
            return;
        if (__instance is NAncientBgContainer background)
        {
            Owner(background)?.Queue();
            return;
        }
        if (__instance is NCrystalSphereScreen crystal)
        {
            if (__originalMethod.Name == "_Ready")
                CrystalStates.GetValue(crystal, screen => new CrystalLayoutState(screen)).Apply();
            else if (CrystalStates.TryGetValue(crystal, out CrystalLayoutState? state))
                state.Queue();
            return;
        }
        if (__instance is NCrystalSphereDialogue dialogue)
        {
            CrystalOwner(dialogue)?.ArrangeDialogue(captureNative: true);
            return;
        }
        if (__instance is NProceedButton proceed)
        {
            CrystalOwner(proceed)?.ArrangeProceed();
            return;
        }
        if (__instance is TheArchitect architect && architect.Node is NCombatEventLayout architectLayout)
        {
            if (States.TryGetValue(architectLayout, out LayoutState? architectState))
                architectState.SetArchitectSpeech((NSpeechBubbleVfx?)AccessTools.Field(typeof(TheArchitect), "_speechBubble").GetValue(architect));
            return;
        }
        if (__instance is NEventLayout layout && (layout.GetType() == typeof(NEventLayout) ||
            layout.GetType() == typeof(NAncientEventLayout) || layout.GetType() == typeof(NCombatEventLayout)))
        {
            // Derived Ready must finish resolving its own fields before wrapping.
            if (__originalMethod.Name == "_Ready" && __originalMethod.DeclaringType == layout.GetType())
            {
                // Ready has resolved the native fields, but SetupLayout has not
                // created options yet. Wrap their common parent synchronously here.
                States.GetValue(layout, value => new LayoutState(value)).Apply();
            }
            else if (States.TryGetValue(layout, out LayoutState? state))
            {
                if (__originalMethod.Name is "AddOptions" or "SetDescription" or "SetDialogue" or "ClearDialogue" or "SetDialogueLineAndAnimate")
                    state.BeginPage();
                else if (__originalMethod.Name == "HideEventVisuals")
                    state.Apply();
                else
                    state.Queue();
            }
        }
        else if (__instance is NEventOptionButton button && __originalMethod.Name == "AnimateIn")
            Owner(button)?.WatchEntrance(button);
    }

    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        List<CodeInstruction> body = instructions.ToList();
        if (__originalMethod.DeclaringType != typeof(NAncientEventLayout) || __originalMethod.Name != "SetDialogueLineAndAnimate")
            return body;
        // Preserve the native tween and delayed focus callback; only its endpoint
        // changes because portrait uses a scrollable transcript instead of clipping.
        MethodInfo conversion = AccessTools.Method(typeof(Variant), "op_Implicit", new[] { typeof(Vector2) });
        int[] matches = body.Select((item, index) => (item, index))
            .Where(pair => pair.item.Calls(conversion)).Select(pair => pair.index).ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException($"Expected one ancient content tween vector, found {matches.Length}.");
        CodeInstruction owner = new(OpCodes.Ldarg_0);
        owner.labels.AddRange(body[matches[0]].labels);
        body[matches[0]].labels.Clear();
        body.InsertRange(matches[0], new[]
        {
            owner, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(PortraitEventPatch), nameof(AncientContentDestination))),
        });
        return body;
    }

    private static Vector2 AncientContentDestination(Vector2 native, NAncientEventLayout layout) =>
        !Entry.IsDisabled && PortraitViewportPatch.IsPortrait && States.TryGetValue(layout, out _)
            ? Vector2.Zero : native;

    internal static async Task AfterAncientNameIntro(NAncientNameBanner banner, Task original)
    {
        // The native task starts a final two-second move before it returns.
        // Wait for both native phases before moving the original title, without
        // skipping signals or retaining an awaiter after the room exits.
        await original;
        if (!GodotObject.IsInstanceValid(banner) || !banner.IsInsideTree())
            return;
        if (await banner.Get("_moveTween").As<Tween>().AwaitFinished(banner))
            Owner(banner)?.SetNameBanner(banner);
    }

    private sealed class LayoutState
    {
        private static readonly string[] RichFonts =
        {
            "normal_font_size", "bold_font_size", "italics_font_size",
            "bold_italics_font_size", "mono_font_size",
        };
        private readonly NEventLayout _layout;
        private readonly TextureRect? _portrait;
        private readonly Control? _portraitFrame;
        private readonly TextureRect? _portraitBackdrop;
        private readonly NAncientEventLayout? _ancient;
        private readonly NAncientBgContainer? _ancientBackground;
        private readonly NCombatEventLayout? _combat;
        private readonly Control _scrollParent;
        private readonly VBoxContainer? _dialogue;
        private readonly Node? _bodyParent;
        private readonly int _bodyIndex;
        private NSpeechBubbleVfx? _architectSpeech;
        private NAncientNameBanner? _nameBanner;
        private readonly Vector2 _portraitSize;
        private readonly VBoxContainer _flow;
        private readonly VBoxContainer _options;
        private readonly MegaLabel? _title;
        private readonly MegaRichTextLabel? _body;
        private readonly MegaLabel? _shared;
        private readonly Panel _surface;
        private readonly ScrollContainer _scroll;
        private readonly Control _content;
        private readonly Dictionary<Control, PortraitControlSnapshot> _geometry = new();
        private readonly Dictionary<(CanvasItem Node, string Name), Variant> _properties = new();
        private readonly Dictionary<(Control Node, string Name), (bool Had, int Value)> _fonts = new();
        private readonly Dictionary<(Control Node, string Name), (bool Had, int Value)> _constants = new();
        private readonly HashSet<NEventOptionButton> _rows = new();
        private readonly HashSet<NEventOptionButton> _entranceReady = new();
        private Vector2 _viewport;
        private bool _active;
        private bool _queued;
        private bool _applying;
        private bool _resetScroll;
        private bool _ancientPositionQueued;
        private bool _ancientArtActive;
        // Neow's body, mouth and eyes retain their native animation and bone children.
        private Node2D? _neowSpine;
        private Callable _neowEnvironmentFilter;
        private readonly Dictionary<GodotObject, Variant> _neowEnvironmentAttachments = new();

        public LayoutState(NEventLayout layout)
        {
            _layout = layout;
            _ancient = layout as NAncientEventLayout;
            _ancientBackground = _ancient?.Get("_ancientBgContainer").As<NAncientBgContainer>();
            _combat = layout as NCombatEventLayout;
            _portrait = layout.Get("_portrait").AsGodotObject() as TextureRect;
            _portraitSize = _portrait?.Size ?? Vector2.Zero;
            _scrollParent = _ancient?.Get("_contentContainer").As<Control>() ?? layout;
            _dialogue = _ancient?.Get("_dialogueContainer").As<VBoxContainer>();
            _options = layout.Get("_optionsContainer").As<VBoxContainer>();
            _flow = (VBoxContainer)_options.GetParent();
            _title = layout.Get("_title").AsGodotObject() as MegaLabel;
            _body = layout.Get("_description").AsGodotObject() as MegaRichTextLabel;
            _shared = layout.Get("_sharedEventLabel").AsGodotObject() as MegaLabel;
            _bodyParent = _body?.GetParent();
            _bodyIndex = _body?.GetIndex() ?? 0;
            if (_options.GetChildCount() != 0)
                throw new InvalidOperationException("Portrait event wrapping must precede native option creation.");
            foreach (Control node in new Control?[] { layout, _portrait, _flow, _options, _title, _body, _shared, _scrollParent, _dialogue }.OfType<Control>())
                Remember(node);

            if (_portrait != null)
            {
                // Wrap the passive art before native event setup adds its VFX.
                // Keeping the original texture node preserves portrait swaps.
                _portraitFrame = new Control
                {
                    Name = "PortraitEventArtFrame", Size = layout.Size,
                    MouseFilter = Control.MouseFilterEnum.Ignore,
                };
                layout.AddChild(_portraitFrame);
                layout.MoveChild(_portraitFrame, _portrait.GetIndex());
                _portrait.Reparent(_portraitFrame, false);
                _geometry[_portrait].Restore();
            }

            if (_combat == null)
            {
                // This passive full-page background draws below the unchanged native
                // portrait frame. Its original clip and animated children remain intact.
                _portraitBackdrop = new TextureRect
                {
                    Name = "PortraitEventBackdrop", Visible = false,
                    ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                    StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
                    MouseFilter = Control.MouseFilterEnum.Ignore,
                };
                layout.AddChild(_portraitBackdrop);
                layout.MoveChild(_portraitBackdrop, 0);
            }

            _surface = new Panel
            {
                Name = "PortraitEventSurface", Visible = false,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            // Reuse the native tiled hover-tip paper without changing reader geometry.
            Texture2D readerBackdrop = ResourceLoader.Load<Texture2D>("res://images/ui/hover_tip.png")
                ?? throw new InvalidOperationException("Native hover-tip background failed to load for the portrait event reader.");
            _surface.AddThemeStyleboxOverride("panel", new StyleBoxTexture
            {
                Texture = readerBackdrop,
                TextureMarginLeft = 55, TextureMarginTop = 43,
                TextureMarginRight = 91, TextureMarginBottom = 32,
                AxisStretchHorizontal = StyleBoxTexture.AxisStretchMode.Tile,
                AxisStretchVertical = StyleBoxTexture.AxisStretchMode.Tile,
                ContentMarginLeft = 0, ContentMarginTop = 0,
                ContentMarginRight = 0, ContentMarginBottom = 0,
            });
            _scroll = new ScrollContainer
            {
                Name = "PortraitEventScroll", Size = _scrollParent.Size,
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
                VerticalScrollMode = ScrollContainer.ScrollMode.Disabled,
                FollowFocus = true, ScrollDeadzone = 24,
                MouseFilter = Control.MouseFilterEnum.Ignore, ClipContents = false,
            };
            // A plain content Control preserves the native VBox's viewport-relative
            // anchors in landscape. A direct VBox child would be placed by ScrollContainer.
            _content = new Control
            {
                Name = "PortraitEventContent", Size = _scrollParent.Size,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ExpandFill,
                MouseFilter = Control.MouseFilterEnum.Pass,
            };
            _scrollParent.AddChild(_surface);
            _scrollParent.AddChild(_scroll);
            _scroll.AddChild(_content);
            _flow.Reparent(_content, false);
            _geometry[_flow].Restore();

            layout.Resized += Queue;
            _content.Resized += Queue;
            _flow.MinimumSizeChanged += Queue;
            if (_body != null)
                _body.MinimumSizeChanged += Queue;
            _scroll.ScrollStarted += () =>
            {
                // Native option release must not select after a transcript drag.
                foreach (NEventOptionButton row in _options.GetChildren().OfType<NEventOptionButton>())
                    row.Set("_isPressed", false);
            };
            Window window = layout.GetWindow();
            window.SizeChanged += Queue;
            layout.TreeExiting += () => window.SizeChanged -= Queue;
            layout.TreeExiting += RestoreNeowEnvironment;
        }

        public void SetArchitectSpeech(NSpeechBubbleVfx? bubble)
        {
            // Read the original formatted speech; do not reproduce dialogue logic.
            _architectSpeech = bubble;
            BeginPage();
        }

        public void Queue()
        {
            if (_queued || _applying)
                return;
            _queued = true;
            Callable.From(() =>
            {
                _queued = false;
                if (GodotObject.IsInstanceValid(_layout) && _layout.IsInsideTree())
                    Apply();
            }).CallDeferred();
        }

        public void BeginPage()
        {
            _resetScroll = true;
            // Native AddOptions deferred the entrance animation. Configure its
            // existing buttons before those animation destinations are captured.
            Apply();
        }

        public void SetNameBanner(NAncientNameBanner banner)
        {
            _nameBanner = banner;
            Queue();
        }

        public void WatchEntrance(NEventOptionButton row)
        {
            Tween? tween = row.Get("_animInTween").AsGodotObject() as Tween;
            if (tween != null && tween.IsValid() && tween.IsRunning())
            {
                // Native EnableButton is already first in this signal's delegate
                // list. Keep its timing, including the instant fast-mode path.
                tween.Finished += () => EntranceFinished(row);
            }
            else if (row.MouseFilter == Control.MouseFilterEnum.Stop)
                EntranceFinished(row);
        }

        private void EntranceFinished(NEventOptionButton row)
        {
            if (!GodotObject.IsInstanceValid(row))
                return;
            _entranceReady.Add(row);
            if (_active && !Entry.IsDisabled && PortraitViewportPatch.IsPortrait)
                row.MouseFilter = Control.MouseFilterEnum.Pass;
        }

        public void Apply()
        {
            if (_applying)
                return;
            _applying = true;
            try
            {
                bool portrait = !Entry.IsDisabled && PortraitViewportPatch.IsPortrait;
                Vector2 viewport = _layout.GetViewportRect().Size;
                bool coordinateChange = _active != portrait || _viewport != viewport;
                _viewport = viewport;
                if (coordinateChange)
                {
                    if (_ancient != null)
                    {
                        Tween? contentTween = _ancient.Get("_contentTween").AsGodotObject() as Tween;
                        if (contentTween != null && contentTween.IsValid() && contentTween.IsRunning())
                            contentTween.FastForwardToCompletion();
                    }
                    foreach (NEventOptionButton row in _options.GetChildren().OfType<NEventOptionButton>())
                    {
                        Tween? tween = row.Get("_animInTween").AsGodotObject() as Tween;
                        if (tween != null && tween.IsValid() && tween.IsRunning())
                            tween.FastForwardToCompletion();
                    }
                }
                if (!portrait)
                {
                    if (_active)
                        Restore();
                    if (_portraitFrame != null)
                    {
                        // Native centered anchors need the original layout space.
                        _portraitFrame.ClipContents = false;
                        Place(_portraitFrame, Vector2.Zero, _layout.Size, false);
                    }
                    // Resizes may arrive in several steps. The wrapper's native
                    // coordinate space must track the final landscape viewport too.
                    Place(_scroll, Vector2.Zero, _scrollParent.Size, false);
                    _content.CustomMinimumSize = Vector2.Zero;
                    _content.Size = _scrollParent.Size;
                    if (_ancient != null && coordinateChange)
                        QueueAncientPosition();
                    return;
                }

                _active = true;
                // Native ancient content grows upward when a new option raises its
                // minimum height. Portrait transcripts keep a stable top edge.
                if (_ancient != null)
                    Saved(_flow, "grow_vertical", (int)Control.GrowDirection.End);
                Place(_layout, Vector2.Zero, viewport);
                if (_combat?.HasCombatStarted == true)
                {
                    // The embedded combat now owns input; no event shell may cover it.
                    _surface.Hide();
                    _scroll.Hide();
                    return;
                }
                _scroll.Show();
                _portraitBackdrop?.Hide();
                bool expandedBackdrop = false;
                TextureRect? nativeBackdrop = null;
                string? portraitResource = null;
                float readingTop = Math.Min(720, viewport.Y * 0.4f);
                if (_portrait != null)
                {
                    // Keep the reading area unchanged while framing the left-hand
                    // subject at twice the former scale without distorting the art.
                    float artHeight = _portraitSize.Y * 984 / _portraitSize.X;
                    float artScale = 1968 / _portraitSize.X;
                    readingTop = 336 + artHeight + 24;
                    _portraitFrame!.ClipContents = true;
                    Place(_portraitFrame, new Vector2(48, 336), new Vector2(984, artHeight), false);
                    Place(_portrait, new Vector2(0, (artHeight - _portraitSize.Y * artScale) * 0.5f), _portraitSize);
                    _portrait.PivotOffset = Vector2.Zero;
                    _portrait.Scale = Vector2.One * artScale;
                    Saved(_portrait, "mouse_filter", (int)Control.MouseFilterEnum.Ignore);
                    // Resolve from the untouched native texture, including Trial/Phobia swaps.
                    if (_portraitBackdrop != null && _portrait.Texture != null &&
                        PortraitEventResources.TryGetValue(_portrait.Texture.ResourcePath, out portraitResource))
                        nativeBackdrop = _portrait;

                }
                if (_ancientBackground?.GetChildren().OfType<Control>()
                    .SingleOrDefault(scene => scene.Name == "Neow") is Control neow)
                {
                    // Neow is a layered native 1920x1080 scene, not an event banner.
                    // Transform its whole canvas so the face, water and lights agree.
                    Place(_ancientBackground, Vector2.Zero, viewport);
                    _ancientBackground.PivotOffset = Vector2.Zero;
                    _ancientBackground.Scale = Vector2.One;
                    // The portrait waterfall meets the pool near the viewport middle.
                    // Keep the native face above that waterline at normal phone ratios.
                    float neowTop = viewport.Y * 0.5f - 384;
                    Place(neow, new Vector2(48, neowTop), new Vector2(1920, 1080));
                    neow.PivotOffset = Vector2.Zero;
                    neow.Scale = Vector2.One * (984f / 1920f);
                    Saved(neow, "clip_contents", true);
                    Saved(neow, "mouse_filter", (int)Control.MouseFilterEnum.Ignore);
                    Saved(_ancientBackground, "mouse_filter", (int)Control.MouseFilterEnum.Ignore);
                    readingTop = neowTop + 1080 * (984f / 1920f) + 24;
                    _ancientArtActive = true;
                }
                TextureRect? nativeAncientBackdrop = _ancientBackground?
                    .FindChildren("*", "TextureRect", true, false).OfType<TextureRect>()
                    .SingleOrDefault(node => node.Name.ToString().Contains("TextureRect", StringComparison.Ordinal) &&
                        System.IO.Path.GetFileNameWithoutExtension(node.Texture?.ResourcePath) is
                            "neow_bg" or "darv_placeholder" or "nonupeipe_placeholder" or
                            "orobas_placeholder" or "pael_placeholder" or "tanx_placeholder" or "vakuu_placeholder");
                string? portraitAncientResource = System.IO.Path.GetFileNameWithoutExtension(nativeAncientBackdrop?.Texture?.ResourcePath) switch
                {
                    "neow_bg" => "res://STS2Portrait/portrait/neow-cave-portrait-v1.png",
                    "darv_placeholder" => "res://STS2Portrait/portrait/rooms/darv_placeholder-portrait-v1.png",
                    "nonupeipe_placeholder" => "res://STS2Portrait/portrait/rooms/nonupeipe_placeholder-portrait-v1.png",
                    "orobas_placeholder" => "res://STS2Portrait/portrait/rooms/orobas_placeholder-portrait-v1.png",
                    "pael_placeholder" => "res://STS2Portrait/portrait/rooms/pael_placeholder-portrait-v1.png",
                    "tanx_placeholder" => "res://STS2Portrait/portrait/rooms/tanx_placeholder-portrait-v1.png",
                    "vakuu_placeholder" => "res://STS2Portrait/portrait/rooms/vakuu_placeholder-portrait-v1.png",
                    _ => null,
                };
                if (nativeAncientBackdrop != null && portraitAncientResource != null)
                {
                    nativeBackdrop = nativeAncientBackdrop;
                    portraitResource = portraitAncientResource;
                }
                if (_portraitBackdrop != null && nativeBackdrop != null && portraitResource != null)
                {
                    // Static portrait art remains under native actors. Reuse the same
                    // material instance so native reflection parameters keep updating.
                    _portraitBackdrop.Material = nativeBackdrop.Material;
                    if (_portraitBackdrop.Texture?.ResourcePath != portraitResource)
                        _portraitBackdrop.Texture = ResourceLoader.Load<Texture2D>(portraitResource)
                            ?? throw new InvalidOperationException($"Portrait event background failed to load: {portraitResource}");
                    Place(_portraitBackdrop, Vector2.Zero, viewport, false);
                    _portraitBackdrop.Show();
                    // Ancient static textures are leaves; their native VFX are siblings.
                    // Ordinary portraits retain child VFX and hide only their own draw.
                    if (nativeBackdrop == nativeAncientBackdrop)
                        Saved(nativeBackdrop, "visible", false);
                    else
                        Saved(nativeBackdrop, "self_modulate", Colors.Transparent);
                    expandedBackdrop = true;
                    if (nativeBackdrop == nativeAncientBackdrop &&
                        System.IO.Path.GetFileNameWithoutExtension(nativeBackdrop.Texture?.ResourcePath) == "neow_bg" &&
                        nativeBackdrop.GetParent() is Control neowArt && neowArt.Name == "Neow")
                    {
                        // The portrait environment is continuous; remove the horizontal cut
                        // and proven passive water/vignette leaves, never the actor parent.
                        Saved(neowArt, "clip_contents", false);
                        foreach (CanvasItem leaf in neowArt.GetChildren().OfType<CanvasItem>())
                            if (leaf.GetChildCount() == 0 &&
                                (leaf is TextureRect vignette &&
                                 System.IO.Path.GetFileNameWithoutExtension(vignette.Texture?.ResourcePath) == "neow_vignette" ||
                                 leaf is Sprite2D && leaf.Name.ToString() is
                                     "water effect" or "water effect2" or "water effect3" or
                                     "water_reflection" or "water_reflection2" or "water_reflection3" or "water_reflection4"))
                                Saved(leaf, "visible", false);
                        if (_neowSpine == null)
                        {
                            _neowSpine = neowArt.GetChildren().OfType<Node2D>()
                                .Single(node => node.GetClass() == "SpineSprite" &&
                                    node.Name.ToString().Contains("Spine", StringComparison.Ordinal));
                            // Native attachment timelines run first; remove only their environment draw references.
                            _neowEnvironmentFilter = Callable.From<GodotObject>(_ => ClearNeowEnvironment());
                            _neowSpine.Connect("before_world_transforms_change", _neowEnvironmentFilter);
                        }
                    }
                }
                if (_nameBanner != null && GodotObject.IsInstanceValid(_nameBanner))
                {
                    // Title is a child of Epithet in the native banner. Keep that
                    // tree and the finished intro style, but reserve separate lines
                    // at the foot of the art instead of sharing the Continue dock.
                    MegaLabel epithet = _nameBanner.Get("_epithetLabel").As<MegaLabel>();
                    MegaRichTextLabel title = _nameBanner.Get("_titleLabel").As<MegaRichTextLabel>();
                    Remember(epithet);
                    Remember(title);
                    Font(epithet, "font_size", 30);
                    Font(title, "normal_font_size", 48);
                    // Native settled Epithet fades its child title as well; keep both readable over portrait art.
                    // Reuse the native reward outline and existing snapshots after the full intro finishes.
                    Saved(epithet, "modulate", Colors.White);
                    Saved(epithet, "theme_override_colors/font_outline_color", StsColors.rewardLabelOutline);
                    Saved(title, "theme_override_colors/font_outline_color", StsColors.rewardLabelOutline);
                    Constant(epithet, "outline_size", 2);
                    Constant(title, "outline_size", 4);
                    Saved(epithet, "vertical_alignment", (int)VerticalAlignment.Center);
                    Saved(title, "vertical_alignment", (int)VerticalAlignment.Center);
                    Place(_nameBanner, new Vector2(72, readingTop - 168), new Vector2(936, 120));
                    Place(epithet, new Vector2(0, 72), new Vector2(936, 48));
                    Place(title, new Vector2(0, -72), new Vector2(936, 72));
                }
                if (_ancient != null)
                {
                    bool lastLine = _ancient.Get("IsDialogueOnLastLine").AsBool();
                    LayoutAncient(viewport, lastLine);
                }
                else
                {
                    Place(_surface, new Vector2(48, readingTop), new Vector2(984, viewport.Y - readingTop - 48), false);
                    Place(_scroll, new Vector2(72, readingTop + 24), new Vector2(936, viewport.Y - readingTop - 96), false);
                }
                // Expanded event art keeps the native text-on-scene presentation.
                // Ancient speech bubbles and option paper already supply their surfaces.
                _surface.Visible = _ancient == null && !expandedBackdrop;
                if (_combat != null && _body != null && _body.GetParent() != _flow)
                {
                    // Only the passive description moves; live option nodes retain parents.
                    _body.Reparent(_flow, false);
                    _flow.MoveChild(_body, 0);
                }
                if (_architectSpeech != null && GodotObject.IsInstanceValid(_architectSpeech) && _body != null)
                {
                    Saved(_architectSpeech, "visible", false);
                    Saved(_body, "text", _architectSpeech.Get("_text").AsString());
                }
                _scroll.VerticalScrollMode = ScrollContainer.ScrollMode.ShowNever;
                _scroll.MouseFilter = Control.MouseFilterEnum.Stop;
                _scroll.ClipContents = true;

                // Width is authoritative before fonts, wrapping and FitContent.
                // Never save a minimum computed at a placeholder's initial width.
                const float width = 936;
                _content.Size = new Vector2(width, Math.Max(1, _content.Size.Y));
                _flow.CustomMinimumSize = Vector2.Zero;
                _flow.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
                // Preserve a running portrait entrance, but never retain a native
                // negative transcript offset after changing coordinate systems.
                _flow.Position = _ancient == null || coordinateChange ? Vector2.Zero : new Vector2(0, _flow.Position.Y);
                _flow.Size = new Vector2(width, Math.Max(1, _flow.Size.Y));
                Saved(_flow, "mouse_filter", (int)Control.MouseFilterEnum.Pass);
                Saved(_options, "mouse_filter", (int)Control.MouseFilterEnum.Pass);
                _options.CustomMinimumSize = Vector2.Zero;
                _options.Size = new Vector2(width, Math.Max(1, _options.Size.Y));
                Constant(_flow, "separation", 24);
                Constant(_options, "separation", 24);
                ConfigureLabel(_title, width, 54);
                if (expandedBackdrop && _ancient == null && _title != null)
                {
                    // Reuse the native reward outline over bright event art.
                    Saved(_title, "theme_override_colors/font_outline_color", StsColors.rewardLabelOutline);
                    Constant(_title, "outline_size", 4);
                }
                ConfigureLabel(_shared, width, 48);
                ConfigureRichText(_body, width);
                if (_body != null)
                {
                    Saved(_body, "horizontal_alignment", (int)HorizontalAlignment.Left);
                    Saved(_body, "vertical_alignment", (int)VerticalAlignment.Top);
                    Saved(_body, "mouse_filter", (int)Control.MouseFilterEnum.Pass);
                }

                foreach (NEventOptionButton row in _options.GetChildren().OfType<NEventOptionButton>())
                    ConfigureRow(row, width);
                float height = _flow.GetCombinedMinimumSize().Y;
                _flow.Size = new Vector2(width, height);
                _content.CustomMinimumSize = new Vector2(0, height);
                if (_ancient != null)
                {
                    // Size short pages to their actual transcript/options. The last
                    // native Proceed stays near the thumb and leaves result VFX clear.
                    bool lastLine = _ancient.Get("IsDialogueOnLastLine").AsBool();
                    float bottom = viewport.Y - (lastLine ? 48 : 216);
                    float panelHeight = Math.Min(height + 48, bottom - readingTop);
                    Saved(_scrollParent, "grow_vertical", (int)Control.GrowDirection.End);
                    Place(_scrollParent, new Vector2(48, bottom - panelHeight), new Vector2(984, panelHeight));
                    if (_nameBanner != null && GodotObject.IsInstanceValid(_nameBanner)
                        && panelHeight < bottom - readingTop)
                    {
                        // Group the native name with a fitted short transcript; full-height pages keep their art boundary.
                        Place(_nameBanner, new Vector2(72, _scrollParent.Position.Y - 144), new Vector2(936, 120));
                    }
                    Place(_surface, Vector2.Zero, _scrollParent.Size, false);
                    Place(_scroll, new Vector2(24, 24), _scrollParent.Size - new Vector2(48, 48), false);
                }
                else if (expandedBackdrop)
                {
                    // The full-page art never determines readerTop. Short text/options
                    // use their real flow height; long pages retain the existing scroll.
                    float panelHeight = Math.Min(height + 48, viewport.Y - readingTop - 48);
                    Place(_surface, new Vector2(48, readingTop), new Vector2(984, panelHeight), false);
                    Place(_scroll, new Vector2(72, readingTop + 24), new Vector2(936, panelHeight - 48), false);
                }
                if (_ancient != null && coordinateChange)
                    QueueAncientPosition();
                if (_resetScroll)
                {
                    _scroll.ScrollVertical = 0;
                    _resetScroll = false;
                    if (_ancient != null)
                        QueueAncientPosition();
                }
            }
            finally
            {
                _applying = false;
            }
        }

        private void ConfigureLabel(MegaLabel? label, float width, int size)
        {
            if (label == null)
                return;
            Remember(label);
            Saved(label, "AutoSizeEnabled", false);
            label.CustomMinimumSize = Vector2.Zero;
            label.Size = new Vector2(width, Math.Max(1, label.Size.Y));
            Saved(label, "autowrap_mode", (int)TextServer.AutowrapMode.WordSmart);
            Saved(label, "mouse_filter", (int)Control.MouseFilterEnum.Ignore);
            Font(label, "font_size", size);
        }

        private void ConfigureRichText(MegaRichTextLabel? label, float width)
        {
            if (label == null)
                return;
            Remember(label);
            Saved(label, "AutoSizeEnabled", false);
            label.CustomMinimumSize = Vector2.Zero;
            label.Size = new Vector2(width, Math.Max(1, label.Size.Y));
            foreach (string font in RichFonts)
                Font(label, font, 48);
            Saved(label, "scroll_active", false);
            Saved(label, "fit_content", true);
        }

        private void ConfigureRow(NEventOptionButton row, float width)
        {
            Remember(row);
            MegaRichTextLabel label = row.Get("_label").As<MegaRichTextLabel>();
            if (_rows.Add(row))
            {
                label.MinimumSizeChanged += Queue;
                row.CustomMinimumSize = Vector2.Zero;
            }
            Saved(row, "_ignoreDragThreshold", 24f);
            if (row.IsEnabled && (_entranceReady.Contains(row) || (_ancient != null && row.MouseFilter == Control.MouseFilterEnum.Stop)))
                row.MouseFilter = Control.MouseFilterEnum.Pass;
            row.Size = new Vector2(width, Math.Max(168, row.Size.Y));
            row.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            bool ancientRow = row.Event is MegaCrit.Sts2.Core.Models.AncientEventModel;
            bool proceed = ancientRow && row.Option.IsProceed;
            HBoxContainer? labelRow = ancientRow ? (HBoxContainer)label.GetParent() : null;
            float labelWidth = width - (ancientRow && !proceed ? 124 : 48);
            if (labelRow != null)
            {
                Place(labelRow, new Vector2(24, 24), new Vector2(width - 48, 1));
                Saved(labelRow, "mouse_filter", (int)Control.MouseFilterEnum.Ignore);
                Saved(label, "size_flags_horizontal", (int)Control.SizeFlags.ExpandFill);
            }
            ConfigureRichText(label, labelWidth);
            if (proceed)
                Saved(label, "horizontal_alignment", (int)HorizontalAlignment.Center);
            bool shared = row.Event.IsShared && row.Event.Owner!.RunState.Players.Count > 1;
            float textHeight = label.GetContentHeight();
            float height = Math.Max(proceed ? 144 : 168, textHeight + 48 + (shared ? 48 : 0));
            Vector2 minimum = new(0, height);
            if (row.CustomMinimumSize != minimum)
                row.CustomMinimumSize = minimum;
            row.Size = new Vector2(width, height);
            row.PivotOffset = row.Size * 0.5f;
            if (labelRow == null)
                Place(label, new Vector2(24, 24), new Vector2(width - 48, textHeight));
            else
            {
                labelRow.Size = new Vector2(width - 48, textHeight);
                if (proceed)
                    labelRow.Position = new Vector2(24, (height - textHeight) * 0.5f);
            }
            Saved(label, "mouse_filter", (int)Control.MouseFilterEnum.Ignore);
            Place(row.VoteContainer, new Vector2(24, height - 48), new Vector2(width - 48, 48));

            // Preserve shader/glow objects and original vote children. Only the
            // ordinary background/border rectangles need the row's new bounds.
            foreach (Control child in row.GetChildren().OfType<Control>())
            {
                if (child is NinePatchRect && child.Name.ToString() is ("Image" or "Outline" or "Shadow"))
                {
                    Place(child, Vector2.Zero, row.Size);
                    child.PivotOffset = row.Size * 0.5f;
                }
                if (child is NinePatchRect)
                    Saved(child, "mouse_filter", (int)Control.MouseFilterEnum.Ignore);
            }
        }

        private void LayoutAncient(Vector2 viewport, bool lastLine)
        {
            // Native dialogue stepping still controls visibility and enable timing.
            // Keep the click target at the bottom, so reading drags cannot advance it.
            NAncientDialogueHitbox hitbox = _ancient!.Get("_dialogueHitbox").As<NAncientDialogueHitbox>();
            Control nextContainer = _ancient.Get("_fakeNextButtonContainer").As<Control>();
            Control nextButton = _ancient.Get("_fakeNextButton").As<Control>();
            Place(hitbox, new Vector2(48, viewport.Y - 192), new Vector2(984, 144));
            Place(nextContainer, hitbox.Position, hitbox.Size);
            Place(nextButton, Vector2.Zero, hitbox.Size);
            foreach (Control decoration in nextContainer.FindChildren("*", "Control", true, false).OfType<Control>())
                Saved(decoration, "mouse_filter", (int)Control.MouseFilterEnum.Ignore);
            MegaLabel nextLabel = _ancient.Get("_fakeNextButtonLabel").As<MegaLabel>();
            ConfigureLabel(nextLabel, 840, 48);
            Saved(nextLabel, "horizontal_alignment", (int)HorizontalAlignment.Center);
            Saved(nextLabel, "size_flags_horizontal", (int)Control.SizeFlags.ExpandFill);
            Saved(hitbox, "_ignoreDragThreshold", 24f);
            Saved(_options, "visible", lastLine);
            Saved(_options, "size_flags_horizontal", (int)Control.SizeFlags.ExpandFill);
            // Empty native dialogue containers must not reserve a blank row on Done.
            Saved(_dialogue!, "visible", _dialogue!.GetChildCount() > 0);
            Saved(_dialogue!, "size_flags_horizontal", (int)Control.SizeFlags.ExpandFill);
            Saved(_dialogue!, "mouse_filter", (int)Control.MouseFilterEnum.Pass);
            Constant(_dialogue!, "separation", 24);
            int current = _ancient.Get("_currentDialogueLine").AsInt32();
            int index = 0;
            foreach (NAncientDialogueLine line in _dialogue!.GetChildren().OfType<NAncientDialogueLine>())
            {
                Remember(line);
                Saved(line, "visible", index++ <= current);
                Saved(line, "mouse_filter", (int)Control.MouseFilterEnum.Pass);
                Saved(line, "size_flags_horizontal", (int)Control.SizeFlags.ExpandFill);
                line.CustomMinimumSize = Vector2.Zero;
                line.Size = new Vector2(936, Math.Max(1, line.Size.Y));
                MegaRichTextLabel text = line.FindChildren("*", "RichTextLabel", true, false).OfType<MegaRichTextLabel>().Single();
                foreach (MarginContainer margin in line.FindChildren("*", "MarginContainer", true, false).OfType<MarginContainer>())
                {
                    Remember(margin);
                    margin.CustomMinimumSize = Vector2.Zero;
                    Saved(margin, "size_flags_horizontal", (int)Control.SizeFlags.ExpandFill);
                }
                ConfigureRichText(text, 800);
                Saved(text, "autowrap_mode", (int)TextServer.AutowrapMode.WordSmart);
                Saved(text, "mouse_filter", (int)Control.MouseFilterEnum.Ignore);
                line.CustomMinimumSize = new Vector2(0, Math.Max(96, text.GetContentHeight() + 24));
            }
        }

        private void QueueAncientPosition()
        {
            if (_ancientPositionQueued)
                return;
            _ancientPositionQueued = true;
            TaskHelper.RunSafely(SettleAncientPosition());
        }

        private async Task SettleAncientPosition()
        {
            // Container layout is deferred. Read the current native line geometry
            // after one frame; never replay dialogue/SFX merely to restore position.
            await _layout.ToSignal(_layout.GetTree(), SceneTree.SignalName.ProcessFrame);
            _ancientPositionQueued = false;
            if (!GodotObject.IsInstanceValid(_layout) || !_layout.IsInsideTree())
                return;
            int current = _ancient!.Get("_currentDialogueLine").AsInt32();
            NAncientDialogueLine? line = _dialogue!.GetChildOrNull<NAncientDialogueLine>(current);
            if (_active && !Entry.IsDisabled && PortraitViewportPatch.IsPortrait)
            {
                _scroll.ScrollVertical = line == null ? 0 : Math.Max(0, (int)line.Position.Y);
                return;
            }
            bool lastLine = _ancient.Get("IsDialogueOnLastLine").AsBool();
            Control next = _ancient.Get("_fakeNextButtonContainer").As<Control>();
            float height = lastLine ? _ancient.Get("_originalContentContainerHeight").AsSingle()
                : next.GlobalPosition.Y - _scrollParent.GlobalPosition.Y;
            _scrollParent.Size = new Vector2(_scrollParent.Size.X, height);
            _scroll.Size = _scrollParent.Size;
            _content.Size = _scrollParent.Size;
            float bottom = line == null ? 0 : line.Position.Y + line.Size.Y;
            if (lastLine)
                bottom += _options.Size.Y + 10;
            _flow.Position = new Vector2(_flow.Position.X, height - bottom);
        }

        private void ClearNeowEnvironment()
        {
            if (!_active || Entry.IsDisabled || !PortraitViewportPatch.IsPortrait)
                return;
            // This native signal is emitted only after the skeleton is loaded and animation apply has completed.
            if (_neowEnvironmentAttachments.Count == 0)
            {
                GodotObject skeleton = _neowSpine!.Call("get_skeleton").AsGodotObject();
                foreach (Variant value in skeleton.Call("get_slots").AsGodotArray())
                {
                    GodotObject slot = value.AsGodotObject();
                    string name = slot.Call("get_data").AsGodotObject().Call("get_name").AsString();
                    // Exact native 4.2.43 environment names; all five actor slots are excluded.
                    if (name is "rocks" or "rock top" or "r waterfalls" or "l waterfall" or
                        "wave left 1" or "wave r 1" or "waterfalls2" or "waterfall D" or
                        "waterfall A" or "waterfall B" or "waterfall C" or "waterfalls" or
                        "wave top 1" or "darkwater" or "main waterfall highlight" or
                        "Layer 1" or "Layer 4" or "mult" or "screen" or "Layer 6" or
                        "Layer 7" or "Layer 5" or "Layer 8" or "Layer 2" or "Layer 3" or
                        "waterfall highlight A" or "waterfall highlight B" or "waterfall highlight C" or
                        "waterfall highlight D" or "waterfall highlight E" or "waterfall highlight F")
                        _neowEnvironmentAttachments.Add(slot, slot.Call("get_attachment"));
                }
            }
            // Null clears only the current draw reference; no native attachment is destroyed.
            foreach (GodotObject slot in _neowEnvironmentAttachments.Keys)
                slot.Call("set_attachment", default(Variant));
        }

        private void RestoreNeowEnvironment()
        {
            // Disconnect first, then restore native references before landscape geometry or TreeExit.
            if (_neowSpine != null && GodotObject.IsInstanceValid(_neowSpine) &&
                _neowSpine.IsConnected("before_world_transforms_change", _neowEnvironmentFilter))
                _neowSpine.Disconnect("before_world_transforms_change", _neowEnvironmentFilter);
            foreach (var entry in _neowEnvironmentAttachments)
                if (GodotObject.IsInstanceValid(entry.Key))
                    entry.Key.Call("set_attachment", entry.Value);
            _neowEnvironmentAttachments.Clear();
            _neowSpine = null;
        }

        private void Restore()
        {
            _active = false;
            RestoreNeowEnvironment();
            _portraitBackdrop?.Hide();
            _scroll.Show();
            if (_combat != null && _body != null && _body.GetParent() != _bodyParent)
            {
                _body.Reparent(_bodyParent!, false);
                _bodyParent!.MoveChild(_body, _bodyIndex);
            }
            _surface.Hide();
            _scroll.VerticalScrollMode = ScrollContainer.ScrollMode.Disabled;
            _scroll.ScrollVertical = 0;
            _scroll.MouseFilter = Control.MouseFilterEnum.Ignore;
            _scroll.ClipContents = false;
            // Restore font/fit and geometry while automatic font fitting is still
            // off. Re-enable native autosizing only after native widths are back.
            foreach (var entry in _fonts)
                if (GodotObject.IsInstanceValid(entry.Key.Node))
                {
                    if (entry.Value.Had)
                        entry.Key.Node.AddThemeFontSizeOverride(entry.Key.Name, entry.Value.Value);
                    else
                        entry.Key.Node.RemoveThemeFontSizeOverride(entry.Key.Name);
                }
            foreach (var entry in _properties.Reverse())
                if (entry.Key.Name != "AutoSizeEnabled" && GodotObject.IsInstanceValid(entry.Key.Node))
                    entry.Key.Node.Set(entry.Key.Name, entry.Value);
            foreach (var entry in _constants)
                if (GodotObject.IsInstanceValid(entry.Key.Node))
                {
                    if (entry.Value.Had)
                        entry.Key.Node.AddThemeConstantOverride(entry.Key.Name, entry.Value.Value);
                    else
                        entry.Key.Node.RemoveThemeConstantOverride(entry.Key.Name);
                }
            foreach (var entry in _geometry)
                if (GodotObject.IsInstanceValid(entry.Key))
                    entry.Value.Restore();
            foreach (var entry in _properties)
                if (entry.Key.Name == "AutoSizeEnabled" && GodotObject.IsInstanceValid(entry.Key.Node))
                    entry.Key.Node.Set(entry.Key.Name, entry.Value);
            foreach (NEventOptionButton row in _entranceReady.Where(GodotObject.IsInstanceValid))
                row.MouseFilter = Control.MouseFilterEnum.Stop;
            _flow.ResetSize();
            if (_ancientArtActive)
            {
                // Restore the original aspect-ratio mapping after restoring anchors.
                // Its existing postfix may queue one final, native-space layout pass.
                _ancientArtActive = false;
                _ancientBackground!.Call("OnWindowChange");
            }
        }

        private void Remember(Control node)
        {
            if (!_geometry.ContainsKey(node))
                _geometry.Add(node, new PortraitControlSnapshot(node));
        }

        private void Place(Control node, Vector2 position, Vector2 size, bool remember = true)
        {
            if (remember)
                Remember(node);
            node.CustomMinimumSize = Vector2.Zero;
            node.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
            node.Position = position;
            node.Size = size;
        }

        private void Saved(CanvasItem node, string name, Variant value)
        {
            if (!_properties.ContainsKey((node, name)))
                _properties.Add((node, name), node.Get(name));
            if (!node.Get(name).Equals(value))
                node.Set(name, value);
        }

        private void Font(Control node, string name, int size)
        {
            if (!_fonts.ContainsKey((node, name)))
                _fonts.Add((node, name), (node.HasThemeFontSizeOverride(name), node.GetThemeFontSize(name)));
            if (node.GetThemeFontSize(name) != size)
                node.AddThemeFontSizeOverride(name, size);
        }

        private void Constant(Control node, string name, int value)
        {
            if (!_constants.ContainsKey((node, name)))
                _constants.Add((node, name), (node.HasThemeConstantOverride(name), node.GetThemeConstant(name)));
            if (node.GetThemeConstant(name) != value)
                node.AddThemeConstantOverride(name, value);
        }
    }

    // The native shader board is independent of event dialogue/option flow.
    // Keep Sphere intact so cells, prizes and the reveal mask share coordinates.
    private sealed class CrystalLayoutState
    {
        private static readonly string[] RichFonts =
            { "normal_font_size", "bold_font_size", "italics_font_size", "bold_italics_font_size", "mono_font_size" };
        private readonly NCrystalSphereScreen _screen;
        private readonly TextureRect _background;
        private readonly Control _sphere, _ui, _instructions;
        private readonly VBoxContainer _right, _instructionText;
        private readonly NDivinationButton[] _tools;
        private readonly MegaRichTextLabel _count, _title, _description;
        private readonly NProceedButton _proceed;
        private readonly NCrystalSphereDialogue _dialogue;
        private readonly Node2D _dialogueBox;
        private readonly Vector2 _backgroundSize, _sphereSize, _cellCenter, _dialogueBoxScale;
        private Vector2 _nativeDialoguePosition;
        private readonly Dictionary<Control, PortraitControlSnapshot> _geometry = new();
        private readonly Dictionary<(Control Node, string Name), Variant> _properties = new();
        private readonly Dictionary<(Control Node, string Name), (bool Had, int Size)> _fonts = new();
        private bool _active, _queued;

        public CrystalLayoutState(NCrystalSphereScreen screen)
        {
            _screen = screen;
            _background = screen.GetChildren().OfType<TextureRect>().Single(node => node.Name == "Bg");
            Control cells = screen.Get("_cellContainer").As<Control>();
            _sphere = cells.GetParent<Control>();
            _backgroundSize = _background.Size; _sphereSize = _sphere.Size; _cellCenter = cells.Position;
            _tools = new[] { screen.Get("_bigDivinationButton").As<NDivinationButton>(),
                screen.Get("_smallDivinationButton").As<NDivinationButton>() };
            _right = _tools[0].GetParent<VBoxContainer>(); _ui = _right.GetParent<Control>();
            _count = screen.Get("_divinationsLeftLabel").As<MegaRichTextLabel>();
            _title = screen.Get("_instructionsTitleLabel").As<MegaRichTextLabel>();
            _description = screen.Get("_instructionsDescriptionLabel").As<MegaRichTextLabel>();
            _instructions = screen.Get("_instructionsContainer").As<Control>();
            _instructionText = _title.GetParent<VBoxContainer>();
            _proceed = screen.Get("_proceedButton").As<NProceedButton>();
            _dialogue = screen.Get("_dialogue").As<NCrystalSphereDialogue>();
            _dialogueBox = _dialogue.Get("_dialogueBox").As<Node2D>();
            _nativeDialoguePosition = _dialogue.Position; _dialogueBoxScale = _dialogueBox.Scale;
            screen.GetViewport().SizeChanged += Queue;
            screen.TreeExiting += () => screen.GetViewport().SizeChanged -= Queue;
            _instructions.Resized += Queue;
        }

        public void Queue()
        {
            if (_queued) return;
            _queued = true;
            Callable.From(() => { _queued = false; Apply(); }).CallDeferred();
        }

        public void Apply()
        {
            if (!GodotObject.IsInstanceValid(_screen) || !_screen.IsInsideTree()) return;
            if (!PortraitViewportPatch.IsPortrait) { Restore(); return; }
            _active = true;
            Vector2 viewport = _screen.GetViewportRect().Size;
            float width = viewport.X - 96;
            // Preserve authored sizes and shader UVs; use one shared board transform.
            float artScale = Math.Max(viewport.X / _backgroundSize.X, viewport.Y / _backgroundSize.Y);
            Place(_background, (viewport - _backgroundSize * artScale) * .5f, _backgroundSize);
            _background.PivotOffset = Vector2.Zero; _background.Scale = Vector2.One * artScale;
            float boardSide = Math.Min(width, viewport.Y - 1056);
            Vector2 boardCenter = new(viewport.X * .5f, 416 + boardSide * .5f);
            Place(_sphere, Vector2.Zero, _sphereSize);
            Vector2 boardScale = Vector2.One * (boardSide / 627f)
                * _screen.GetGlobalTransform().Scale / _background.GetGlobalTransform().Scale;
            _sphere.Scale = boardScale;
            _sphere.Position = _background.GetGlobalTransform().AffineInverse()
                * (_screen.GetGlobalTransform() * boardCenter) - _cellCenter * boardScale;

            Place(_ui, Vector2.Zero, viewport);
            Place(_count, new Vector2(48, 336), new Vector2(width, 64));
            Saved(_count, "AutoSizeEnabled", false);
            foreach (string font in RichFonts) Font(_count, font, 48);
            float toolsTop = 416 + boardSide + 24;
            Place(_right, new Vector2(48, toolsTop), new Vector2(width, viewport.Y - toolsTop - 48));
            foreach (NDivinationButton button in _tools)
            {
                Remember(button); button.CustomMinimumSize = new Vector2(0, 144);
                button.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                TextureRect image = button.GetChildren().OfType<TextureRect>().Single(node => node.Name == "ButtonImage");
                foreach (TextureRect art in image.GetChildren().OfType<TextureRect>().Prepend(image))
                {
                    Saved(art, "expand_mode", (int)TextureRect.ExpandModeEnum.IgnoreSize);
                    Place(art, Vector2.Zero, new Vector2(width, 144));
                }
                MegaLabel label = button.Get("_label").As<MegaLabel>();
                Saved(label, "AutoSizeEnabled", false); Font(label, "font_size", 42);
                Place(label, new Vector2(132, 16), new Vector2(width - 156, 112));
                TextureRect icon = button.GetChildren().OfType<TextureRect>().Single(node => node.Name == "SizeIcon");
                Place(icon, new Vector2(24, 24), new Vector2(96, 96));
                foreach (Control hotkey in button.GetChildren().OfType<Control>().Where(node => node.Name == "HotkeyIcon"))
                    Saved(hotkey, "visible", false);
            }
            // Use the original rich-text scrolling instead of replacing instruction text.
            Remember(_instructionText);
            _instructionText.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            _instructionText.OffsetLeft = 24; _instructionText.OffsetTop = 16;
            _instructionText.OffsetRight = -24; _instructionText.OffsetBottom = -16;
            foreach (MegaRichTextLabel label in new[] { _title, _description })
            {
                Remember(label); label.CustomMinimumSize = Vector2.Zero;
                Saved(label, "AutoSizeEnabled", false);
                foreach (string font in RichFonts) Font(label, font, label == _title ? 40 : 36);
            }
            Saved(_description, "fit_content", false);
            Saved(_description, "scroll_active", true);
            Saved(_description, "mouse_filter", (int)Control.MouseFilterEnum.Stop);
            _description.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
            ArrangeDialogue(captureNative: false);
            ArrangeProceed();
        }

        public void ArrangeDialogue(bool captureNative)
        {
            if (!_active || !PortraitViewportPatch.IsPortrait) return;
            // Play writes X and keeps Y; preserve the native X for landscape.
            // The original typewriter, fade and scale tweens continue running.
            if (captureNative) _nativeDialoguePosition.X = _dialogue.Position.X;
            _dialogue.Position = new Vector2(_screen.GetViewportRect().Size.X * .5f + 63, 584);
            _dialogueBox.Scale = _dialogueBoxScale * 1.5f;
        }

        public void ArrangeProceed()
        {
            if (!_active || !PortraitViewportPatch.IsPortrait) return;
            // Reuse rest-site's completed transition, without another shared tween transpiler.
            CompleteProceed();
            Vector2 viewport = _screen.GetViewportRect().Size;
            Place(_proceed, new Vector2(48, _proceed.IsEnabled ? viewport.Y - 192 : viewport.Y + 48),
                new Vector2(viewport.X - 96, 144));
            _proceed.PivotOffset = new Vector2((viewport.X - 96) * .5f, 72);
            Control image = _proceed.Get("_buttonImage").As<Control>();
            // Retain the native arrow, label and feedback using the verified reward geometry.
            Place(image, new Vector2((viewport.X - 480) * .5f, -24), new Vector2(384, 192));
            MegaLabel label = _proceed.Get("_label").As<MegaLabel>();
            Font(label, "font_size", 48); Place(label, Vector2.Zero, image.Size);
            foreach (Control hotkey in _proceed.GetChildren().OfType<Control>().Where(node => node.Name == "HotkeyIcon"))
                Saved(hotkey, "visible", false);
        }

        private void CompleteProceed()
        {
            Tween? tween = _proceed.Get("_animTween").AsGodotObject() as Tween;
            if (GodotObject.IsInstanceValid(tween) && tween!.IsValid() && tween.IsRunning()) tween.FastForwardToCompletion();
        }

        private void Restore()
        {
            if (!_active) return;
            _active = false;
            CompleteProceed();
            foreach (var entry in _fonts)
                if (entry.Value.Had) entry.Key.Node.AddThemeFontSizeOverride(entry.Key.Name, entry.Value.Size);
                else entry.Key.Node.RemoveThemeFontSizeOverride(entry.Key.Name);
            foreach (var entry in _properties.Reverse())
                if (entry.Key.Name != "AutoSizeEnabled") entry.Key.Node.Set(entry.Key.Name, entry.Value);
            foreach (PortraitControlSnapshot snapshot in _geometry.Values) snapshot.Restore();
            foreach (var entry in _properties)
                if (entry.Key.Name == "AutoSizeEnabled") entry.Key.Node.Set(entry.Key.Name, entry.Value);
            _dialogue.Position = _nativeDialoguePosition; _dialogueBox.Scale = _dialogueBoxScale;
            _proceed.Position = _proceed.Get(_proceed.IsEnabled ? "ShowPos" : "HidePos").AsVector2();
        }

        private void Remember(Control node)
        {
            if (!_geometry.ContainsKey(node)) _geometry.Add(node, new PortraitControlSnapshot(node));
        }
        private void Place(Control node, Vector2 position, Vector2 size)
        {
            Remember(node); node.CustomMinimumSize = Vector2.Zero;
            node.SetAnchorsPreset(Control.LayoutPreset.TopLeft); node.Position = position; node.Size = size;
        }
        private void Saved(Control node, string name, Variant value)
        {
            if (!_properties.ContainsKey((node, name))) _properties.Add((node, name), node.Get(name));
            if (!node.Get(name).Equals(value)) node.Set(name, value);
        }
        private void Font(Control node, string name, int size)
        {
            if (!_fonts.ContainsKey((node, name))) _fonts.Add((node, name), (node.HasThemeFontSizeOverride(name), node.GetThemeFontSize(name)));
            if (node.GetThemeFontSize(name) != size) node.AddThemeFontSizeOverride(name, size);
        }
    }
}


// AnimateVfx is async, unlike the layout callbacks above. Observe its original
// task separately so all native intro tweens finish before taking a snapshot.
internal sealed class PortraitAncientNameIntroPatch : IPatchMethod
{
    public static string PatchId => "sts2portrait_ancient_name_intro";
    public static string Description => "Keeps the settled ancient name in the portrait art region";
    public static bool IsCritical => true;
    public static ModPatchTarget[] GetTargets() => new[]
    {
        PatchTarget.Method(typeof(NAncientNameBanner), "AnimateVfx"),
    };

    public static void Postfix(NAncientNameBanner __instance, ref Task __result)
    {
        if (!Entry.IsDisabled)
            __result = PortraitEventPatch.AfterAncientNameIntro(__instance, __result);
    }
}
