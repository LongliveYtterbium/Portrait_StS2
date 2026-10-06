using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.addons.mega_text;
using STS2RitsuLib.Patching.Models;

namespace STS2Portrait.Patches;

/// <summary>Reads original reward cards before committing one native selection.</summary>
internal sealed class PortraitCardRewardPatch : IPatchMethod
{
    public static string PatchId => "sts2portrait_card_reward";
    public static string Description => "Readable native card rewards with explicit touch confirmation";
    public static bool IsCritical => true;
    public static ModPatchTarget[] GetTargets() => new[]
    {
        PatchTarget.Method(typeof(NCardRewardSelectionScreen), "_Ready"),
        PatchTarget.Method(typeof(NCardRewardSelectionScreen), "RefreshOptions"),
        PatchTarget.Method(typeof(NCardRewardSelectionScreen), "AfterOverlayOpened"),
        PatchTarget.Method(typeof(NCardRewardSelectionScreen), "UpdateControllerIcons"),
        PatchTarget.Method(typeof(NCardRewardSelectionScreen), "SelectCard"),
        PatchTarget.Method(typeof(NCardRewardSelectionScreen), "OnAlternateRewardSelected"),
        PatchTarget.Method(typeof(NCardRewardSelectionScreen), "OptionSelected"),
        PatchTarget.Method(typeof(NCommonBanner), "AnimateIn"),
        PatchTarget.Method(typeof(NCommonBanner), "OnWindowChange"),
        PatchTarget.Method(typeof(NCardHolder), "DoCardHoverEffects"),
        PatchTarget.Method(typeof(NCardHolder), "CreateHoverTips"),
        PatchTarget.Method(typeof(NCard), "UpdateVisuals"),
    };

