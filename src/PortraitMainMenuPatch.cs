using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Godot;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using STS2RitsuLib.Patching.Models;

namespace STS2Portrait.Patches;

/// <summary>
/// Reflows the existing menu controls into a thumb-sized vertical menu. Original
/// buttons, localization, focus behavior and signals remain owned by the game.
/// </summary>
internal sealed class PortraitMainMenuPatch : IPatchMethod
{
    public static string PatchId => "sts2portrait_main_menu";
    public static string Description => "Reflow the main menu for a portrait phone display";
    public static bool IsCritical => true;
    public static ModPatchTarget[] GetTargets() => new[]
    {
        PatchTarget.Method(typeof(NMainMenu), "_Ready"),
        PatchTarget.Method(typeof(NMainMenu), "RefreshButtons"),
        PatchTarget.Method(typeof(NMainMenu), "OnWindowChange"),
        PatchTarget.Method(typeof(NSingleplayerSubmenu), "_Ready"),
        PatchTarget.Method(typeof(NSingleplayerSubmenu), "RefreshButtons"),
        PatchTarget.Method(typeof(NBackButton), "OnEnable"),
        PatchTarget.Method(typeof(NSubmenuButton), "RefreshLabels"),
    };

    private static readonly ConditionalWeakTable<NMainMenu, MenuState> States = new();

    private static readonly ConditionalWeakTable<NSingleplayerSubmenu, ModeState> ModeStates = new();

    public static void Prefix(Node __instance)
    {
        if (Entry.IsDisabled || !PortraitViewportPatch.IsPortrait)
            return;
        // Refresh cached destinations before the native Back tween captures them.
        if (__instance is NBackButton back && back.GetParent() is NSingleplayerSubmenu owner
            && ModeStates.TryGetValue(owner, out ModeState? state))
            state.Apply();
    }

    public static void Postfix(Node __instance)
    {
        if (Entry.IsDisabled)
            return;

        // Keep the mode selector on its own native owner between the main menu
        // and character selection. Other submenu buttons retain their layout.
        if (__instance is NSingleplayerSubmenu modes)
        {
            ModeStates.GetValue(modes, screen => new ModeState(screen)).Queue();
            return;
        }
        if (__instance is NSubmenuButton button)
        {
            if (button.GetParent() is NSingleplayerSubmenu owner && ModeStates.TryGetValue(owner, out ModeState? state))
                state.Queue();
            return;
        }
        if (__instance is not NMainMenu menu)
            return;

        // NMainMenuBg also handles the window signal and writes its own scale.
        // Apply after all native handlers and framework initialization finish.
        Callable.From(() =>
        {
            if (GodotObject.IsInstanceValid(__instance) && __instance.IsInsideTree())
                Apply(menu);
        }).CallDeferred();
    }

    private static void Apply(NMainMenu __instance)
    {
        MenuState state = States.GetValue(__instance, menu => new MenuState(menu));
        if (!PortraitViewportPatch.IsPortrait)
        {
            state.Restore();
            return;
        }

        // Use the physical aspect directly because this postfix can run during the
        // same signal in which the viewport patch updates the logical canvas.
        Vector2I window = __instance.GetWindow().Size;
        float width = 1080;
        float height = (float)Math.Round(width * window.Y / window.X);
        state.Active = true;

        float backgroundScale = height / 1200f;
        Place(state.Background, new Vector2((width - 2560 * backgroundScale) / 2, 0), new Vector2(2560, 1200));
        state.Background.PivotOffset = Vector2.Zero;
        state.Background.Scale = Vector2.One * backgroundScale;

        // The Spine origin lies outside the artwork. Global transforms compensate
        // for the cropped background while preserving the original drawing order;
        // TopLevel would incorrectly draw the logo above confirmation dialogs.
        state.Logo.GlobalPosition = new Vector2(-100, height * 0.065f);
        state.Logo.GlobalScale = new Vector2(0.45f, 0.45f);

        NMainMenuTextButton[] visible = state.Buttons.Where(button => button.Visible).ToArray();
        float blockHeight = visible.Length * 120.8f - 20 + 12;
        float top = Math.Min(height * 0.56f, height - blockHeight - 140);
        // RitsuLib owns row positions and focus scrolling. Configure its existing
        // layout inputs through the generated Godot properties instead of fighting
        // RefreshLayout every frame. These members were verified in RitsuLib 0.6.2.
        state.ButtonContainer.Set("_designAnchor", Vector2.Zero);
        state.ButtonContainer.Set("_designOffsets", new Rect2(160, top, 760, blockHeight));
        state.ButtonContainer.Set("_separation", 20f);

        foreach (NMainMenuTextButton button in visible)
        {
            // Keep the thumb-sized hit area while restoring the native text-only
            // artwork, locale font and color/scale feedback owned by this button.
            button.CustomMinimumSize = new Vector2(button.CustomMinimumSize.X,
                Math.Max(88f, button.CustomMinimumSize.Y));
            Label label = button.label!;
            label.SetAnchorsPreset(Control.LayoutPreset.Center);
            label.OffsetLeft = -360; label.OffsetRight = 360;
            label.OffsetTop = -48; label.OffsetBottom = 48;
            label.AddThemeFontSizeOverride("font_size", 44);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            label.VerticalAlignment = VerticalAlignment.Center;
        }
        state.ButtonContainer.Call("RequestLayout");
    }

