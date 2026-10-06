using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen;
using STS2RitsuLib.Patching.Models;

namespace STS2Portrait.Patches;

/// <summary>Wraps the summary before its native children enter the tree.</summary>
internal sealed class PortraitGameOverFactoryPatch : IPatchMethod
{
    public static string PatchId => "sts2portrait_game_over_factory";
    public static string Description => "Prepare the native game-over scroll before initialization";
    public static bool IsCritical => true;
    public static ModPatchTarget[] GetTargets() => new[]
    {
        PatchTarget.Method(typeof(NGameOverScreen), "Create"),
    };

    public static void Postfix(NGameOverScreen? __result)
    {
        if (!Entry.IsDisabled && __result != null)
            PortraitGameOverPatch.Prepare(__result);
    }
}

/// <summary>Reflows the native score summary without changing completion gates.</summary>
internal sealed class PortraitGameOverPatch : IPatchMethod
{
    public static string PatchId => "sts2portrait_game_over";
    public static string Description => "Readable game-over summary and reachable native continuation";
    public static bool IsCritical => true;
    public static ModPatchTarget[] GetTargets() => new[]
    {
        PatchTarget.Method(typeof(NGameOverScreen), "_Ready"),
        PatchTarget.Method(typeof(NGameOverScreen), "AddScoreLine"),
        PatchTarget.Method(typeof(NGameOverScreen), "OpenSummaryScreen"),
        PatchTarget.Method(typeof(NGameOverScreen), "HideSummary"),
        PatchTarget.Method(typeof(NGameOverScreen), "ShowLeaderboard"),
        PatchTarget.Method(typeof(NGameOverScreen), "AfterOverlayShown"),
        PatchTarget.Method(typeof(NCommonBanner), "AnimateIn"),
        PatchTarget.Method(typeof(NCommonBanner), "OnWindowChange"),
        PatchTarget.Method(typeof(NGameOverContinueButton), "OnEnable"),
        PatchTarget.Method(typeof(NGameOverContinueButton), "OnDisable"),
        PatchTarget.Method(typeof(NReturnToMainMenuButton), "OnEnable"),
        PatchTarget.Method(typeof(NReturnToMainMenuButton), "OnDisable"),
        PatchTarget.Method(typeof(NReturnToMainMenuButton), "SetLabelForUnlock"),
        PatchTarget.Method(typeof(NBadge), "_Ready"),
        PatchTarget.Method(typeof(NDiscoveredItem), "SetText"),
    };

    private static readonly ConditionalWeakTable<NGameOverScreen, LayoutState> States = new();
    private static readonly string[] RichFontSizes =
        { "normal_font_size", "bold_font_size", "italics_font_size", "bold_italics_font_size", "mono_font_size" };

    internal static void Prepare(NGameOverScreen screen) => States.Add(screen, new LayoutState(screen));

    private static LayoutState? Find(Node node)
    {
        for (Node? owner = node; owner != null; owner = owner.GetParent())
            if (owner is NGameOverScreen screen && States.TryGetValue(screen, out LayoutState? state)) return state;
        return null;
    }

    public static void Prefix(Node __instance, MethodBase __originalMethod)
    {
        if (Entry.IsDisabled || Find(__instance) is not LayoutState state || !state.Active) return;
        if (__instance is NCommonBanner && __originalMethod.Name == "AnimateIn") state.ArrangeBanner();
        else if (__instance is NGameOverContinueButton or NReturnToMainMenuButton
            && __originalMethod.Name is "OnEnable" or "OnDisable")
            state.ArrangeButton((NButton)__instance, false);
    }

    public static void Postfix(Node __instance, MethodBase __originalMethod)
    {
        if (Entry.IsDisabled || Find(__instance) is not LayoutState state) return;
        if (__instance is NGameOverScreen && __originalMethod.Name == "_Ready") state.Initialize();
        else if (__instance is NCommonBanner && __originalMethod.Name == "OnWindowChange")
        {
            if (state.Active) state.ArrangeBanner();
        }
        else
        {
            if (__instance is NGameOverScreen && __originalMethod.Name == "OpenSummaryScreen")
                state.SummaryOpened = true;
            state.Queue();
        }
    }

