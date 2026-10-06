using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.InspectScreens;
using MegaCrit.Sts2.addons.mega_text;
using STS2RitsuLib.Patching.Models;
using OpCodes = System.Reflection.Emit.OpCodes;

namespace STS2Portrait.Patches;

/// <summary>Reflows the two native inspect screens without rebuilding their models.</summary>
internal sealed class PortraitInspectPatch : IPatchMethod
{
    public static string PatchId => "sts2portrait_inspect";
    public static string Description => "Readable native card and relic inspection";
    public static bool IsCritical => true;
    public static ModPatchTarget[] GetTargets() => new[]
    {
        PatchTarget.Method(typeof(NInspectCardScreen), "_Ready"),
        PatchTarget.Method(typeof(NInspectCardScreen), "Open"),
        PatchTarget.Method(typeof(NInspectCardScreen), "Close"),
        PatchTarget.Method(typeof(NInspectCardScreen), "UpdateCardDisplay"),
        PatchTarget.Method(typeof(NInspectRelicScreen), "_Ready"),
        PatchTarget.Method(typeof(NInspectRelicScreen), "Open"),
        PatchTarget.Method(typeof(NInspectRelicScreen), "Close"),
        PatchTarget.Method(typeof(NInspectRelicScreen), "UpdateRelicDisplay"),
    };