    private static void Place(Control control, Vector2 position, Vector2 size)
    {
        // The background is a free Control; the menu rows use their own scroller.
        control.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        control.Position = position;
        control.Size = size;
        control.Scale = Vector2.One;
    }

    private sealed class ModeState
    {
        private readonly NSingleplayerSubmenu _screen;
        private readonly NBackButton _back;
        private readonly List<(NSubmenuButton Button, TextureRect Background, TextureRect Icon, TextureRect Lock, MegaLabel Title, MegaRichTextLabel Description)> _rows = new();
        private readonly List<PortraitControlSnapshot> _controls = new();
        private readonly List<(Control Control, string Name, Variant Value)> _properties = new();
        private readonly List<(Control Control, string Name, bool HadOverride, int Size)> _fonts = new();
        private bool _active;
        private bool _queued;
        private float _height;

        public ModeState(NSingleplayerSubmenu screen)
        {
            _screen = screen;
            NSubmenuButton[] buttons = screen.GetChildren().OfType<NSubmenuButton>().ToArray();
            foreach (string name in new[] { "StandardButton", "DailyButton", "CustomRunButton" })
            {
                NSubmenuButton button = buttons.Single(node => node.Name == name);
                Control[] children = button.FindChildren("*", "", true, false).OfType<Control>().ToArray();
                TextureRect background = children.OfType<TextureRect>().Single(node => node.Name == "BgPanel");
                TextureRect icon = children.OfType<TextureRect>().Single(node => node.Name == "Icon");
                TextureRect locked = children.OfType<TextureRect>().Single(node => node.Name == "Lock");
                MegaLabel title = children.OfType<MegaLabel>().Single(node => node.Name == "Title");
                MegaRichTextLabel description = children.OfType<MegaRichTextLabel>().Single(node => node.Name == "Description");
                _rows.Add((button, background, icon, locked, title, description));
                foreach (Control control in new Control[] { button, background, icon, locked, title, description })
                    _controls.Add(new PortraitControlSnapshot(control));
                foreach (Control label in new Control[] { title, description })
                {
                    foreach (string property in new[] { "AutoSizeEnabled", "horizontal_alignment", "vertical_alignment" })
                        _properties.Add((label, property, label.Get(property)));
                    string[] fonts = label is MegaLabel
                        ? new[] { "font_size" }
                        : new[] { "normal_font_size", "bold_font_size", "italics_font_size", "bold_italics_font_size", "mono_font_size" };
                    foreach (string font in fonts)
                        _fonts.Add((label, font, label.HasThemeFontSizeOverride(font), label.GetThemeFontSize(font)));
                }
            }
            // Keep the native paper, arrow, hotkeys and submenu-pop signal.
            _back = screen.GetChildren().OfType<NBackButton>().Single();
            _controls.Add(new PortraitControlSnapshot(_back));
            _properties.Add((_back, "_posOffset", _back.Get("_posOffset")));
            // Reflow after native window handlers; no per-frame layout polling.
            Window window = screen.GetWindow();
            window.SizeChanged += Queue;
            screen.Resized += Queue;
            screen.TreeExiting += () =>
            {
                window.SizeChanged -= Queue;
                screen.Resized -= Queue;
            };
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

        public void Apply()
        {
            if (!PortraitViewportPatch.IsPortrait)
            {
                Restore();
                return;
            }
            Vector2I window = _screen.GetWindow().Size;
            float height = (float)Math.Round(1080f * window.Y / window.X);
            // The viewport can resize after the window; update an old native destination as well.
            bool changed = !_active || height != _height ||
                _back.Get("_showPos").AsVector2() != new Vector2(48, height - 192);
            _active = true;
            _height = height;
            if (changed)
            {
                // Finish only a geometry transition; normal native show/hide
                // animations keep their original 0.35-second tween and signals.
                Tween? tween = _back.Get("_moveTween").AsGodotObject() as Tween;
                if (tween != null && tween.IsValid() && tween.IsRunning())
                    tween.FastForwardToCompletion();
                _back.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
                _back.Size = new Vector2(264, 144);
                // Native OnWindowChange derives both destinations from this
                // offset; the paper's negative child offsets now fit on screen.
                _back.Set("_posOffset", new Vector2(-48, 192));
                _back.Call("OnWindowChange");
            }
            float rowHeight = Math.Clamp((height - 560) / 3, 288, 400);
            float top = Math.Max(64, (height - 400 - (rowHeight * 3 + 48)) / 2);
            // Finalize font-dependent minimum sizes before placing any labels.
            foreach (var row in _rows)
            {
                row.Title.AutoSizeEnabled = false;
                row.Description.AutoSizeEnabled = false;
            }
            foreach (var font in _fonts)
                font.Control.AddThemeFontSizeOverride(font.Name, font.Control is MegaLabel ? 48 : rowHeight < 320 ? 32 : 36);
            for (int i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                // Preserve native input, locked state, tint, icon and focus signals.
                Place(row.Button, new Vector2(48, top + i * (rowHeight + 24)), new Vector2(984, rowHeight));
                row.Button.PivotOffset = row.Button.Size / 2;
                Place(row.Background, Vector2.Zero, row.Button.Size);
                Vector2 iconPosition = new(44, (rowHeight - 168) / 2);
                Place(row.Icon, iconPosition, new Vector2(220, 168));
                Place(row.Lock, iconPosition, new Vector2(220, 168));
                Place(row.Title, new Vector2(312, 28), new Vector2(624, 66));
                Place(row.Description, new Vector2(312, 112), new Vector2(624, rowHeight - 136));
                row.Title.HorizontalAlignment = HorizontalAlignment.Left;
                row.Title.VerticalAlignment = VerticalAlignment.Center;
                row.Description.HorizontalAlignment = HorizontalAlignment.Left;
                row.Description.VerticalAlignment = VerticalAlignment.Top;
            }
        }

        private void Restore()
        {
            if (!_active)
                return;
            // Complete the old destination before restoring native geometry.
            Tween? tween = _back.Get("_moveTween").AsGodotObject() as Tween;
            if (tween != null && tween.IsValid() && tween.IsRunning())
                tween.FastForwardToCompletion();
            _active = false;
            // Restore text minimum sizes before applying the native rectangles.
            foreach (var font in _fonts)
            {
                if (font.HadOverride)
                    font.Control.AddThemeFontSizeOverride(font.Name, font.Size);
                else
                    font.Control.RemoveThemeFontSizeOverride(font.Name);
            }
            foreach (var property in _properties)
                property.Control.Set(property.Name, property.Value);
            foreach (PortraitControlSnapshot control in _controls)
                control.Restore();
            // Recalculate the original destinations for the current landscape
            // height after restoring the native offset and saved rectangle.
            _back.Call("OnWindowChange");
        }
    }

