using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.addons.mega_text;
using STS2RitsuLib.Patching.Models;
using OpCodes = System.Reflection.Emit.OpCodes;

namespace STS2Portrait.Patches;

/// <summary>Places the native hand in ten slots with hold-to-read, drag-to-play input.</summary>
internal sealed class PortraitHandPatch : IPatchMethod
{
    public static string PatchId => "sts2portrait_hand";
    public static string Description => "Keep native hand cards reachable in portrait";
    public static bool IsCritical => true;
    public static ModPatchTarget[] GetTargets() => new[]
    {
        PatchTarget.Method(typeof(NPlayerHand), "_Ready"),
        PatchTarget.Method(typeof(NPlayerHand), "RefreshLayout"),
        PatchTarget.Method(typeof(NPlayerHand), "AnimDisable"),
        PatchTarget.Method(typeof(NPlayerHand), "AnimEnable"),
        PatchTarget.Method(typeof(NPlayerHand), "AnimOut"),
        PatchTarget.Method(typeof(NMouseCardPlay), "Start"),
        PatchTarget.Method(typeof(NMouseCardPlay), "_Input"),
        PatchTarget.Method(typeof(NMouseCardPlay), "TargetSelection"),
        PatchTarget.Method(typeof(NCardPlay), "Cleanup"),
        PatchTarget.Method(typeof(NHandCardHolder), "SetTargetPosition"),
        PatchTarget.Method(typeof(NHandCardHolder), "SetTargetScale"),
        PatchTarget.Method(typeof(NCard), "UpdateVisuals"),
        PatchTarget.Method(typeof(NPlayerHand), "OnHolderPressed"),
        PatchTarget.Method(typeof(NPlayerHand), "DeselectCard"),
        PatchTarget.Method(typeof(NPlayerHand), "UpdateSelectedCardContainer"),
        PatchTarget.Method(typeof(NPlayerHand), "RefreshSelectModeConfirmButton"),
        PatchTarget.Method(typeof(NPlayerHand), "OnSelectModeSourceFinished"),
        PatchTarget.Method(typeof(NSelectedHandCardContainer), "RefreshHolderPositions"),
        PatchTarget.Method(typeof(NUpgradePreview), "Reload"),
        PatchTarget.Method(typeof(NCardHolder), "DoCardHoverEffects"),
        PatchTarget.Method(typeof(NConfirmButton), "OnEnable"),
        PatchTarget.Method(typeof(NConfirmButton), "OnDisable"),
        PatchTarget.Method(typeof(NConfirmButton), "OnWindowChange"),
    };

    private static readonly ConditionalWeakTable<NPlayerHand, LayoutState> States = new();
    private static readonly ConditionalWeakTable<NCard, MiniCardState> MiniCards = new();
    private const float CardScale = 0.5f;
    private const float HeldCardScale = 1f;
    // Match the native held card center to the lowered full-text panel.
    private const float HeldCardCenterOffset = 557f;

    private static LayoutState? StateFor(Node node)
    {
        for (Node? parent = node; parent != null; parent = parent.GetParent())
            if (parent is NPlayerHand hand && States.TryGetValue(hand, out LayoutState? state))
                return state;
        return null;
    }

    // These geometry checks replace only the portrait drag/cancel thresholds.
    // Native StartCardDrag, TargetManager and TryPlayCard still own card play.
    internal static bool TryDragZone(NMouseCardPlay play, bool cancelZone, out bool result)
    {
        LayoutState? state = StateFor(play);
        result = false;
        if (Entry.IsDisabled || state?.HeldPlay != play)
            return false;
        result = state.IsInDragZone(cancelZone);
        return true;
    }

    public static void Prefix(Node __instance, MethodBase __originalMethod, object[] __args)
    {
        if (Entry.IsDisabled)
            return;
        LayoutState? state = StateFor(__instance);
        if (state != null && PortraitViewportPatch.IsPortrait)
        {
            if (__instance is NPlayerHand selectionHand && selectionHand.IsInCardSelection &&
                __originalMethod.Name == "OnHolderPressed" && __args[0] is NCardHolder selected)
                state.ReadSelection(selected.CardNode);
            else if (__instance is NConfirmButton confirm &&
                __originalMethod.Name is "OnEnable" or "OnDisable")
                state.PlaceConfirm(confirm, false);
        }
        if (state?.HeldPlay == null)
            return;
        if (__originalMethod.Name == "Cleanup" && __instance == state.HeldPlay)
        {
            // Close the passive preview before Finished returns the real holder.
            state.CloseHold();
        }
        else if (__originalMethod.Name == "TargetSelection" && __instance == state.HeldPlay)
        {
            // A held card always uses the native release-to-target path.
            // Early release is cancelled by the input postfix, never latched.
            state.BeginTargeting();
            __args[0] = TargetMode.ReleaseMouseToTarget;
        }
        else if (__instance == state.HeldPlay.Holder)
        {
            // The enlarged original card stays below the enemy area, beside
            // its full text. The native targeting arrow follows the pointer.
            if (__originalMethod.Name == "SetTargetPosition")
                __args[0] = new Vector2(222,
                    state.HeldPlay.Holder.GetViewportRect().Size.Y - HeldCardCenterOffset);
            else if (__originalMethod.Name == "SetTargetScale")
                __args[0] = Vector2.One * HeldCardScale;
        }
    }

    public static void Postfix(Node __instance, MethodBase __originalMethod, object[] __args)
    {
        if (Entry.IsDisabled)
            return;
        if (__instance is NPlayerHand hand)
        {
            if (__originalMethod.Name == "_Ready")
                States.GetValue(hand, node => new LayoutState(node)).Queue();
            else if (States.TryGetValue(hand, out LayoutState? state))
            {
                if (__originalMethod.Name == "UpdateSelectedCardContainer")
                {
                    // ChildEnteredTree fires before the new holder's Ready.
                    // Let Ready capture its original card-travel endpoint before
                    // placing that holder in the portrait slot.
                    state.CompleteSelectedTween();
                    state.Queue();
                    return;
                }
                if (__originalMethod.Name == "DeselectCard")
                    state.ReadSelection((NCard)__args[0]);
                state.Apply();
            }
        }
        else if (__instance is NMouseCardPlay play)
        {
            if (__originalMethod.Name == "Start")
                StateFor(play)?.BeginHold(play);
            else if (__originalMethod.Name == "_Input")
                StateFor(play)?.AfterInput(play, (InputEvent)__args[0]);
        }
        else if (__instance is NCard card && __originalMethod.Name == "UpdateVisuals")
        {
            LayoutState? state = StateFor(card);
            state?.RefreshMiniCard(card);
            state?.RefreshPreview(card);
        }
        else if (__instance is NConfirmButton confirm)
        {
            if (__originalMethod.Name == "OnWindowChange")
                StateFor(confirm)?.PlaceConfirm(confirm, true);
        }
        else if (__instance is NSelectedHandCardContainer or NUpgradePreview)
            StateFor(__instance)?.Queue();
        else if (__instance is NSelectedHandCardHolder selected)
        {
            LayoutState? state = StateFor(selected);
            if (state?.UsesSelectionSlots == true)
            {
                // Finish only the native display tween before applying its
                // miniature endpoint. The original input handler still runs.
                Complete(selected.Get("_hoverTween").AsGodotObject() as Tween);
                selected.Scale = Vector2.One * CardScale;
            }
        }
    }

    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        List<CodeInstruction> body = instructions.ToList();
        if (__originalMethod.DeclaringType != typeof(NPlayerHand) || __originalMethod.Name != "AnimOut")
            return body;

