using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using MegaCrit.Sts2.Core.Entities.UI;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.CustomRun;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.addons.mega_text;
using STS2RitsuLib.Patching.Models;

namespace STS2Portrait.Patches;

/// <summary>Reuses the character screen's artwork, information and native actions.</summary>
internal sealed class PortraitCharacterSelectPatch : IPatchMethod
{
    public static string PatchId => "sts2portrait_character_select";
    public static string Description => "Arrange character selection vertically";
    public static bool IsCritical => true;
    public static ModPatchTarget[] GetTargets() => new[]
    {
        PatchTarget.Method(typeof(NCharacterSelectScreen), "_Ready"),
        PatchTarget.Method(typeof(NCharacterSelectScreen), "OnSubmenuOpened"),
        PatchTarget.Method(typeof(NCharacterSelectScreen), "SelectCharacter"),
        PatchTarget.Method(typeof(NCustomRunScreen), "_Ready"),
        PatchTarget.Method(typeof(NCustomRunScreen), "AfterInitialized"),
        PatchTarget.Method(typeof(NCustomRunScreen), "OnSubmenuOpened"),
        PatchTarget.Method(typeof(NCustomRunScreen), "SelectCharacter"),
        PatchTarget.Method(typeof(NBackButton), "OnEnable"),
        PatchTarget.Method(typeof(NBackButton), "OnDisable"),
        PatchTarget.Method(typeof(NBackButton), "OnWindowChange"),
        PatchTarget.Method(typeof(NConfirmButton), "OnEnable"),
        PatchTarget.Method(typeof(NConfirmButton), "OnDisable"),
        PatchTarget.Method(typeof(NConfirmButton), "OnWindowChange"),
    };

    private static readonly ConditionalWeakTable<NCharacterSelectScreen, LayoutState> States = new();

    private static readonly ConditionalWeakTable<NCustomRunScreen, CustomLayoutState> CustomStates = new();

    public static void Prefix(Node __instance, MethodBase __originalMethod)
    {
        if (Entry.IsDisabled)
            return;
        // Preserve the existing character screen's native animation destinations.
        if (__instance is NCharacterSelectScreen characters && __originalMethod.Name != "_Ready")
            States.GetValue(characters, screen => new LayoutState(screen)).Apply();
        else if (__instance is NCustomRunScreen custom && __originalMethod.Name == "OnSubmenuOpened")
            CustomStates.GetValue(custom, screen => new CustomLayoutState(screen)).Apply();
        else if (__instance is NBackButton or NConfirmButton)
            FindCustomState(__instance)?.BeforeAction((Control)__instance);
    }

    public static void Postfix(Node __instance, MethodBase __originalMethod)
    {
        if (Entry.IsDisabled)
            return;
        if (__instance is NCharacterSelectScreen characters)
            States.GetValue(characters, screen => new LayoutState(screen)).Queue(__originalMethod.Name == "SelectCharacter");
        else if (__instance is NCustomRunScreen custom)
            CustomStates.GetValue(custom, screen => new CustomLayoutState(screen)).Queue();
        else if (__originalMethod.Name == "OnWindowChange")
            FindCustomState(__instance)?.Queue();
    }

    private static CustomLayoutState? FindCustomState(Node node)
    {
        // Shared button hooks must never create an owner for another screen.
        for (Node? parent = node.GetParent(); parent != null; parent = parent.GetParent())
            if (parent is NCustomRunScreen custom)
                return CustomStates.TryGetValue(custom, out CustomLayoutState? state) ? state : null;
        return null;
    }

    private sealed class LayoutState
    {
        private readonly NCharacterSelectScreen _screen;
        private readonly Control _info;
        private readonly VBoxContainer _infoBox;
        private readonly ScrollContainer _details;
        private readonly int _infoBoxIndex;
        private readonly Control _relic;
        private readonly RichTextLabel _relicDescription;
        private readonly Control _animated;
        private readonly Control _static;
        private readonly Control _characters;
        private readonly HBoxContainer _buttons;
        private readonly NAscensionPanel _ascension;
        private readonly NConfirmButton _confirm;
        private readonly NBackButton[] _backButtons;
        private readonly Dictionary<Control, PortraitControlSnapshot> _original = new();
        private readonly Dictionary<Control, Vector2> _buttonOffsets = new();
        private readonly Dictionary<(Control Control, string Name), (bool HadOverride, int Size)> _fonts = new();
        private readonly Dictionary<RichTextLabel, (bool Fit, bool Scroll, bool AutoSize)> _richSettings = new();
        private readonly Dictionary<Control, Control.MouseFilterEnum> _inputFilters = new();
        private readonly Dictionary<Control, (Panel Panel, Label Label)> _actions = new();
        private readonly Dictionary<Control, bool> _actionArtwork = new();
        private readonly Dictionary<Control, Rect2> _artworkBounds = new();
        // Bind only the native static base; the animated scene keeps its original owners.
        private TextureRect? _portraitBackdrop;
        private TextureRect? _backgroundBaseImage;
        private Texture2D? _backgroundBaseTexture;
        private bool _active;
        private bool _queued;
        private bool _resetDetails;
        private float _layoutHeight;