    private sealed class LayoutState
    {
        private readonly NGameOverScreen _screen;
        private readonly NRunSummary _summary;
        private readonly CenterContainer _center;
        private readonly Control _stage;
        private readonly ScrollContainer _scroll;
        private readonly Control _content;
        private readonly HBoxContainer _badges;
        private readonly ScrollContainer _badgeScroll;
        private readonly Dictionary<Control, PortraitControlSnapshot> _geometry = new();
        private readonly Dictionary<(Control Node, string Name), Variant> _properties = new();
        private readonly Dictionary<(Control Node, string Name), (bool Had, int Value)> _fonts = new();
        private readonly Dictionary<(Control Node, string Name), (bool Had, int Value)> _constants = new();
        private readonly Dictionary<NButton, Vector2> _nativeButtonOffsets = new();
        private NCommonBanner _banner = null!;
        private MegaRichTextLabel _quote = null!;
        private MegaRichTextLabel _victory = null!;
        private GridContainer _scoreLines = null!;
        private Control _scoreBar = null!;
        private HBoxContainer _discoveries = null!;
        private NButton[] _buttons = Array.Empty<NButton>();
        private Control _leaderboard = null!;
        private bool _initialized;
        private bool _queued;
        private bool _applying;
        private Vector2 _viewport;

        public bool Active { get; private set; }
        public bool SummaryOpened { get; set; }

        public LayoutState(NGameOverScreen screen)
        {
            _screen = screen;
            _summary = Descendants(screen).OfType<NRunSummary>().Single();
            _center = (CenterContainer)_summary.GetParent();
            _stage = (Control)_center.GetParent();
            _badges = Descendants(_summary).OfType<HBoxContainer>().Single(node => node.Name == "BadgeContainer");
            Remember(_center);
            _scroll = new ScrollContainer
            {
                Name = "PortraitGameOverScroll", MouseFilter = Control.MouseFilterEnum.Ignore,
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
                VerticalScrollMode = ScrollContainer.ScrollMode.Disabled,
                FollowFocus = true, ScrollDeadzone = 24, ClipContents = false,
            };
            _content = new Control { Name = "PortraitGameOverContent", MouseFilter = Control.MouseFilterEnum.Ignore };
            _scroll.AddThemeStyleboxOverride("panel", new StyleBoxEmpty());
            int index = _center.GetIndex();
            _stage.AddChild(_scroll);
            _stage.MoveChild(_scroll, index);
            _scroll.AddChild(_content);
            // Factory-time wrapping avoids NScoreLine/NBadge exit cancellation.
            _center.Reparent(_content, false);
            _badgeScroll = new ScrollContainer
            {
                Name = "PortraitGameOverBadges", MouseFilter = Control.MouseFilterEnum.Ignore,
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
                VerticalScrollMode = ScrollContainer.ScrollMode.Disabled,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                FollowFocus = true, ScrollDeadzone = 24, ClipContents = false,
            };
            Node badgeParent = _badges.GetParent();
            _badgeScroll.AddThemeStyleboxOverride("panel", new StyleBoxEmpty());
            index = _badges.GetIndex();
            badgeParent.AddChild(_badgeScroll);
            badgeParent.MoveChild(_badgeScroll, index);
            _badges.Reparent(_badgeScroll, false);
        }

        public void Initialize()
        {
            _banner = _screen.Get("_banner").As<NCommonBanner>();
            _quote = _screen.Get("_deathQuote").As<MegaRichTextLabel>();
            _victory = _screen.Get("_victoryDamageLabel").As<MegaRichTextLabel>();
            _scoreLines = _screen.Get("_scoreLineContainer").As<GridContainer>();
            _scoreBar = _screen.Get("_scoreBar").As<Control>();
            _discoveries = Descendants(_summary).OfType<HBoxContainer>().Single(node => node.Name == "DiscoveredContents");
            _leaderboard = _screen.Get("_leaderboard").As<Control>();
            _buttons = Descendants(_screen).Where(node => node is NGameOverContinueButton or NReturnToMainMenuButton)
                .Cast<NButton>().ToArray();
            foreach (NButton button in _buttons)
            {
                Complete(button.Get("_tween").AsGodotObject() as Tween);
                Remember(button);
                _nativeButtonOffsets.Add(button, button.Position - button.Get("_showPosition").AsVector2());
                button.VisibilityChanged += Queue;
            }
            _summary.MinimumSizeChanged += Queue;
            _summary.VisibilityChanged += Queue;
            _leaderboard.VisibilityChanged += Queue;
            _scroll.ScrollStarted += CancelReadingPresses;
            _badgeScroll.ScrollStarted += CancelReadingPresses;
            Window window = _screen.GetTree().Root;
            window.SizeChanged += Queue;
            // The window outlives this screen; all remaining signal sources are owned children.
            _screen.TreeExiting += () =>
            {
                _initialized = false;
                window.SizeChanged -= Queue;
            };
            _initialized = true;
            Apply();
        }

