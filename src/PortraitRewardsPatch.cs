using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.addons.mega_text;
using STS2RitsuLib.Patching.Models;
using OpCodes = System.Reflection.Emit.OpCodes;

namespace STS2Portrait.Patches;

/// <summary>Gives the original reward list touch-sized rows and engine scrolling.</summary>
internal sealed class PortraitRewardsPatch : IPatchMethod
{
    public static string PatchId => "sts2portrait_rewards";
    public static string Description => "Arrange native rewards in a portrait scrolling list";
    public static bool IsCritical => true;
    public static ModPatchTarget[] GetTargets() => new[]
    {
        PatchTarget.Method(typeof(NRewardsScreen), "_Ready"),
        PatchTarget.Method(typeof(NRewardsScreen), "UpdateScreenState"),
        PatchTarget.Method(typeof(NRewardsScreen), "AfterOverlayShown"),
        PatchTarget.Method(typeof(NRewardsScreen), "_GuiInput"),
        PatchTarget.Method(typeof(NRewardsScreen), "_Process"),
        PatchTarget.Method(typeof(NRewardsScreen), "ProcessGuiFocus"),
        PatchTarget.Method(typeof(NProceedButton), "OnEnable"),
        PatchTarget.Method(typeof(NProceedButton), "OnDisable"),
    };

    private static readonly ConditionalWeakTable<NRewardsScreen, LayoutState> States = new();

    private static NRewardsScreen? Owner(Node node)
    {
        for (Node? current = node; current != null; current = current.GetParent())
            if (current is NRewardsScreen screen)
                return screen;
        return null;
    }

    public static bool Prefix(Node __instance, MethodBase __originalMethod)
    {
        // Only replace this screen's old scrolling; collection and completion
        // remain in the original reward models and screen callbacks.
        return Entry.IsDisabled || !PortraitViewportPatch.IsPortrait
            || __instance is not NRewardsScreen screen
            || !States.TryGetValue(screen, out LayoutState? state) || !state.Active
            || __originalMethod.Name is not ("_GuiInput" or "_Process" or "ProcessGuiFocus");
    }

