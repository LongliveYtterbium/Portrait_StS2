using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.PauseMenu;
using STS2RitsuLib.Patching.Models;

namespace STS2Portrait.Patches;

/// <summary>Reflows the native pause menu and shared confirmation popup.</summary>
internal sealed class PortraitPopupPatch : IPatchMethod
{
    public static string PatchId => "sts2portrait_popup";
    public static string Description => "Readable pause and confirmation controls";
    public static bool IsCritical => true;
    public static ModPatchTarget[] GetTargets() => new[]
    {
        PatchTarget.Method(typeof(NPauseMenu), "_Ready"),
        PatchTarget.Method(typeof(NPauseMenu), "Initialize"),
        PatchTarget.Method(typeof(NPauseMenu), "RefreshLabels"),
        PatchTarget.Method(typeof(NPauseMenu), "OnSubmenuOpened"),
        PatchTarget.Method(typeof(NVerticalPopup), "_Ready"),
        PatchTarget.Method(typeof(NVerticalPopup), "SetText", new[] { typeof(LocString), typeof(LocString) }),
        PatchTarget.Method(typeof(NVerticalPopup), "SetText", new[] { typeof(string), typeof(string) }),
        PatchTarget.Method(typeof(NVerticalPopup), "InitYesButton"),
        PatchTarget.Method(typeof(NVerticalPopup), "InitNoButton"),
        PatchTarget.Method(typeof(NVerticalPopup), "HideNoButton"),
        PatchTarget.Method(typeof(NBackButton), "OnEnable"),
        PatchTarget.Method(typeof(NBackButton), "OnDisable"),
        PatchTarget.Method(typeof(NBackButton), "OnWindowChange"),
    };

    private static readonly ConditionalWeakTable<Control, LayoutState> States = new();

    public static void Prefix(Node __instance, MethodBase __originalMethod)
    {
        if (Entry.IsDisabled) return;
        // Wrap before the native pause Ready computes absolute focus paths.
        // Its already-ready menu buttons have no hotkeys or exit cancellation.
        if (__instance is NPauseMenu pause && __originalMethod.Name == "_Ready")
            States.GetValue(pause, owner => new LayoutState(owner));
        else if (__instance is NBackButton back && __originalMethod.Name is "OnEnable" or "OnDisable")
            FindPause(back)?.PlaceBack(false);
    }

    public static void Postfix(Node __instance, MethodBase __originalMethod)
    {
        if (Entry.IsDisabled) return;
        if (__instance is NVerticalPopup popup && __originalMethod.Name == "_Ready")
            States.GetValue(popup, owner => new LayoutState(owner)).Queue();
        else if (__instance is NBackButton back && __originalMethod.Name == "OnWindowChange")
            FindPause(back)?.PlaceBack(true);
        else if (__instance is Control owner && States.TryGetValue(owner, out LayoutState? state))
        {
            if (__originalMethod.Name is "SetText" or "OnSubmenuOpened") state.ResetScroll();
            // Let other mods finish cloning and adding pause buttons before
            // recording native geometry or applying the first portrait layout.
            state.Queue();
        }
    }

    private static LayoutState? FindPause(Node node)
    {
        for (Node? ancestor = node.GetParent(); ancestor != null; ancestor = ancestor.GetParent())
            if (ancestor is NPauseMenu pause && States.TryGetValue(pause, out LayoutState? state)) return state;
        return null;
    }

    private sealed class LayoutState
    {
        private readonly Control _owner;
        private readonly ScrollContainer _scroll;
        private readonly Control _content;
        private readonly Control _panel;
        private readonly MegaLabel _title;
        private readonly MegaRichTextLabel? _body;
        private readonly int _titleIndex;
        private readonly int _bodyIndex;
        private NButton[] _buttons;
        private readonly VBoxContainer? _buttonBox;
        private readonly Control? _titleBox;
        private readonly NBackButton? _back;
        private readonly Panel? _backSurface;
        private readonly Label? _backLabel;
        private readonly Panel? _popupSurface;
        private readonly Dictionary<Control, PortraitControlSnapshot> _geometry = new();
        private readonly Dictionary<(Control Node, string Name), Variant> _properties = new();
        private readonly Dictionary<(Control Node, string Name), (bool Had, int Value)> _fonts = new();
        private readonly Dictionary<(Control Node, string Name), (bool Had, int Value)> _constants = new();
        private bool _active;
        private bool _queued;
        private bool _applying;
        private Vector2 _viewport;