        public void Queue()
        {
            if (!_initialized || _queued) return;
            _queued = true;
            Callable.From(() =>
            {
                _queued = false;
                if (GodotObject.IsInstanceValid(_screen) && _screen.IsInsideTree()) Apply();
            }).CallDeferred();
        }

        private void Apply()
        {
            if (_applying) return;
            _applying = true;
            try
            {
                Vector2 viewport = _screen.GetViewportRect().Size;
                if (!PortraitViewportPatch.IsPortrait)
                {
                    if (Active) Restore();
                    ArrangeLandscapeShell(viewport);
                    return;
                }
                bool changingSpace = !Active || _viewport != viewport;
                if (changingSpace)
                {
                    Complete(_banner.Get("_tween").AsGodotObject() as Tween);
                    foreach (NButton button in _buttons) Complete(button.Get("_tween").AsGodotObject() as Tween);
                }
                Active = true;
                _viewport = viewport;
                float width = viewport.X - 96;
                // Reserve the native scrollbar width before calculating any container minimum.
                float contentWidth = width - _scroll.GetVScrollBar().GetCombinedMinimumSize().X;
                if (changingSpace) ArrangeBanner();
                Readable(_quote, 40);
                Saved(_quote, "scroll_active", true);
                Place(_quote, new Vector2(48 - _banner.Position.X, 156), new Vector2(width, 120));
                Readable(_victory, 44);
                Saved(_victory, "scroll_active", true);
                Place(_victory, new Vector2(48, 672), new Vector2(width, Math.Max(240, viewport.Y - 912)));
                foreach (NButton button in _buttons) ArrangeButton(button, true);

                // Keep native asynchronous scoring and unlock changes; only reflow their controls.
                Remember(_summary);
                _summary.CustomMinimumSize = new Vector2(contentWidth, 0);
                _summary.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                Constant(_summary, "margin_left", 24);
                Constant(_summary, "margin_right", 24);
                // Center within the sized content rectangle, not around its origin.
                Saved(_center, "use_top_left", false);
                Saved(_center, "mouse_filter", (int)Control.MouseFilterEnum.Pass);
                // Pure summary decoration must not swallow touch drags headed for the scroll.
                foreach (Control control in Descendants(_summary).OfType<Control>())
                    if (control is not ScrollContainer)
                        Saved(control, "mouse_filter", (int)(control is NClickableControl
                            ? Control.MouseFilterEnum.Pass : Control.MouseFilterEnum.Ignore));
                Saved(_scoreLines, "columns", 1);
                Constant(_scoreLines, "v_separation", 12);
                Constant(_scoreLines, "h_separation", 0);
                foreach (NScoreLine line in _scoreLines.GetChildren().OfType<NScoreLine>())
                {
                    Remember(line);
                    line.CustomMinimumSize = new Vector2(contentWidth - 48, 80);
                    line.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                    foreach (MegaLabel label in line.GetChildren().OfType<MegaLabel>())
                    {
                        LabelFont(label, 40);
                        if (label.Name == "Label")
                        {
                            Remember(label);
                            label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                            Saved(label, "autowrap_mode", (int)TextServer.AutowrapMode.WordSmart);
                        }
                    }
                }
                _badgeScroll.HorizontalScrollMode = ScrollContainer.ScrollMode.ShowNever;
                _badgeScroll.MouseFilter = Control.MouseFilterEnum.Stop;
                _badgeScroll.ClipContents = true;
                _badgeScroll.CustomMinimumSize = new Vector2(0, _badges.GetChildCount() == 0 ? 0 : 160);
                Constant(_badges, "separation", 12);
                foreach (NBadge badge in _badges.GetChildren().OfType<NBadge>())
                {
                    Remember(badge);
                    badge.CustomMinimumSize = new Vector2(120, 120);
                    badge.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
                    Saved(badge, "mouse_filter", (int)Control.MouseFilterEnum.Pass);
                    Saved(badge, "_ignoreDragThreshold", 24f);
                }
                ArrangeProgress(contentWidth - 48);
                Constant(_discoveries, "separation", 16);
                foreach (NDiscoveredItem item in _discoveries.GetChildren().OfType<NDiscoveredItem>())
                {
                    Remember(item);
                    item.CustomMinimumSize = new Vector2(160, 96);
                    Saved(item, "mouse_filter", (int)Control.MouseFilterEnum.Pass);
                    Saved(item, "_ignoreDragThreshold", 24f);
                    foreach (MegaLabel label in Descendants(item).OfType<MegaLabel>()) LabelFont(label, 40);
                }
                HBoxContainer discoveryHeader = Descendants(_summary).OfType<HBoxContainer>().Single(node => node.Name == "DiscoveryHeader");
                foreach (TextureRect decoration in discoveryHeader.GetChildren().OfType<TextureRect>())
                {
                    Remember(decoration);
                    decoration.CustomMinimumSize = new Vector2(96, 4);
                }
                LabelFont(_screen.Get("_discoveryLabel").As<MegaLabel>(), 40);
                _scroll.VerticalScrollMode = ScrollContainer.ScrollMode.Auto;
                _scroll.ClipContents = true;
                _scroll.MouseFilter = Control.MouseFilterEnum.Stop;
                _scroll.Visible = _summary.Visible && !_leaderboard.Visible;
                _content.MouseFilter = Control.MouseFilterEnum.Pass;
                // Child font/minimum changes precede the enclosing sizes.
                float contentHeight = Math.Max(1, _summary.GetCombinedMinimumSize().Y);
                Place(_center, Vector2.Zero, new Vector2(contentWidth, contentHeight));
                _content.CustomMinimumSize = new Vector2(contentWidth, contentHeight);
                _content.Size = new Vector2(contentWidth, contentHeight);
                Place(_scroll, new Vector2(48, 672), new Vector2(width, Math.Max(240, viewport.Y - 912)));
                Place(_leaderboard, new Vector2(48, 672), new Vector2(width, Math.Max(440, viewport.Y - 912)));
                _scoreLines.QueueSort();
                _center.QueueSort();
                _badgeScroll.QueueSort();
                _scroll.QueueSort();
            }
            finally { _applying = false; }
        }