    private static readonly ConditionalWeakTable<NCardRewardSelectionScreen, LayoutState> States = new();
    private static readonly FieldInfo ActiveHoverTips =
        typeof(NHoverTipSet).GetField("_activeHoverTips", BindingFlags.Static | BindingFlags.NonPublic)!;
    // Read the native alternative identity; a sole option can also be REROLL.
    private static readonly FieldInfo ExtraOptions =
        typeof(NCardRewardSelectionScreen).GetField("_extraOptions", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static LayoutState? FindState(Node node)
    {
        for (Node? owner = node; owner != null; owner = owner.GetParent())
            if (owner is NCardRewardSelectionScreen screen && States.TryGetValue(screen, out LayoutState? state))
                return state;
        return null;
    }

    // No __result is shared between void callbacks and the native async choice method.
    public static bool Prefix(Node __instance, MethodBase __originalMethod, object[] __args)
    {
        if (Entry.IsDisabled || __instance is not NCardRewardSelectionScreen screen
            || !States.TryGetValue(screen, out LayoutState? state))
            return true;
        if (__originalMethod.Name == "OptionSelected")
            state.BeginChoice();
        if (!PortraitViewportPatch.IsPortrait)
            return true;
        if (__originalMethod.Name == "SelectCard")
            return state.Select((NCardHolder)__args[0]);
        if (__originalMethod.Name == "OnAlternateRewardSelected")
            return state.TakeAlternative();
        if (__originalMethod.Name == "RefreshOptions")
            state.ClearSelection();
        return true;
    }

    public static void Postfix(Node __instance, MethodBase __originalMethod, object[] __args)
    {
        if (Entry.IsDisabled)
            return;
        if (__instance is NCardRewardSelectionScreen screen)
        {
            // _Ready resolves every needed field before its call to RefreshOptions.
            if (__originalMethod.Name == "RefreshOptions")
            {
                LayoutState state = States.GetValue(screen, value => new LayoutState(value));
                state.SetCards((IReadOnlyList<CardCreationResult>)__args[0]);
            }
            else if (States.TryGetValue(screen, out LayoutState? state))
                state.Queue();
        }
        else if (__instance is NCardHolder holder && (__originalMethod.Name == "DoCardHoverEffects" || __originalMethod.Name == "CreateHoverTips"))
            FindState(holder)?.KeepCardScale(holder);
        else if (__instance is NCard card && __originalMethod.Name == "UpdateVisuals")
            FindState(card)?.RefreshReading(card);
        else if (__instance is NCommonBanner)
            FindState(__instance)?.Queue();
    }

    private sealed class LayoutState
    {
        private readonly NCardRewardSelectionScreen _screen;
        private readonly Control _ui;
        private readonly Control _row;
        private readonly NCommonBanner _banner;
        private readonly HBoxContainer _alternatives;
        private readonly Control _inspectPrompt;
        private readonly Button _previous;
        private readonly Button _next;
        private readonly Label _pageNumber;
        private readonly Panel _reading;
        private readonly Label _placeholder;
        private readonly ScrollContainer _readingScroll;
        private readonly VBoxContainer _content;
        private readonly Label _title;
        private readonly Label _cost;
        private readonly Button _confirm;
        private readonly TextureRect _confirmOutline;
        private readonly Control _selectionLayer;
        private readonly Panel _selectionOutline;
        private MegaRichTextLabel? _body;
        private readonly Dictionary<Control, PortraitControlSnapshot> _geometry = new();
        private readonly Dictionary<(Control Node, string Name), Variant> _properties = new();
        private readonly Dictionary<(Control Node, string Name), (bool Had, int Value)> _fonts = new();
        private readonly (bool Had, int Value) _alternativeSeparation;
        private NCardHolder[] _cards = Array.Empty<NCardHolder>();
        private NCardHolder? _selected;
        private int _page;
        private bool _awaiting;
        private bool _committing;
        private bool _active;
        private bool _queued;
        private bool _applying;

        public LayoutState(NCardRewardSelectionScreen screen)
        {
            _screen = screen;
            _ui = screen.Get("_ui").As<Control>();
            _row = screen.Get("_cardRow").As<Control>();
            _banner = screen.Get("_banner").As<NCommonBanner>();
            _alternatives = screen.Get("_rewardAlternativesContainer").As<HBoxContainer>();
            _inspectPrompt = screen.Get("_inspectPrompt").As<Control>();
            _alternativeSeparation = (_alternatives.HasThemeConstantOverride("separation"),
                _alternatives.GetThemeConstant("separation"));
            _previous = new Button { Name = "PortraitRewardPrevious", Text = "上一组", Visible = false };
            _next = new Button { Name = "PortraitRewardNext", Text = "下一组", Visible = false };
            _pageNumber = new Label
            {
                Name = "PortraitRewardPage", Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            _previous.AddThemeFontSizeOverride("font_size", 36);
            _next.AddThemeFontSizeOverride("font_size", 36);
            _pageNumber.AddThemeFontSizeOverride("font_size", 42);
            _previous.Pressed += () => ChangePage(-1);
            _next.Pressed += () => ChangePage(1);
            _ui.AddChild(_previous);
            _ui.AddChild(_next);
            _ui.AddChild(_pageNumber);
            _selectionLayer = new Control
            {
                Name = "PortraitRewardSelectionLayer", ClipContents = true,
                MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false,
            };
            _ui.AddChild(_selectionLayer);
            _selectionOutline = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
            _selectionOutline.AddThemeStyleboxOverride("panel", new StyleBoxFlat
            {
                BgColor = Colors.Transparent, BorderColor = new Color(0.88f, 0.73f, 0.36f),
                BorderWidthLeft = 3, BorderWidthRight = 3, BorderWidthTop = 3, BorderWidthBottom = 3,
                CornerRadiusTopLeft = 16, CornerRadiusTopRight = 16,
                CornerRadiusBottomLeft = 16, CornerRadiusBottomRight = 16,
            });
            _selectionLayer.AddChild(_selectionOutline);
            _reading = new Panel { Name = "PortraitRewardReading", Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
            // Reuse the native tiled hover-tip paper without changing reader geometry.
            _reading.AddThemeStyleboxOverride("panel", new StyleBoxTexture
            {
                Texture = ResourceLoader.Load<Texture2D>("res://images/ui/hover_tip.png")
                    ?? throw new InvalidOperationException("Native hover-tip background failed to load for portrait card rewards."),
                TextureMarginLeft = 55, TextureMarginTop = 43,
                TextureMarginRight = 91, TextureMarginBottom = 32,
                AxisStretchHorizontal = StyleBoxTexture.AxisStretchMode.Tile,
                AxisStretchVertical = StyleBoxTexture.AxisStretchMode.Tile,
                ContentMarginLeft = 0, ContentMarginTop = 0,
                ContentMarginRight = 0, ContentMarginBottom = 0,
            });
            _ui.AddChild(_reading);
            _placeholder = new Label
            {
                // These two fixed short lines fit the reading width. Wrapping at
                // initial zero width would grow the minimum height before layout.
                Text = "轻点卡牌查看完整内容\n再次点击该牌或下方按钮领取", AutowrapMode = TextServer.AutowrapMode.Off,
                MouseFilter = Control.MouseFilterEnum.Ignore, VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            _placeholder.AddThemeFontSizeOverride("font_size", 40);
            _reading.AddChild(_placeholder);
            _readingScroll = new ScrollContainer
            {
                Name = "PortraitRewardDescriptionScroll", Visible = false,
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
                VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
            };
            _reading.AddChild(_readingScroll);
            _content = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Pass };
            _content.AddThemeConstantOverride("separation", 12);
            _readingScroll.AddChild(_content);
            _content.MinimumSizeChanged += Queue;
            _title = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, MouseFilter = Control.MouseFilterEnum.Ignore };
            _cost = new Label { MouseFilter = Control.MouseFilterEnum.Ignore };
            _content.AddChild(_title);
            _content.AddChild(_cost);
            _confirm = new Button { Name = "PortraitConfirmReward", Text = "先选牌", Disabled = true, Visible = false };
            _confirm.AddThemeFontSizeOverride("font_size", 40);
            // Reuse the native confirmation check and paper with the existing action callback.
            _confirm.Icon = ResourceLoader.Load<Texture2D>("res://images/atlases/compressed.sprites/confirm_button_tick.tres")
                ?? throw new InvalidOperationException("Native confirmation check failed to load for portrait card rewards.");
            _confirm.ExpandIcon = true;
            _confirm.AddThemeConstantOverride("icon_max_width", 56);
            _confirm.AddThemeConstantOverride("h_separation", 12);
            var normal = new StyleBoxTexture
            {
                Texture = ResourceLoader.Load<Texture2D>("res://images/atlases/ui_atlas.sprites/confirm_button.tres")
                    ?? throw new InvalidOperationException("Native confirmation paper failed to load for portrait card rewards."),
                // Include the native atlas margin canvas; an empty style region clips its paper edges.
                RegionRect = new Rect2(0, 0, 300, 202),
                TextureMarginLeft = 0, TextureMarginTop = 0,
                TextureMarginRight = 0, TextureMarginBottom = 0,
                AxisStretchHorizontal = StyleBoxTexture.AxisStretchMode.Stretch,
                AxisStretchVertical = StyleBoxTexture.AxisStretchMode.Stretch,
                // Match the original atlas padding at the compact button scale.
                ContentMarginLeft = 42, ContentMarginTop = 24,
                ContentMarginRight = 0, ContentMarginBottom = 44,
                ModulateColor = new Color(0.9f, 0.9f, 0.9f),
            };
            _confirm.AddThemeStyleboxOverride("normal", normal);
            var hover = (StyleBoxTexture)normal.Duplicate();
            hover.ModulateColor = new Color(1.1f, 1.1f, 1.1f);
            _confirm.AddThemeStyleboxOverride("hover", hover);
            var pressed = (StyleBoxTexture)normal.Duplicate();
            pressed.ModulateColor = new Color(0.7f, 0.7f, 0.7f);
            _confirm.AddThemeStyleboxOverride("pressed", pressed);
            var disabled = (StyleBoxTexture)normal.Duplicate();
            disabled.ModulateColor = new Color(0.45f, 0.45f, 0.45f);
            _confirm.AddThemeStyleboxOverride("disabled", disabled);
            // The native filled outline paints behind the paper with its own additive material.
            _confirm.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
            _confirmOutline = new TextureRect
            {
                Name = "PortraitRewardConfirmOutline", Visible = false,
                MouseFilter = Control.MouseFilterEnum.Ignore, ShowBehindParent = true,
                Texture = ResourceLoader.Load<Texture2D>("res://images/atlases/compressed.sprites/confirm_button_outline.tres")
                    ?? throw new InvalidOperationException("Native confirmation outline failed to load for portrait card rewards."),
                Material = ResourceLoader.Load<CanvasItemMaterial>("res://themes/canvas_item_material_additive_shared.tres")
                    ?? throw new InvalidOperationException("Native confirmation outline material failed to load for portrait card rewards."),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                Modulate = new Color("F0B400"),
            };
            _confirm.AddChild(_confirmOutline);
            _confirmOutline.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            _confirm.FocusEntered += () => _confirmOutline.Visible = !_confirm.Disabled;
            _confirm.FocusExited += _confirmOutline.Hide;
            _confirm.Pressed += Confirm;
            _ui.AddChild(_confirm);
            Window window = screen.GetWindow();
            window.SizeChanged += Queue;
            screen.TreeExiting += () => window.SizeChanged -= Queue;
        }

        public void SetCards(IReadOnlyList<CardCreationResult> options)
        {
            ClearSelection();
            _page = 0;
            // Native focus moves children to the front, so never use live order.
            _cards = options.Select(option => _screen.GetCardHolder(option.Card)).ToArray();
            // Pooled holders can be reused: their new native pose needs a fresh snapshot.
            foreach (NCardHolder holder in _cards)
                _geometry.Remove(holder);
            Queue();
        }

        public void BeginChoice()
        {
            _awaiting = true;
            ClearSelection();
            _cards = _cards.Where(holder => GodotObject.IsInstanceValid(holder)
                && holder.GetParent() == _row && holder.CardNode != null && !holder.IsQueuedForDeletion()).ToArray();
            _page = Math.Min(_page, Math.Max(0, (_cards.Length - 1) / 3));
            Queue();
        }

        private void ChangePage(int direction)
        {
            _page = Math.Clamp(_page + direction, 0, Math.Max(0, (_cards.Length - 1) / 3));
            ClearSelection();
            Queue();
        }

        public bool Select(NCardHolder holder)
        {
            if (_committing)
            {
                _awaiting = false;
                return true;
            }
            if (!_awaiting || !_cards.Contains(holder))
                return false;
            // A second tap on the previewed card shares the explicit button's
            // native commit path; tapping a different card only changes preview.
            if (_selected == holder)
            {
                Confirm();
                return false;
            }
            // Selection arrives through the native Pressed signal and its 350 ms
            // opening guard. Nothing is preselected or added to the player's deck.
            _selected = holder;
            RefreshReading(holder.CardNode!);
            _readingScroll.ScrollVertical = 0;
            _confirm.Text = "领取此牌";
            _confirm.Disabled = false;
            // Native disabling retains focus, so synchronize the passive outline on selection.
            _confirmOutline.Visible = _confirm.HasFocus();
            PlaceSelection();
            return false;
        }

        public bool TakeAlternative()
        {
            if (!_awaiting)
                return false;
            _awaiting = false;
            ClearSelection();
            return true;
        }

        private void Confirm()
        {
            if (!_active || !PortraitViewportPatch.IsPortrait || !_awaiting
                || _selected == null || !GodotObject.IsInstanceValid(_selected)
                || _selected.GetParent() != _row || _selected.CardNode == null)
                return;
            _confirm.Disabled = true;
            _confirmOutline.Hide();
            _committing = true;
            try
            {
                // The generated Godot method bridge calls the real SelectCard:
                // original TCS, synchronizer, deck addition and VFX retain ownership.
                _screen.Call("SelectCard", _selected);
            }
            finally
            {
                _committing = false;
            }
        }

        public void ClearSelection()
        {
            _selected = null;
            _confirm.Disabled = true;
            _confirmOutline.Hide();
            _confirm.Text = "先选牌";
            _placeholder.Show();
            _readingScroll.Hide();
            _selectionOutline.Hide();
        }

        public void RefreshReading(NCard card)
        {
            if (_selected?.CardNode != card)
                return;
            MegaRichTextLabel source = card.Get("_descriptionLabel").As<MegaRichTextLabel>();
            if (_body == null)
            {
                _body = new MegaRichTextLabel
                {
                    Name = "PortraitRewardDescription", AutoSizeEnabled = false, BbcodeEnabled = true,
                    FitContent = true, ScrollActive = false, MouseFilter = Control.MouseFilterEnum.Pass,
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                };
            }
            _body.Theme = source.Theme;
            _body.CustomEffects = source.CustomEffects.Duplicate();
            foreach (string font in new[] { "normal_font", "bold_font", "italics_font", "bold_italics_font", "mono_font" })
                _body.AddThemeFontOverride(font, source.GetThemeFont(font));
            foreach (string size in new[] { "normal_font_size", "bold_font_size", "italics_font_size", "bold_italics_font_size", "mono_font_size" })
                _body.AddThemeFontSizeOverride(size, 40);
            foreach (string color in new[] { "default_color", "font_shadow_color", "font_outline_color" })
                _body.AddThemeColorOverride(color, source.GetThemeColor(color));
            // Explicit native fonts must exist before MegaRichTextLabel enters the tree.
            if (_body.GetParent() == null)
                _content.AddChild(_body);
            // Keep native keyword and associated-card explanations in the same measured reader.
            // Native hover construction still runs, including its original seen-state updates.
            _body.Text = source.Text + string.Concat(IHoverTip.RemoveDupes(card.Model!.HoverTips).Select(tip => tip switch
            {
                HoverTip text => "\n\n[b]" + text.Title + "[/b]\n" + text.Description,
                CardHoverTip preview => "\n\n[b]" + preview.Card.Title + "[/b]\n"
                    + preview.Card.GetDescriptionForPile(PileType.Deck),
                _ => throw new InvalidOperationException("Unsupported native card reward hover tip type."),
            }));
            Label title = card.Get("_titleLabel").As<Label>();
            _title.Theme = title.Theme;
            _title.AddThemeFontOverride("font", title.GetThemeFont("font"));
            _title.AddThemeFontSizeOverride("font_size", 46);
            _title.AddThemeColorOverride("font_color", title.GetThemeColor("font_color"));
            _title.Text = title.Text;
            Label energy = card.Get("_energyLabel").As<Label>();
            Label star = card.Get("_starLabel").As<Label>();
            _cost.Theme = energy.Theme;
            _cost.AddThemeFontOverride("font", energy.GetThemeFont("font"));
            _cost.AddThemeFontSizeOverride("font_size", 36);
            _cost.Text = string.Join("    ", new[]
            {
                energy.IsVisibleInTree() ? "能量 " + energy.Text : "",
                star.IsVisibleInTree() ? "星能 " + star.Text : "",
            }.Where(text => text.Length > 0));
            _placeholder.Hide();
            _readingScroll.Show();
            Queue();
        }

        public void KeepCardScale(NCardHolder holder)
        {
            if (!_active || !PortraitViewportPatch.IsPortrait || !_cards.Contains(holder))
                return;
            Complete(holder.Get("_hoverTween").AsGodotObject() as Tween);
            holder.Scale = Vector2.One;
            // Original tips remain owned by the holder; only their overlay display is hidden.
            var tips = (Dictionary<Control, NHoverTipSet>)ActiveHoverTips.GetValue(null)!;
            if (tips.TryGetValue(holder, out NHoverTipSet? tip))
                tip.Hide();
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

        private void Apply()
        {
            if (_applying)
                return;
            _applying = true;
            try
            {
                if (!PortraitViewportPatch.IsPortrait)
                {
                    if (_active)
                        Restore();
                    return;
                }
                // Finish only old-coordinate presentation tweens, preserving their
                // completion callbacks. Capture holder geometry after final native poses.
                Complete(_screen.Get("_cardTween").AsGodotObject() as Tween);
                Complete(_screen.Get("_buttonTween").AsGodotObject() as Tween);
                Complete(_banner.Get("_tween").AsGodotObject() as Tween);
                _active = true;
                float width = _screen.GetViewportRect().Size.X;
                float height = _screen.GetViewportRect().Size.Y;
                float inner = width - 96;
                // Keep the original three-card row, with less empty space above its paper banner.
                Place(_banner, new Vector2((width - 654) / 2, 192), new Vector2(654, 162));
                Font(_banner.label, "font_size", 48);
                Saved(_inspectPrompt, "visible", false);
                // Keep the populated row in its native parent: NGridCardHolder's
                // _ExitTree frees CardNode, so reparenting this subtree is destructive.
                Place(_row, new Vector2(width / 2, 576), Vector2.Zero);
                _selectionLayer.Position = new Vector2(48, 360);
                _selectionLayer.Size = new Vector2(inner, 432);
                _selectionLayer.Show();
                int first = _page * 3;
                int pageCount = Math.Min(3, _cards.Length - first);
                for (int index = 0; index < _cards.Length; index++)
                {
                    NCardHolder holder = _cards[index];
                    if (!GodotObject.IsInstanceValid(holder) || holder.GetParent() != _row || holder.CardNode == null)
                        continue;
                    Remember(holder);
                    KeepCardScale(holder);
                    bool shown = index >= first && index < first + pageCount;
                    Saved(holder, "visible", shown);
                    if (shown)
                        holder.Position = new Vector2((index - first - (pageCount - 1) / 2f) * 336, 0);
                }
                bool browsing = _cards.Length > 3;
                _previous.Visible = _next.Visible = _pageNumber.Visible = browsing;
                _previous.Position = new Vector2(48, 816);
                _next.Position = new Vector2(width - 192, 816);
                _previous.Size = _next.Size = new Vector2(144, 144);
                _previous.Disabled = _page == 0;
                _next.Disabled = first + pageCount >= _cards.Length;
                _pageNumber.Position = new Vector2(216, 816);
                _pageNumber.Size = new Vector2(inner - 336, 144);
                _pageNumber.Text = $"{first + 1}–{first + pageCount} / {_cards.Length}";
                float readingY = browsing ? 984 : 816;
                _reading.Position = new Vector2(48, readingY);
                // Fit the existing measured content at its real width; long text keeps the same scroll.
                _readingScroll.Size = new Vector2(inner - 48, _readingScroll.Size.Y);
                _content.CustomMinimumSize = new Vector2(_readingScroll.Size.X - 24, 0);
                float contentHeight = _selected != null
                    ? _content.GetCombinedMinimumSize().Y : _placeholder.GetMinimumSize().Y;
                _reading.Size = new Vector2(inner, Math.Min(height - 432 - readingY, contentHeight + 48));
                // Plain guidance needs no panel; selected card text keeps the native reading paper.
                _reading.SelfModulate = _selected == null ? Colors.Transparent : Colors.White;
                _reading.Show();
                _placeholder.Position = _readingScroll.Position = new Vector2(24, 24);
                _placeholder.Size = _readingScroll.Size = _reading.Size - new Vector2(48, 48);
                IReadOnlyList<CardRewardAlternative> extraOptions =
                    (IReadOnlyList<CardRewardAlternative>)ExtraOptions.GetValue(_screen)!;
                bool singleSkip = extraOptions.Count == 1 && extraOptions[0].OptionId == "Skip";
                // The native 300:202 atlas paints at (43,25) with a 257x132 region.
                // Center the original inline check and text inside that painted area at 1.6x.
                float textWidth = _confirm.GetThemeFont("font")
                    .GetStringSize(_confirm.Text, HorizontalAlignment.Left, -1, 40).X;
                float groupPadding = (411.2f - 56 - 12 - textWidth) * .5f;
                foreach (string state in new[] { "normal", "hover", "pressed", "disabled" })
                {
                    var paper = (StyleBoxTexture)_confirm.GetThemeStylebox(state);
                    paper.ContentMarginLeft = singleSkip ? 68.8f + groupPadding : 42;
                    paper.ContentMarginRight = singleSkip ? groupPadding : 0;
                    paper.ContentMarginTop = singleSkip ? 40 : 24;
                    paper.ContentMarginBottom = singleSkip ? 72 : 44;
                }
                // Center the compact pair and align its painted centers without changing either action.
                float actionLeft = (width - 864) * .5f;
                _confirm.Position = singleSkip ? new Vector2(actionLeft, height - 335.2f)
                    : new Vector2((width - 288) / 2, height - 408);
                _confirm.Size = singleSkip ? new Vector2(480, 323.2f) : new Vector2(288, 194);
                _confirm.Show();
                NCardRewardAlternativeButton[] alternatives = _alternatives.GetChildren()
                    .OfType<NCardRewardAlternativeButton>().Where(button => !button.IsQueuedForDeletion()).ToArray();
                Place(_alternatives, singleSkip ? new Vector2(actionLeft + 528, height - 253.6f)
                    : new Vector2(48, height - 192), singleSkip ? new Vector2(336, 128) : new Vector2(inner, 144));
                _alternatives.AddThemeConstantOverride("separation", 24);
                foreach (NCardRewardAlternativeButton button in alternatives)
                {
                    Remember(button);
                    Vector2 alternativeSize = singleSkip ? new Vector2(336, 128)
                        : new Vector2((inner - 24 * (alternatives.Length - 1)) / alternatives.Length, 144);
                    button.CustomMinimumSize = alternativeSize;
                    button.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                    if (singleSkip)
                    {
                        // Resize the original leaf, preserving its native texture, HSV and focus tween.
                        button.PivotOffset = alternativeSize * .5f;
                        TextureRect image = button.Get("_image").As<TextureRect>();
                        Saved(image, "expand_mode", (int)TextureRect.ExpandModeEnum.IgnoreSize);
                        Saved(image, "stretch_mode", (int)TextureRect.StretchModeEnum.KeepAspectCentered);
                        Place(image, Vector2.Zero, alternativeSize);
                    }
                    MegaLabel label = button.Get("_label").As<MegaLabel>();
                    Font(label, "font_size", 40);
                    Place(label, new Vector2(24, 0), new Vector2(alternativeSize.X - 48, alternativeSize.Y));
                }
                PlaceSelection();
            }
            finally
            {
                _applying = false;
            }
        }

        private void PlaceSelection()
        {
            if (!_active || _selected == null || !GodotObject.IsInstanceValid(_selected)
                || _selected.GetParent() != _row || _selected.CardNode == null)
            {
                _selectionOutline.Hide();
                return;
            }
            // Keep display-only decoration outside CardRow: its native focus setup
            // enumerates every child, so adding decoration there would change navigation.
            _selectionOutline.Position = _selected.GlobalPosition - _selectionLayer.GlobalPosition - new Vector2(152, 213);
            _selectionOutline.Size = new Vector2(304, 426);
            _selectionOutline.Show();
        }

        private void Restore()
        {
            _active = false;
            ClearSelection();
            Complete(_screen.Get("_cardTween").AsGodotObject() as Tween);
            Complete(_screen.Get("_buttonTween").AsGodotObject() as Tween);
            Complete(_banner.Get("_tween").AsGodotObject() as Tween);
            foreach (NCardHolder holder in _cards.Where(GodotObject.IsInstanceValid))
                Complete(holder.Get("_hoverTween").AsGodotObject() as Tween);
            foreach (var item in _properties)
                if (GodotObject.IsInstanceValid(item.Key.Node) && _screen.IsAncestorOf(item.Key.Node))
                    item.Key.Node.Set(item.Key.Name, item.Value);
            foreach (var item in _fonts)
                if (GodotObject.IsInstanceValid(item.Key.Node) && _screen.IsAncestorOf(item.Key.Node))
                {
                    if (item.Value.Had)
                        item.Key.Node.AddThemeFontSizeOverride(item.Key.Name, item.Value.Value);
                    else
                        item.Key.Node.RemoveThemeFontSizeOverride(item.Key.Name);
                }
            foreach (var item in _geometry)
                if (GodotObject.IsInstanceValid(item.Key) && _screen.IsAncestorOf(item.Key))
                    item.Value.Restore();
            if (_alternativeSeparation.Had)
                _alternatives.AddThemeConstantOverride("separation", _alternativeSeparation.Value);
            else
                _alternatives.RemoveThemeConstantOverride("separation");
            foreach (NCardHolder holder in _cards.Where(GodotObject.IsInstanceValid))
                if (holder.GetParent() == _row && holder.CardNode != null)
                {
                    // Release the hidden portrait tip before native focus recreates it in landscape.
                    NHoverTipSet.Remove(holder);
                    holder.Call("DoCardHoverEffects", holder.Get("_isFocused"));
                }
            // Native banner cache depends on the new viewport, not the old snapshot.
            _banner.Call("OnWindowChange");
            _screen.Call("UpdateControllerIcons");
            _previous.Hide();
            _next.Hide();
            _pageNumber.Hide();
            _selectionLayer.Hide();
            _reading.Hide();
            _confirm.Hide();
        }

        private static void Complete(Tween? tween)
        {
            if (tween != null && tween.IsValid() && tween.IsRunning())
                tween.FastForwardToCompletion();
        }

        private void Remember(Control node)
        {
            if (!_geometry.ContainsKey(node))
                _geometry.Add(node, new PortraitControlSnapshot(node));
        }

        private void Place(Control node, Vector2 position, Vector2 size)
        {
            Remember(node);
            node.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
            node.CustomMinimumSize = Vector2.Zero;
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
            if (node is MegaLabel)
                Saved(node, "AutoSizeEnabled", false);
            node.AddThemeFontSizeOverride(name, size);
        }
    }
}