    private static readonly ConditionalWeakTable<Control, LayoutState> States = new();
    private static readonly FieldInfo ActiveHoverTips = AccessTools.Field(typeof(NHoverTipSet), "_activeHoverTips");

    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        List<CodeInstruction> body = instructions.ToList();
        if (__originalMethod.DeclaringType != typeof(NInspectCardScreen) || __originalMethod.Name != "Open") return body;
        // Installed Open has exactly one 1.75 start scale and one 2.0 endpoint.
        // Keep the original animation and its ratio while fitting the available portrait height.
        foreach (float native in new[] { 1.75f, 2f })
        {
            int[] indices = body.Select((instruction, index) => (instruction, index))
                .Where(item => item.instruction.opcode == OpCodes.Ldc_R4 && Equals(item.instruction.operand, native))
                .Select(item => item.index).ToArray();
            if (indices.Length != 1) throw new InvalidOperationException($"Expected one InspectCard Open scale {native}, found {indices.Length}.");
            body.InsertRange(indices[0] + 1, new[]
            {
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(PortraitInspectPatch), nameof(CardScale))),
            });
        }
        return body;
    }

    private static float CardScale(float native, NInspectCardScreen screen) =>
        !Entry.IsDisabled && PortraitViewportPatch.IsPortrait && States.TryGetValue(screen, out LayoutState? state) && state.Active
            ? native * state.CardScale / 2f : native;

    public static void Prefix(Control __instance, MethodBase __originalMethod)
    {
        if (!States.TryGetValue(__instance, out LayoutState? state)) return;
        if (__originalMethod.Name == "Open") state.BeforeOpen();
        else if (__originalMethod.Name == "Close") state.BeforeClose();
    }

    public static void Postfix(Control __instance, MethodBase __originalMethod)
    {
        if (__originalMethod.Name == "_Ready")
        {
            if (!Entry.IsDisabled) States.GetValue(__instance, screen => new LayoutState(screen));
            return;
        }
        if (!States.TryGetValue(__instance, out LayoutState? state)) return;
        if (__originalMethod.Name == "Open") state.AfterOpen();
        else if (__originalMethod.Name is "UpdateCardDisplay" or "UpdateRelicDisplay") state.RefreshReading();
    }

    private sealed class LayoutState
    {
        private readonly Control _screen;
        private readonly NCard? _card;
        private readonly Control? _popup;
        private readonly NButton _left;
        private readonly NButton _right;
        private readonly NTickbox? _upgrade;
        private readonly MegaLabel? _upgradeLabel;
        private readonly Label _titleSource;
        private readonly MegaRichTextLabel _bodySource;
        private readonly MegaLabel? _rarity;
        private readonly MegaRichTextLabel? _flavor;
        private readonly Control _hoverTipRect;
        private readonly Panel _reading;
        private readonly ScrollContainer _scroll;
        private readonly VBoxContainer _content;
        private readonly Button _close;
        private readonly Panel? _relicSurface;
        private readonly Dictionary<Control, PortraitControlSnapshot> _geometry = new();
        private readonly Dictionary<(Control Node, string Name), Variant> _properties = new();
        private readonly Dictionary<(Control Node, string Name), (bool Had, int Value)> _fonts = new();
        private NHoverTipSet? _hiddenTips;
        private bool _tipsWereVisible;
        private bool _active;
        private bool _opened;
        private bool _queued;
        private bool _relicFitQueued;
        private bool _applying;
        private Vector2 _viewport;

        public bool Active => _active;
        public float CardScale { get; private set; } = 2f;

        public LayoutState(Control screen)
        {
            _screen = screen;
            _left = screen.Get("_leftButton").As<NButton>();
            _right = screen.Get("_rightButton").As<NButton>();
            _hoverTipRect = screen.Get("_hoverTipRect").As<Control>();
            if (screen is NInspectCardScreen)
            {
                _card = screen.Get("_card").As<NCard>();
                _upgrade = screen.Get("_upgradeTickbox").As<NTickbox>();
                _upgradeLabel = Descendants(_upgrade).OfType<MegaLabel>().Single(node => node.Name == "ShowUpgradeLabel");
                _titleSource = _card.Get("_titleLabel").As<Label>();
                _bodySource = _card.Get("_descriptionLabel").As<MegaRichTextLabel>();
            }
            else
            {
                _popup = screen.Get("_popup").As<Control>();
                _titleSource = screen.Get("_nameLabel").As<MegaLabel>();
                _rarity = screen.Get("_rarityLabel").As<MegaLabel>();
                _bodySource = screen.Get("_description").As<MegaRichTextLabel>();
                _flavor = screen.Get("_flavor").As<MegaRichTextLabel>();
                _relicSurface = new Panel { Name = "PortraitInspectRelicSurface", Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
                // Keep the original relic-popup artwork outside the actual text reader.
                _relicSurface.AddThemeStyleboxOverride("panel", new StyleBoxTexture
                {
                    Texture = _popup.GetChildren().OfType<TextureRect>().Single(node => node.Name == "Bg").Texture,
                    ContentMarginLeft = 0, ContentMarginTop = 0,
                    ContentMarginRight = 0, ContentMarginBottom = 0,
                });
                _popup.AddChild(_relicSurface);
                _popup.MoveChild(_relicSurface, 0);
            }

            _reading = new Panel { Name = "PortraitInspectReading", Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
            _reading.AddThemeStyleboxOverride("panel", SurfaceStyle());
            (_popup ?? screen).AddChild(_reading);
            _scroll = new ScrollContainer
            {
                Name = "PortraitInspectTextScroll", HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
                VerticalScrollMode = ScrollContainer.ScrollMode.Auto, ScrollDeadzone = 24,
                MouseFilter = Control.MouseFilterEnum.Stop,
            };
            _reading.AddChild(_scroll);
            _content = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Pass };
            _content.AddThemeConstantOverride("separation", 24);
            _scroll.AddChild(_content);
            // Text shaping can lower a previous minimum after the first layout.
            // Refit only display sizes; the general Queue rebuilds all text rows.
            _content.MinimumSizeChanged += QueueRelicFit;

            // The native backstop has no visible close action. This button only calls native Close.
            _close = new Button
            {
                Name = "PortraitInspectClose", Text = "", Visible = false,
                MouseFilter = Control.MouseFilterEnum.Stop, ExpandIcon = true,
                IconAlignment = HorizontalAlignment.Center,
                Icon = ResourceLoader.Load<Texture2D>("res://images/atlases/compressed.sprites/back_button_arrow.tres")
                    ?? throw new InvalidOperationException("Native inspect Back arrow failed to load."),
            };
            _close.AddThemeFontOverride("font", _titleSource.GetThemeFont("font"));
            _close.AddThemeFontSizeOverride("font_size", 48);
            _close.AddThemeConstantOverride("icon_max_width", 80);
            // Remove the native scene's asymmetric overscan and preserve the painted paper's aspect ratio.
            var paper = (AtlasTexture)(ResourceLoader.Load<AtlasTexture>("res://images/atlases/ui_atlas.sprites/back_button.tres")
                ?? throw new InvalidOperationException("Native inspect Back paper failed to load.")).Duplicate();
            float paperPadding = paper.Region.Size.X * 144f / 264f - paper.Region.Size.Y;
            paper.Margin = new Rect2(0, paperPadding * .5f, 0, paperPadding);
            StyleBoxTexture normal = new()
            {
                Texture = paper,
                // Preserve the duplicate atlas canvas instead of clipping its padded paper.
                RegionRect = new Rect2(Vector2.Zero, paper.GetSize()),
                ContentMarginLeft = 0, ContentMarginTop = 0,
                ContentMarginRight = 0, ContentMarginBottom = 0,
            };
            normal.ModulateColor = Colors.White;
            _close.AddThemeStyleboxOverride("normal", normal);
            var hover = (StyleBoxTexture)normal.Duplicate();
            hover.ModulateColor = new Color(1.1f, 1.1f, 1.1f);
            _close.AddThemeStyleboxOverride("hover", hover);
            var pressed = (StyleBoxTexture)normal.Duplicate();
            pressed.ModulateColor = new Color(.7f, .7f, .7f);
            _close.AddThemeStyleboxOverride("pressed", pressed);
            // Native Back draws its solid gold outline behind the paper, never as a focus overlay.
            var outline = (AtlasTexture)(ResourceLoader.Load<AtlasTexture>("res://images/atlases/compressed.sprites/back_button_outline.tres")
                ?? throw new InvalidOperationException("Native inspect Back outline failed to load.")).Duplicate();
            float outlinePadding = outline.Region.Size.X * 144f / 264f - outline.Region.Size.Y;
            outline.Margin = new Rect2(0, outlinePadding * .5f, 0, outlinePadding);
            var focusOutline = new TextureRect
            {
                Name = "PortraitInspectBackOutline", Visible = false, Texture = outline,
                Material = ResourceLoader.Load<CanvasItemMaterial>("res://themes/canvas_item_material_additive_shared.tres")
                    ?? throw new InvalidOperationException("Native inspect Back outline material failed to load."),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.Scale,
                MouseFilter = Control.MouseFilterEnum.Ignore, ShowBehindParent = true,
                Modulate = new Color("F0B400"),
            };
            _close.AddChild(focusOutline);
            focusOutline.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            _close.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
            _close.FocusEntered += focusOutline.Show;
            _close.FocusExited += focusOutline.Hide;
            _close.Pressed += () =>
            {
                if (_opened && _active) _screen.Call("Close");
            };
            screen.AddChild(_close);
            // Capture native anchors before any future resize can complete an old-coordinate tween.
            // These controls have static scene geometry; only their displayed data changes later.
            Remember(_card as Control ?? _popup!);
            foreach (NButton arrow in new[] { _left, _right })
            {
                Remember(arrow);
                foreach (Control icon in arrow.GetChildren().OfType<TextureRect>()) Remember(icon);
            }
            if (_upgrade != null)
            {
                Remember(_upgrade);
                Remember(_upgradeLabel!);
                Control visual = _upgrade.Get("_imageContainer").As<Control>();
                Remember(visual);
                foreach (Control image in visual.GetChildren().OfType<Control>()) Remember(image);
            }
            else
            {
                Remember(_screen.Get("_relicImage").As<Control>().GetParent<Control>());
            }
            Viewport viewport = screen.GetViewport();
            viewport.SizeChanged += Queue;
            screen.TreeExiting += () => viewport.SizeChanged -= Queue;
        }

        public void BeforeOpen()
        {
            _opened = false;
            Apply();
        }

        public void AfterOpen()
        {
            _opened = true;
            if (!_active) return;
            _close.Show();
            _reading.Show();
            RefreshReading();
        }

        public void BeforeClose()
        {
            _opened = false;
            _close.Hide();
            _reading.Hide();
            // Native Close still fades its artwork, clears tips, and releases hotkeys.
        }

        private void Queue()
        {
            if (_queued) return;
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
                bool portrait = !Entry.IsDisabled && PortraitViewportPatch.IsPortrait;
                bool changed = portrait != _active || viewport != _viewport;
                if (changed) CompleteNativeTweens();
                _viewport = viewport;
                if (!portrait)
                {
                    if (changed) Restore();
                    return;
                }
                _active = true;
                float width = viewport.X - 96;
                if (_card != null)
                {
                    // Reserve footer, text, and upgrade input first; artwork uses the remaining space.
                    // With the portrait viewport's 1080 design width this stays positive even at 1080x1081.
                    float available = viewport.Y - 552;
                    float readingHeight = Math.Min(600, available * .4f);
                    float readingTop = viewport.Y - 216 - readingHeight;
                    float upgradeTop = readingTop - 168;
                    float artHeight = upgradeTop - 360;
                    float cardY = 336 + artHeight * .5f;
                    CardScale = Math.Min(2, Math.Min(artHeight / 422, (width - 336) / 300));
                    Place(_card, new Vector2(viewport.X * .5f, cardY), _card.Size);
                    _card.Scale = Vector2.One * CardScale;
                    Place(_left, new Vector2(48, cardY - 72), new Vector2(144, 144));
                    Place(_right, new Vector2(viewport.X - 192, cardY - 72), new Vector2(144, 144));
                    Place(_upgrade!, new Vector2(48, upgradeTop), new Vector2(width, 144));
                    Saved(_upgradeLabel!, "AutoSizeEnabled", false);
                    Font(_upgradeLabel!, "font_size", 48);
                    Saved(_upgradeLabel!, "mouse_filter", (int)Control.MouseFilterEnum.Ignore);
                    Control visual = _upgrade!.Get("_imageContainer").As<Control>();
                    Remember(visual);
                    visual.CustomMinimumSize = new Vector2(96, 96);
                    visual.PivotOffset = new Vector2(48, 48);
                    foreach (Control image in visual.GetChildren().OfType<Control>())
                    {
                        Remember(image);
                        image.PivotOffset = new Vector2(48, 48);
                    }
                    Place(_reading, new Vector2(48, readingTop), new Vector2(width, readingHeight), false);
                }
                else
                {
                    Place(_popup!, new Vector2(48, 336), new Vector2(width, viewport.Y - 576));
                    Control background = _popup!.GetChildren().OfType<TextureRect>().Single(node => node.Name == "Bg");
                    Saved(background, "self_modulate", Colors.Transparent);
                    Place(_relicSurface!, Vector2.Zero, _popup.Size, false);
                    _relicSurface!.Show();
                    // Name, rarity, description, and flavor share the scroll on short screens.
                    Saved(_titleSource, "visible", false);
                    Saved(_rarity!, "visible", false);
                    Control frame = _screen.Get("_relicImage").As<Control>().GetParent<Control>();
                    float imageExtent = Math.Min(304, _popup.Size.Y * .3f);
                    Place(frame, new Vector2((width - imageExtent) * .5f, 24), new Vector2(304, 304));
                    frame.PivotOffset = Vector2.Zero;
                    frame.Scale = Vector2.One * imageExtent / 304;
                    // Keep original formatted source labels and their hierarchy intact.
                    Saved(_bodySource.GetParent<Control>(), "visible", false);
                    float readingTop = imageExtent + 48;
                    Place(_reading, new Vector2(24, readingTop), new Vector2(width - 48, _popup.Size.Y - readingTop - 24), false);
                    float arrowY = 336 + 24 + imageExtent * .5f - 72;
                    Place(_left, new Vector2(48, arrowY), new Vector2(144, 144));
                    Place(_right, new Vector2(viewport.X - 192, arrowY), new Vector2(144, 144));
                }
                foreach (NButton arrow in new[] { _left, _right })
                {
                    arrow.PivotOffset = new Vector2(72, 72);
                    Control icon = arrow.GetChildren().OfType<TextureRect>().Single();
                    Remember(icon);
                    icon.PivotOffset = new Vector2(72, 72);
                }
                Place(_scroll, new Vector2(24, 24), _reading.Size - new Vector2(48, 48), false);
                Place(_close, new Vector2(48, viewport.Y - 192), new Vector2(264, 144), false);
                _close.Visible = _opened;
                _reading.Visible = _opened;
                // Native Open and paging use these caches for their original finite tweens.
                UpdatePositionCaches();
                if (_opened) RefreshReading();
            }
            finally { _applying = false; }
        }

        public void RefreshReading()
        {
            if (!_active || !_opened) return;
            foreach (Node row in _content.GetChildren())
            {
                _content.RemoveChild(row);
                row.QueueFree();
            }
            AddTitle(_titleSource);
            if (_card != null) AddCardCost(_card);
            else if (_rarity != null && !string.IsNullOrEmpty(_rarity.Text)) AddTitle(_rarity, 42);
            AddBody(_bodySource, 48);
            if (_flavor != null && !string.IsNullOrEmpty(_flavor.Text)) AddBody(_flavor, 42);

            // Native display creation already computed keyword and associated-card text.
            // Mirror those rendered fields into the same scroll instead of covering it.
            var tips = (Dictionary<Control, NHoverTipSet>)ActiveHoverTips.GetValue(null)!;
            if (tips.TryGetValue(_screen, out NHoverTipSet? tip))
            {
                if (_hiddenTips != tip)
                {
                    _hiddenTips = tip;
                    _tipsWereVisible = tip.Visible;
                }
                VFlowContainer text = tip.Get("_textHoverTipContainer").As<VFlowContainer>();
                foreach (Control block in text.GetChildren().OfType<Control>())
                {
                    Label title = Descendants(block).OfType<Label>().Single(node => node.Name == "Title");
                    MegaRichTextLabel body = Descendants(block).OfType<MegaRichTextLabel>().Single(node => node.Name == "Description");
                    if (title.Visible && !string.IsNullOrEmpty(title.Text)) AddTitle(title);
                    AddBody(body, 48);
                }
                foreach (NCard card in Descendants(tip.Get("_cardHoverTipContainer").As<Node>()).OfType<NCard>())
                {
                    AddTitle(card.Get("_titleLabel").As<Label>());
                    AddCardCost(card);
                    AddBody(card.Get("_descriptionLabel").As<MegaRichTextLabel>(), 48);
                }
                tip.Hide();
            }
            _scroll.ScrollVertical = 0;
            QueueRelicFit();
        }

        private void QueueRelicFit()
        {
            if (_relicFitQueued || !_active || !_opened) return;
            _relicFitQueued = true;
            Callable.From(() =>
            {
                _relicFitQueued = false;
                if (!GodotObject.IsInstanceValid(_screen) || !_screen.IsInsideTree()
                    || Entry.IsDisabled || !PortraitViewportPatch.IsPortrait || !_active || !_opened) return;

                // Reuse the same settled content measurement for card and relic text.
                // Keep the maximum independent of the previous fit so later long text can grow.
                float maximum = _card != null
                    ? Math.Min(600, (_viewport.Y - 552) * .4f)
                    : _popup!.Size.Y - _reading.Position.Y - 24;
                float height = Math.Min(maximum, Math.Max(192, _content.GetCombinedMinimumSize().Y + 48));
                Vector2 readingSize = new(_reading.Size.X, height);
                Vector2 scrollSize = readingSize - new Vector2(48, 48);
                // Explicitly shrink old offsets after minimum-size propagation;
                // unchanged sizes must not queue another container layout.
                if (_reading.Size != readingSize) _reading.Size = readingSize;
                if (_scroll.Size != scrollSize) _scroll.Size = scrollSize;
                if (_popup != null)
                {
                    Vector2 surfaceSize = new(_popup.Size.X, _reading.Position.Y + height + 24);
                    if (_relicSurface!.Size != surfaceSize) _relicSurface.Size = surfaceSize;
                }
            }).CallDeferred();
        }

        private void AddTitle(Label source, int size = 54)
        {
            Label title = new()
            {
                Text = source.Text, Theme = source.Theme, AutowrapMode = TextServer.AutowrapMode.WordSmart,
                Size = new Vector2(_scroll.Size.X, 1), MouseFilter = Control.MouseFilterEnum.Pass,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Modulate = source.Modulate,
            };
            title.AddThemeFontOverride("font", source.GetThemeFont("font"));
            title.AddThemeFontSizeOverride("font_size", size);
            title.AddThemeColorOverride("font_color", source.GetThemeColor("font_color"));
            _content.AddChild(title);
        }

        private void AddCardCost(NCard card)
        {
            Label energy = card.Get("_energyLabel").As<Label>();
            Label star = card.Get("_starLabel").As<Label>();
            bool hasEnergy = card.Get("_energyIcon").As<Control>().Visible;
            bool hasStar = card.Get("_starIcon").As<Control>().Visible;
            string text = string.Join("    ", new[] { hasEnergy ? "能量 " + energy.Text : "", hasStar ? "星能 " + star.Text : "" }.Where(value => value.Length > 0));
            if (text.Length == 0) return;
            Label cost = new() { Text = text, Theme = energy.Theme, MouseFilter = Control.MouseFilterEnum.Pass };
            cost.AddThemeFontOverride("font", energy.GetThemeFont("font"));
            cost.AddThemeFontSizeOverride("font_size", 48);
            _content.AddChild(cost);
        }

        private void AddBody(MegaRichTextLabel source, int size)
        {
            MegaRichTextLabel body = new()
            {
                AutoSizeEnabled = false, BbcodeEnabled = true, FitContent = true, ScrollActive = false,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                Theme = source.Theme, CustomEffects = source.CustomEffects.Duplicate(),
                Size = new Vector2(_scroll.Size.X, 1), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                MouseFilter = Control.MouseFilterEnum.Pass,
            };
            foreach (string font in TextFonts) body.AddThemeFontOverride(font, source.GetThemeFont(font));
            foreach (string font in TextSizes) body.AddThemeFontSizeOverride(font, size);
            foreach (string color in new[] { "default_color", "font_outline_color", "font_shadow_color" })
                body.AddThemeColorOverride(color, source.GetThemeColor(color));
            _content.AddChild(body);
            body.Text = source.Text;
        }

        private void UpdatePositionCaches()
        {
            _screen.Set(_card != null ? "_cardPosition" : "_popupPosition", (_card as Control ?? _popup!).Position);
            _screen.Set("_leftButtonX", _left.Position.X);
            _screen.Set("_rightButtonX", _right.Position.X);
        }

        private void CompleteNativeTweens()
        {
            Complete(_screen.Get(_card != null ? "_openTween" : "_screenTween").AsGodotObject() as Tween);
            Complete(_screen.Get(_card != null ? "_cardTween" : "_popupTween").AsGodotObject() as Tween);
            Complete(_left.Get("_animTween").AsGodotObject() as Tween);
            Complete(_right.Get("_animTween").AsGodotObject() as Tween);
            if (_upgrade != null) Complete(_upgrade.Get("_tween").AsGodotObject() as Tween);
        }

        private void Restore()
        {
            _active = false;
            _close.Hide();
            _reading.Hide();
            _relicSurface?.Hide();
            foreach (var font in _fonts)
                if (font.Value.Had) font.Key.Node.AddThemeFontSizeOverride(font.Key.Name, font.Value.Value);
                else font.Key.Node.RemoveThemeFontSizeOverride(font.Key.Name);
            foreach (PortraitControlSnapshot snapshot in _geometry.Values) snapshot.Restore();
            foreach (var property in _properties.Reverse()) property.Key.Node.Set(property.Key.Name, property.Value);
            // Original center anchors now resolve against the current landscape viewport.
            UpdatePositionCaches();
            if (_hiddenTips != null && GodotObject.IsInstanceValid(_hiddenTips) && !_hiddenTips.IsQueuedForDeletion())
            {
                _hiddenTips.Visible = _tipsWereVisible;
                _hiddenTips.SetAlignment(_hoverTipRect, HoverTip.GetHoverTipAlignment(_screen));
            }
            _hiddenTips = null;
        }

        private void Remember(Control node)
        {
            if (!_geometry.ContainsKey(node)) _geometry.Add(node, new PortraitControlSnapshot(node));
        }

        private void Place(Control node, Vector2 position, Vector2 size, bool remember = true)
        {
            if (remember) Remember(node);
            node.CustomMinimumSize = Vector2.Zero;
            node.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
            node.Position = position;
            node.Size = size;
        }

        private void Saved(Control node, string property, Variant value)
        {
            if (!_properties.ContainsKey((node, property))) _properties.Add((node, property), node.Get(property));
            node.Set(property, value);
        }

        private void Font(Control node, string name, int size)
        {
            if (!_fonts.ContainsKey((node, name))) _fonts.Add((node, name), (node.HasThemeFontSizeOverride(name), node.GetThemeFontSize(name)));
            if (!node.HasThemeFontSizeOverride(name) || node.GetThemeFontSize(name) != size) node.AddThemeFontSizeOverride(name, size);
        }
    }

    private static readonly string[] TextFonts = { "normal_font", "bold_font", "italics_font", "bold_italics_font", "mono_font" };
    private static readonly string[] TextSizes = { "normal_font_size", "bold_font_size", "italics_font_size", "bold_italics_font_size", "mono_font_size" };

    private static IEnumerable<Node> Descendants(Node owner)
    {
        foreach (Node child in owner.GetChildren())
        {
            yield return child;
            foreach (Node nested in Descendants(child)) yield return nested;
        }
    }

    private static void Complete(Tween? tween)
    {
        if (tween != null && tween.IsValid() && tween.IsRunning()) tween.FastForwardToCompletion();
    }

    // Only text readers use hover-tip paper; actions keep the native blue-paper shape.
    private static StyleBoxTexture SurfaceStyle(bool reading = true) => new()
    {
        Texture = ResourceLoader.Load<Texture2D>(reading
            ? "res://images/ui/hover_tip.png" : "res://images/packed/common_ui/event_button.png")
            ?? throw new InvalidOperationException("Native portrait-inspect paper failed to load."),
        TextureMarginLeft = reading ? 55 : 192, TextureMarginTop = reading ? 43 : 50,
        TextureMarginRight = reading ? 91 : 192, TextureMarginBottom = reading ? 32 : 50,
        AxisStretchHorizontal = reading ? StyleBoxTexture.AxisStretchMode.Tile : StyleBoxTexture.AxisStretchMode.Stretch,
        AxisStretchVertical = reading ? StyleBoxTexture.AxisStretchMode.Tile : StyleBoxTexture.AxisStretchMode.Stretch,
        ContentMarginLeft = 0, ContentMarginTop = 0,
        ContentMarginRight = 0, ContentMarginBottom = 0,
    };
}