        public void ArrangeBanner()
        {
            if (!Active) return;
            float width = _screen.GetViewportRect().Size.X;
            // Clear the 312 px top bar and retain the native summary animation delta of -32.
            Place(_banner, new Vector2((width - 654) / 2, SummaryOpened ? 336 : 368), new Vector2(654, 162));
            LabelFont(_banner.label, 60);
            Saved(_banner, "_showPos", _banner.GlobalPosition);
            Saved(_banner, "_hidePos", _banner.GlobalPosition + new Vector2(0, 50));
        }

        public void ArrangeButton(NButton button, bool updatePosition)
        {
            if (!Active) return;
            Vector2 viewport = _screen.GetViewportRect().Size;
            Vector2 position = new((viewport.X - 840) / 2, viewport.Y - 192);
            Remember(button);
            button.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
            button.Size = new Vector2(840, 144);
            button.PivotOffset = button.Size * .5f;
            Saved(button, "_showPosition", position);
            Tween? tween = button.Get("_tween").AsGodotObject() as Tween;
            if (updatePosition && (tween == null || !tween.IsValid() || !tween.IsRunning()))
                button.Position = button.IsEnabled ? position : position + new Vector2(0, 190);
            foreach (MegaLabel label in button.GetChildren().OfType<MegaLabel>())
            {
                LabelFont(label, 48);
                Place(label, new Vector2(36, 0), new Vector2(768, 144));
            }
        }

        private void ArrangeProgress(float width)
        {
            Constant(_scoreBar, "margin_top", 96);
            Control background = _scoreBar.GetChildren().OfType<Control>().Single(node => node.Name == "ScoreBg");
            Remember(background);
            background.CustomMinimumSize = new Vector2(width, 24);
            Control padder = _scoreBar.GetChildren().OfType<Control>().Single(node => node.Name == "Padder");
            Remember(padder);
            padder.CustomMinimumSize = new Vector2(0, 192);
            MegaLabel progress = _screen.Get("_scoreProgress").As<MegaLabel>();
            MegaLabel remaining = _screen.Get("_unlocksRemaining").As<MegaLabel>();
            LabelFont(progress, 40);
            LabelFont(remaining, 36);
            Place(progress, new Vector2(0, 36), new Vector2(width, 64));
            Place(remaining, new Vector2(0, 104), new Vector2(width, 72));
            Saved(progress, "horizontal_alignment", (int)HorizontalAlignment.Center);
            Saved(remaining, "horizontal_alignment", (int)HorizontalAlignment.Center);
        }