        public LayoutState(Control owner)
        {
            _owner = owner;
            _scroll = new ScrollContainer
            {
                Name = "PortraitPopupScroll", MouseFilter = Control.MouseFilterEnum.Ignore,
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
                VerticalScrollMode = ScrollContainer.ScrollMode.Disabled,
                FollowFocus = true, ScrollDeadzone = 24,
            };
            if (owner is NPauseMenu)
            {
                _buttonBox = Descendants(owner).OfType<VBoxContainer>().Single(node => node.Name == "ButtonContainer");
                _panel = _buttonBox.GetParent<Control>();
                _titleBox = _panel.GetChildren().OfType<Control>().Single(node => node.Name == "PausedText");
                _title = _titleBox.GetChildren().OfType<MegaLabel>().Single();
                _buttons = _buttonBox.GetChildren().OfType<NPauseMenuButton>().Cast<NButton>().ToArray();
                _back = owner.GetChildren().OfType<NBackButton>().Single();
                _content = new Control { Name = "PortraitPauseContent", MouseFilter = Control.MouseFilterEnum.Ignore };
                Remember(_panel);
                int index = _panel.GetIndex();
                owner.AddChild(_scroll);
                owner.MoveChild(_scroll, index);
                _scroll.AddChild(_content);
                // Keep this wrapper on landscape, with scrolling disabled and a
                // full-size stage. Original nodes never change parent on rotation.
                _panel.Reparent(_content, false);
                _scroll.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
                _content.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                _content.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
                _panel.MinimumSizeChanged += Queue;
                _buttonBox.ChildOrderChanged += Queue;
                _scroll.ScrollStarted += CancelPausePresses;
                foreach (NButton button in _buttons) button.VisibilityChanged += Queue;
                _backSurface = AddSurface(_back, "PortraitPauseBackSurface");
                _backLabel = new Label
                {
                    Name = "PortraitPauseBackLabel", MouseFilter = Control.MouseFilterEnum.Ignore,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    Text = new LocString("main_menu_ui", "MULTIPLAYER_WARNING_POPUP.back").GetFormattedText(),
                };
                _backLabel.AddThemeFontSizeOverride("font_size", 48);
                _backSurface.AddChild(_backLabel);
                _backLabel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            }
            else
            {
                NVerticalPopup popup = (NVerticalPopup)owner;
                _panel = popup;
                _title = popup.Get("TitleLabel").As<MegaLabel>();
                _body = popup.Get("BodyLabel").As<MegaRichTextLabel>();
                _titleIndex = _title.GetIndex();
                _bodyIndex = _body.GetIndex();
                _buttons = new NButton[] { popup.NoButton, popup.YesButton };
                _content = new VBoxContainer { Name = "PortraitConfirmationText", MouseFilter = Control.MouseFilterEnum.Pass };
                _content.AddThemeConstantOverride("separation", 24);
                _content.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                _content.SizeFlagsVertical = Control.SizeFlags.Fill;
                _scroll.Visible = false;
                owner.AddChild(_scroll);
                _scroll.AddChild(_content);
                _content.MinimumSizeChanged += Queue;
                _popupSurface = AddSurface(owner, "PortraitConfirmationSurface");
                // Reuse the original popup paper and HSV tint; keep worn corners as text height changes.
                _popupSurface.Material = owner.Material;
                _popupSurface.AddThemeStyleboxOverride("panel", new StyleBoxTexture
                {
                    Texture = owner.Get("texture").As<Texture2D>(),
                    TextureMarginLeft = 160, TextureMarginTop = 160,
                    TextureMarginRight = 160, TextureMarginBottom = 160,
                    AxisStretchHorizontal = StyleBoxTexture.AxisStretchMode.Stretch,
                    AxisStretchVertical = StyleBoxTexture.AxisStretchMode.Stretch,
                    ContentMarginLeft = 0, ContentMarginTop = 0,
                    ContentMarginRight = 0, ContentMarginBottom = 0,
                });
            }
            Window window = owner.GetWindow();
            window.SizeChanged += Queue;
            owner.VisibilityChanged += Queue;
            owner.TreeExiting += () => window.SizeChanged -= Queue;
        }