        public LayoutState(NCharacterSelectScreen screen)
        {
            _screen = screen;
            Control[] children = screen.GetChildren().OfType<Control>().ToArray();
            _info = children.Single(node => node.Name == "InfoPanel");
            _infoBox = _info.GetChildren().OfType<VBoxContainer>().Single();
            _infoBoxIndex = _infoBox.GetIndex();
            _relic = _infoBox.GetChildren().OfType<Control>().Single(node => node.Name == "Relic");
            _relicDescription = _relic.GetChildren().OfType<RichTextLabel>().Single();
            // Use Godot's existing scrolling rather than duplicating rich text or
            // truncating localized descriptions. Reparent back on landscape exit.
            _details = new ScrollContainer
            {
                Name = "PortraitCharacterDetails", Visible = false,
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
                VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
                FollowFocus = true,
            };
            _info.AddChild(_details);
            _relicDescription.Resized += () =>
            {
                if (_active)
                    _relic.CustomMinimumSize = new Vector2(0, Math.Max(168, _relicDescription.Size.Y + 72));
            };
            _animated = children.Single(node => node.Name == "AnimatedBg");
            // Its native class is TextureRect, but the attached C# script derives
            // from Control. Match the managed base actually used by that script.
            _static = children.Single(node => node.Name == "StaticBg");
            _characters = children.Single(node => node.Name == "CharSelectButtons");
            _buttons = _characters.GetChildren().OfType<HBoxContainer>().Single();
            _ascension = children.OfType<NAscensionPanel>().Single();
            _confirm = children.OfType<NConfirmButton>().Single();
            _backButtons = children.OfType<NBackButton>().ToArray();
            foreach (Control button in _backButtons.Cast<Control>().Append(_confirm))
            {
                _buttonOffsets.Add(button, button.Get("_posOffset").AsVector2());
                foreach (Control artwork in button.GetChildren().OfType<Control>())
                    _actionArtwork.Add(artwork, artwork.Visible);
                // Keep the original button's input, focus and animation behavior.
                // Its portrait-only skin adds an explicit localized action label.
                Panel panel = new() { Name = "PortraitActionPanel", Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
                bool primary = button == _confirm;
                // Keep the native action owner and use the game's existing paper textures.
                panel.AddThemeStyleboxOverride("panel", new StyleBoxTexture
                {
                    Texture = ResourceLoader.Load<Texture2D>(primary
                        ? "res://images/packed/common_ui/event_button.png" : "res://images/ui/hover_tip.png"),
                    TextureMarginLeft = primary ? 192 : 55, TextureMarginTop = primary ? 50 : 43,
                    TextureMarginRight = primary ? 192 : 91, TextureMarginBottom = primary ? 50 : 32,
                    AxisStretchHorizontal = primary ? StyleBoxTexture.AxisStretchMode.Stretch : StyleBoxTexture.AxisStretchMode.Tile,
                    AxisStretchVertical = primary ? StyleBoxTexture.AxisStretchMode.Stretch : StyleBoxTexture.AxisStretchMode.Tile,
                    ContentMarginLeft = 0, ContentMarginTop = 0,
                    ContentMarginRight = 0, ContentMarginBottom = 0,
                });
                button.AddChild(panel);
                Label label = new()
                {
                    Name = "PortraitActionLabel", Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                };
                label.AddThemeFontSizeOverride("font_size", 48);
                button.AddChild(label);
                _actions.Add(button, (panel, label));
            }
            // Reuse the scene's size signal rather than polling every frame.
            screen.Resized += () => Queue();
            screen.TreeExiting += RestoreBackground;
        }

        public void Queue(bool resetDetails = false)
        {
            _resetDetails |= resetDetails;
            if (_queued)
                return;
            _queued = true;
            Callable.From(() =>
            {
                _queued = false;
                if (GodotObject.IsInstanceValid(_screen) && _screen.IsInsideTree())
                {
                    Apply();
                    if (_resetDetails)
                        _details.ScrollVertical = 0;
                    _resetDetails = false;
                }
            }).CallDeferred();
        }

        public void Apply()
        {
            RestoreBackground();
            // Character changes replace only the game's own background nodes.
            // Release snapshots of those nodes; the mod never destroys a node.
            foreach (Control control in _original.Keys.Where(control => !GodotObject.IsInstanceValid(control)).ToArray())
            {
                _original.Remove(control);
                _artworkBounds.Remove(control);
            }

            bool portrait = PortraitViewportPatch.IsPortrait;
            Vector2I window = _screen.GetWindow().Size;
            float height = (float)Math.Round(1080.0 * window.Y / window.X);
            if (_active != portrait || (_active && height != _layoutHeight))
            {
                // Native tweens capture their destination at creation. Complete
                // them through the game's helper before changing coordinate space,
                // so their final write cannot undo the new layout or skip signals.
                Tween?[] transitions =
                {
                    _screen.Get("_infoPanelTween").AsGodotObject() as Tween,
                    _confirm.Get("_moveTween").AsGodotObject() as Tween,
                    _ascension.Get("_tween").AsGodotObject() as Tween,
                };
                foreach (Tween? tween in transitions.Concat(_backButtons.Select(button => button.Get("_moveTween").AsGodotObject() as Tween)))
                    if (tween != null && tween.IsValid() && tween.IsRunning())
                        tween.FastForwardToCompletion();
            }

            if (!portrait)
            {
                if (!_active)
                    return;
                _active = false;
                foreach (var entry in _fonts)
                {
                    if (entry.Value.HadOverride)
                        entry.Key.Control.AddThemeFontSizeOverride(entry.Key.Name, entry.Value.Size);
                    else
                        entry.Key.Control.RemoveThemeFontSizeOverride(entry.Key.Name);
                }
                foreach (var entry in _richSettings)
                {
                    entry.Key.FitContent = entry.Value.Fit;
                    entry.Key.ScrollActive = entry.Value.Scroll;
                    entry.Key.Set("AutoSizeEnabled", entry.Value.AutoSize);
                }
                foreach (var entry in _inputFilters)
                    entry.Key.MouseFilter = entry.Value;
                if (_infoBox.GetParent() == _details)
                {
                    _infoBox.Reparent(_info, false);
                    _info.MoveChild(_infoBox, _infoBoxIndex);
                }
                _details.Hide();
                foreach (var visual in _actions.Values)
                {
                    visual.Panel.Hide();
                    visual.Label.Hide();
                }
                foreach (var artwork in _actionArtwork)
                    artwork.Key.Visible = artwork.Value;
                foreach (PortraitControlSnapshot snapshot in _original.Values.Reverse())
                    snapshot.Restore();
                foreach (var entry in _buttonOffsets)
                {
                    entry.Key.Set("_posOffset", entry.Value);
                    entry.Key.Call("OnWindowChange");
                }
                _screen.Set("_infoPanelPosFinalVal", _info.Position);
                return;
            }

            _active = true;
            _layoutHeight = height;
            // Fixed lower regions total 1332 canvas units: details 620, ascension
            // 224, portraits 224, actions 144, three gaps 24 and safe bottom 48.
            float artworkHeight = height - 1332;
            Place(_animated, Vector2.Zero, new Vector2(1080, artworkHeight));
            Place(_static, new Vector2((1080 - 2560 * artworkHeight / 1200) / 2, 0), new Vector2(2560, 1200));
            _static.Scale = Vector2.One * (artworkHeight / 1200);
            foreach (Control background in _animated.GetChildren().OfType<Control>()
                .Where(node => node.Name.ToString().EndsWith("_bg", StringComparison.Ordinal)))
            {
                if (!_artworkBounds.TryGetValue(background, out Rect2 bounds))
                {
                    Node2D? spine = background.GetChildren().OfType<Node2D>()
                        .SingleOrDefault(node => node.GetClass() == "SpineSprite");
                    if (spine != null)
                    {
                        // Export bounds are immutable. Measuring the current
                        // animation frame would cause the artwork to keep zooming.
                        GodotObject data = spine.Get("skeleton_data_res").AsGodotObject();
                        float x = data.Call("get_x").AsSingle();
                        float y = data.Call("get_y").AsSingle();
                        float width = data.Call("get_width").AsSingle();
                        float exportedHeight = data.Call("get_height").AsSingle();
                        // Spine exports Y upwards; Godot's canvas points downwards.
                        Rect2 source = new(x, -y - exportedHeight, width, exportedHeight);
                        Transform2D transform = spine.Transform;
                        bounds = new Rect2(transform * source.Position, Vector2.Zero);
                        bounds = bounds.Expand(transform * new Vector2(source.End.X, source.Position.Y));
                        bounds = bounds.Expand(transform * source.End);
                        bounds = bounds.Expand(transform * new Vector2(source.Position.X, source.End.Y));
                    }
                    else
                    {
                        // The game's random-character scene uses a gradient
                        // TextureRect instead of a Spine animation.
                        bounds = background.GetChildren().OfType<TextureRect>().Single().GetRect();
                    }
                    _artworkBounds.Add(background, bounds);
                    Log.Info($"{Entry.LogTag} character artwork {background.Name} bounds={bounds}");
                }
                // Native character/leg attachment bottoms are Spine Y=-2401; convert through
                // each scene's original .46 scale and Y offset. Keep cached export bounds intact.
                // Fit only these two bodies to the artwork region so their flat edge meets the panel.
                if (background.Name == "SILENT_bg")
                    bounds.Size = new Vector2(bounds.Size.X, 1084.46f - bounds.Position.Y);
                else if (background.Name == "DEFECT_bg")
                {
                    // The original backgroundbottom mesh has a flat, nonzero-alpha top edge.
                    // Fit that visible environment edge instead of the export's upper glow extent.
                    bounds.Position = new Vector2(bounds.Position.X, -85.37045f);
                    bounds.Size = new Vector2(bounds.Size.X, 1077.46f - bounds.Position.Y);
                }
                Vector2 target = new(1080, artworkHeight);
                float scale = Math.Max(target.X / bounds.Size.X, target.Y / bounds.Size.Y);
                Vector2 position = (target - bounds.Size * scale) * 0.5f - bounds.Position * scale;
                // The native head's authored right edge is 1729.88 after the scene transform.
                // Keep that face inside the phone; preserve the existing body fit and animation tree.
                if (background.Name == "DEFECT_bg")
                    position.X = Math.Min(position.X, target.X - 48 - 1729.88f * scale);
                Place(background, position, background.Size);
                background.Scale = Vector2.One * scale;
            }

            // These two native TextureRects are static backgrounds, separate from Spine and fire slots.
            TextureRect? nativeBackground = _animated.GetChildren().OfType<Control>()
                .Where(node => node.Name.ToString().EndsWith("_bg", StringComparison.Ordinal))
                .SelectMany(node => node.GetChildren().OfType<TextureRect>())
                .SingleOrDefault(node => node.Name == "TextureRect" &&
                    (node.Texture?.ResourcePath is
                        "res://animations/character_select/silent/character_select_silent_bg.png" or
                        "res://animations/character_select/necrobinder/character_select_necrobinder_bg.png"));
            string? portraitResource = nativeBackground?.Texture?.ResourcePath switch
            {
                "res://animations/character_select/silent/character_select_silent_bg.png" =>
                    "res://STS2Portrait/portrait/characters/character_select_silent_bg-portrait-v1.png",
                "res://animations/character_select/necrobinder/character_select_necrobinder_bg.png" =>
                    "res://STS2Portrait/portrait/characters/character_select_necrobinder_bg-portrait-v1.png",
                _ => null,
            };
            if (nativeBackground != null && portraitResource != null)
            {
                Texture2D portraitTexture = ResourceLoader.Load<Texture2D>(portraitResource)
                    ?? throw new InvalidOperationException($"Portrait character background failed to load: {portraitResource}");
                if (_portraitBackdrop == null)
                {
                    // The screen owns this quad: native SelectCharacter clears every AnimatedBg child.
                    _portraitBackdrop = new TextureRect
                    {
                        Name = "PortraitCharacterBackdrop", Visible = false,
                        MouseFilter = Control.MouseFilterEnum.Ignore,
                        ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                        StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
                    };
                    _screen.AddChild(_portraitBackdrop);
                    _screen.MoveChild(_portraitBackdrop, _animated.GetIndex());
                }
                _backgroundBaseImage = nativeBackground;
                _backgroundBaseTexture = nativeBackground.Texture;
                nativeBackground.Texture = null;
                _portraitBackdrop.Texture = portraitTexture;
                _portraitBackdrop.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
                _portraitBackdrop.Position = Vector2.Zero;
                _portraitBackdrop.Size = new Vector2(1080, height);
                _portraitBackdrop.Show();
            }

            Place(_info, new Vector2(48, artworkHeight), new Vector2(984, 620));
            ArrangeInformation();
            _screen.Set("_infoPanelPosFinalVal", _info.Position);
            Place(_ascension, new Vector2(48, height - 688), new Vector2(984, 224));
            ArrangeAscension();
            Place(_characters, new Vector2(48, height - 440), new Vector2(984, 224));
            Vector2 minimum = _buttons.GetCombinedMinimumSize();
            // Six native 100-wide portraits with 16-unit gaps fit without reducing
            // their 48 dp targets at the 360 dp phone baseline.
            const float buttonScale = 1.44f;
            Place(_buttons, new Vector2((984 - minimum.X * buttonScale) / 2, 0), minimum);
            _buttons.Scale = Vector2.One * buttonScale;

            // Native enable/disable tweens use these positions. Move their hidden
            // destinations fully outside the phone, preserving disabled behavior.
            ConfigureAction(_confirm, new Vector2(468, 192), new Vector2(612, height - 192), new Vector2(1160, height - 192));
            foreach (NBackButton button in _backButtons)
                ConfigureAction(button, new Vector2(-48, 192), new Vector2(48, height - 192), new Vector2(-320, height - 192));
        }

        private void RestoreBackground()
        {
            // Restore before rotation, native character replacement, or the screen leaving the tree.
            if (_portraitBackdrop != null && GodotObject.IsInstanceValid(_portraitBackdrop))
                _portraitBackdrop.Hide();
            if (_backgroundBaseImage != null && GodotObject.IsInstanceValid(_backgroundBaseImage))
                _backgroundBaseImage.Texture = _backgroundBaseTexture;
            _backgroundBaseImage = null;
            _backgroundBaseTexture = null;
        }

        private void ArrangeInformation()
        {
            Remember(_infoBox);
            Remember(_relic);
            // Pass display input to the existing ScrollContainer while preserving native hover.
            // Keep native Ignore and Pass filters, and restore each original Stop in landscape.
            foreach (Control display in _infoBox.FindChildren("*", "", true, false).OfType<Control>().Prepend(_infoBox))
            {
                if (!_inputFilters.ContainsKey(display))
                    _inputFilters.Add(display, display.MouseFilter);
                if (_inputFilters[display] == Control.MouseFilterEnum.Stop)
                    display.MouseFilter = Control.MouseFilterEnum.Pass;
            }
            if (_infoBox.GetParent() != _details)
                _infoBox.Reparent(_details, false);
            _details.Show();
            Place(_info.GetChildren().OfType<NinePatchRect>().Single(), Vector2.Zero, new Vector2(984, 620));
            Place(_details, new Vector2(24, 24), new Vector2(936, 572));
            _infoBox.CustomMinimumSize = new Vector2(904, 0);
            _infoBox.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            _infoBox.SizeFlagsVertical = Control.SizeFlags.ShrinkBegin;

            Label title = _infoBox.GetChildren().OfType<Label>().Single();
            Font(title, "font_size", 68);
            title.CustomMinimumSize = new Vector2(0, 96);
            Control stats = _infoBox.GetChildren().OfType<Control>().Single(node => node.Name == "HpGoldSpacer");
            Remember(stats);
            stats.CustomMinimumSize = new Vector2(0, 72);
            Place(stats.GetChildren().OfType<HBoxContainer>().Single(), Vector2.Zero, new Vector2(904, 72));
            foreach (Label label in stats.FindChildren("*", "", true, false).OfType<Label>())
                Font(label, "font_size", 42);
            foreach (TextureRect icon in stats.FindChildren("*", "", true, false).OfType<TextureRect>())
            {
                Remember(icon);
                icon.CustomMinimumSize = new Vector2(56, 56);
            }
            RichTextLabel description = _infoBox.GetChildren().OfType<RichTextLabel>().Single();
            // Keep body text compact while preserving native title emphasis and detail scrolling.
            RichFont(description, 44);
            description.CustomMinimumSize = Vector2.Zero;

            Control relicTitle = _relic.GetChildren().OfType<MarginContainer>().Single();
            Place(relicTitle, new Vector2(96, 0), new Vector2(808, 64));
            RichFont(relicTitle.GetChildren().OfType<RichTextLabel>().Single(), 48);
            RichFont(_relicDescription, 44);
            Place(_relicDescription, new Vector2(96, 72), new Vector2(808, 96));
            _relic.CustomMinimumSize = new Vector2(0, Math.Max(168, _relicDescription.GetContentHeight() + 72));
            Place(_relic.GetChildren().OfType<Control>().Single(node => node.Name == "Icon"), new Vector2(0, 8), new Vector2(80, 80));
        }

        private void ArrangeAscension()
        {
            Place(_ascension.GetChildren().OfType<NinePatchRect>().Single(), Vector2.Zero, new Vector2(984, 224));
            HBoxContainer row = _ascension.GetChildren().OfType<HBoxContainer>().Single();
            Place(row, Vector2.Zero, new Vector2(984, 224));
            foreach (Control holder in row.GetChildren().OfType<Control>())
            {
                if (holder.Name.ToString().Contains("ArrowContainer", StringComparison.Ordinal))
                {
                    Remember(holder);
                    holder.CustomMinimumSize = new Vector2(144, 144);
                    Control arrow = holder.GetChildren().OfType<Control>().Single();
                    Place(arrow, Vector2.Zero, new Vector2(144, 144));
                }
                else if (holder.Name == "AscensionDescription")
                {
                    Remember(holder);
                    holder.CustomMinimumSize = new Vector2(0, 224);
                    holder.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                    RichTextLabel description = holder.GetChildren().OfType<RichTextLabel>().Single();
                    RichFont(description, 42);
                    description.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
                }
            }
        }

        private void RichFont(RichTextLabel label, int size)
        {
            if (!_richSettings.ContainsKey(label))
                _richSettings.Add(label, (label.FitContent, label.ScrollActive, label.Get("AutoSizeEnabled").AsBool()));
            // Keep the portrait reading size; native auto-size is restored with the original fit mode.
            label.Set("AutoSizeEnabled", false);
            Font(label, "normal_font_size", size);
            Font(label, "bold_font_size", size);
            label.FitContent = true;
            label.ScrollActive = false;
        }

        private void Font(Control control, string name, int size)
        {
            Remember(control);
            var key = (control, name);
            if (!_fonts.ContainsKey(key))
                _fonts.Add(key, (control.HasThemeFontSizeOverride(name), control.GetThemeFontSize(name)));
            control.AddThemeFontSizeOverride(name, size);
        }

        private void ConfigureAction(Control button, Vector2 offset, Vector2 shown, Vector2 hidden)
        {
            Remember(button);
            bool primary = button == _confirm;
            button.Size = new Vector2(primary ? 420 : 264, 144);
            foreach (Control artwork in _actionArtwork.Keys.Where(node => node.GetParent() == button))
                artwork.Visible = primary ? false : _actionArtwork[artwork];
            var visual = _actions[button];
            visual.Panel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            visual.Label.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            visual.Label.Text = primary
                ? new LocString("settings_ui", "INPUT_SETTINGS.INPUT_TITLE.confirm").GetFormattedText()
                : new LocString("main_menu_ui", "MULTIPLAYER_WARNING_POPUP.back").GetFormattedText();
            if (primary)
            {
                visual.Panel.Show();
                visual.Label.Show();
            }
            else
            {
                // Keep the native back arrow, outline and shadow on their original action owner.
                visual.Panel.Hide();
                visual.Label.Hide();
                foreach (NHotkeyIcon hotkey in button.FindChildren("*", "", true, false).OfType<NHotkeyIcon>())
                {
                    if (!_actionArtwork.ContainsKey(hotkey))
                        _actionArtwork.Add(hotkey, hotkey.Visible);
                    hotkey.Hide();
                }
            }
            button.Set("_posOffset", offset);
            button.Set("_showPos", shown);
            button.Set("_hidePos", hidden);
            button.Position = button.Get("_isEnabled").AsBool() ? shown : hidden;
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

    /// <summary>Reflows only the native singleplayer custom-run controls.</summary>
    private sealed class CustomLayoutState
    {
        private readonly NCustomRunScreen _screen;
        private readonly Control _left;
        private readonly Control _right;
        private readonly MegaLabel _title;
        private readonly HBoxContainer _seed;
        private readonly HBoxContainer _characters;
        private readonly NAscensionPanel _ascension;
        private readonly HBoxContainer _modifierHeader;
        private readonly NCustomRunRandomizeButton _randomize;
        private readonly NCustomRunModifiersList _modifiers;
        private readonly NScrollableContainer _scroll;
        private readonly Control _mask;
        private readonly VBoxContainer _content;
        private readonly MegaLabel _disclaimer;
        private readonly NConfirmButton _confirm;
        private readonly NBackButton _back;
        private readonly Dictionary<Control, PortraitControlSnapshot> _geometry = new();
        private readonly Dictionary<(GodotObject Object, string Name), Variant> _properties = new();
        private readonly Dictionary<(Control Control, string Name), (bool HadOverride, int Size)> _fonts = new();
        private readonly Dictionary<(Control Control, string Name), (bool HadOverride, int Value)> _constants = new();
        private readonly Dictionary<Control, (Panel Panel, Label Label)> _actions = new();
        private bool _active;
        private bool _queued;
        private float _height;

        public CustomLayoutState(NCustomRunScreen screen)
        {
            _screen = screen;
            Control[] children = screen.GetChildren().OfType<Control>().ToArray();
            _left = children.Single(node => node.Name == "LeftContainer");
            _right = children.Single(node => node.Name == "RightContainer");
            _title = _left.GetChildren().OfType<MegaLabel>().Single();
            _seed = _left.GetChildren().OfType<HBoxContainer>().Single();
            _characters = (HBoxContainer)screen.Get("_charButtonContainer").AsGodotObject();
            _ascension = (NAscensionPanel)screen.Get("_ascensionPanel").AsGodotObject();
            _randomize = (NCustomRunRandomizeButton)screen.Get("_randomizeButton").AsGodotObject();
            _modifierHeader = (HBoxContainer)_randomize.GetParent();
            _modifiers = (NCustomRunModifiersList)screen.Get("_modifiersList").AsGodotObject();
            _scroll = _modifiers.GetChildren().OfType<NScrollableContainer>().Single();
            _mask = _scroll.GetChildren().OfType<Control>().Single(node => node.Name == "Mask");
            _content = _mask.GetChildren().OfType<VBoxContainer>().Single();
            _disclaimer = (MegaLabel)screen.Get("_disclaimer").AsGodotObject();
            _confirm = (NConfirmButton)screen.Get("_confirmButton").AsGodotObject();
            _back = (NBackButton)screen.Get("_backButton").AsGodotObject();
            // The submenu is cached. Keep one subscription and detach from Window
            // when this native screen actually leaves the tree.
            Window window = screen.GetWindow();
            window.SizeChanged += Queue;
            screen.Resized += Queue;
            _ascension.VisibilityChanged += Queue;
            _content.MinimumSizeChanged += Queue;
            screen.TreeExiting += () =>
            {
                window.SizeChanged -= Queue;
                screen.Resized -= Queue;
                _ascension.VisibilityChanged -= Queue;
                _content.MinimumSizeChanged -= Queue;
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

        private bool UsesPortrait => PortraitViewportPatch.IsPortrait &&
            (MultiplayerUiMode)_screen.Get("_uiMode").AsInt32() == MultiplayerUiMode.Singleplayer;

        public void BeforeAction(Control button)
        {
            if (!_active || !UsesPortrait || (button != _confirm && button != _back))
                return;
            Vector2I window = _screen.GetWindow().Size;
            SetActionDestination(button, (float)Math.Round(1080f * window.Y / window.X));
        }

        public void Apply()
        {
            bool portrait = UsesPortrait;
            Vector2I window = _screen.GetWindow().Size;
            float height = (float)Math.Round(1080f * window.Y / window.X);
            bool coordinatesChanged = _active != portrait || (_active && _height != height);
            if (coordinatesChanged)
            {
                // Complete native transitions before changing coordinates; retain
                // their completion signals and never replace the selection flow.
                foreach (GodotObject owner in new GodotObject[] { _back, _confirm, _ascension })
                {
                    Tween? tween = owner.Get(owner == _ascension ? "_tween" : "_moveTween").AsGodotObject() as Tween;
                    if (tween != null && tween.IsValid() && tween.IsRunning())
                        tween.FastForwardToCompletion();
                }
            }
            if (!portrait)
            {
                Restore();
                return;
            }
            _active = true;
            _height = height;
            // Full-screen parents must pass through empty areas, allowing their
            // native children in both former columns to receive touch input.
            foreach (Control panel in new[] { _left, _right })
            {
                Saved(panel, "mouse_filter", (int)Control.MouseFilterEnum.Ignore);
                Place(panel, Vector2.Zero, new Vector2(1080, height));
            }
            Text(_title, 48);
            Saved(_title, "autowrap_mode", (int)TextServer.AutowrapMode.WordSmart);
            Saved(_title, "vertical_alignment", (int)VerticalAlignment.Center);
            Place(_title, new Vector2(48, 48), new Vector2(300, 120));
            Place(_seed, new Vector2(372, 48), new Vector2(660, 120));
            MegaLabel seedLabel = _seed.GetChildren().OfType<MegaLabel>().Single();
            LineEdit seedInput = _seed.GetChildren().OfType<LineEdit>().Single();
            Text(seedLabel, 40);
            Remember(seedLabel);
            seedLabel.CustomMinimumSize = new Vector2(144, 120);
            Font(seedInput, "font_size", 40);
            seedInput.CustomMinimumSize = new Vector2(0, 120);
            seedInput.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;

            Control characterHolder = (Control)_characters.GetParent();
            Place(characterHolder, new Vector2(48, 192), new Vector2(984, 216));
            // The same native 100-wide, 148-high portraits already used by the
            // normal screen yield five complete 144-wide touch targets.
            Vector2 characterMinimum = _characters.GetCombinedMinimumSize();
            Place(_characters, new Vector2((984 - characterMinimum.X * 1.44f) / 2, 0), characterMinimum);
            _characters.Scale = Vector2.One * 1.44f;
            Place(_ascension, new Vector2(48, 432), new Vector2(984, 192));
            ArrangeAscension();
            // Retain native no-ascension visibility while reclaiming its space.
            float headerTop = _ascension.Visible ? 648 : 432;
            Place(_modifierHeader, new Vector2(48, headerTop), new Vector2(984, 144));
            Constant(_modifierHeader, "separation", 24);
            MegaLabel modifierTitle = _modifierHeader.GetChildren().OfType<MegaLabel>().Single();
            Text(modifierTitle, 48);
            Remember(modifierTitle);
            modifierTitle.CustomMinimumSize = new Vector2(0, 144);
            modifierTitle.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            Saved(modifierTitle, "vertical_alignment", (int)VerticalAlignment.Center);
            foreach (Control hotkey in modifierTitle.GetChildren().OfType<Control>())
                Saved(hotkey, "visible", false);
            Remember(_randomize);
            _randomize.CustomMinimumSize = new Vector2(288, 144);
            _randomize.SizeFlagsVertical = Control.SizeFlags.Fill;
            RichTextLabel randomText = _randomize.GetChildren().OfType<RichTextLabel>().Single();
            Text(randomText, 40);
            foreach (NHotkeyIcon hotkey in _randomize.GetChildren().OfType<NHotkeyIcon>())
                Saved(hotkey, "visible", false);

            float listTop = headerTop + 168;
            float listHeight = height - 312 - listTop;
            Place(_modifiers, new Vector2(48, listTop), new Vector2(984, listHeight));
            Place(_scroll, Vector2.Zero, new Vector2(984, listHeight));
            Place(_mask, Vector2.Zero, new Vector2(888, listHeight));
            Place(_scroll.Scrollbar, new Vector2(912, 0), new Vector2(72, listHeight));
            Saved(_modifiers, "mouse_filter", (int)Control.MouseFilterEnum.Ignore);
            Saved(_mask, "mouse_filter", (int)Control.MouseFilterEnum.Pass);
            // Clip offscreen rows from hit testing as well as rendering.
            Saved(_mask, "clip_contents", true);
            Saved(_content, "mouse_filter", (int)Control.MouseFilterEnum.Pass);
            Remember(_content);
            // Do not write the native content's Y during reflow or selection;
            // NScrollableContainer owns the drag position and scroll inertia.
            _content.AnchorRight = 0;
            _content.Position = new Vector2(0, _content.Position.Y);
            _content.Size = new Vector2(888, _content.Size.Y);
            Constant(_content, "separation", 16);
            foreach (NRunModifierTickbox tickbox in _content.GetChildren().OfType<NRunModifierTickbox>().Where(node => !node.IsQueuedForDeletion()))
            {
                Remember(tickbox);
                tickbox.CustomMinimumSize = new Vector2(0, 144);
                Saved(tickbox, "mouse_filter", (int)Control.MouseFilterEnum.Pass);
                // NButton already cancels a press after this native drag threshold.
                Saved(tickbox, "_ignoreDragThreshold", 24f);
                Saved(tickbox.GetChildren().OfType<Control>().Single(node => node.Name == "Highlight"),
                    "mouse_filter", (int)Control.MouseFilterEnum.Ignore);
                HBoxContainer row = tickbox.GetChildren().OfType<HBoxContainer>().Single();
                Saved(row, "mouse_filter", (int)Control.MouseFilterEnum.Pass);
                Constant(row, "separation", 24);
                Control visuals = row.GetChildren().OfType<Control>().Single(node => node.Name == "TickboxVisuals");
                Remember(visuals);
                visuals.CustomMinimumSize = new Vector2(96, 96);
                visuals.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
                Saved(visuals, "mouse_filter", (int)Control.MouseFilterEnum.Ignore);
                foreach (TextureRect icon in visuals.GetChildren().OfType<TextureRect>())
                {
                    Saved(icon, "expand_mode", (int)TextureRect.ExpandModeEnum.IgnoreSize);
                    Place(icon, new Vector2(12, 12), new Vector2(72, 72));
                    Saved(icon, "mouse_filter", (int)Control.MouseFilterEnum.Ignore);
                }
                RichTextLabel description = row.GetChildren().OfType<RichTextLabel>().Single();
                Text(description, 36);
                description.CustomMinimumSize = new Vector2(0, 96);
                description.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                Saved(description, "fit_content", true);
                Saved(description, "scroll_active", false);
                Saved(description, "mouse_filter", (int)Control.MouseFilterEnum.Ignore);
            }
            // Native scroll limits use Content.Size.Y; follow the real rows after
            // their font and wrapping minima settle, preserving native scroll Y.
            _content.Size = new Vector2(_content.Size.X, _content.GetCombinedMinimumSize().Y);
            // Recalculate native scrollbar visibility after changing its viewport;
            // keep the original padding values and native scroll calculations.
            _scroll.UpdatePadding(_scroll.Get("_paddingTop").AsSingle(), _scroll.Get("_paddingBottom").AsSingle());
            Text(_disclaimer, 28);
            Saved(_disclaimer, "autowrap_mode", (int)TextServer.AutowrapMode.WordSmart);
            Saved(_disclaimer, "vertical_alignment", (int)VerticalAlignment.Center);
            Place(_disclaimer, new Vector2(48, height - 288), new Vector2(984, 72));
            foreach (Control button in new Control[] { _back, _confirm })
                ArrangeAction(button, height, coordinatesChanged);
        }

        private void ArrangeAscension()
        {
            Place(_ascension.GetChildren().OfType<NinePatchRect>().Single(), Vector2.Zero, new Vector2(984, 192));
            HBoxContainer row = _ascension.GetChildren().OfType<HBoxContainer>().Single();
            Place(row, Vector2.Zero, new Vector2(984, 192));
            foreach (Control holder in row.GetChildren().OfType<Control>())
            {
                if (holder.Name.ToString().Contains("GlyphHolder", StringComparison.Ordinal))
                    Saved(holder, "visible", false);
                else if (holder.Name.ToString().Contains("ArrowContainer", StringComparison.Ordinal))
                {
                    Remember(holder);
                    holder.CustomMinimumSize = new Vector2(144, 144);
                    Place(holder.GetChildren().OfType<Control>().Single(), Vector2.Zero, new Vector2(144, 144));
                }
                else if (holder.Name == "AscensionDescription")
                {
                    Remember(holder);
                    holder.CustomMinimumSize = new Vector2(0, 192);
                    holder.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                    RichTextLabel description = holder.GetChildren().OfType<RichTextLabel>().Single();
                    Text(description, 36);
                    Saved(description, "fit_content", false);
                    Saved(description, "scroll_active", true);
                    description.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
                }
            }
        }

        private void SetActionDestination(Control button, float height)
        {
            bool primary = button == _confirm;
            // Save the original offset once; the game's window callback restores
            // its show/hide destinations after leaving this portrait owner.
            Saved(button, "_posOffset", new Vector2(primary ? 468 : -48, 192));
            button.Set("_showPos", new Vector2(primary ? 612 : 48, height - 192));
            button.Set("_hidePos", new Vector2(primary ? 1160 : -320, height - 192));
        }

        private void ArrangeAction(Control button, float height, bool snap)
        {
            Remember(button);
            bool primary = button == _confirm;
            if (!_actions.TryGetValue(button, out var action))
            {
                foreach (Control artwork in button.GetChildren().OfType<Control>())
                    Saved(artwork, "visible", false);
                // A display-only skin on the original button retains all native
                // Released handlers, enabled gates and start/back transitions.
                Panel panel = new() { Name = "PortraitCustomActionPanel", MouseFilter = Control.MouseFilterEnum.Ignore };
                // Match the existing character-confirm paper and centered text at the same touch size.
                panel.AddThemeStyleboxOverride("panel", new StyleBoxTexture
                {
                    Texture = ResourceLoader.Load<Texture2D>("res://images/packed/common_ui/event_button.png"),
                    TextureMarginLeft = 192, TextureMarginTop = 50,
                    TextureMarginRight = 192, TextureMarginBottom = 50,
                    AxisStretchHorizontal = StyleBoxTexture.AxisStretchMode.Stretch,
                    AxisStretchVertical = StyleBoxTexture.AxisStretchMode.Stretch,
                    ContentMarginLeft = 0, ContentMarginTop = 0,
                    ContentMarginRight = 0, ContentMarginBottom = 0,
                });
                Label label = new()
                {
                    Name = "PortraitCustomActionLabel", MouseFilter = Control.MouseFilterEnum.Ignore,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    Text = primary ? new LocString("settings_ui", "INPUT_SETTINGS.INPUT_TITLE.confirm").GetFormattedText()
                        : new LocString("main_menu_ui", "MULTIPLAYER_WARNING_POPUP.back").GetFormattedText(),
                };
                label.AddThemeFontOverride("font", _disclaimer.GetThemeFont("font"));
                label.AddThemeFontSizeOverride("font_size", 48);
                button.AddChild(panel);
                button.AddChild(label);
                action = (panel, label);
                _actions.Add(button, action);
            }
            else
            {
                foreach (Control artwork in button.GetChildren().OfType<Control>().Where(node => node != action.Panel && node != action.Label))
                    Saved(artwork, "visible", false);
            }
            button.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
            button.Size = new Vector2(primary ? 420 : 264, 144);
            button.PivotOffset = button.Size / 2;
            action.Panel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            action.Label.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            if (primary)
            {
                action.Panel.Show();
                action.Label.Show();
            }
            else
            {
                // Reuse the native red paper, arrow, shadow and outline on the existing Back owner.
                action.Panel.Hide();
                action.Label.Hide();
                foreach (Control artwork in button.GetChildren().OfType<Control>().Where(node => node != action.Panel && node != action.Label))
                    Saved(artwork, "visible", _properties[(artwork, "visible")]);
                foreach (NHotkeyIcon hotkey in button.FindChildren("*", "", true, false).OfType<NHotkeyIcon>())
                    Saved(hotkey, "visible", false);
            }
            SetActionDestination(button, height);
            Tween? tween = button.Get("_moveTween").AsGodotObject() as Tween;
            if (snap || tween == null || !tween.IsValid() || !tween.IsRunning())
                button.Position = button.Get(button.Get("_isEnabled").AsBool() ? "_showPos" : "_hidePos").AsVector2();
        }

        private void Restore()
        {
            if (!_active)
                return;
            _active = false;
            // Font and fit-content minima must return before native rectangles.
            foreach (var item in _fonts)
                if (item.Value.HadOverride)
                    item.Key.Control.AddThemeFontSizeOverride(item.Key.Name, item.Value.Size);
                else
                    item.Key.Control.RemoveThemeFontSizeOverride(item.Key.Name);
            foreach (var item in _constants)
                if (item.Value.HadOverride)
                    item.Key.Control.AddThemeConstantOverride(item.Key.Name, item.Value.Value);
                else
                    item.Key.Control.RemoveThemeConstantOverride(item.Key.Name);
            foreach (var item in _properties)
                item.Key.Object.Set(item.Key.Name, item.Value);
            foreach (var action in _actions.Values)
            {
                action.Panel.Hide();
                action.Label.Hide();
            }
            foreach (PortraitControlSnapshot snapshot in _geometry.Values.Reverse())
                snapshot.Restore();
            _back.Call("OnWindowChange");
            _confirm.Call("OnWindowChange");
            _scroll.UpdatePadding(_scroll.Get("_paddingTop").AsSingle(), _scroll.Get("_paddingBottom").AsSingle());
        }

        private void Text(Control label, int size)
        {
            if (label is MegaLabel or MegaRichTextLabel)
                Saved(label, "AutoSizeEnabled", false);
            string[] names = label is RichTextLabel
                ? new[] { "normal_font_size", "bold_font_size", "italics_font_size", "bold_italics_font_size", "mono_font_size" }
                : new[] { "font_size" };
            foreach (string name in names)
                Font(label, name, size);
        }

        private void Font(Control control, string name, int size)
        {
            Remember(control);
            var key = (control, name);
            if (!_fonts.ContainsKey(key))
                _fonts.Add(key, (control.HasThemeFontSizeOverride(name), control.GetThemeFontSize(name)));
            if (control.GetThemeFontSize(name) != size)
                control.AddThemeFontSizeOverride(name, size);
        }

        private void Constant(Control control, string name, int value)
        {
            var key = (control, name);
            if (!_constants.ContainsKey(key))
                _constants.Add(key, (control.HasThemeConstantOverride(name), control.GetThemeConstant(name)));
            if (control.GetThemeConstant(name) != value)
                control.AddThemeConstantOverride(name, value);
        }

        private void Saved(GodotObject owner, string name, Variant value)
        {
            var key = (owner, name);
            if (!_properties.ContainsKey(key))
                _properties.Add(key, owner.Get(name));
            owner.Set(name, value);
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
            if (!_geometry.ContainsKey(control))
                _geometry.Add(control, new PortraitControlSnapshot(control));
        }
    }
}