    private sealed class MenuState
    {
        public readonly Control Background;
        public readonly Control ButtonContainer;
        public readonly Node2D Logo;
        public readonly NMainMenuTextButton[] Buttons;
        private readonly List<PortraitControlSnapshot> _controls = new();
        private readonly Vector2 _logoPosition;
        private readonly Vector2 _logoScale;
        private readonly bool _logoTopLevel;
        private readonly Vector2 _designAnchor;
        private readonly Rect2 _designOffsets;
        private readonly float _separation;
        private readonly List<(Label Label, bool HadOverride, int FontSize, HorizontalAlignment H, VerticalAlignment V)> _labels = new();
        public bool Active;

        public MenuState(NMainMenu menu)
        {
            // Type plus a name keyword avoids dependence on complete scene paths.
            Node[] nodes = menu.FindChildren("*", "", true, false).ToArray();
            Background = nodes.OfType<Control>().Single(node => node.Name.ToString() == "BgContainer");
            ButtonContainer = nodes.OfType<Control>().Single(node => node.Name.ToString() == "MainMenuTextButtons");
            // Fail explicitly if the required framework layout contract changes.
            if (ButtonContainer.GetType().FullName != "STS2RitsuLib.Ui.MainMenu.NMainMenuScroller")
                throw new InvalidOperationException("Portrait menu requires the RitsuLib 0.6.2 main-menu scroller.");
            _designAnchor = ButtonContainer.Get("_designAnchor").AsVector2();
            _designOffsets = ButtonContainer.Get("_designOffsets").AsRect2();
            _separation = ButtonContainer.Get("_separation").AsSingle();
            Logo = nodes.OfType<Node2D>().Single(node => node.Name.ToString() == "Logo");
            Buttons = ButtonContainer.GetChildren().OfType<NMainMenuTextButton>().ToArray();
            _logoPosition = Logo.Position;
            _logoScale = Logo.Scale;
            _logoTopLevel = Logo.TopLevel;
            _controls.Add(new PortraitControlSnapshot(Background));
            _controls.Add(new PortraitControlSnapshot(ButtonContainer));

            foreach (NMainMenuTextButton button in Buttons)
            {
                Label label = button.label ?? throw new InvalidOperationException("Main menu label was not initialized.");
                _controls.Add(new PortraitControlSnapshot(button));
                _controls.Add(new PortraitControlSnapshot(label));
                _labels.Add((label, label.HasThemeFontSizeOverride("font_size"), label.GetThemeFontSize("font_size"), label.HorizontalAlignment, label.VerticalAlignment));

            }
        }