        public void ResetScroll() => _scroll.ScrollVertical = 0;

        public void Queue()
        {
            if (_queued || _applying) return;
            _queued = true;
            Callable.From(() =>
            {
                _queued = false;
                if (GodotObject.IsInstanceValid(_owner) && _owner.IsInsideTree()) Apply();
            }).CallDeferred();
        }

        public void Apply()
        {
            if (_applying) return;
            _applying = true;
            try
            {
                if (_buttonBox != null)
                {
                    // Include late-added native buttons, even when an extension
                    // starts hidden, and subscribe once for its visibility toggle.
                    NButton[] buttons = _buttonBox.GetChildren().OfType<NPauseMenuButton>().Cast<NButton>().ToArray();
                    foreach (NButton button in buttons.Where(button => !_buttons.Contains(button)))
                        button.VisibilityChanged += Queue;
                    _buttons = buttons;
                }
                if (Entry.IsDisabled || !PortraitViewportPatch.IsPortrait) { Restore(); return; }
                Vector2 viewport = _owner.GetViewportRect().Size;
                bool changed = !_active || viewport != _viewport;
                if (changed) CompleteTweens();
                _active = true;
                _viewport = viewport;
                _scroll.Visible = true;
                _scroll.MouseFilter = Control.MouseFilterEnum.Stop;
                _scroll.ClipContents = true;
                _scroll.VerticalScrollMode = ScrollContainer.ScrollMode.Auto;
                if (_body == null) ApplyPause(changed);
                else ApplyPopup();
            }
            finally { _applying = false; }
        }

        private void ApplyPause(bool changed)
        {
            float width = Math.Min(840, _viewport.X - 96);
            Saved(_panel, "mouse_filter", (int)Control.MouseFilterEnum.Pass);
            Saved(_buttonBox!, "mouse_filter", (int)Control.MouseFilterEnum.Pass);
            Constant(_panel, "separation", 24);
            Constant(_buttonBox!, "separation", 16);
            Remember(_titleBox!);
            _titleBox!.CustomMinimumSize = new Vector2(0, 96);
            Saved(_titleBox, "mouse_filter", (int)Control.MouseFilterEnum.Pass);
            LabelFont(_title, 54);
            Fill(_title);
            Saved(_title, "mouse_filter", (int)Control.MouseFilterEnum.Ignore);
            foreach (NButton button in _buttons)
            {
                Remember(button);
                button.CustomMinimumSize = new Vector2(0, 144);
                button.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                Saved(button, "mouse_filter", (int)Control.MouseFilterEnum.Pass);
                MegaLabel label = button.GetChildren().OfType<MegaLabel>().Single();
                LabelFont(label, 48);
                Saved(label, "autowrap_mode", (int)TextServer.AutowrapMode.WordSmart);
                Fill(label);
                Saved(label, "mouse_filter", (int)Control.MouseFilterEnum.Ignore);
                TextureRect image = button.GetChildren().OfType<TextureRect>().Single();
                Saved(image, "expand_mode", (int)TextureRect.ExpandModeEnum.IgnoreSize);
                Fill(image);
                image.PivotOffset = new Vector2(width, 144) * .5f;
            }
            float needed = _panel.GetCombinedMinimumSize().Y;
            float height = Math.Min(needed, _viewport.Y - 288);
            Place(_scroll, new Vector2((_viewport.X - width) * .5f, Math.Max(48, (_viewport.Y - 192 - height) * .5f)), new Vector2(width, height));
            _content.MouseFilter = Control.MouseFilterEnum.Pass;
            _content.CustomMinimumSize = new Vector2(0, needed);
            Place(_panel, Vector2.Zero, new Vector2(width, needed));
            PlaceBack(changed);
        }

