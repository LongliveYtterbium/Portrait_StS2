using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.RestSite;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.addons.mega_text;
using STS2RitsuLib.Patching.Models;

namespace STS2Portrait.Patches;

/// <summary>Shows the original rest options as readable portrait rows.</summary>
internal sealed class PortraitRestSitePatch : IPatchMethod
{
    public static string PatchId => "sts2portrait_rest_site";
    public static string Description => "Readable native rest-site choices in portrait";
    public static bool IsCritical => true;
    public static ModPatchTarget[] GetTargets() => new[]
    {
        PatchTarget.Method(typeof(NRestSiteRoom), "UpdateRestSiteOptions"),
        PatchTarget.Method(typeof(NRestSiteRoom), "_Ready"),
        PatchTarget.Method(typeof(NRestSiteButton), "_Ready"),
        PatchTarget.Method(typeof(NRestSiteButton), "RefreshTextState"),
        PatchTarget.Method(typeof(NProceedButton), "OnEnable"),
        PatchTarget.Method(typeof(NProceedButton), "OnDisable"),
    };

    private static readonly ConditionalWeakTable<NRestSiteRoom, LayoutState> States = new();

    private static LayoutState? Owner(Node node)
    {
        for (Node? current = node; current != null; current = current.GetParent())
            if (current is NRestSiteRoom room && States.TryGetValue(room, out LayoutState? state))
                return state;
        return null;
    }

    public static void Prefix(Node __instance, MethodBase __originalMethod)
    {
        if (!Entry.IsDisabled && __instance is NRestSiteRoom room &&
            __originalMethod.Name == "UpdateRestSiteOptions")
        {
            // Ready has cached the empty HBox. Replace only its layout owner
            // before native creation; live buttons must never leave the tree.
            States.GetValue(room, value => new LayoutState(value));
        }
    }

    public static void Postfix(Node __instance, MethodBase __originalMethod)
    {
        if (Entry.IsDisabled)
            return;
        LayoutState? state = Owner(__instance);
        if (state == null)
            return;
        if (__instance is NRestSiteButton button)
            state.RefreshRow(button);
        else if (__instance is NProceedButton)
            state.ArrangeProceed();
        else if (__originalMethod.Name == "UpdateRestSiteOptions")
            state.BeginOptions();
        else
            state.Queue();
    }

    internal static void EntranceFinished(NRestSiteButton button)
    {
        if (GodotObject.IsInstanceValid(button) && button.IsInsideTree())
            Owner(button)?.MarkEntranceFinished(button);
    }

    private sealed class LayoutState
    {
        private readonly NRestSiteRoom _room;
        private readonly HBoxContainer _nativeOptions;
        private readonly BoxContainer _options;
        private readonly ScrollContainer _scroll;
        private readonly Control _content;
        private readonly Control _background;
        private readonly Vector2 _nativeBackgroundPosition;
        private readonly Vector2 _nativeBackgroundScale;
        private readonly MegaLabel _header;
        private readonly MegaRichTextLabel _description;
        private readonly NProceedButton _proceed;
        private readonly Control _proceedImage;
        private readonly MegaLabel _proceedLabel;
        private readonly Panel _pageSurface;
        private readonly TextureRect? _nativeBackdrop;
        private readonly TextureRect? _portraitBackdrop;
        private readonly (CanvasItem Node, bool Visible)[] _portraitBackgroundLayers = Array.Empty<(CanvasItem, bool)>();
        private readonly Dictionary<NRestSiteButton, RowReading> _rows = new();
        private readonly HashSet<NRestSiteButton> _entranceReady = new();
        private readonly Dictionary<Control, PortraitControlSnapshot> _geometry = new();
        private readonly Dictionary<(Control Node, string Name), Variant> _properties = new();
        private readonly Dictionary<(Control Node, string Name), (bool Had, int Value)> _fonts = new();
        private readonly Dictionary<(Control Node, string Name), (bool Had, int Value)> _constants = new();
        private Vector2 _viewport;
        private bool _active;
        private bool _queued;
        private bool _applying;
        private bool _resetScroll;