        public void Restore()
        {
            if (!Active)
                return;

            Active = false;
            foreach (PortraitControlSnapshot snapshot in _controls)
                snapshot.Restore();
            foreach (var entry in _labels)
            {
                if (entry.HadOverride)
                    entry.Label.AddThemeFontSizeOverride("font_size", entry.FontSize);
                else
                    entry.Label.RemoveThemeFontSizeOverride("font_size");
                entry.Label.HorizontalAlignment = entry.H;
                entry.Label.VerticalAlignment = entry.V;
            }
            Logo.TopLevel = _logoTopLevel;
            Logo.Position = _logoPosition;
            Logo.Scale = _logoScale;
            ButtonContainer.Set("_designAnchor", _designAnchor);
            ButtonContainer.Set("_designOffsets", _designOffsets);
            ButtonContainer.Set("_separation", _separation);
            ButtonContainer.Call("RequestLayout");
        }
    }
}

/// <summary>Restores original geometry for the menu and character screens.</summary>
internal readonly struct PortraitControlSnapshot
{
    private readonly Control _control;
    private readonly Vector4 _anchors;
    private readonly Vector4 _offsets;
    private readonly Vector2 _scale;
    private readonly Vector2 _pivot;
    private readonly Vector2 _minimum;
    private readonly Control.SizeFlags _horizontalFlags;
    private readonly Control.SizeFlags _verticalFlags;

    public PortraitControlSnapshot(Control control)
    {
        _control = control;
        _anchors = new Vector4(control.AnchorLeft, control.AnchorTop, control.AnchorRight, control.AnchorBottom);
        _offsets = new Vector4(control.OffsetLeft, control.OffsetTop, control.OffsetRight, control.OffsetBottom);
        _scale = control.Scale;
        _pivot = control.PivotOffset;
        _minimum = control.CustomMinimumSize;
        _horizontalFlags = control.SizeFlagsHorizontal;
        _verticalFlags = control.SizeFlagsVertical;
    }

    public void Restore()
    {
        // Containers also own minimum-size constraints; restore them before bounds.
        _control.CustomMinimumSize = _minimum;
        _control.SizeFlagsHorizontal = _horizontalFlags;
        _control.SizeFlagsVertical = _verticalFlags;
        _control.AnchorLeft = _anchors.X; _control.AnchorTop = _anchors.Y;
        _control.AnchorRight = _anchors.Z; _control.AnchorBottom = _anchors.W;
        _control.OffsetLeft = _offsets.X; _control.OffsetTop = _offsets.Y;
        _control.OffsetRight = _offsets.Z; _control.OffsetBottom = _offsets.W;
        _control.Scale = _scale;
        _control.PivotOffset = _pivot;
    }
}