        private void ApplyPopup()
        {
            float width = Math.Min(912, _viewport.X - 96);
            if (_body!.GetParent() != _content)
            {
                Remember(_title); Remember(_body);
                _title.Reparent(_content, false);
                _body.Reparent(_content, false);
            }
            LabelFont(_title, 54);
            Remember(_title);
            _title.CustomMinimumSize = Vector2.Zero;
            _title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            Saved(_title, "autowrap_mode", (int)TextServer.AutowrapMode.WordSmart);
            Saved(_title, "mouse_filter", (int)Control.MouseFilterEnum.Ignore);
            Saved(_body, "AutoSizeEnabled", false);
            Remember(_body);
            _body.CustomMinimumSize = Vector2.Zero;
            _body.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            Saved(_body, "autowrap_mode", (int)TextServer.AutowrapMode.WordSmart);
            Saved(_body, "fit_content", true);
            Saved(_body, "scroll_active", false);
            Saved(_body, "mouse_filter", (int)Control.MouseFilterEnum.Pass);
            foreach (string font in new[] { "normal_font_size", "bold_font_size", "italics_font_size", "bold_italics_font_size", "mono_font_size" }) Font(_body, font, 48);
            // The original text nodes own shaping. Their container's minimum
            // signal remeasures after width changes; no cached content-height guess.
            float textHeight = Math.Clamp(_content.GetCombinedMinimumSize().Y, 192, _viewport.Y - 424);
            float height = textHeight + 232;
            Remember(_panel);
            _panel.SetAnchorsPreset(Control.LayoutPreset.Center);
            _panel.OffsetLeft = -width * .5f; _panel.OffsetRight = width * .5f;
            _panel.OffsetTop = -height * .5f; _panel.OffsetBottom = height * .5f;
            Saved(_panel, "self_modulate", new Color(1, 1, 1, 0));
            if (_owner.GetParent() is NSettingsScreenPopup settings)
                Saved(settings, "self_modulate", new Color(1, 1, 1, 0));
            _popupSurface!.Show();
            _popupSurface.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            Place(_scroll, new Vector2(48, 32), new Vector2(width - 96, textHeight));
            bool both = _buttons[0].Visible;
            float buttonWidth = both ? (width - 120) * .5f : width - 96;
            for (int i = 0; i < _buttons.Length; i++)
            {
                NButton button = _buttons[i];
                if (!button.Visible) continue;
                Place(button, new Vector2(48 + (both ? i * (buttonWidth + 24) : 0), height - 176), new Vector2(buttonWidth, 144));
                Control visuals = button.GetChildren().OfType<Control>().Single(node => node.Name == "Visuals");
                Fill(visuals);
                visuals.PivotOffset = button.Size * .5f;
                foreach (TextureRect image in visuals.GetChildren().OfType<TextureRect>())
                {
                    Saved(image, "expand_mode", (int)TextureRect.ExpandModeEnum.IgnoreSize);
                    Fill(image);
                }
                MegaLabel label = visuals.GetChildren().OfType<MegaLabel>().Single();
                LabelFont(label, 48);
                Fill(label);
                foreach (NHotkeyIcon icon in button.GetChildren().OfType<NHotkeyIcon>()) Saved(icon, "visible", false);
            }
        }

