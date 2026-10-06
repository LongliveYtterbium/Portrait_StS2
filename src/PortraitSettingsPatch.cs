using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;
using STS2RitsuLib.Patching.Models;

namespace STS2Portrait.Patches;

/// <summary>Gives the native settings controls phone-sized bounds and readable text.</summary>
internal sealed class PortraitSettingsPatch : IPatchMethod
{
    public static string PatchId => "sts2portrait_settings";
    public static string Description => "Arrange the native settings screen for portrait play";
    public static bool IsCritical => true;
    public static ModPatchTarget[] GetTargets() => new[]
    {
        PatchTarget.Method(typeof(NSettingsScreen), "_Ready"),
        PatchTarget.Method(typeof(NSettingsScreen), "OnSubmenuOpened"),
        PatchTarget.Method(typeof(NSettingsScreen), "OnSubmenuShown"),
        PatchTarget.Method(typeof(NSettingsTabManager), "SwitchTabTo"),
        PatchTarget.Method(typeof(NSettingsTabManager), "UpdateControllerButton"),
        PatchTarget.Method(typeof(NResolutionDropdown), "PopulateDropdownItems"),
        PatchTarget.Method(typeof(NDropdown), "OpenDropdown"),
    };

    private static readonly ConditionalWeakTable<NSettingsScreen, LayoutState> States = new();

    public static void Postfix(Node __instance, MethodBase __originalMethod)
    {
        if (Entry.IsDisabled)
            return;
        Node? ancestor = __instance;
        while (ancestor != null && ancestor is not NSettingsScreen)
            ancestor = ancestor.GetParent();
        if (ancestor is not NSettingsScreen screen)
            return;

        // Child dropdowns populate before the screen is ready. The screen's own
        // ready callback captures their final native structure exactly once.
        if (__instance == screen && __originalMethod.Name == "_Ready")
            States.GetValue(screen, value => new LayoutState(value)).Apply();
        else if (States.TryGetValue(screen, out LayoutState? state))
            state.Queue();
    }

    private sealed class LayoutState
    {
        private const float ContentWidth = 936;
        private const float InputWidth = 440;
        private const float TitleWidth = 436;
        private readonly NSettingsScreen _screen;
        private readonly NSettingsTabManager _tabs;
        private readonly NScrollableContainer _scroll;
        private readonly Control _mask;
        private readonly Control _clipper;
        private readonly NSettingsPanel[] _panels;
        private readonly NBackButton _back;
        private readonly Vector2 _backOffset;
        private readonly Panel _backSurface;
        private readonly Label _backLabel;
        private readonly Dictionary<Control, PortraitControlSnapshot> _original = new();
        private readonly Dictionary<(Control Control, string Name), Variant> _properties = new();
        private readonly Dictionary<(Control Control, string Name), (bool Had, int Value)> _fonts = new();
        private readonly Dictionary<(Control Control, string Name), (bool Had, int Value)> _constants = new();
        private bool _active;
        private bool _queued;
        private bool _refreshQueued;
        private float _height;