        private sealed record RowReading(Panel Surface, MegaRichTextLabel Body,
            MegaLabel Title, Control Visuals, Control Outline);

        public LayoutState(NRestSiteRoom room)
        {
            _room = room;
            _viewport = room.GetViewportRect().Size;
            _nativeOptions = room.Get("_choicesContainer").As<HBoxContainer>();
            if (_nativeOptions.GetChildCount() != 0)
                throw new InvalidOperationException("Portrait rest layout must precede native option creation.");
            _header = room.Get("Header").As<MegaLabel>();
            _description = room.Get("Description").As<MegaRichTextLabel>();
            _background = room.Get("BgContainer").As<Control>();
            _nativeBackgroundPosition = new Vector2(_background.AnchorLeft, _background.AnchorTop)
                * NGame.devResolution + new Vector2(_background.OffsetLeft, _background.OffsetTop);
            _nativeBackgroundScale = _background.Scale;
            _proceed = room.Get("_proceedButton").As<NProceedButton>();
            _proceedImage = _proceed.Get("_buttonImage").As<Control>();
            _proceedLabel = _proceed.Get("_label").As<MegaLabel>();
            foreach (Control node in new Control[] { _header, _description, _background,
                _proceed, _proceedImage, _proceedLabel })
                Remember(node);

            _scroll = new ScrollContainer
            {
                Name = "PortraitRestScroll", HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
                VerticalScrollMode = ScrollContainer.ScrollMode.Disabled, ClipContents = false,
                MouseFilter = Control.MouseFilterEnum.Ignore, FollowFocus = true, ScrollDeadzone = 24,
            };
            _scroll.AddThemeStyleboxOverride("panel", new StyleBoxEmpty());
            _content = new Control
            {
                Name = "PortraitRestContent", Size = room.Size,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ExpandFill,
                MouseFilter = Control.MouseFilterEnum.Pass,
            };
            _options = new BoxContainer
            {
                Name = "PortraitRestChoices", Vertical = false, Theme = _nativeOptions.Theme,
                Alignment = _nativeOptions.Alignment, MouseFilter = Control.MouseFilterEnum.Pass,
            };
            _options.AddThemeConstantOverride("separation", _nativeOptions.GetThemeConstant("separation"));
            // Keep the wrapper under ChoicesScreen so native result/FTUE fades
            // still own visibility. Only original buttons are direct Box children.
            _nativeOptions.GetParent().AddChild(_scroll);
            _scroll.AddChild(_content);
            _content.AddChild(_options);
            Place(_scroll, Vector2.Zero, room.Size, false);
            _options.AnchorLeft = _nativeOptions.AnchorLeft;
            _options.AnchorTop = _nativeOptions.AnchorTop;
            _options.AnchorRight = _nativeOptions.AnchorRight;
            _options.AnchorBottom = _nativeOptions.AnchorBottom;
            _options.OffsetLeft = _nativeOptions.OffsetLeft;
            _options.OffsetTop = _nativeOptions.OffsetTop;
            _options.OffsetRight = _nativeOptions.OffsetRight;
            _options.OffsetBottom = _nativeOptions.OffsetBottom;
            Remember(_options);
            _nativeOptions.Hide();
            room.Set("_choicesContainer", _options);
            // Native foreground art overscans its camera canvas. A non-interactive
            // page backing separates it from the prompt without reparenting live art.
            _pageSurface = new Panel
            {
                Name = "PortraitRestPage", Visible = false,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            _pageSurface.AddThemeStyleboxOverride("panel", new StyleBoxFlat
            {
                BgColor = new Color(0.065f, 0.08f, 0.075f, 1f),
                BorderColor = new Color(0.62f, 0.53f, 0.34f, 0.85f), BorderWidthTop = 2,
            });
            room.AddChild(_pageSurface);
            room.MoveChild(_pageSurface, _nativeOptions.GetParent().GetIndex());
            // Match only the four verified static biome backdrops. Underdocks water stays native.
            _nativeBackdrop = _background.FindChildren("*", "TextureRect", true, false).OfType<TextureRect>()
                .SingleOrDefault(node => node.Name.ToString().Contains("RestSiteBG", StringComparison.OrdinalIgnoreCase) &&
                    System.IO.Path.GetFileNameWithoutExtension(node.Texture?.ResourcePath) is
                        "overgrowth_rest_site_bg" or "hive_rest_site_00" or "glory_rest_site_00" or "underdocks_rest_site_bg");
            if (_nativeBackdrop != null)
            {
                string stem = System.IO.Path.GetFileNameWithoutExtension(_nativeBackdrop.Texture!.ResourcePath);
                string resourcePath = stem == "overgrowth_rest_site_bg"
                    ? "res://STS2Portrait/portrait/overgrowth-rest-portrait-v1.png"
                    : $"res://STS2Portrait/portrait/rooms/{stem}-portrait-v1.png";
                Texture2D texture = ResourceLoader.Load<Texture2D>(resourcePath)
                    ?? throw new InvalidOperationException($"Portrait rest background is missing from the STS2Portrait resource pack: {resourcePath}");
                _portraitBackdrop = new TextureRect
                {
                    Name = "PortraitRestBackdrop", Visible = false, Texture = texture,
                    ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                    StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
                    MouseFilter = Control.MouseFilterEnum.Ignore,
                };
                // Draw behind the native art tree; all original actors and effects stay live.
                room.AddChild(_portraitBackdrop);
                room.MoveChild(_portraitBackdrop, 0);
                // Cache only verified static cutters and the shared vignette for exact restore.
                // Keep every actor, prop, fire, water and shader layer; the existing
                // Overgrowth wall-light exception does not extend to another biome.
                _portraitBackgroundLayers = _nativeBackdrop.GetParent().FindChildren("*", "", true, false)
                    .OfType<CanvasItem>().Where(node =>
                        node is TextureRect rect && rect.Material == null &&
                        node.Name.ToString().StartsWith("RestSiteForeground", StringComparison.Ordinal) &&
                        System.IO.Path.GetFileNameWithoutExtension(rect.Texture?.ResourcePath) is
                            "overgrowth_rest_site_cutter_2" or "vignette_2" or
                            "hive_rest_site_cutter_left" or "hive_rest_site_cutter_right" or
                            "glory_rest_site_cutter_1" or "glory_rest_site_cutter_2" or
                            "underdocks_rest_site_cutter_l" or "underdocks_rest_site_cutter_r" ||
                        node is Sprite2D sprite && node.Name.ToString().StartsWith("WallLight", StringComparison.Ordinal) &&
                        sprite.Texture?.ResourcePath.Contains("overgrowth_rest_site_wall_light_", StringComparison.OrdinalIgnoreCase) == true)
                    .Select(node => (node, node.Visible)).ToArray();
            }
            _scroll.ScrollStarted += CancelPresses;
            _content.Resized += Queue;
            _options.MinimumSizeChanged += Queue;
            Window window = room.GetWindow();
            window.SizeChanged += Queue;
            room.TreeExiting += () => window.SizeChanged -= Queue;
        }

        public void Queue()
        {
            if (_queued || _applying)
                return;
            _queued = true;
            Callable.From(() =>
            {
                _queued = false;
                if (GodotObject.IsInstanceValid(_room) && _room.IsInsideTree())
                    Apply();
            }).CallDeferred();
        }

        public void BeginOptions()
        {
            _resetScroll = true;
            Queue();
        }

        public void RefreshRow(NRestSiteButton row)
        {
            if (!_rows.TryGetValue(row, out RowReading? reading))
            {
                MegaLabel title = row.Get("_label").As<MegaLabel>();
                Control visuals = row.Get("_visuals").As<Control>();
                Control outline = row.Get("_outline").As<Control>();
                foreach (Control node in new Control[] { row, title, visuals, outline })
                    Remember(node);
                Panel surface = AddSurface(row, "PortraitRestOptionSurface");
                MegaRichTextLabel body = new()
                {
                    Name = "PortraitRestOptionDescription", Visible = false,
                    Theme = _description.Theme, AutoSizeEnabled = false,
                    BbcodeEnabled = true, FitContent = true, ScrollActive = false,
                    AutowrapMode = TextServer.AutowrapMode.WordSmart,
                    MouseFilter = Control.MouseFilterEnum.Ignore,
                    CustomEffects = _description.CustomEffects.Duplicate(),
                    Size = new Vector2(744, 1),
                };
                // Copy the native renderer and formatted text; never generate
                // another option list or reproduce healing/upgrade calculations.
                foreach (string name in new[] { "normal_font", "bold_font", "italics_font", "bold_italics_font", "mono_font" })
                    body.AddThemeFontOverride(name, _description.GetThemeFont(name));
                foreach (string name in new[] { "normal_font_size", "bold_font_size", "italics_font_size", "bold_italics_font_size", "mono_font_size" })
                    body.AddThemeFontSizeOverride(name, 42);
                foreach (string name in new[] { "default_color", "font_outline_color", "font_shadow_color" })
                    body.AddThemeColorOverride(name, _description.GetThemeColor(name));
                row.AddChild(body);
                reading = new RowReading(surface, body, title, visuals, outline);
                _rows.Add(row, reading);
                body.MinimumSizeChanged += Queue;
                row.TreeExiting += () =>
                {
                    _rows.Remove(row);
                    _entranceReady.Remove(row);
                };
            }
            reading.Body.Text = row.Option.Description.GetFormattedText();
            Queue();
        }

        public void MarkEntranceFinished(NRestSiteButton row)
        {
            _entranceReady.Add(row);
            // Do not snapshot the initial Ignore value: after the native entrance
            // completes, its landscape state is Stop, even across rotation.
            row.MouseFilter = _active && !Entry.IsDisabled && PortraitViewportPatch.IsPortrait
                ? Control.MouseFilterEnum.Pass : Control.MouseFilterEnum.Stop;
        }

        private NRestSiteButton[] LiveRows() => _options.GetChildren().OfType<NRestSiteButton>()
            .Where(row => !row.IsQueuedForDeletion()).ToArray();

        private void Apply()
        {
            if (_applying)
                return;
            _applying = true;
            try
            {
                bool portrait = !Entry.IsDisabled && PortraitViewportPatch.IsPortrait;
                Vector2 viewport = _room.GetViewportRect().Size;
                bool resized = _viewport != viewport;
                _viewport = viewport;
                if (!portrait)
                {
                    if (_active)
                        Restore();
                    if (resized)
                    {
                        Finish(_proceed, "_animTween");
                        _proceed.Position = _proceed.Get(_proceed.IsEnabled ? "ShowPos" : "HidePos").AsVector2();
                    }
                    // The plain content preserves native viewport-relative Box
                    // anchors on subsequent landscape resize notifications too.
                    Place(_scroll, Vector2.Zero, viewport, false);
                    _content.CustomMinimumSize = Vector2.Zero;
                    _content.Size = viewport;
                    return;
                }
                if (!_active)
                {
                    CancelPresses();
                    _content.Size = new Vector2(viewport.X - 96, 1);
                }
                _active = true;
                _options.Vertical = true;
                _options.Alignment = BoxContainer.AlignmentMode.Begin;
                Constant(_options, "separation", 20);
                _scroll.VerticalScrollMode = ScrollContainer.ScrollMode.Auto;
                _scroll.MouseFilter = Control.MouseFilterEnum.Stop;
                _scroll.ClipContents = true;
                // Establish width before measuring the native formatted descriptions.
                // The final vertical budget is based on those measured row heights.
                Place(_scroll, new Vector2(48, 0), new Vector2(viewport.X - 96, viewport.Y), false);
                float width = Math.Min(_scroll.Size.X, _content.Size.X);

                NRestSiteButton[] rows = LiveRows();
                foreach (NRestSiteButton row in rows)
                {
                    if (!_rows.ContainsKey(row))
                        RefreshRow(row);
                    ConfigureRow(row, _rows[row], width);
                }
                for (int i = 0; i < rows.Length; i++)
                {
                    Saved(rows[i], "focus_neighbor_left", rows[i].GetPath());
                    Saved(rows[i], "focus_neighbor_right", rows[i].GetPath());
                    Saved(rows[i], "focus_neighbor_top", rows[(i + rows.Length - 1) % rows.Length].GetPath());
                    Saved(rows[i], "focus_neighbor_bottom", rows[(i + 1) % rows.Length].GetPath());
                }
                float contentHeight = _options.GetCombinedMinimumSize().Y;
                // Reserve three comfortable rows even when only two options exist.
                // Longer descriptions and extra native options keep the same scroll.
                float artHeight = Math.Clamp(viewport.Y - 696 - Math.Max(688, contentHeight), 480, 720);
                float artBottom = 336 + artHeight;
                float listTop = artBottom + 144;
                Place(_pageSurface, new Vector2(0, artBottom), new Vector2(viewport.X, viewport.Y - artBottom), false);
                _pageSurface.Show();
                if (_portraitBackdrop != null)
                {
                    Saved(_nativeBackdrop!, "visible", false);
                    Place(_portraitBackdrop, Vector2.Zero, viewport, false);
                    _portraitBackdrop.Show();
                    foreach (var layer in _portraitBackgroundLayers)
                        layer.Node.Hide();
                    // Let the continuous background show behind the native rows.
                    // A second opaque page would cut through the original fire light.
                    _pageSurface.Hide();
                }
                Saved(_header, "AutoSizeEnabled", false);
                Font(_header, 48);
                Place(_header, new Vector2(48, artBottom + 24), new Vector2(viewport.X - 96, 96));
                Saved(_description, "self_modulate", Colors.Transparent);
                Place(_scroll, new Vector2(48, listTop), new Vector2(viewport.X - 96, viewport.Y - 216 - listTop), false);

                // Keep native actors, props and animation signals in their tree.
                // Biomes without portrait art retain the lower page backing.
                float artScale = Math.Max((viewport.X - 96) / NGame.devResolution.X, artHeight / NGame.devResolution.Y);
                Vector2 artOrigin = new(48 + (viewport.X - 96 - NGame.devResolution.X * artScale) * 0.5f,
                    336 + (artHeight - NGame.devResolution.Y * artScale) * 0.5f);
                _background.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
                _background.Position = artOrigin + _nativeBackgroundPosition * artScale;
                _background.Scale = _nativeBackgroundScale * artScale;
                Place(_options, Vector2.Zero, new Vector2(width, contentHeight));
                _content.CustomMinimumSize = new Vector2(0, contentHeight);
                if (_resetScroll)
                {
                    _scroll.ScrollVertical = 0;
                    _resetScroll = false;
                }
                ArrangeProceed();
            }
            finally
            {
                _applying = false;
            }
        }

        private void ConfigureRow(NRestSiteButton row, RowReading reading, float width)
        {
            Saved(row, "_ignoreDragThreshold", 24f);
            if (_entranceReady.Contains(row))
                row.MouseFilter = Control.MouseFilterEnum.Pass;
            row.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            Saved(reading.Title, "AutoSizeEnabled", false);
            Saved(reading.Title, "autowrap_mode", (int)TextServer.AutowrapMode.WordSmart);
            Saved(reading.Title, "horizontal_alignment", (int)HorizontalAlignment.Left);
            Constant(reading.Title, "line_spacing", 0);
            Font(reading.Title, 48);
            float textWidth = width - 240;
            Place(reading.Title, new Vector2(216, 24), new Vector2(textWidth, 1));
            int lines = Math.Max(1, reading.Title.GetLineCount());
            float titleHeight = reading.Title.GetLineHeight(0) * lines;
            Place(reading.Title, new Vector2(216, 24), new Vector2(textWidth, titleHeight));
            Saved(row, "_labelPosition", reading.Title.Position);
            reading.Body.Size = new Vector2(textWidth, Math.Max(1, reading.Body.Size.Y));
            float bodyHeight = reading.Body.GetContentHeight();
            float height = Math.Max(216, 24 + titleHeight + 12 + bodyHeight + 24);
            row.CustomMinimumSize = new Vector2(0, height);
            row.Size = new Vector2(width, height);
            row.PivotOffset = row.Size * 0.5f;
            Place(reading.Visuals, new Vector2(24, (height - 112) * 0.5f), new Vector2(168, 112));
            reading.Visuals.PivotOffset = new Vector2(84, 56);
            // Native full-rect anchors include extra outline offsets. Give the
            // smaller portrait icon an explicit matching frame and pivot.
            Place(reading.Outline, Vector2.Zero, new Vector2(168, 112));
            reading.Outline.PivotOffset = new Vector2(84, 56);
            reading.Surface.Size = row.Size;
            // Native rest icons and their focus outline already identify the option over the camp art.
            reading.Surface.Hide();
            reading.Body.Position = new Vector2(216, 24 + titleHeight + 12);
            reading.Body.Size = new Vector2(textWidth, Math.Max(1, bodyHeight));
            reading.Body.Show();
        }

        public void ArrangeProceed()
        {
            if (!_active || Entry.IsDisabled || !PortraitViewportPatch.IsPortrait)
                return;
            // Rewards already patches the native animation destination caller.
            // Finish the original rest animation rather than patching it twice.
            Finish(_proceed, "_animTween");
            Vector2 viewport = _room.GetViewportRect().Size;
            // Keep the original arrow, shader, outline and hover feedback. Its
            // generous hit area is separate from the scroll and is not full-width.
            Vector2 size = new(480, 180);
            Place(_proceed, new Vector2(viewport.X - 528, _proceed.IsEnabled ? viewport.Y - 204 : viewport.Y + 48), size);
            _proceed.PivotOffset = size * 0.5f;
            Place(_proceedImage, Vector2.Zero, size);
            _proceedImage.PivotOffset = size * 0.5f;
            foreach (Control child in _proceed.GetChildren().OfType<Control>())
                if (child.Name.ToString().Contains("Hotkey", StringComparison.OrdinalIgnoreCase))
                    Saved(child, "visible", false);
            Saved(_proceedLabel, "AutoSizeEnabled", false);
            Saved(_proceedLabel, "horizontal_alignment", (int)HorizontalAlignment.Center);
            Saved(_proceedLabel, "vertical_alignment", (int)VerticalAlignment.Center);
            Font(_proceedLabel, 48);
            // Leave the native arrow head clear of the centered localized label.
            Place(_proceedLabel, new Vector2(48, 0), new Vector2(336, size.Y));
        }

        private void CancelPresses()
        {
            foreach (NRestSiteButton row in LiveRows())
                row.Set("_isPressed", false);
        }

        private void Restore()
        {
            _active = false;
            CancelPresses();
            Finish(_proceed, "_animTween");
            foreach (NRestSiteButton row in LiveRows())
                Finish(row, "_currentTween");
            foreach (var pair in _fonts)
                if (pair.Value.Had)
                    pair.Key.Node.AddThemeFontSizeOverride(pair.Key.Name, pair.Value.Value);
                else
                    pair.Key.Node.RemoveThemeFontSizeOverride(pair.Key.Name);
            foreach (var pair in _properties.Reverse())
                if (pair.Key.Name != "AutoSizeEnabled")
                    pair.Key.Node.Set(pair.Key.Name, pair.Value);
            foreach (var pair in _constants)
                if (pair.Value.Had)
                    pair.Key.Node.AddThemeConstantOverride(pair.Key.Name, pair.Value.Value);
                else
                    pair.Key.Node.RemoveThemeConstantOverride(pair.Key.Name);
            _options.Vertical = false;
            _options.Alignment = _nativeOptions.Alignment;
            foreach (PortraitControlSnapshot geometry in _geometry.Values)
                geometry.Restore();
            foreach (var pair in _properties)
                if (pair.Key.Name == "AutoSizeEnabled")
                    pair.Key.Node.Set(pair.Key.Name, pair.Value);
            foreach (RowReading reading in _rows.Values)
            {
                reading.Surface.Hide();
                reading.Body.Hide();
            }
            foreach (NRestSiteButton row in _entranceReady)
                row.MouseFilter = Control.MouseFilterEnum.Stop;
            _portraitBackdrop?.Hide();
            foreach (var layer in _portraitBackgroundLayers)
                if (GodotObject.IsInstanceValid(layer.Node))
                    layer.Node.Visible = layer.Visible;
            _pageSurface.Hide();
            _proceed.Position = _proceed.Get(_proceed.IsEnabled ? "ShowPos" : "HidePos").AsVector2();
            _scroll.VerticalScrollMode = ScrollContainer.ScrollMode.Disabled;
            _scroll.MouseFilter = Control.MouseFilterEnum.Ignore;
            _scroll.ClipContents = false;
            _scroll.ScrollVertical = 0;
            _content.CustomMinimumSize = Vector2.Zero;
            _options.QueueSort();
        }

        private static void Finish(Node node, string field)
        {
            if (node.Get(field).AsGodotObject() is Tween tween && tween.IsValid() && tween.IsRunning())
                tween.FastForwardToCompletion();
        }

        private void Remember(Control node)
        {
            if (_geometry.ContainsKey(node))
                return;
            _geometry.Add(node, new PortraitControlSnapshot(node));
            node.TreeExiting += () =>
            {
                _geometry.Remove(node);
                foreach (var key in _properties.Keys.Where(key => key.Node == node).ToArray())
                    _properties.Remove(key);
                foreach (var key in _fonts.Keys.Where(key => key.Node == node).ToArray())
                    _fonts.Remove(key);
                foreach (var key in _constants.Keys.Where(key => key.Node == node).ToArray())
                    _constants.Remove(key);
            };
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
            Remember(node);
            if (!_properties.ContainsKey((node, name)))
                _properties.Add((node, name), node.Get(name));
            node.Set(name, value);
        }

        private void Font(Control node, int size)
        {
            var key = (node, "font_size");
            if (!_fonts.ContainsKey(key))
                _fonts.Add(key, (node.HasThemeFontSizeOverride("font_size"), node.GetThemeFontSize("font_size")));
            if (!node.HasThemeFontSizeOverride("font_size") || node.GetThemeFontSize("font_size") != size)
                node.AddThemeFontSizeOverride("font_size", size);
        }

        private void Constant(Control node, string name, int value)
        {
            var key = (node, name);
            if (!_constants.ContainsKey(key))
                _constants.Add(key, (node.HasThemeConstantOverride(name), node.GetThemeConstant(name)));
            if (!node.HasThemeConstantOverride(name) || node.GetThemeConstant(name) != value)
                node.AddThemeConstantOverride(name, value);
        }

        private static Panel AddSurface(Control parent, string name)
        {
            Panel panel = new() { Name = name, Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
            // Reuse the game's event-choice paper and its original nine-patch
            // margins; the existing native row remains the sole input owner.
            panel.AddThemeStyleboxOverride("panel", new StyleBoxTexture
            {
                Texture = ResourceLoader.Load<Texture2D>("res://images/packed/common_ui/event_button.png"),
                TextureMarginLeft = 192, TextureMarginTop = 50,
                TextureMarginRight = 192, TextureMarginBottom = 50,
                ContentMarginLeft = 0, ContentMarginTop = 0,
                ContentMarginRight = 0, ContentMarginBottom = 0,
            });
            parent.AddChild(panel);
            parent.MoveChild(panel, 0);
            return panel;
        }
    }
}

/// <summary>Preserves the native asynchronous rest-button entrance gate.</summary>
internal sealed class PortraitRestSiteEntrancePatch : IPatchMethod
{
    public static string PatchId => "sts2portrait_rest_site_entrance";
    public static string Description => "Enable rest-option scrolling after native entrance";
    public static bool IsCritical => true;
    public static ModPatchTarget[] GetTargets() => new[]
    {
        PatchTarget.Method(typeof(NRestSiteButton), "AnimateIn"),
    };

    public static void Postfix(NRestSiteButton __instance, ref Task __result)
    {
        if (!Entry.IsDisabled)
            __result = FinishEntrance(__result, __instance);
    }

    private static async Task FinishEntrance(Task original, NRestSiteButton button)
    {
        await original;
        PortraitRestSitePatch.EntranceFinished(button);
    }
}