        private void CancelReadingPresses()
        {
            foreach (NClickableControl item in _badges.GetChildren().OfType<NClickableControl>()
                .Concat(_discoveries.GetChildren().OfType<NClickableControl>()))
            {
                item.Set("_isPressed", false);
                NHoverTipSet.Remove(item);
            }
        }

        private void ArrangeLandscapeShell(Vector2 viewport)
        {
            _scroll.VerticalScrollMode = ScrollContainer.ScrollMode.Disabled;
            _scroll.MouseFilter = Control.MouseFilterEnum.Ignore;
            _scroll.ClipContents = false;
            _scroll.ScrollVertical = 0;
            _scroll.CustomMinimumSize = Vector2.Zero;
            _content.MouseFilter = Control.MouseFilterEnum.Ignore;
            _content.CustomMinimumSize = viewport;
            _content.Size = viewport;
            _scroll.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            _scroll.Show();
            _badgeScroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
            _badgeScroll.MouseFilter = Control.MouseFilterEnum.Ignore;
            _badgeScroll.ClipContents = false;
            _badgeScroll.CustomMinimumSize = Vector2.Zero;
            _badgeScroll.ScrollHorizontal = 0;
            _center.QueueSort();
            _badgeScroll.QueueSort();
            _scroll.QueueSort();
        }

        private void Restore()
        {
            Complete(_banner.Get("_tween").AsGodotObject() as Tween);
            foreach (NButton button in _buttons) Complete(button.Get("_tween").AsGodotObject() as Tween);
            Active = false;
            CancelReadingPresses();
            foreach (var item in _properties) item.Key.Node.Set(item.Key.Name, item.Value);
            foreach (var item in _fonts)
                if (item.Value.Had) item.Key.Node.AddThemeFontSizeOverride(item.Key.Name, item.Value.Value);
                else item.Key.Node.RemoveThemeFontSizeOverride(item.Key.Name);
            foreach (var item in _constants)
                if (item.Value.Had) item.Key.Node.AddThemeConstantOverride(item.Key.Name, item.Value.Value);
                else item.Key.Node.RemoveThemeConstantOverride(item.Key.Name);
            foreach (var item in _geometry.Reverse())
                if (GodotObject.IsInstanceValid(item.Key)) item.Value.Restore();
            // Recompute cached destinations from restored viewport-relative anchors.
            foreach (NButton button in _buttons)
            {
                Vector2 show = button.Position - _nativeButtonOffsets[button];
                button.Set("_showPosition", show);
                if (button.IsEnabled) button.Position = show;
            }
            _banner.Call("OnWindowChange");
        }

        private void Remember(Control node) => _geometry.TryAdd(node, new PortraitControlSnapshot(node));
        private void Saved(Control node, string name, Variant value)
        {
            _properties.TryAdd((node, name), node.Get(name));
            if (!node.Get(name).Equals(value)) node.Set(name, value);
        }
        private void Font(Control node, string name, int value)
        {
            _fonts.TryAdd((node, name), (node.HasThemeFontSizeOverride(name), node.GetThemeFontSize(name)));
            if (!node.HasThemeFontSizeOverride(name) || node.GetThemeFontSize(name) != value) node.AddThemeFontSizeOverride(name, value);
        }
        private void LabelFont(MegaLabel label, int size)
        {
            Saved(label, "AutoSizeEnabled", false);
            Font(label, "font_size", size);
        }
        private void Readable(MegaRichTextLabel label, int size)
        {
            Saved(label, "AutoSizeEnabled", false);
            Saved(label, "fit_content", false);
            Saved(label, "autowrap_mode", (int)TextServer.AutowrapMode.WordSmart);
            foreach (string name in RichFontSizes) Font(label, name, size);
        }
        private void Constant(Control node, string name, int value)
        {
            _constants.TryAdd((node, name), (node.HasThemeConstantOverride(name), node.GetThemeConstant(name)));
            if (!node.HasThemeConstantOverride(name) || node.GetThemeConstant(name) != value) node.AddThemeConstantOverride(name, value);
        }
        private void Place(Control node, Vector2 position, Vector2 size)
        {
            Remember(node);
            node.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
            if (node.Position != position) node.Position = position;
            if (node.Size != size) node.Size = size;
        }
    }

    private static void Complete(Tween? tween)
    {
        if (tween != null && tween.IsValid() && tween.IsRunning()) tween.FastForwardToCompletion();
    }

    private static IEnumerable<Node> Descendants(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            yield return child;
            foreach (Node nested in Descendants(child)) yield return nested;
        }
    }
}