        public LayoutState(NSettingsScreen screen)
        {
            _screen = screen;
            _tabs = screen.GetChildren().OfType<NSettingsTabManager>().Single();
            _scroll = screen.GetChildren().OfType<NScrollableContainer>().Single();
            _mask = _scroll.GetChildren().OfType<Control>().Single(node => node.Name == "Mask");
            _clipper = _mask.GetChildren().OfType<Control>().Single(node => node.Name == "Clipper");
            _panels = _clipper.GetChildren().OfType<NSettingsPanel>().ToArray();
            if (_panels.Length != 4)
                throw new InvalidOperationException($"Expected four native settings panels, found {_panels.Length}.");
            _back = screen.GetChildren().OfType<NBackButton>().Single();
            _backOffset = _back.Get("_posOffset").AsVector2();
            foreach (Control child in _back.GetChildren().OfType<Control>())
                _properties.Add((child, "visible"), child.Visible);

            // Reuse the native back button and its signal/hotkey/tween ownership.
            // Only its portrait skin is new; no second action or settings model exists.
            _backSurface = new Panel
            {
                Name = "PortraitSettingsBackSurface", Visible = false,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            _backSurface.AddThemeStyleboxOverride("panel", new StyleBoxFlat
            {
                BgColor = new Color(0.09f, 0.10f, 0.12f, 0.98f),
                BorderColor = new Color(0.64f, 0.49f, 0.25f),
                BorderWidthLeft = 2, BorderWidthRight = 2, BorderWidthTop = 2, BorderWidthBottom = 2,
                CornerRadiusTopLeft = 18, CornerRadiusTopRight = 18,
                CornerRadiusBottomLeft = 18, CornerRadiusBottomRight = 18,
            });
            _back.AddChild(_backSurface);
            _backSurface.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            _backLabel = new Label
            {
                Name = "PortraitSettingsBackLabel", HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            _backLabel.AddThemeFontSizeOverride("font_size", 48);
            _backSurface.AddChild(_backLabel);
            _backLabel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            screen.Resized += Queue;
            foreach (NSettingsPanel panel in _panels)
                panel.Content.MinimumSizeChanged += QueueRefresh;
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
            // Native resolution changes can replace dropdown entries. Drop only
            // dead snapshots; live controls retain their original restoration data.
            foreach (Control control in _original.Keys.Where(control => !GodotObject.IsInstanceValid(control)).ToArray())
                _original.Remove(control);
            foreach (var key in _properties.Keys.Where(key => !GodotObject.IsInstanceValid(key.Control)).ToArray())
                _properties.Remove(key);
            foreach (var key in _fonts.Keys.Where(key => !GodotObject.IsInstanceValid(key.Control)).ToArray())
                _fonts.Remove(key);
            foreach (var key in _constants.Keys.Where(key => !GodotObject.IsInstanceValid(key.Control)).ToArray())
                _constants.Remove(key);
            if (!PortraitViewportPatch.IsPortrait)
            {
                if (_active)
                    Restore();
                return;
            }
            _active = true;
            Vector2I window = _screen.GetWindow().Size;
            _height = (float)Math.Round(1080.0 * window.Y / window.X);

            Place(_tabs, new Vector2(48, 48), new Vector2(984, 144));
            Constant(_tabs, "separation", 12);
            foreach (Control child in _tabs.GetChildren().OfType<Control>())
            {
                if (child is NSettingsTab tab)
                {
                    Remember(tab);
                    tab.CustomMinimumSize = new Vector2(0, 144);
                    tab.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                    tab.SizeFlagsVertical = Control.SizeFlags.Fill;
                    foreach (Control visual in tab.GetChildren().OfType<Control>())
                    {
                        Fill(visual);
                        if (visual is TextureRect)
                            Saved(visual, "expand_mode", (int)TextureRect.ExpandModeEnum.IgnoreSize);
                        if (visual is Label label)
                            LabelFont(label, 42);
                    }
                }
                else if (child is NHotkeyIcon)
                    Saved(child, "visible", false);
            }

            // Keep the existing gradient, clipper, content and scroll algorithm.
            // Native scrolling measures Size rather than transformed bounds.
            Place(_scroll, Vector2.Zero, new Vector2(1080, _height));
            Place(_mask, Vector2.Zero, new Vector2(1080, _height - 216));
            Place(_clipper, new Vector2(48, 228), new Vector2(984, _height - 468));
            Place(_scroll.Scrollbar, new Vector2(1008, 276), new Vector2(36, _height - 564));
            _mask.Call("OnResized");
            foreach (NSettingsPanel panel in _panels)
            {
                // Preserve Y: the original scroller owns the player's position.
                Place(panel, new Vector2(0, panel.Position.Y), new Vector2(ContentWidth, panel.Size.Y));
                Place(panel.Content, Vector2.Zero, new Vector2(ContentWidth, panel.Content.Size.Y));
                panel.Content.CustomMinimumSize = new Vector2(ContentWidth, 0);
                Constant(panel.Content, "separation", 12);
                if (panel is NInputSettingsPanel)
                    ArrangeInputs(panel);
                else
                    foreach (Control row in panel.Content.GetChildren().OfType<Control>())
                        ArrangeRow(row);
                foreach (NSettingsDropdown dropdown in panel.GetChildren().OfType<NSettingsDropdown>())
                    ArrangeDropdown(dropdown);
            }

            Remember(_back);
            _back.Size = new Vector2(264, 144);
            _back.Set("_posOffset", new Vector2(-56, 192));
            _back.Set("_showPos", new Vector2(56, _height - 192));
            _back.Set("_hidePos", new Vector2(-320, _height - 192));
            _back.Position = _back.Get("_isEnabled").AsBool()
                ? new Vector2(56, _height - 192) : new Vector2(-320, _height - 192);
            foreach (Control artwork in _back.GetChildren().OfType<Control>().Where(node => node != _backSurface))
                // Restore the native arrow instead of the added flat portrait frame.
                Saved(artwork, "visible", _properties[(artwork, "visible")]);
            foreach (NHotkeyIcon hotkey in _back.FindChildren("*", "", true, false).OfType<NHotkeyIcon>())
                Saved(hotkey, "visible", false);
            _backLabel.Text = new LocString("main_menu_ui", "MULTIPLAYER_WARNING_POPUP.back").GetFormattedText();
            _backSurface.Hide();
            QueueRefresh();
        }

        private void ArrangeRow(Control row)
        {
            // Several settings scripts derive from Control while attached to a
            // native MarginContainer, so detect its real engine class here.
            if (!row.IsClass("MarginContainer"))
                return;
            Remember(row);
            row.CustomMinimumSize = new Vector2(0, 168);
            Constant(row, "margin_top", 12);
            Constant(row, "margin_bottom", 12);
            Control content = row.GetChildren().OfType<HBoxContainer>().SingleOrDefault() ?? row;
            if (content is HBoxContainer box)
                Constant(box, "separation", 36);
            RichTextLabel label = content.GetChildren().OfType<RichTextLabel>().Single();
            RichFont(label, 42);
            label.CustomMinimumSize = new Vector2(TitleWidth, 144);
            label.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
            label.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            Control input = content.GetChildren().OfType<Control>().Single(node => node != label);
            ArrangeInput(input);
        }

        private void ArrangeInput(Control input)
        {
            Remember(input);
            float width = input is NTickbox ? 144 : InputWidth;
            input.CustomMinimumSize = new Vector2(width, 144);
            input.SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd;
            input.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            if (input is NTickbox)
            {
                Control visuals = input.GetChildren().OfType<Control>().Single(node => node.Name == "TickboxVisuals");
                Place(visuals, new Vector2(28, 28), new Vector2(88, 88));
                foreach (Control image in visuals.GetChildren().OfType<Control>())
                    Fill(image);
            }
            else if (input is NPaginator)
            {
                foreach (NSettingsPaginatorArrow arrow in input.GetChildren().OfType<NSettingsPaginatorArrow>())
                {
                    bool left = arrow.Name.ToString().Contains("Left", StringComparison.Ordinal);
                    Place(arrow, new Vector2(left ? 0 : InputWidth - 144, 0), new Vector2(144, 144));
                    TextureRect image = arrow.GetChildren().OfType<TextureRect>().Single();
                    Place(image, new Vector2(32, 32), new Vector2(80, 80));
                    Saved(image, "expand_mode", (int)TextureRect.ExpandModeEnum.IgnoreSize);
                }
                Control labels = input.GetChildren().OfType<Control>().Single(node => node.Name == "LabelContainer");
                Remember(labels);
                labels.CustomMinimumSize = Vector2.Zero;
                Place(labels, new Vector2(144, 0), new Vector2(InputWidth - 288, 144));
                // Keep the native mask and both sliding labels in their original
                // hierarchy so the paginator still owns its transition animation.
                Control mask = labels.GetChildren().OfType<Control>().Single(node => node.Name == "Mask");
                Fill(mask);
                foreach (Label label in mask.GetChildren().OfType<Label>())
                {
                    Fill(label);
                    LabelFont(label, 42);
                }
            }
            else if (input is NSettingsSlider)
            {
                NSlider slider = input.GetChildren().OfType<NSlider>().Single();
                Place(slider, new Vector2(96, 0), new Vector2(InputWidth - 96, 144));
                Label value = input.GetChildren().OfType<Label>().Single();
                Place(value, Vector2.Zero, new Vector2(96, 144));
                LabelFont(value, 42);
            }
            else if (input is NButton)
            {
                foreach (Control visual in input.GetChildren().OfType<Control>())
                {
                    Fill(visual);
                    if (visual is TextureRect)
                        Saved(visual, "expand_mode", (int)TextureRect.ExpandModeEnum.IgnoreSize);
                    if (visual is Label label)
                        LabelFont(label, 42);
                }
            }
            foreach (NSelectionReticle reticle in input.GetChildren().OfType<NSelectionReticle>())
                Fill(reticle);
        }

        private void ArrangeDropdown(NSettingsDropdown dropdown)
        {
            Remember(dropdown);
            dropdown.Size = new Vector2(InputWidth, 144);
            dropdown.CustomMinimumSize = new Vector2(InputWidth, 144);
            Control container = dropdown.GetChildren().OfType<Control>().Single(node => node.Name == "Container");
            Fill(container);
            Control current = container.GetChildren().OfType<Control>().Single(node => node.Name == "CurrentOption");
            Fill(current);
            foreach (Control visual in current.GetChildren().OfType<Control>())
            {
                Fill(visual);
                if (visual is Label label)
                {
                    LabelFont(label, 42);
                    label.OffsetRight = -52;
                }
            }
            TextureRect arrow = container.GetChildren().OfType<TextureRect>().Single(node => node.Name == "Arrow");
            Place(arrow, new Vector2(InputWidth - 44, 56), new Vector2(32, 32));
            Control dismisser = container.GetChildren().OfType<Control>().Single(node => node.Name == "Dismisser");
            Place(dismisser, -dropdown.GlobalPosition, new Vector2(1080, _height));
            NDropdownContainer popup = container.GetChildren().OfType<NDropdownContainer>().Single();
            // The original list and its own scrolling remain intact. TopLevel
            // lets this existing popup escape the page's clipping rectangle.
            Remember(popup);
            float popupHeight = Math.Min(720, _height - 480);
            Saved(popup, "top_level", true);
            Saved(popup, "_maxHeight", popupHeight);
            float top = Math.Clamp(dropdown.GlobalPosition.Y + 144, 216, _height - 240 - popupHeight);
            Place(popup, new Vector2(Math.Clamp(dropdown.GlobalPosition.X, 48, 1080 - InputWidth - 48), top), new Vector2(InputWidth, popupHeight));
            VBoxContainer items = popup.GetChildren().OfType<VBoxContainer>().Single();
            Place(items, new Vector2(0, items.Position.Y), new Vector2(InputWidth - 48, items.Size.Y));
            items.CustomMinimumSize = new Vector2(InputWidth - 48, 0);
            Constant(items, "separation", 0);
            foreach (NDropdownItem item in items.GetChildren().OfType<NDropdownItem>())
            {
                Remember(item);
                item.CustomMinimumSize = new Vector2(0, 144);
                item.SizeFlagsHorizontal = Control.SizeFlags.Fill;
                foreach (Control visual in item.GetChildren().OfType<Control>())
                {
                    Fill(visual);
                    if (visual is Label label)
                        LabelFont(label, 42);
                    else if (visual is RichTextLabel rich)
                    {
                        RichFont(rich, 42);
                        Saved(rich, "fit_content", false);
                    }
                }
            }
            foreach (Control visual in popup.GetChildren().OfType<Control>().Where(node => node is ColorRect))
                Fill(visual);
            NDropdownScrollbar scrollbar = popup.GetChildren().OfType<NDropdownScrollbar>().Single();
            Place(scrollbar, new Vector2(InputWidth - 40, 0), new Vector2(40, popupHeight));
            foreach (NSelectionReticle reticle in dropdown.GetChildren().OfType<NSelectionReticle>())
                Fill(reticle);
        }

        private void ArrangeInputs(NSettingsPanel panel)
        {
            foreach (Control row in panel.Content.GetChildren().OfType<Control>())
            {
                if (row is NInputSettingsEntry)
                {
                    Remember(row);
                    row.CustomMinimumSize = new Vector2(0, 168);
                    HBoxContainer columns = row.FindChildren("*", "", true, false).OfType<HBoxContainer>().Single();
                    ArrangeColumns(columns);
                }
                else if (row.Name == "Header")
                {
                    ArrangeColumns((HBoxContainer)row);
                    row.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
                }
                else if (row.Name == "KeyboardOnlyMode")
                    ArrangeRow(row);
                else if (row is HBoxContainer line)
                {
                    Remember(line);
                    line.CustomMinimumSize = new Vector2(0, 168);
                    Constant(line, "separation", 36);
                    RichTextLabel prompt = line.GetChildren().OfType<RichTextLabel>().Single();
                    RichFont(prompt, 42);
                    prompt.CustomMinimumSize = new Vector2(TitleWidth, 144);
                    prompt.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                    ArrangeInput(line.GetChildren().OfType<NButton>().Single());
                }
                else
                {
                    Remember(row);
                    row.CustomMinimumSize = new Vector2(0, row.CustomMinimumSize.Y);
                    row.SizeFlagsHorizontal = Control.SizeFlags.Fill;
                }
            }
            Control promptPanel = panel.GetChildren().OfType<Control>().Single(node => node.Name == "ListeningPrompt");
            // This native top-level prompt retains a landscape minimum width.
            Remember(promptPanel);
            promptPanel.CustomMinimumSize = Vector2.Zero;
            Place(promptPanel, new Vector2((1080 - ContentWidth) / 2, 300), new Vector2(ContentWidth, 240));
            foreach (Control visual in promptPanel.GetChildren().OfType<Control>())
            {
                Fill(visual);
                if (visual is RichTextLabel label)
                {
                    RichFont(label, 42);
                    Saved(label, "fit_content", false);
                }
            }
        }

        private void ArrangeColumns(HBoxContainer columns)
        {
            Remember(columns);
            columns.CustomMinimumSize = new Vector2(912, 144);
            Constant(columns, "separation", 0);
            Control[] children = columns.GetChildren().OfType<Control>().ToArray();
            if (children.Length != 4)
                throw new InvalidOperationException($"Expected four input columns, found {children.Length}.");
            for (int index = 0; index < children.Length; index++)
            {
                Control column = children[index];
                Remember(column);
                column.CustomMinimumSize = new Vector2(index == 0 ? 300 : 204, 144);
                column.SizeFlagsHorizontal = Control.SizeFlags.Fill;
                column.SizeFlagsVertical = Control.SizeFlags.Fill;
                if (column is Label heading)
                    LabelFont(heading, 42);
                foreach (Label label in column.GetChildren().OfType<Label>())
                {
                    Fill(label);
                    LabelFont(label, 42);
                }
                foreach (TextureRect icon in column.GetChildren().OfType<TextureRect>())
                    Place(icon, new Vector2(62, 32), new Vector2(80, 80));
            }
        }

        private void QueueRefresh()
        {
            if (_refreshQueued)
                return;
            _refreshQueued = true;
            Callable.From(() =>
            {
                _refreshQueued = false;
                if (!GodotObject.IsInstanceValid(_screen) || !_screen.IsInsideTree())
                    return;
                // Container sorting precedes this deferred measurement. Native
                // panel/list methods calculate all scroll limits from real sizes.
                foreach (NSettingsPanel panel in _panels)
                    panel.Call(panel is NInputSettingsPanel ? "OnViewportSizeChange" : "RefreshSize");
                foreach (NDropdownContainer popup in _screen.FindChildren("*", "", true, false).OfType<NDropdownContainer>())
                    popup.RefreshLayout();
                _scroll.Call("UpdateScrollLimitBottom");
            }).CallDeferred();
        }

        private void Restore()
        {
            _active = false;
            foreach (var item in _properties)
                if (GodotObject.IsInstanceValid(item.Key.Control))
                    item.Key.Control.Set(item.Key.Name, item.Value);
            foreach (var item in _fonts)
                if (GodotObject.IsInstanceValid(item.Key.Control))
                {
                    if (item.Value.Had)
                        item.Key.Control.AddThemeFontSizeOverride(item.Key.Name, item.Value.Value);
                    else
                        item.Key.Control.RemoveThemeFontSizeOverride(item.Key.Name);
                }
            foreach (var item in _constants)
                if (GodotObject.IsInstanceValid(item.Key.Control))
                {
                    if (item.Value.Had)
                        item.Key.Control.AddThemeConstantOverride(item.Key.Name, item.Value.Value);
                    else
                        item.Key.Control.RemoveThemeConstantOverride(item.Key.Name);
                }
            foreach (var item in _original)
                if (GodotObject.IsInstanceValid(item.Key))
                    item.Value.Restore();
            _backSurface.Hide();
            _back.Set("_posOffset", _backOffset);
            _back.Call("OnWindowChange");
            _mask.Call("OnResized");
            QueueRefresh();
        }

        private void RichFont(RichTextLabel label, int size)
        {
            Remember(label);
            Font(label, "normal_font_size", size);
            Font(label, "bold_font_size", size);
            Saved(label, "fit_content", true);
            Saved(label, "scroll_active", false);
            Saved(label, "autowrap_mode", (int)TextServer.AutowrapMode.WordSmart);
        }

        private void LabelFont(Label label, int size)
        {
            Remember(label);
            // SetTextAutoSize runs again when a setting changes. Preserve the
            // native mode for landscape while keeping the portrait value readable.
            if (label is MegaLabel)
                Saved(label, "AutoSizeEnabled", false);
            Font(label, "font_size", size);
            Saved(label, "autowrap_mode", (int)TextServer.AutowrapMode.WordSmart);
            Saved(label, "vertical_alignment", (int)VerticalAlignment.Center);
        }

        private void Font(Control control, string name, int size)
        {
            var key = (control, name);
            if (!_fonts.ContainsKey(key))
                _fonts.Add(key, (control.HasThemeFontSizeOverride(name), control.GetThemeFontSize(name)));
            control.AddThemeFontSizeOverride(name, size);
        }

        private void Constant(Control control, string name, int value)
        {
            var key = (control, name);
            if (!_constants.ContainsKey(key))
                _constants.Add(key, (control.HasThemeConstantOverride(name), control.GetThemeConstant(name)));
            control.AddThemeConstantOverride(name, value);
        }

        private void Saved(Control control, string property, Variant value)
        {
            var key = (control, property);
            if (!_properties.ContainsKey(key))
                _properties.Add(key, control.Get(property));
            control.Set(property, value);
        }

        private void Fill(Control control)
        {
            Remember(control);
            control.CustomMinimumSize = Vector2.Zero;
            control.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        }

        private void Place(Control control, Vector2 position, Vector2 size)
        {
            Remember(control);
            control.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
            control.Position = position;
            control.Size = size;
            control.PivotOffset = Vector2.Zero;
            control.Scale = Vector2.One;
        }

        private void Remember(Control control)
        {
            if (!_original.ContainsKey(control))
                _original.Add(control, new PortraitControlSnapshot(control));
        }
    }
}