    public static void Postfix(Node __instance, MethodBase __originalMethod)
    {
        if (Entry.IsDisabled || __instance is not NRewardsScreen screen)
            return;
        if (__originalMethod.Name == "_Ready")
            States.GetValue(screen, value => new LayoutState(value)).Queue();
        else if (__originalMethod.Name is "UpdateScreenState" or "AfterOverlayShown"
            && States.TryGetValue(screen, out LayoutState? state))
        {
            if (__originalMethod.Name == "AfterOverlayShown")
                state.FinishEntryAnimation();
            state.Queue();
        }
    }

    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        CodeInstruction[] body = instructions.ToArray();
        if (__originalMethod.DeclaringType != typeof(NProceedButton)
            || __originalMethod.Name is not ("OnEnable" or "OnDisable"))
            return body;
        bool hidden = __originalMethod.Name == "OnDisable";
        MethodInfo getter = AccessTools.PropertyGetter(typeof(NProceedButton), hidden ? "HidePos" : "ShowPos");
        CodeInstruction[] calls = body.Where(item => item.Calls(getter)).ToArray();
        if (calls.Length != 1)
            throw new InvalidOperationException($"Expected one proceed destination in {__originalMethod.Name}, found {calls.Length}.");
        // Target the actual animation caller: tiny position getters can already
        // be inlined. The native tween chain and callback behavior are retained.
        calls[0].opcode = OpCodes.Call;
        calls[0].operand = AccessTools.Method(typeof(PortraitRewardsPatch), hidden ? nameof(HiddenPosition) : nameof(ShownPosition));
        return body;
    }

    private static Vector2 ShownPosition(NProceedButton button) =>
        !Entry.IsDisabled && PortraitViewportPatch.IsPortrait
            && Owner(button) is NRewardsScreen screen
            && States.TryGetValue(screen, out LayoutState? state) && state.Active
            ? state.ProceedPosition
            : button.Get("ShowPos").AsVector2();

    private static Vector2 HiddenPosition(NProceedButton button) =>
        !Entry.IsDisabled && PortraitViewportPatch.IsPortrait && Owner(button) != null
            ? new Vector2(48, button.GetViewportRect().Size.Y + 48)
            : button.Get("HidePos").AsVector2();

    private sealed class LayoutState
    {
        private readonly NRewardsScreen _screen;
        private readonly Control _window;
        private readonly Control _mask;
        private readonly VBoxContainer _list;
        private readonly Control _nativeScrollbar;
        private readonly NProceedButton _proceed;
        private readonly Control _proceedImage;
        private readonly MegaLabel _proceedLabel;
        private readonly Node _listParent;
        private readonly int _listIndex;
        private readonly ScrollContainer _scroll;
        private readonly Panel _panelSkin;
        private readonly Dictionary<Control, PortraitControlSnapshot> _geometry = new();
        private readonly Dictionary<(Control Node, string Name), Variant> _properties = new();
        private readonly Dictionary<(Control Node, string Name), (bool Had, int Size)> _fonts = new();
        private readonly Dictionary<(Control Node, string Name), (bool Had, int Value)> _constants = new();
        private readonly HashSet<NRewardButton> _rows = new();
        private readonly HashSet<NLinkedRewardSet> _linked = new();
        private Vector2 _viewport;
        private bool _queued;
        private int _initialChoiceCount = -1;
        private float _panelTop;

        public bool Active { get; private set; }
        public Vector2 ProceedPosition { get; private set; }

        public LayoutState(NRewardsScreen screen)
        {
            _screen = screen;
            // Resolve original cached nodes after Ready instead of scene paths.
            _window = screen.Get("_rewardsWindow").As<Control>();
            _mask = screen.Get("_rewardContainerMask").As<Control>();
            _list = screen.Get("_rewardsContainer").As<VBoxContainer>();
            _nativeScrollbar = screen.Get("_scrollbar").As<Control>();
            _proceed = screen.Get("_proceedButton").As<NProceedButton>();
            _proceedImage = _proceed.Get("_buttonImage").As<Control>();
            _proceedLabel = _proceed.Get("_label").As<MegaLabel>();
            _listParent = _list.GetParent();
            _listIndex = _list.GetIndex();

            // This is the same reversible native VBox reparent pattern already
            // used by character selection; reward controls are never recreated.
            _scroll = new ScrollContainer
            {
                Name = "PortraitRewardScroll", Visible = false,
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
                VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
                FollowFocus = true, ScrollDeadzone = 24,
            };
            _window.AddChild(_scroll);
            _scroll.ScrollStarted += CancelPendingRewardPress;
            _list.MinimumSizeChanged += () =>
            {
                if (Active)
                    Queue();
            };
            // Hide only the old slab texture: its banner and label remain native.
            _panelSkin = new Panel
            {
                Name = "PortraitRewardSurface", Visible = false,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            _panelSkin.AddThemeStyleboxOverride("panel", new StyleBoxFlat
            {
                BgColor = new Color(0.045f, 0.085f, 0.078f, 0.97f),
                BorderColor = new Color(0.56f, 0.48f, 0.30f, 0.85f),
                BorderWidthLeft = 2, BorderWidthTop = 2, BorderWidthRight = 2, BorderWidthBottom = 2,
                CornerRadiusTopLeft = 24, CornerRadiusTopRight = 24,
                CornerRadiusBottomLeft = 24, CornerRadiusBottomRight = 24,
            });
            _window.AddChild(_panelSkin);
            _window.MoveChild(_panelSkin, 0);
            screen.Resized += Queue;
            Window window = screen.GetWindow();
            window.SizeChanged += Queue;
            screen.TreeExiting += () => window.SizeChanged -= Queue;
        }

        public void Queue()
        {
            if (_queued)
                return;
            _queued = true;
            Callable.From(() =>
            {
                _queued = false;
                if (GodotObject.IsInstanceValid(_screen) && _screen.IsInsideTree())
                    Apply();
            }).CallDeferred();
        }

        public void FinishEntryAnimation()
        {
            if (PortraitViewportPatch.IsPortrait)
                CompleteTween(_screen.Get("_fadeTween").AsGodotObject() as Tween);
        }

        private void Apply()
        {
            bool portrait = !Entry.IsDisabled && PortraitViewportPatch.IsPortrait;
            Vector2 viewport = _screen.GetViewportRect().Size;
            bool coordinateChange = Active != portrait || _viewport != viewport;
            if (coordinateChange)
            {
                CompleteTween(_screen.Get("_fadeTween").AsGodotObject() as Tween);
                CompleteTween(_proceed.Get("_animTween").AsGodotObject() as Tween);
                CompleteTween(_proceed.Get("_hoverTween").AsGodotObject() as Tween);
            }
            _viewport = viewport;
            if (!portrait)
            {
                if (Active)
                    Restore();
                // Final viewport resizing can follow the orientation transition.
                // Refresh the real destination on every later landscape resize.
                if (coordinateChange)
                    _proceed.Position = _proceed.IsEnabled ? ShownPosition(_proceed) : HiddenPosition(_proceed);
                return;
            }

            Active = true;
            float height = viewport.Y;
            Remember(_list);
            if (_list.GetParent() != _scroll)
            {
                _list.Reparent(_scroll, false);
                _list.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
                _list.Position = Vector2.Zero;
                RefreshNavigationPaths();
            }
            _list.CustomMinimumSize = Vector2.Zero;
            _list.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            _list.SizeFlagsVertical = Control.SizeFlags.ShrinkBegin;
            Constant(_list, "separation", 24);
            Saved(_mask, "visible", false);
            // Native UpdateScreenState can recompute its obsolete scrollbar.
            _nativeScrollbar.Hide();
            _nativeScrollbar.MouseFilter = Control.MouseFilterEnum.Ignore;

            int choices = _list.GetChildCount();
            if (coordinateChange || _initialChoiceCount < 0)
                _initialChoiceCount = choices;
            // Let the initial native text measurement settle before anchoring.
            // Once a reward is removed, keep the top edge still until rotation.
            bool centerPanel = choices == _initialChoiceCount;
            float maxBodyHeight = height - (centerPanel ? 336 : _panelTop) - 360;
            float bodyHeight = Math.Min(_list.GetCombinedMinimumSize().Y, maxBodyHeight);
            Place(_scroll, new Vector2(24, 120), new Vector2(936, bodyHeight), false);
            _scroll.Show();

            foreach (NRewardButton row in _list.FindChildren("*", "", true, false).OfType<NRewardButton>())
                ConfigureRow(row);
            foreach (NLinkedRewardSet linked in _list.GetChildren().OfType<NLinkedRewardSet>())
                ConfigureLinked(linked);

            bodyHeight = Math.Min(_list.GetCombinedMinimumSize().Y, maxBodyHeight);
            float panelHeight = bodyHeight + 144;
            if (centerPanel)
                _panelTop = 336 + (height - 48 - 336 - panelHeight - 168) * 0.5f;
            Place(_window, new Vector2(48, _panelTop), new Vector2(984, panelHeight));
            Place(_scroll, new Vector2(24, 120), new Vector2(936, bodyHeight), false);
            Place(_panelSkin, Vector2.Zero, _window.Size, false);
            // Native reward rows and the paper banner already define this list.
            _panelSkin.Hide();
            Control background = _window.GetChildren().OfType<Control>().Single(node => node.Name == "Background");
            Saved(background, "expand_mode", (int)TextureRect.ExpandModeEnum.IgnoreSize);
            Saved(background, "self_modulate", Colors.Transparent);
            Place(background, Vector2.Zero, _window.Size);
            Control banner = background.GetChildren().OfType<Control>().Single(node => node.Name == "Banner");
            Saved(banner, "expand_mode", (int)TextureRect.ExpandModeEnum.IgnoreSize);
            Place(banner, new Vector2(132, 0), new Vector2(720, 112));
            MegaLabel header = _screen.Get("_headerLabel").As<MegaLabel>();
            Font(header, "font_size", 54);
            // Center text on the paper body, above the ribbon tails and transparent padding.
            Place(header, new Vector2(24, -16), new Vector2(672, 112));

            Vector2 proceedPosition = new(48, _panelTop + panelHeight + 24);
            if (ProceedPosition != proceedPosition)
            {
                // Finish the existing tween before changing its destination.
                CompleteTween(_proceed.Get("_animTween").AsGodotObject() as Tween);
                ProceedPosition = proceedPosition;
            }
            Remember(_proceed);
            Remember(_proceedLabel);
            // Keep the native arrow, outline, shadow and HSV feedback with their original label child.
            Saved(_proceedImage, "visible", true);
            foreach (Control hotkey in _proceed.GetChildren().OfType<Control>().Where(node => node.Name == "HotkeyIcon"))
                Saved(hotkey, "visible", false);
            Place(_proceed, _proceed.IsEnabled ? ShownPosition(_proceed) : HiddenPosition(_proceed), new Vector2(984, 144));
            _proceed.PivotOffset = new Vector2(492, 72);
            // Preserve the native texture aspect while retaining the full-width touch target.
            Place(_proceedImage, new Vector2(300, -24), new Vector2(384, 192));
            Font(_proceedLabel, "font_size", 48);
            Place(_proceedLabel, Vector2.Zero, _proceedImage.Size);
            // UpdateText continues to write this original label, including Skip.
        }

        private void ConfigureRow(NRewardButton row)
        {
            Remember(row);
            // Preserve the native click while passing drag events to the scroll.
            Saved(row, "mouse_filter", (int)Control.MouseFilterEnum.Pass);
            MegaRichTextLabel label = row.Get("_label").As<MegaRichTextLabel>();
            if (_rows.Add(row))
            {
                row.Resized += () =>
                {
                    if (Active && GodotObject.IsInstanceValid(row))
                        PlaceRowContents(row);
                };
                // FitContent settles its minimum after the resize notification.
                label.MinimumSizeChanged += () =>
                {
                    if (Active && GodotObject.IsInstanceValid(row))
                        UpdateRowHeight(row);
                };
            }
            row.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            foreach (string font in new[] { "normal_font_size", "bold_font_size", "italics_font_size", "bold_italics_font_size", "mono_font_size" })
                Font(label, font, 48);
            Saved(label, "fit_content", true);
            Saved(label, "scroll_active", false);
            UpdateRowHeight(row);
            PlaceRowContents(row);
        }

        private void UpdateRowHeight(NRewardButton row)
        {
            MegaRichTextLabel label = row.Get("_label").As<MegaRichTextLabel>();
            // Include the original container margins around the shaped text.
            Control margin = (Control)label.GetParent();
            float height = Math.Max(144, margin.GetCombinedMinimumSize().Y + 48);
            Vector2 minimum = new(0, height);
            if (row.CustomMinimumSize != minimum)
                row.CustomMinimumSize = minimum;
        }

        private void PlaceRowContents(NRewardButton row)
        {
            float height = Math.Max(144, row.Size.Y);
            Control icon = row.Get("_iconContainer").As<Control>();
            // Enlarge the original icon holder, retaining child reward-specific
            // offsets and the original relic/potion pickup animation source.
            Place(icon, new Vector2(30, (height - 84) / 2), new Vector2(56, 56));
            icon.PivotOffset = Vector2.Zero;
            icon.Scale = Vector2.One * 1.5f;
            MegaRichTextLabel label = row.Get("_label").As<MegaRichTextLabel>();
            Control margin = (Control)label.GetParent();
            Place(margin, new Vector2(144, 24), new Vector2(Math.Max(144, row.Size.X - 168), height - 48));
            Saved(margin, "mouse_filter", (int)Control.MouseFilterEnum.Ignore);
            Control reticle = row.Get("_reticle").As<Control>();
            Place(reticle, Vector2.Zero, row.Size);
            reticle.PivotOffset = row.Size * 0.5f;
        }

        private void ConfigureLinked(NLinkedRewardSet linked)
        {
            Control rewards = linked.Get("_rewardContainer").As<Control>();
            if (_linked.Add(linked))
                rewards.Resized += () =>
                {
                    if (Active && GodotObject.IsInstanceValid(linked))
                        PlaceChains(linked);
                };
            Constant(rewards, "separation", 24);
            PlaceChains(linked);
        }

        private void PlaceChains(NLinkedRewardSet linked)
        {
            // Keep linked choices linked. Only move their existing decorations
            // to the gaps produced by the real native child row sizes.
            Control rewards = linked.Get("_rewardContainer").As<Control>();
            Control holder = linked.Get("_chainsContainer").As<Control>();
            NRewardButton[] rows = rewards.GetChildren().OfType<NRewardButton>().ToArray();
            TextureRect[] chains = holder.GetChildren().OfType<TextureRect>().ToArray();
            for (int i = 0; i < chains.Length; i++)
            {
                Remember(chains[i]);
                chains[i].Size = new Vector2(72, 72);
                chains[i].GlobalPosition = new Vector2(
                    rows[i].GlobalPosition.X + rows[i].Size.X * 0.5f - 36,
                    rows[i].GlobalPosition.Y + rows[i].Size.Y - 24);
            }
        }

        private void CancelPendingRewardPress()
        {
            if (!Active)
                return;
            foreach (NRewardButton row in _rows.Where(GodotObject.IsInstanceValid))
            {
                if (!row.Get("_isPressed").AsBool())
                    continue;
                // Native NClickableControl does not clear its custom press
                // state on ScrollBegin. Never re-enable an async reward claim.
                row.Set("_isPressed", false);
                row.Call("OnUnfocus");
            }
        }

        private void Restore()
        {
            CancelPendingRewardPress();
            Active = false;
            _list.Reparent(_listParent, false);
            _listParent.MoveChild(_list, _listIndex);
            RefreshNavigationPaths();
            _scroll.Hide();
            _panelSkin.Hide();
            // Reverse property order restores FitContent before re-enabling
            // MegaRichTextLabel autosizing, avoiding its incompatible-mode guard.
            foreach (var entry in _properties.Reverse())
                if (GodotObject.IsInstanceValid(entry.Key.Node))
                    entry.Key.Node.Set(entry.Key.Name, entry.Value);
            foreach (var entry in _fonts)
                if (GodotObject.IsInstanceValid(entry.Key.Node))
                {
                    if (entry.Value.Had)
                        entry.Key.Node.AddThemeFontSizeOverride(entry.Key.Name, entry.Value.Size);
                    else
                        entry.Key.Node.RemoveThemeFontSizeOverride(entry.Key.Name);
                }
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
            _list.ResetSize();
            _list.Position = new Vector2(_list.Position.X, 35);
            _screen.Set("_targetDragPos", _list.Position);
            _screen.Set("_scrollbarPressed", false);
            bool canScroll = _screen.Get("CanScroll").AsBool();
            _nativeScrollbar.Visible = canScroll;
            _nativeScrollbar.MouseFilter = canScroll ? Control.MouseFilterEnum.Stop : Control.MouseFilterEnum.Ignore;
        }

        private void RefreshNavigationPaths()
        {
            // Native UpdateScreenState uses absolute node paths. Reparenting
            // changes those paths without changing the original choice order.
            Control[] choices = _list.GetChildren().OfType<Control>().ToArray();
            for (int i = 0; i < choices.Length; i++)
            {
                choices[i].FocusNeighborLeft = choices[i].GetPath();
                choices[i].FocusNeighborRight = choices[i].GetPath();
                choices[i].FocusNeighborTop = choices[Math.Max(i - 1, 0)].GetPath();
                choices[i].FocusNeighborBottom = choices[Math.Min(i + 1, choices.Length - 1)].GetPath();
            }
        }

        private static void CompleteTween(Tween? tween)
        {
            if (tween != null && tween.IsValid() && tween.IsRunning())
                tween.FastForwardToCompletion();
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

        private void Saved(Control node, string name, Variant value)
        {
            if (!_properties.ContainsKey((node, name)))
                _properties.Add((node, name), node.Get(name));
            node.Set(name, value);
        }

        private void Font(Control node, string name, int size)
        {
            if (!_fonts.ContainsKey((node, name)))
                _fonts.Add((node, name), (node.HasThemeFontSizeOverride(name), node.GetThemeFontSize(name)));
            if (node is MegaLabel || node is MegaRichTextLabel)
                Saved(node, "AutoSizeEnabled", false);
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
}