        public void PlaceBack(bool snap)
        {
            if (_back == null || !_active || Entry.IsDisabled || !PortraitViewportPatch.IsPortrait) return;
            Remember(_back);
            _back.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
            _back.Size = new Vector2(288, 144);
            Vector2 shown = new(48, _viewport.Y - 192);
            Vector2 hidden = new(-336, shown.Y);
            Transform2D parent = _back.GetParent<Control>().GetGlobalTransform();
            _back.Set("_showPos", parent * shown);
            _back.Set("_hidePos", parent * hidden);
            // Show the native red paper, arrow, shadow and focus outline over the same large hit area.
            foreach (Control artwork in _back.GetChildren().OfType<Control>().Where(node => node != _backSurface))
                Saved(artwork, "visible", true);
            foreach (NHotkeyIcon hotkey in _back.FindChildren("*", "", true, false).OfType<NHotkeyIcon>())
                Saved(hotkey, "visible", false);
            _backSurface!.Hide();
            if (snap) _back.Position = _back.Get("_isEnabled").AsBool() ? shown : hidden;
        }

        private void CancelPausePresses()
        {
            // ScrollContainer's real touch drag must not activate a menu item.
            foreach (NButton button in _buttons) button.Set("_isPressed", false);
        }

        private void CompleteTweens()
        {
            foreach (NButton button in _buttons)
                button.Get("_tween").As<Tween>()?.FastForwardToCompletion();
            if (_back != null) _back.Get("_moveTween").As<Tween>()?.FastForwardToCompletion();
        }

        private void Restore()
        {
            if (!_active) return;
            CompleteTweens();
            _active = false;
            _scroll.ScrollVertical = 0;
            if (_body != null)
            {
                // The owned surface occupies index zero; preserve the original
                // sibling order after the two text nodes return from the reader.
                _title.Reparent(_owner, false); _owner.MoveChild(_title, _titleIndex + 1);
                _body.Reparent(_owner, false); _owner.MoveChild(_body, _bodyIndex + 1);
                _scroll.Hide();
                _popupSurface!.Hide();
            }
            foreach (var item in _fonts)
                if (item.Value.Had) item.Key.Node.AddThemeFontSizeOverride(item.Key.Name, item.Value.Value);
                else item.Key.Node.RemoveThemeFontSizeOverride(item.Key.Name);
            foreach (var item in _constants)
                if (item.Value.Had) item.Key.Node.AddThemeConstantOverride(item.Key.Name, item.Value.Value);
                else item.Key.Node.RemoveThemeConstantOverride(item.Key.Name);
            // Undo FitContent before AutoSizeEnabled, then restore native bounds;
            // MegaRichTextLabel rejects auto sizing while fit-content is active.
            foreach (var item in _properties.Reverse()) item.Key.Node.Set(item.Key.Name, item.Value);
            // The pause panel was saved before its children. Restore child minima
            // first so its native bounds are not clamped by portrait constraints.
            foreach (PortraitControlSnapshot snapshot in _geometry.Values.Reverse()) snapshot.Restore();
            _scroll.MouseFilter = Control.MouseFilterEnum.Ignore;
            _scroll.ClipContents = false;
            _scroll.VerticalScrollMode = ScrollContainer.ScrollMode.Disabled;
            if (_back != null)
            {
                _scroll.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
                _content.CustomMinimumSize = Vector2.Zero;
                _content.MouseFilter = Control.MouseFilterEnum.Ignore;
                _backSurface!.Hide();
                // Native back geometry is viewport-relative; refresh it after
                // restoring the original anchors instead of replaying old Y.
                _back.Call("OnWindowChange");
            }
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
        private void Fill(Control node)
        {
            Remember(node);
            node.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        }
    }

    private static IEnumerable<Node> Descendants(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            yield return child;
            foreach (Node nested in Descendants(child)) yield return nested;
        }
    }

    private static Panel AddSurface(Control parent, string name)
    {
        Panel panel = new() { Name = name, Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(.075f, .10f, .105f, .99f), BorderColor = new Color(.64f, .49f, .25f),
            BorderWidthLeft = 2, BorderWidthRight = 2, BorderWidthTop = 2, BorderWidthBottom = 2,
            CornerRadiusTopLeft = 18, CornerRadiusTopRight = 18, CornerRadiusBottomLeft = 18, CornerRadiusBottomRight = 18,
        });
        parent.AddChild(panel);
        parent.MoveChild(panel, 0);
        panel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        return panel;
    }
}