        FieldInfo destination = AccessTools.Field(typeof(NPlayerHand), "_hidePosition");
        int[] loads = body.Select((instruction, index) => (instruction, index))
            .Where(value => value.instruction.opcode == OpCodes.Ldsfld && Equals(value.instruction.operand, destination))
            .Select(value => value.index).ToArray();
        if (loads.Length != 1)
            throw new InvalidOperationException($"Expected one native hand exit destination, found {loads.Length}.");
        // Transform only the native endpoint; keep its cancellation, duration and tween chain.
        body.Insert(loads[0] + 1, new CodeInstruction(OpCodes.Call,
            AccessTools.Method(typeof(PortraitHandPatch), nameof(HandExitDestination))));
        return body;
    }

    private static Vector2 HandExitDestination(Vector2 destination)
    {
        // The held reader starts at H - 792; clear it and both card rows by another 48 units.
        return !Entry.IsDisabled && PortraitViewportPatch.IsPortrait
            ? new Vector2(destination.X, Math.Max(destination.Y, 840f))
            : destination;
    }

    private static void Complete(Tween? tween)
    {
        if (tween != null && tween.IsValid() && tween.IsRunning())
            tween.FastForwardToCompletion();
    }

    private sealed class MiniCardState
    {
        private readonly Control _energyIcon;
        private readonly Control _starIcon;
        private readonly MegaLabel _energy;
        private readonly MegaLabel _star;
        private readonly MegaLabel _title;
        private readonly PortraitControlSnapshot[] _controls;
        private readonly (MegaLabel Label, bool AutoSize, bool HadSize, int Size,
            bool Clip, TextServer.OverrunBehavior Overrun, bool HadOutline, int Outline)[] _labels;
        private bool _active;

        public MiniCardState(NCard card)
        {
            _energyIcon = card.Get("_energyIcon").As<Control>();
            _starIcon = card.Get("_starIcon").As<Control>();
            _energy = card.Get("_energyLabel").As<MegaLabel>();
            _star = card.Get("_starLabel").As<MegaLabel>();
            _title = card.Get("_titleLabel").As<MegaLabel>();
            _controls = new[] { _energyIcon, _starIcon, _energy, _star, _title }
                .Select(control => new PortraitControlSnapshot(control)).ToArray();
            _labels = new[] { _energy, _star, _title }.Select(label =>
                (label, label.AutoSizeEnabled, label.HasThemeFontSizeOverride("font_size"),
                    label.GetThemeFontSize("font_size"), label.ClipText, label.TextOverrunBehavior,
                    label.HasThemeConstantOverride("outline_size"), label.GetThemeConstant("outline_size"))).ToArray();
            // Reparenting out of the resting hand (drag, discard or inspect)
            // exits the tree, restoring the original card before its next owner.
            card.TreeExiting += Restore;
        }

        public void Apply()
        {
            _active = true;
            foreach (var entry in _labels)
            {
                entry.Label.AutoSizeEnabled = false;
                entry.Label.ClipText = true;
            }
            // At 0.5 card scale, a 1.75 icon with font 48 renders font 42.
            // Keep the native text, dynamic colors and unplayable icon signals.
            _energyIcon.Scale = _starIcon.Scale = Vector2.One * 1.75f;
            _energyIcon.Position = new Vector2(-170, -211);
            _starIcon.Position = new Vector2(-170, -89);
            _energy.Position = new Vector2(-4, -8);
            _star.Position = new Vector2(-7, -11);
            _energy.Size = _star.Size = new Vector2(72, 80);
            _energy.AddThemeFontSizeOverride("font_size", 48);
            _star.AddThemeFontSizeOverride("font_size", 48);
            // Keep the enlarged digits distinct without scaling the original
            // thick miniature outline into the neighboring title.
            _energy.AddThemeConstantOverride("outline_size", 4);
            _star.AddThemeConstantOverride("outline_size", 4);
            // Reserve the top-left corner for the cost. Full names remain in
            // the reading panel; this only clips the original miniature title.
            _title.Position = new Vector2(-40, -204);
            _title.Size = new Vector2(145, 54);
            _title.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        }

        public void Restore()
        {
            if (!_active)
                return;
            _active = false;
            foreach (var entry in _labels)
            {
                entry.Label.AutoSizeEnabled = entry.AutoSize;
                entry.Label.ClipText = entry.Clip;
                entry.Label.TextOverrunBehavior = entry.Overrun;
                if (entry.HadOutline)
                    entry.Label.AddThemeConstantOverride("outline_size", entry.Outline);
                else
                    entry.Label.RemoveThemeConstantOverride("outline_size");
                if (entry.HadSize)
                    entry.Label.AddThemeFontSizeOverride("font_size", entry.Size);
                else
                    entry.Label.RemoveThemeFontSizeOverride("font_size");
            }
            foreach (PortraitControlSnapshot snapshot in _controls)
                snapshot.Restore();
            // Re-evaluate current costs/names, never restore an old text value.
            foreach (var entry in _labels)
                if (entry.AutoSize)
                    entry.Label.Call("AdjustFontSize");
        }
    }

    private sealed class LayoutState
    {
        private readonly NPlayerHand _hand;
        private readonly PortraitControlSnapshot _containerOriginal;
        private readonly Control _slots;
        private readonly Panel[] _slotPanels = new Panel[10];
        private Panel? _preview;
        private Label? _previewTitle;
        private Label? _previewEnergy;
        private Label? _previewStar;
        private MegaRichTextLabel? _previewText;
        private ScrollContainer? _previewScroll;
        private Label? _holdHint;
        private bool _targetingStarted;
        private int _heldZIndex;
        private Control.MouseFilterEnum _cardMouseFilter;
        private ulong _readStartedAt;
        public NMouseCardPlay? HeldPlay { get; private set; }
        private bool _active;
        private bool _applying;
        private bool _queued;
        private readonly NSelectedHandCardContainer _selected;
        private readonly NUpgradePreview _upgrade;
        private readonly NConfirmButton _confirm;
        private readonly MegaRichTextLabel _selectionHeader;
        private readonly Dictionary<Control, PortraitControlSnapshot> _selectionGeometry = new();
        private readonly Dictionary<(Control Node, string Name), Variant> _selectionProperties = new();
        private readonly Dictionary<(Control Node, string Name), (bool Had, int Value)> _selectionFonts = new();
        private readonly StyleBoxFlat _selectedSlotStyle;
        private readonly StyleBoxFlat _emptySlotStyle;
        private readonly Panel _confirmSurface;
        private readonly Panel _peekSurface;
        private readonly Label _peekLabel;
        private VBoxContainer? _selectionText;
        private GridContainer? _upgradeColumns;
        private bool _readingColumns;
        private Label? _beforeCost;
        private Label? _afterHeading;
        private MegaRichTextLabel? _beforeText;
        private NCard? _selectionReadCard;
        private bool _selectionReading;
        private bool _selectionChrome;
        private Vector2 _selectionViewport;

        public bool UsesSelectionSlots => !Entry.IsDisabled && PortraitViewportPatch.IsPortrait &&
            (_hand.IsInCardSelection || _selected.Holders.Count != 0 || _upgrade.Card != null);

        public LayoutState(NPlayerHand hand)
        {
            _hand = hand;
            _containerOriginal = new PortraitControlSnapshot(hand.CardHolderContainer);
            _selected = hand.Get("_selectedHandCardContainer").As<NSelectedHandCardContainer>();
            _upgrade = hand.Get("_upgradePreview").As<NUpgradePreview>();
            _confirm = hand.Get("_selectModeConfirmButton").As<NConfirmButton>();
            _selectionHeader = hand.Get("_selectionHeader").As<MegaRichTextLabel>();
            foreach (Control control in new Control[] { _selected, _upgrade, _confirm, _selectionHeader, hand.PeekButton })
                RememberSelection(control);
            foreach (Control child in _upgrade.GetChildren().OfType<Control>()) RememberSelection(child);
            Control confirmImage = _confirm.Get("_buttonImage").As<Control>();
            _confirmSurface = SelectionSurface(confirmImage, "PortraitHandConfirmSurface", "确认", out _);
            Control peekVisuals = hand.PeekButton.Get("_visuals").As<Control>();
            _peekSurface = SelectionSurface(peekVisuals, "PortraitHandPeekSurface", "查看战场", out _peekLabel);
            _slots = new Control
            {
                Name = "PortraitHandSlots", Visible = false,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            hand.AddChild(_slots);
            hand.MoveChild(_slots, 0);
            StyleBoxFlat slotStyle = new()
            {
                BgColor = new Color(0.06f, 0.10f, 0.08f, 0.42f),
                BorderColor = new Color(0.50f, 0.46f, 0.32f, 0.35f),
                BorderWidthLeft = 1, BorderWidthTop = 1, BorderWidthRight = 1, BorderWidthBottom = 1,
                CornerRadiusTopLeft = 12, CornerRadiusTopRight = 12,
                CornerRadiusBottomLeft = 12, CornerRadiusBottomRight = 12,
            };
            _emptySlotStyle = slotStyle;
            _selectedSlotStyle = (StyleBoxFlat)slotStyle.Duplicate();
            _selectedSlotStyle.BorderColor = new Color(.94f, .76f, .35f);
            _selectedSlotStyle.BorderWidthLeft = _selectedSlotStyle.BorderWidthRight = 4;
            _selectedSlotStyle.BorderWidthTop = _selectedSlotStyle.BorderWidthBottom = 4;
            for (int index = 0; index < _slotPanels.Length; index++)
            {
                Panel slot = new()
                {
                    Name = $"PortraitHandSlot{index + 1}",
                    MouseFilter = Control.MouseFilterEnum.Ignore,
                };
                slot.AddThemeStyleboxOverride("panel", slotStyle);
                _slots.AddChild(slot);
                _slotPanels[index] = slot;
            }
            hand.Resized += Queue;
            hand.ModeChanged += Queue;
            hand.PeekButton.Toggled += _ => Queue();
            Window window = hand.GetWindow();
            window.SizeChanged += Queue;
            hand.TreeExiting += () =>
            {
                window.SizeChanged -= Queue;
                CloseHold();
            };
        }

        private void RememberSelection(Control control)
        {
            if (_selectionGeometry.TryAdd(control, new PortraitControlSnapshot(control)) && control is NCardHolder)
            {
                // Native selection owns holder replacement and destruction.
                // Restore live display state before a card changes that owner.
                control.TreeExiting += () =>
                {
                    foreach (var key in _selectionProperties.Keys.Where(key =>
                        GodotObject.IsInstanceValid(key.Node) &&
                        (key.Node == control || control.IsAncestorOf(key.Node))).ToArray())
                    {
                        key.Node.Set(key.Name, _selectionProperties[key]);
                        _selectionProperties.Remove(key);
                    }
                    if (_selectionGeometry.Remove(control, out PortraitControlSnapshot snapshot)) snapshot.Restore();
                };
            }
        }

        private void SelectionSaved(Control node, string name, Variant value)
        {
            _selectionProperties.TryAdd((node, name), node.Get(name));
            if (!node.Get(name).Equals(value)) node.Set(name, value);
        }

        private void SelectionPlace(Control node, Vector2 position, Vector2 size)
        {
            RememberSelection(node);
            node.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
            node.CustomMinimumSize = Vector2.Zero;
            node.Position = position;
            node.Size = size;
        }

        private static Panel SelectionSurface(Control parent, string name, string text, out Label label)
        {
            Panel panel = new() { Name = name, Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
            // Reuse the native tooltip skin without changing action bounds or label padding.
            panel.AddThemeStyleboxOverride("panel", new StyleBoxTexture
            {
                Texture = ResourceLoader.Load<Texture2D>("res://images/ui/hover_tip.png")
                    ?? throw new InvalidOperationException($"Native hover-tip background failed to load for {name}."),
                TextureMarginLeft = 55, TextureMarginTop = 43,
                TextureMarginRight = 91, TextureMarginBottom = 32,
                AxisStretchHorizontal = StyleBoxTexture.AxisStretchMode.Tile,
                AxisStretchVertical = StyleBoxTexture.AxisStretchMode.Tile,
                ModulateColor = Colors.White,
                ContentMarginLeft = 0, ContentMarginTop = 0,
                ContentMarginRight = 0, ContentMarginBottom = 0,
            });
            parent.AddChild(panel);
            parent.MoveChild(panel, 0);
            label = new Label { Text = text, MouseFilter = Control.MouseFilterEnum.Ignore,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            label.AddThemeFontSizeOverride("font_size", 48);
            panel.AddChild(label);
            label.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            return panel;
        }

        private int SelectionSlot(NCard card) => card.Model is { } model
            ? PileType.Hand.GetPile(model.Owner).Cards.ToList().IndexOf(model) : -1;

        private Vector2 SlotCenter(int index)
        {
            Vector2 viewport = _hand.GetViewportRect().Size;
            float pitch = (viewport.X - 96 - 4 * 24) / 5 + 24;
            return new Vector2(viewport.X * .5f + (index % 5 - 2) * pitch,
                viewport.Y - 508.5f + index / 5 * 223);
        }

        private void SetSlotStyle(int index, bool selected)
        {
            // Keep resting and empty positions open; decorate only an active native selection.
            _slotPanels[index].Visible = selected;
            StyleBoxFlat style = selected ? _selectedSlotStyle : _emptySlotStyle;
            if (_slotPanels[index].GetThemeStylebox("panel") != style)
                _slotPanels[index].AddThemeStyleboxOverride("panel", style);
        }

        public void CompleteSelectedTween()
        {
            if (UsesSelectionSlots)
                Complete(_hand.Get("_selectedCardScaleTween").AsGodotObject() as Tween);
        }

        public void ReadSelection(NCard? card)
        {
            if (!PortraitViewportPatch.IsPortrait || !_hand.IsInCardSelection || _hand.PeekButton.IsPeeking)
                return;
            if (card != null && GodotObject.IsInstanceValid(card) && card.Model != null)
                _selectionReadCard = card;
        }

        public void PlaceConfirm(NConfirmButton button, bool snap)
        {
            if (button != _confirm || !PortraitViewportPatch.IsPortrait ||
                (!_hand.IsInCardSelection && !_selectionChrome)) return;
            RememberSelection(button);
            button.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
            button.CustomMinimumSize = Vector2.Zero;
            button.Size = new Vector2(312, 120);
            button.PivotOffset = button.Size * .5f;
            Vector2 shown = new(_hand.GetViewportRect().Size.X - 360, _hand.GetViewportRect().Size.Y - 144);
            Vector2 hidden = new(_hand.GetViewportRect().Size.X + 48, shown.Y);
            // Confirm animates local position. Preserve its native enabled
            // state and set only the destination before its original tween.
            button.Set("_showPos", shown);
            button.Set("_hidePos", hidden);
            if (snap) button.Position = button.Get("_isEnabled").AsBool() ? shown : hidden;
        }

        private void ArrangeSelection()
        {
            Vector2 viewport = _hand.GetViewportRect().Size;
            bool changed = !_selectionChrome || _selectionViewport != viewport;
            _selectionViewport = viewport;
            _selectionChrome = true;
            if (changed) Complete(_confirm.Get("_moveTween").AsGodotObject() as Tween);
            PlaceConfirm(_confirm, changed);
            SelectionPlace(_selectionHeader, new Vector2(48, 336), new Vector2(viewport.X - 96, 120));
            foreach (string name in new[] { "normal_font_size", "bold_font_size", "italics_font_size", "bold_italics_font_size", "mono_font_size" })
            {
                _selectionFonts.TryAdd((_selectionHeader, name),
                    (_selectionHeader.HasThemeFontSizeOverride(name), _selectionHeader.GetThemeFontSize(name)));
                if (!_selectionHeader.HasThemeFontSizeOverride(name) || _selectionHeader.GetThemeFontSize(name) != 48)
                    _selectionHeader.AddThemeFontSizeOverride(name, 48);
            }

            Control image = _confirm.Get("_buttonImage").As<Control>();
            RememberSelection(image);
            SelectionSaved(image, "expand_mode", (int)TextureRect.ExpandModeEnum.IgnoreSize);
            SelectionPlace(image, Vector2.Zero, _confirm.Size);
            // Draw the native confirm paper, tick and animation feedback inside the existing touch area.
            SelectionSaved(image, "self_modulate", Colors.White);
            foreach (TextureRect decoration in _confirm.GetChildren().OfType<TextureRect>().Where(node => node != image))
                SelectionSaved(decoration, "visible", true);
            foreach (Control child in image.GetChildren().OfType<Control>().Where(node => node != _confirmSurface))
                SelectionSaved(child, "visible", child.Name != "HotkeyIcon");
            _confirmSurface.Size = _confirm.Size;
            _confirmSurface.Hide();

            NPeekButton peek = _hand.PeekButton;
            SelectionPlace(peek, new Vector2(48, viewport.Y - 144), new Vector2(288, 120));
            Control visuals = peek.Get("_visuals").As<Control>();
            RememberSelection(visuals);
            SelectionSaved(visuals, "expand_mode", (int)TextureRect.ExpandModeEnum.IgnoreSize);
            SelectionPlace(visuals, Vector2.Zero, peek.Size);
            visuals.PivotOffset = peek.Size * .5f;
            // Keep the original eye texture and pulse material on their native managed owner.
            SelectionSaved(visuals, "self_modulate", Colors.White);
            foreach (NHotkeyIcon hotkey in peek.GetChildren().OfType<NHotkeyIcon>()) SelectionSaved(hotkey, "visible", false);
            _peekSurface.Size = peek.Size;
            _peekSurface.Hide();
            _peekLabel.Text = peek.IsPeeking ? "返回选牌" : "查看战场";

            if (UsesSelectionSlots)
            {
                CompleteSelectedTween();
                SelectionPlace(_selected, Vector2.Zero, Vector2.Zero);
                _selected.Scale = Vector2.One;
                foreach (NSelectedHandCardHolder holder in _selected.Holders)
                {
                    if (holder.CardNode is not { } card || holder.IsQueuedForDeletion()) continue;
                    int index = SelectionSlot(card);
                    if (index < 0) continue; // Native removal is already animating this card out of Hand.
                    RememberSelection(holder);
                    Complete(holder.Get("_tween").AsGodotObject() as Tween);
                    Complete(holder.Get("_hoverTween").AsGodotObject() as Tween);
                    holder.Position = SlotCenter(index);
                    holder.Scale = Vector2.One * CardScale;
                    RefreshMiniCard(card);
                    SetSlotStyle(index, true);
                }
                ArrangeUpgrade();
            }

            if (!_hand.IsInCardSelection || peek.IsPeeking)
            {
                if (_selectionReading) _preview?.Hide();
                if (!_hand.IsInCardSelection)
                {
                    SetSelectionReading(false);
                    _selectionReadCard = null;
                }
                return;
            }
            // Landscape clears the display reader, not the native selection.
            // Rebind from its current live holder when portrait returns.
            if (_selectionReadCard == null)
                ReadSelection(_selected.Holders.LastOrDefault(holder => !holder.IsQueuedForDeletion())?.CardNode);
            NCard? read = UpgradeHolder(false)?.CardNode ?? _selectionReadCard;
            if (read == null || !GodotObject.IsInstanceValid(read) || !read.IsInsideTree() || read.Model == null)
                return;
            if (_preview == null) CreatePreview(read);
            SetSelectionReading(true);
            RefreshPreview(read);
            _preview!.Show();
        }

        private NPreviewCardHolder? UpgradeHolder(bool before)
        {
            if (_upgrade.Card == null) return null;
            return _upgrade.Get(before ? "_before" : "_after").As<Control>().GetChildren()
                .OfType<NPreviewCardHolder>().FirstOrDefault(holder => !holder.IsQueuedForDeletion());
        }

        private void ArrangeUpgrade()
        {
            NPreviewCardHolder? before = UpgradeHolder(true), after = UpgradeHolder(false);
            if (before?.CardNode is not { } card || after?.CardNode == null) return;
            int index = SelectionSlot(card);
            if (index < 0) return;
            SelectionPlace(_upgrade, Vector2.Zero, Vector2.Zero);
            _upgrade.Scale = Vector2.One;
            Control beforeMarker = _upgrade.Get("_before").As<Control>();
            Control afterMarker = _upgrade.Get("_after").As<Control>();
            SelectionPlace(beforeMarker, SlotCenter(index), Vector2.Zero);
            float readingHeight = _hand.GetViewportRect().Size.Y - 1118;
            SelectionPlace(afterMarker, new Vector2(222, 480 + readingHeight * .5f), Vector2.Zero);
            // Reload owns visibility; restoration uses the current Card value,
            // never a stale snapshot from a previously selected card.
            _upgrade.Get("_arrows").As<Control>().Hide();
            foreach (NPreviewCardHolder holder in new[] { before, after })
            {
                RememberSelection(holder);
                Complete(holder.Get("_hoverTween").AsGodotObject() as Tween);
                SelectionSaved(holder, "_scaleOnHover", false);
                holder.Position = Vector2.Zero;
            }
            before.Scale = Vector2.One * CardScale;
            after.Scale = Vector2.One * Math.Min(1f, (readingHeight - 48) / 422);
            // A short reader cannot fit the original 422-unit card plus padding.
            // Keep its native model/holder alive; show its computed text instead.
            if (readingHeight < 422 + 48)
                SelectionSaved(after, "visible", false);
            else if (_selectionProperties.TryGetValue((after, "visible"), out Variant nativeVisible))
                after.Set("visible", nativeVisible);
            // The preview panel is passive; the original after-card draws above
            // its left artwork area while the right side scrolls complete text.
            SelectionSaved(after, "z_index", 11);
            SelectionSaved(after.Hitbox, "mouse_filter", (int)Control.MouseFilterEnum.Ignore);
            RefreshMiniCard(card);
            SetSlotStyle(index, true);
        }

        private void RestoreSelection()
        {
            SetSelectionReading(false);
            _selectionReadCard = null;
            if (!_selectionChrome) return;
            _selectionChrome = false;
            Complete(_confirm.Get("_moveTween").AsGodotObject() as Tween);
            Complete(_hand.Get("_selectedCardScaleTween").AsGodotObject() as Tween);
            foreach (var item in _selectionFonts)
                if (item.Value.Had) item.Key.Node.AddThemeFontSizeOverride(item.Key.Name, item.Value.Value);
                else item.Key.Node.RemoveThemeFontSizeOverride(item.Key.Name);
            foreach (var item in _selectionProperties.Reverse().Where(item => item.Key.Name != "expand_mode"))
                if (GodotObject.IsInstanceValid(item.Key.Node)) item.Key.Node.Set(item.Key.Name, item.Value);
            foreach (var item in _selectionGeometry.Reverse())
                if (GodotObject.IsInstanceValid(item.Key)) item.Value.Restore();
            // FitWidth derives a minimum from current height. Restore geometry
            // while IgnoreSize is active, then restore native texture behavior.
            foreach (var item in _selectionProperties.Where(item => item.Key.Name == "expand_mode"))
                if (GodotObject.IsInstanceValid(item.Key.Node)) item.Key.Node.Set(item.Key.Name, item.Value);
            _upgrade.Get("_arrows").As<Control>().Visible = _upgrade.Card != null;
            foreach (NSelectedHandCardHolder holder in _selected.Holders)
                if (holder.CardNode is { } card) RefreshMiniCard(card);
            if (UpgradeHolder(true)?.CardNode is { } before) RefreshMiniCard(before);
            _confirmSurface.Hide(); _peekSurface.Hide();
            _confirm.Call("OnWindowChange");
            // Recompute native layout for the current selection, not a prior
            // count captured before portrait or before a subsequent resize.
            _selected.Call("RefreshHolderPositions");
            _hand.Call("UpdateSelectedCardContainer", _selected.Holders.Count);
        }

        public void BeginHold(NMouseCardPlay play)
        {
            // Shortcut play skips the first drag await and may already have
            // finished before Start returns. Leave that native path untouched.
            NCard? card = play.Holder.CardNode;
            if (!PortraitViewportPatch.IsPortrait || _hand.IsInCardSelection
                || play.Get("_skipStartCardDrag").AsBool() || card?.Model == null)
                return;
            SetSelectionReading(false);
            RefreshMiniCard(card);
            HeldPlay = play;
            _targetingStarted = false;
            _readStartedAt = Time.GetTicksMsec();
            _heldZIndex = play.Holder.ZIndex;
            _cardMouseFilter = card.MouseFilter;
            if (_preview == null)
                CreatePreview(card);
            _previewScroll!.ScrollVertical = 0;
            _preview!.Show();
            RefreshPreview(card);
            play.Holder.ZIndex = _preview.ZIndex + 1;
            card.MouseFilter = Control.MouseFilterEnum.Ignore;
            // BeginDrag has already set the native hover scale before Start.
            play.Holder.SetScaleInstantly(Vector2.One * HeldCardScale);
            play.Holder.SetTargetPosition(Vector2.Zero);
            _hand.GetTree().ProcessFrame += UpdateHeldPreview;
            UpdateHeldPreview();
        }

        public bool IsInDragZone(bool cancelZone)
        {
            // Keep the session cancellable during a deferred orientation change.
            if (!PortraitViewportPatch.IsPortrait)
                return cancelZone;
            float height = _hand.GetViewportRect().Size.Y;
            float y = _hand.GetViewport().GetMousePosition().Y;
            return cancelZone ? y >= height - 614 : y < height - 638;
        }

        public void BeginTargeting()
        {
            _targetingStarted = true;
            _previewScroll!.ScrollVertical = 0;
            UpdateHeldPreview();
        }

        public void AfterInput(NMouseCardPlay play, InputEvent inputEvent)
        {
            if (HeldPlay != play || inputEvent is not InputEventMouseButton mouse
                || mouse.ButtonIndex != MouseButton.Left || !mouse.IsReleased())
                return;
            // Releasing in either the hand or the narrow hysteresis gap cancels.
            // Also cancel a press/release that finished before the native async
            // loop entered TargetSelection: it must never become a second tap.
            bool cancel = !_targetingStarted
                || mouse.Position.Y >= _hand.GetViewportRect().Size.Y - 638;
            if (!cancel && play.Holder.CardModel?.TargetType is TargetType.AnyEnemy or TargetType.AnyAlly)
            {
                NTargetManager? targets = NTargetManager.Instance;
                // Native release over empty space switches to click targeting.
                // This portrait gesture ends on release instead of staying armed.
                cancel = targets == null || !targets.IsInSelection
                    || targets.Get("HoveredNode").AsGodotObject() == null;
            }
            if (cancel)
                play.CancelPlayCard();
        }

        private void UpdateHeldPreview()
        {
            if (HeldPlay?.Holder.CardNode?.Model is not { } model || _preview == null)
                return;
            bool singleTarget = model.TargetType is TargetType.AnyEnemy or TargetType.AnyAlly;
            _holdHint!.Text = _targetingStarted
                ? "松手出牌"
                : singleTarget ? "上拖选目标" : "上拖后松手";
            // Shape the actual hint at its final width before measuring. A CJK
            // line can be taller than the empty label's default font metrics.
            _holdHint.Size = new Vector2(324, _holdHint.Size.Y);
            _holdHint.GetLineCount();
            float hintHeight = _holdHint.GetLineHeight(0)
                + _holdHint.GetThemeStylebox("normal").GetMinimumSize().Y;
            _holdHint.Position = new Vector2(24, 588 - hintHeight);
            _holdHint.Size = new Vector2(324, hintHeight);
            if (_targetingStarted || !_previewText!.IsFinished())
                return;
            // Long text scrolls only for reading, after a one-second dwell.
            // It stops at the end and stops immediately when targeting begins.
            // No drag event is consumed by this passive ScrollContainer.
            float overflow = _previewText.GetContentHeight() - _previewScroll!.Size.Y;
            if (overflow > 0)
            {
                double elapsed = (Time.GetTicksMsec() - _readStartedAt) / 1000.0;
                _previewScroll.ScrollVertical = (int)Math.Min(overflow, Math.Max(0, elapsed - 1) * 36);
            }
        }

        private void CreatePreview(NCard card)
        {
            _preview = new Panel
            {
                Name = "PortraitCardReadingPanel", MouseFilter = Control.MouseFilterEnum.Ignore,
                ZIndex = 10, ClipContents = true, Visible = false,
            };
            // Keep the native skin at full opacity; existing reader geometry owns text padding.
            _preview.AddThemeStyleboxOverride("panel", new StyleBoxTexture
            {
                Texture = ResourceLoader.Load<Texture2D>("res://images/ui/hover_tip.png")
                    ?? throw new InvalidOperationException("Native hover-tip background failed to load for PortraitCardReadingPanel."),
                TextureMarginLeft = 55, TextureMarginTop = 43,
                TextureMarginRight = 91, TextureMarginBottom = 32,
                AxisStretchHorizontal = StyleBoxTexture.AxisStretchMode.Tile,
                AxisStretchVertical = StyleBoxTexture.AxisStretchMode.Tile,
                ModulateColor = Colors.White,
                ContentMarginLeft = 0, ContentMarginTop = 0,
                ContentMarginRight = 0, ContentMarginBottom = 0,
            });
            _hand.AddChild(_preview);
            _previewTitle = new Label
            {
                Name = "PortraitCardTitle", MouseFilter = Control.MouseFilterEnum.Ignore,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                // Keep the minimum size independent of wrapping; PlacePreview
                // measures the full native title after assigning its final width.
                ClipText = true, MaxLinesVisible = -1,
                TextOverrunBehavior = TextServer.OverrunBehavior.NoTrimming,
            };
            _previewEnergy = new Label { Name = "PortraitCardEnergy", MouseFilter = Control.MouseFilterEnum.Ignore };
            _previewStar = new Label { Name = "PortraitCardStar", MouseFilter = Control.MouseFilterEnum.Ignore };
            _preview.AddChild(_previewTitle);
            _preview.AddChild(_previewEnergy);
            _preview.AddChild(_previewStar);
            _previewScroll = new ScrollContainer
            {
                Name = "PortraitCardDescriptionScroll", HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
                VerticalScrollMode = ScrollContainer.ScrollMode.ShowNever, MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            _preview.AddChild(_previewScroll);
            _previewText = CreateTextMirror(card, "PortraitCardDescription");
            _previewScroll.AddChild(_previewText);
            _previewScroll.GetVScrollBar().MouseFilter = Control.MouseFilterEnum.Ignore;
            _previewScroll.GetHScrollBar().MouseFilter = Control.MouseFilterEnum.Ignore;
            _selectionText = new VBoxContainer
            {
                Name = "PortraitHandSelectionText", Visible = false,
                MouseFilter = Control.MouseFilterEnum.Pass, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            };
            _selectionText.AddThemeConstantOverride("separation", 12);
            _preview.AddChild(_selectionText);
            _beforeCost = new Label
            {
                Name = "PortraitHandBeforeCost", Visible = false,
                MouseFilter = Control.MouseFilterEnum.Ignore, AutowrapMode = TextServer.AutowrapMode.WordSmart,
            };
            _selectionText.AddChild(_beforeCost);
            _beforeText = CreateTextMirror(card, "PortraitHandBeforeDescription");
            _beforeText.Visible = false;
            _beforeText.MouseFilter = Control.MouseFilterEnum.Pass;
            _selectionText.AddChild(_beforeText);
            _afterHeading = new Label
            {
                Name = "PortraitHandAfterHeading", Text = "升级后", Visible = false,
                MouseFilter = Control.MouseFilterEnum.Ignore, AutowrapMode = TextServer.AutowrapMode.WordSmart,
            };
            _afterHeading.AddThemeFontSizeOverride("font_size", 48);
            _selectionText.AddChild(_afterHeading);
            // Reuse both display descriptions in two equal-width columns. Their
            // full content sets the row height inside the existing scroll area.
            _upgradeColumns = new GridContainer
            {
                Name = "PortraitHandUpgradeColumns", Columns = 2, Visible = false,
                MouseFilter = Control.MouseFilterEnum.Pass, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            };
            _upgradeColumns.AddThemeConstantOverride("h_separation", 24);
            _upgradeColumns.AddThemeConstantOverride("v_separation", 12);
            _selectionText.AddChild(_upgradeColumns);
            _hand.PeekButton.AddTargets(_preview);
            _holdHint = new Label
            {
                Name = "PortraitCardHoldHint", MouseFilter = Control.MouseFilterEnum.Ignore,
                AutowrapMode = TextServer.AutowrapMode.WordSmart, ClipText = true,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _holdHint.AddThemeFontSizeOverride("font_size", 42);
            _holdHint.AddThemeColorOverride("font_color", new Color(0.88f, 0.84f, 0.71f));
            _preview.AddChild(_holdHint);
        }

        private static MegaRichTextLabel CreateTextMirror(NCard card, string name)
        {
            MegaRichTextLabel source = card.Get("_descriptionLabel").As<MegaRichTextLabel>();
            MegaRichTextLabel display = new()
            {
                Name = name, AutoSizeEnabled = false,
                BbcodeEnabled = true, FitContent = true, ScrollActive = false,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                MouseFilter = Control.MouseFilterEnum.Ignore, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                Theme = source.Theme, CustomEffects = source.CustomEffects.Duplicate(),
            };
            // MegaRichTextLabel requires an explicit native font override before
            // Ready. Preserve actual fonts/effects rather than parsing card text.
            foreach (string font in new[] { "normal_font", "bold_font", "italics_font", "bold_italics_font", "mono_font" })
                display.AddThemeFontOverride(font, source.GetThemeFont(font));
            foreach (string size in new[] { "normal_font_size", "bold_font_size", "italics_font_size", "bold_italics_font_size", "mono_font_size" })
                display.AddThemeFontSizeOverride(size, 48);
            foreach (string color in new[] { "default_color", "font_shadow_color", "font_outline_color" })
                display.AddThemeColorOverride(color, source.GetThemeColor(color));
            return display;
        }

        private void SetSelectionReading(bool enabled)
        {
            if (_selectionReading == enabled || _preview == null) return;
            if (!enabled) SetUpgradeColumns(false);
            _selectionReading = enabled;
            _previewText!.CustomMinimumSize = Vector2.Zero;
            _previewTitle!.ClipText = !enabled;
            if (enabled)
            {
                // Reuse only our display labels. Native cards and holders keep
                // their parents, callbacks and the original selection lifetime.
                _previewTitle.Reparent(_selectionText!);
                _previewEnergy!.Reparent(_selectionText!);
                _previewStar!.Reparent(_selectionText!);
                _previewText.Reparent(_selectionText!);
                _selectionText!.MoveChild(_previewTitle, 0);
                _selectionText.Reparent(_previewScroll!);
                _selectionText.Show();
            }
            else
            {
                _preview.Hide();
                _selectionText!.Reparent(_preview);
                _previewTitle.Reparent(_preview);
                _previewEnergy!.Reparent(_preview);
                _previewStar!.Reparent(_preview);
                _previewText.Reparent(_previewScroll!);
                _selectionText.Hide();
            }
            _holdHint!.Visible = !enabled;
            _previewText.MouseFilter = enabled ? Control.MouseFilterEnum.Pass : Control.MouseFilterEnum.Ignore;
            _previewScroll!.MouseFilter = enabled ? Control.MouseFilterEnum.Stop : Control.MouseFilterEnum.Ignore;
            _previewScroll.VerticalScrollMode = enabled ? ScrollContainer.ScrollMode.Auto : ScrollContainer.ScrollMode.ShowNever;
            _previewScroll.GetVScrollBar().MouseFilter = enabled ? Control.MouseFilterEnum.Stop : Control.MouseFilterEnum.Ignore;
            _previewScroll.ScrollVertical = 0;
        }

        private void SetUpgradeColumns(bool enabled)
        {
            if (_readingColumns == enabled) return;
            _readingColumns = enabled;
            // The short reader needs its height for actual text, not repeated
            // section gaps. Restore the long reader's spacing on the same exit.
            _selectionText!.AddThemeConstantOverride("separation", enabled ? 6 : 12);
            _upgradeColumns!.AddThemeConstantOverride("v_separation", enabled ? 6 : 12);
            if (enabled)
            {
                // Only our mirrors move. The before-card keeps its native
                // ReturnCard signal and the after-card keeps the upgrade model.
                _beforeCost!.Reparent(_upgradeColumns!);
                _afterHeading!.Reparent(_upgradeColumns!);
                _beforeText!.Reparent(_upgradeColumns!);
                _previewText!.Reparent(_upgradeColumns!);
                _upgradeColumns!.Show();
            }
            else
            {
                _beforeCost!.Reparent(_selectionText!);
                _beforeText!.Reparent(_selectionText!);
                _afterHeading!.Reparent(_selectionText!);
                _previewText!.Reparent(_selectionText!);
                // Restore the same vertical reading order before changing to
                // simple selection, long portrait, landscape, or native hold.
                Control[] rows = { _previewTitle!, _beforeCost, _beforeText, _afterHeading,
                    _previewEnergy!, _previewStar!, _previewText };
                for (int index = 0; index < rows.Length; index++)
                    _selectionText!.MoveChild(rows[index], index);
                _upgradeColumns!.Hide();
            }
            _previewScroll!.ScrollVertical = 0;
        }

        public void RefreshPreview(NCard card)
        {
            if (_previewText == null)
                return;
            NCard? before = _selectionReading ? UpgradeHolder(true)?.CardNode : null;
            NCard? reading = _selectionReading ? UpgradeHolder(false)?.CardNode ?? _selectionReadCard : HeldPlay?.Holder.CardNode;
            if (reading == null || !GodotObject.IsInstanceValid(reading) || (card != reading && card != before)) return;
            card = reading;
            // Read the real card's already-computed text. A second NCard using
            // the same Model would overwrite DynamicVars' target preview cache.
            MegaRichTextLabel source = card.Get("_descriptionLabel").As<MegaRichTextLabel>();
            if (_previewText.Text != source.Text)
            {
                _previewText.Text = source.Text;
                _previewScroll!.ScrollVertical = 0;
                _readStartedAt = Time.GetTicksMsec();
            }
            Label title = card.Get("_titleLabel").As<Label>();
            Label energy = card.Get("_energyLabel").As<Label>();
            Label star = card.Get("_starLabel").As<Label>();
            bool columns = before != null && _hand.GetViewportRect().Size.Y - 1118 < 422 + 48;
            CopyLabel(_previewTitle!, title, title.Text, columns ? 48 : 54);
            CopyLabel(_previewEnergy!, energy, "能量 " + energy.Text, 42);
            CopyLabel(_previewStar!, star, "星能 " + star.Text, 42);
            _previewEnergy!.Visible = card.Get("_energyIcon").As<Control>().Visible;
            _previewStar!.Visible = card.Get("_starIcon").As<Control>().Visible;
            if (_selectionReading)
            {
                bool comparing = before != null;
                SetUpgradeColumns(columns);
                _beforeCost!.Visible = _beforeText!.Visible = _afterHeading!.Visible = comparing;
                if (before != null)
                {
                    Label beforeTitle = before.Get("_titleLabel").As<Label>();
                    // Ordinary native upgrades append "+" to this same name.
                    // Preserve both full names when a card actually changes it.
                    bool repeatedTitle = title.Text == beforeTitle.Text || title.Text == beforeTitle.Text + "+";
                    string costs = columns && repeatedTitle ? "升级前" : "升级前 · " + beforeTitle.Text;
                    if (before.Get("_energyIcon").As<Control>().Visible)
                        costs += " · 能量 " + before.Get("_energyLabel").As<Label>().Text;
                    if (before.Get("_starIcon").As<Control>().Visible)
                        costs += " · 星能 " + before.Get("_starLabel").As<Label>().Text;
                    CopyLabel(_beforeCost, beforeTitle, costs, 42);
                    string afterCosts = "升级后";
                    if (columns)
                    {
                        if (_previewEnergy.Visible) afterCosts += " · " + _previewEnergy.Text;
                        if (_previewStar.Visible) afterCosts += " · " + _previewStar.Text;
                        // Costs come from the native visible icons/labels; no
                        // value is recalculated or inferred from the model.
                        _previewEnergy.Hide();
                        _previewStar.Hide();
                    }
                    _afterHeading.Text = afterCosts;
                    int headingSize = columns ? 42 : 48;
                    if (_afterHeading.GetThemeFontSize("font_size") != headingSize)
                        _afterHeading.AddThemeFontSizeOverride("font_size", headingSize);
                    string beforeBody = before.Get("_descriptionLabel").As<MegaRichTextLabel>().Text;
                    if (_beforeText.Text != beforeBody)
                    {
                        _beforeText.Text = beforeBody;
                        _previewScroll!.ScrollVertical = 0;
                    }
                }
            }
            PlacePreview();
        }

        private static void CopyLabel(Label display, Label source, string text, int size)
        {
            display.Theme = source.Theme;
            display.AddThemeFontOverride("font", source.GetThemeFont("font"));
            display.AddThemeFontSizeOverride("font_size", size);
            display.AddThemeColorOverride("font_color", source.GetThemeColor("font_color"));
            display.Text = text;
        }

        private void PlacePreview()
        {
            if (_preview == null)
                return;
            float width = _hand.GetViewportRect().Size.X - 96;
            if (_selectionReading)
            {
                // The whole header and both calculated descriptions scroll
                // together, leaving a usable reader even at 1080x1440.
                float height = _hand.GetViewportRect().Size.Y - 1118;
                float left = _readingColumns || UpgradeHolder(false) == null ? 24 : 372;
                _preview.Position = new Vector2(48, 480);
                _preview.Size = new Vector2(width, height);
                float verticalPadding = _readingColumns ? 12 : 24;
                _previewScroll!.Position = new Vector2(left, verticalPadding);
                _previewScroll.Size = new Vector2(width - left - 24, height - verticalPadding * 2);
                _previewText!.CustomMinimumSize = Vector2.Zero;
                return;
            }
            float textWidth = width - 396;
            float costWidth = (textWidth - 24) * 0.5f;
            _preview.Position = new Vector2(48, _hand.GetViewportRect().Size.Y - 792);
            _preview.Size = new Vector2(width, 612);
            _previewTitle!.Position = new Vector2(372, 24);
            // GetLineCount shapes synchronously at this width. A short title
            // keeps the reading space; wrapped titles move the costs and body down.
            _previewTitle.Size = new Vector2(textWidth, _previewTitle.Size.Y);
            int titleLines = _previewTitle.GetLineCount();
            float titleHeight = _previewTitle.GetThemeStylebox("normal").GetMinimumSize().Y;
            for (int line = 0; line < titleLines; line++)
                titleHeight += _previewTitle.GetLineHeight(line);
            titleHeight += Math.Max(0, titleLines - 1) * _previewTitle.GetThemeConstant("line_spacing");
            _previewTitle.Size = new Vector2(textWidth, titleHeight);
            float costsY = 24 + titleHeight + 12;
            float costHeight = Math.Max(_previewEnergy!.GetLineHeight(), _previewStar!.GetLineHeight());
            _previewEnergy.Position = new Vector2(372, costsY);
            _previewEnergy.Size = new Vector2(costWidth, costHeight);
            _previewStar.Position = new Vector2(372 + costWidth + 24, costsY);
            _previewStar.Size = new Vector2(costWidth, costHeight);
            float bodyY = costsY + costHeight + 15;
            _previewScroll!.Position = new Vector2(372, bodyY);
            _previewScroll.Size = new Vector2(textWidth, 588 - bodyY);
            _previewText!.CustomMinimumSize = new Vector2(textWidth - 24, 0);
        }

        public void CloseHold()
        {
            if (HeldPlay != null)
            {
                _hand.GetTree().ProcessFrame -= UpdateHeldPreview;
                if (GodotObject.IsInstanceValid(HeldPlay.Holder))
                {
                    HeldPlay.Holder.ZIndex = _heldZIndex;
                    if (HeldPlay.Holder.CardNode is { } card)
                        card.MouseFilter = _cardMouseFilter;
                }
            }
            HeldPlay = null;
            _targetingStarted = false;
            _preview?.Hide();
        }

        public void Queue()
        {
            if (_queued)
                return;
            _queued = true;
            Callable.From(() =>
            {
                _queued = false;
                if (GodotObject.IsInstanceValid(_hand) && _hand.IsInsideTree())
                    Apply();
            }).CallDeferred();
        }

        public void RefreshMiniCard(NCard card)
        {
            // All three native owners can occupy a fixed selection slot.
            // The after-upgrade artwork and a dragged card keep their full art.
            Node? parent = card.GetParent();
            bool inSlot = parent is NHandCardHolder && parent.GetParent() == _hand.CardHolderContainer;
            if (UsesSelectionSlots)
                inSlot |= parent is NSelectedHandCardHolder && parent.GetParent() == _selected
                    || parent == UpgradeHolder(true);
            if (PortraitViewportPatch.IsPortrait && inSlot)
                MiniCards.GetValue(card, node => new MiniCardState(node)).Apply();
            else if (MiniCards.TryGetValue(card, out MiniCardState? state))
                state.Restore();
        }

        public void Apply()
        {
            if (_applying)
                return;
            _applying = true;
            try
            {
                if (!PortraitViewportPatch.IsPortrait)
                {
                    HeldPlay?.CancelPlayCard();
                    CloseHold();
                    _slots.Hide();
                    RestoreSelection();
                    foreach (NHandCardHolder holder in _hand.ActiveHolders)
                        if (holder.CardNode is { } card)
                            RefreshMiniCard(card);
                    if (_active)
                    {
                        _active = false;
                        _containerOriginal.Restore();
                        // The native layout restores current cards, including cards
                        // drawn since entering portrait. Do not replay stale poses.
                        _hand.ForceRefreshCardIndices();
                    }
                    return;
                }

                _active = true;
                PlacePreview();
                float width = _hand.GetViewportRect().Size.X;
                float height = _hand.GetViewportRect().Size.Y;
                NHandCardHolder[] holders = _hand.ActiveHolders.ToArray();
                float slotWidth = (width - 96 - 4 * 24) / 5;
                float pitch = slotWidth + 24;

                // Ten native hand slots: two rows of five. Each 150x211 card
                // keeps its touch width above 144, with 12 units between rows.
                // Leave Hand itself untouched: native disable/entry/exit tweens
                // animate its position and a dragged card is reparented to Hand.
                Control container = _hand.CardHolderContainer;
                container.AnchorLeft = container.AnchorRight = 0.5f;
                container.AnchorTop = container.AnchorBottom = 1f;
                container.OffsetLeft = container.OffsetRight = 0;
                container.OffsetTop = container.OffsetBottom = -508.5f;
                _slots.Position = new Vector2(48, height - 614);
                _slots.Size = new Vector2(width - 96, 434);
                _slots.Show();
                for (int index = 0; index < _slotPanels.Length; index++)
                {
                    _slotPanels[index].Position = new Vector2(index % 5 * pitch, index / 5 * 223);
                    _slotPanels[index].Size = new Vector2(slotWidth, 211);
                    bool selected = UsesSelectionSlots &&
                        (_selected.Holders.Any(holder => holder.CardNode is { } card && SelectionSlot(card) == index)
                        || (UpgradeHolder(true)?.CardNode is { } before && SelectionSlot(before) == index));
                    SetSlotStyle(index, selected);
                }

                for (int index = 0; index < holders.Length; index++)
                {
                    NHandCardHolder holder = holders[index];
                    int slot = UsesSelectionSlots && holder.CardNode != null ? SelectionSlot(holder.CardNode) : index;
                    if (slot < 0) continue;
                    float x = (slot % 5 - 2) * pitch;
                    float y = slot / 5 * 223;
                    if (holder.CardNode is { } card)
                        RefreshMiniCard(card);
                    if (holder == _hand.FocusedHolder)
                    {
                        // Native focus starts a synchronous lerp toward the fan.
                        // Restore both axes before GUI dispatch hit-tests again.
                        holder.SetScaleInstantly(Vector2.One * CardScale);
                        holder.Position = new Vector2(x, y);
                    }
                    holder.SetTargetAngle(0);
                    holder.SetTargetScale(Vector2.One * CardScale);
                    holder.SetTargetPosition(new Vector2(x, y));
                }
                if (_hand.IsInCardSelection || _selectionChrome) ArrangeSelection();
            }
            finally
            {
                _applying = false;
            }
        }
    }
}

/// <summary>Adapts the native drag and cancel zones to the portrait hand bounds.</summary>
internal sealed class PortraitCardReadGatePatch : IPatchMethod
{
    public static string PatchId => "sts2portrait_card_drag_zones";
    public static string Description => "Use portrait hand bounds for native drag-to-play gestures";
    public static bool IsCritical => true;
    public static ModPatchTarget[] GetTargets() => new[]
    {
        PatchTarget.Method(typeof(NMouseCardPlay), "IsCardInPlayZone"),
        PatchTarget.Method(typeof(NMouseCardPlay), "IsCardInCancelZone"),
    };

    public static bool Prefix(NMouseCardPlay __instance, MethodBase __originalMethod, ref bool __result)
    {
        if (!PortraitHandPatch.TryDragZone(__instance,
            __originalMethod.Name == "IsCardInCancelZone", out bool inZone))
            return true;
        __result = inZone;
        return false;
    }
}
