using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.addons.mega_text;
using STS2RitsuLib.Patching.Models;
using OpCodes = System.Reflection.Emit.OpCodes;

namespace STS2Portrait.Patches;

/// <summary>Reflows native deck viewing and selection without replacing their grid or rules.</summary>
internal sealed class PortraitDeckSelectPatch : IPatchMethod
{
    public static string PatchId => "sts2portrait_deck_select";
    public static string Description => "Readable native deck viewing and upgrade selection";
    public static bool IsCritical => true;
    public static ModPatchTarget[] GetTargets() => new[]
    {
        // Reflow only the native out-of-combat multi-transform result animation.
        PatchTarget.Method(typeof(NCardPreviewContainer), "_Ready"),
        PatchTarget.Method(typeof(NCardPreviewContainer), "ReformatElements"),
        // Simple grids retain native multi-selection and manual confirmation rules.
        PatchTarget.Method(typeof(NSimpleCardSelectScreen), "_Ready"),
        PatchTarget.Method(typeof(NSimpleCardSelectScreen), "OnCardClicked"),
        // Bundle preview and confirmation remain owned by the native two-step selector.
        PatchTarget.Method(typeof(NChooseABundleSelectionScreen), "_Ready"),
        PatchTarget.Method(typeof(NChooseABundleSelectionScreen), "OnBundleClicked"),
        PatchTarget.Method(typeof(NChooseABundleSelectionScreen), "CancelSelection"),
        // Generated card choices retain their original row, skip and completion rules.
        PatchTarget.Method(typeof(NChooseACardSelectionScreen), "_Ready"),
        PatchTarget.Method(typeof(NDeckViewScreen), "_Ready"),
        PatchTarget.Method(typeof(NDeckViewScreen), "DisplayCards"),
        // Pandora results retain their native Inspect, upgrade toggle and capstone close.
        PatchTarget.Method(typeof(NSimpleCardsViewScreen), "_Ready"),
        // Combat piles use the same native grid, but own their order and return.
        PatchTarget.Method(typeof(NCardPileScreen), "_Ready"),
        PatchTarget.Method(typeof(NCardPileScreen), "OnPileContentsChanged"),
        // Combat selection keeps its own completion rules and Peek pile controls.
        PatchTarget.Method(typeof(NCombatPileCardSelectScreen), "_Ready"),
        PatchTarget.Method(typeof(NCombatPileCardSelectScreen), "OnCardClicked"),
        PatchTarget.Method(typeof(NCombatPileCardSelectScreen), "UpdatePileContents"),
        // Selection limits must count every eligible card, not virtualized holders.
        PatchTarget.Method(typeof(NCombatPileCardSelectScreen), "UpdateConfirmButton"),
        PatchTarget.Method(typeof(NCombatPileCardSelectScreen), "CheckIfSelectionComplete"),
        PatchTarget.Method(typeof(NCombatPileCardSelectScreen), "CompleteSelection"),
        PatchTarget.Method(typeof(NCombatPileCardSelectScreen), "_ExitTree"),
        PatchTarget.Method(typeof(NCombatCardPile), "AnimIn"),
        PatchTarget.Method(typeof(NExhaustPileButton), "SetAnimInOutPositions"),
        PatchTarget.Method(typeof(NDeckCardSelectScreen), "_Ready"),
        PatchTarget.Method(typeof(NDeckCardSelectScreen), "OnCardClicked"),
        PatchTarget.Method(typeof(NDeckCardSelectScreen), "PreviewSelection", Type.EmptyTypes),
        PatchTarget.Method(typeof(NDeckCardSelectScreen), "CancelSelection"),
        PatchTarget.Method(typeof(NDeckUpgradeSelectScreen), "_Ready"),
        PatchTarget.Method(typeof(NDeckUpgradeSelectScreen), "OnCardClicked"),
        PatchTarget.Method(typeof(NDeckUpgradeSelectScreen), "CancelSelection"),
        // Transformation keeps native selection, candidate cycling and completion.
        PatchTarget.Method(typeof(NDeckTransformSelectScreen), "_Ready"),
        PatchTarget.Method(typeof(NDeckTransformSelectScreen), "OnCardClicked"),
        PatchTarget.Method(typeof(NDeckTransformSelectScreen), "OpenPreviewScreen"),
        PatchTarget.Method(typeof(NDeckTransformSelectScreen), "CancelSelection"),
        PatchTarget.Method(typeof(NTransformPreview), "Initialize"),
        // Enchantment selection retains its native eligibility and confirmation rules.
        PatchTarget.Method(typeof(NDeckEnchantSelectScreen), "_Ready"),
        PatchTarget.Method(typeof(NDeckEnchantSelectScreen), "OnCardClicked"),
        PatchTarget.Method(typeof(NDeckEnchantSelectScreen), "PreviewSelection", Type.EmptyTypes),
        PatchTarget.Method(typeof(NDeckEnchantSelectScreen), "CancelSelection"),
        PatchTarget.Method(typeof(NCardGrid), "InitGrid", Type.EmptyTypes),
        PatchTarget.Method(typeof(NCardGrid), "ProcessMouseEvent"),
        PatchTarget.Method(typeof(NCardGrid), "AllocateCardHolders"),
        PatchTarget.Method(typeof(NCardGrid), "ReallocateAll"),
        PatchTarget.Method(typeof(NCardHolder), "OnMousePressed"),
        PatchTarget.Method(typeof(NCardHolder), "OnMouseReleased"),
        PatchTarget.Method(typeof(NCardHolder), "DoCardHoverEffects"),
        PatchTarget.Method(typeof(NGridCardHolder), "_ExitTree"),
        PatchTarget.Method(typeof(NPreviewCardHolder), "_ExitTree"),
        PatchTarget.Method(typeof(NCard), "UpdateVisuals"),
        PatchTarget.Method(typeof(NBackButton), "OnEnable"),
        PatchTarget.Method(typeof(NBackButton), "OnDisable"),
        PatchTarget.Method(typeof(NBackButton), "OnWindowChange"),
        PatchTarget.Method(typeof(NConfirmButton), "OnEnable"),
        PatchTarget.Method(typeof(NConfirmButton), "OnDisable"),
        PatchTarget.Method(typeof(NConfirmButton), "OnWindowChange"),
    };

    private static readonly ConditionalWeakTable<Control, LayoutState> States = new();
    private static readonly ConditionalWeakTable<NChooseACardSelectionScreen, ChoiceLayoutState> ChoiceStates = new();
    private static readonly ConditionalWeakTable<NChooseABundleSelectionScreen, BundleLayoutState> BundleStates = new();
    // The public CurrentlyDisplayedCards contains only virtualized holders.
    // Read the complete native filtered grid list without rerunning its filter.
    private static readonly FieldInfo GridCards = AccessTools.Field(typeof(NCardGrid), "_cards");
    // Simple selection survives native holder rebuilds; restore only its visible highlights.
    private static readonly FieldInfo SimpleSelectedCards = AccessTools.Field(typeof(NSimpleCardSelectScreen), "_selectedCards");
    // A partially selected transform grid uses the same native HashSet contract.
    private static readonly FieldInfo TransformSelectedCards = AccessTools.Field(typeof(NDeckTransformSelectScreen), "_selectedCards");
    // Native completion owns the lifetime of the waiting play-pile card drawing.
    private static readonly FieldInfo CombatSelectionCompletion = AccessTools.Field(typeof(NCardGridSelectionScreen), "_completionSource");
    private static LayoutState? Find(Node node)
    {
        for (Node? parent = node; parent != null; parent = parent.GetParent())
            if (parent is NCardGridSelectionScreen or NDeckViewScreen or NSimpleCardsViewScreen or NCardPileScreen && States.TryGetValue((Control)parent, out LayoutState? state))
                return state;
        return null;
    }

    private static ChoiceLayoutState? FindChoice(Node node)
    {
        for (Node? parent = node; parent != null; parent = parent.GetParent())
            if (parent is NChooseACardSelectionScreen choice && ChoiceStates.TryGetValue(choice, out ChoiceLayoutState? state))
                return state;
        return null;
    }

    private static BundleLayoutState? FindBundle(Node node)
    {
        for (Node? parent = node; parent != null; parent = parent.GetParent())
            if (parent is NChooseABundleSelectionScreen screen && BundleStates.TryGetValue(screen, out BundleLayoutState? state))
                return state;
        return null;
    }

    public static void Prefix(Node __instance, MethodBase __originalMethod, object[] __args)
    {
        if (!Entry.IsDisabled && PortraitViewportPatch.IsPortrait
            && __instance is NBackButton or NConfirmButton && __originalMethod.Name is "OnEnable" or "OnDisable")
            FindBundle(__instance)?.ButtonDestination((Control)__instance, false);
        if (__instance is NCombatCardPile choicePile && __originalMethod.Name == "AnimIn"
            && !Entry.IsDisabled && PortraitViewportPatch.IsPortrait)
            FindChoice(__instance)?.SyncPileTarget(choicePile);
        LayoutState? state = Find(__instance);
        if (state == null)
            return;
        if (__instance is NCardHolder holder && __originalMethod.Name == "_ExitTree")
            state.RestoreHolder(holder);
        // Restore before native completion or tree-exit cancellation resumes OnPlay.
        if (__instance is NCombatPileCardSelectScreen && __originalMethod.Name is "CompleteSelection" or "_ExitTree")
            state.Restore(playingOnly: true);
        if (Entry.IsDisabled || !PortraitViewportPatch.IsPortrait || !state.Active)
            return;
        if (__instance is NCardHolder released && __originalMethod.Name == "OnMouseReleased")
            state.BeforeRelease(released, (InputEvent)__args[0]);
        else if (__instance is NBackButton or NConfirmButton && __originalMethod.Name is "OnEnable" or "OnDisable")
            state.ButtonDestination((Control)__instance, false);
        else if (__instance is NCardGrid && __originalMethod.Name == "InitGrid")
            state.PrepareGridWidth();
        else if (__instance is NCombatCardPile pile && __originalMethod.Name == "AnimIn")
            state.SyncPeekPileTarget(pile);
        else if (__instance is NTransformPreview)
            state.BeforeTransformInitialize();
    }

    public static void Postfix(Node __instance, MethodBase __originalMethod, object[] __args)
    {
        if (Entry.IsDisabled)
            return;
        if (__instance is NCardPreviewContainer resultContainer
            && resultContainer.GetParent() is NGlobalUi && resultContainer.Name == "CardPreviewContainer")
        {
            if (__originalMethod.Name == "_Ready")
            {
                // This signal belongs to the same native container and expires with it.
                // Rerun native positions on resize, including landscape restoration.
                resultContainer.Resized += () =>
                {
                    if (resultContainer.GetChildCount() > 0
                        && resultContainer.GetChildren().All(child => child is NCardTransformVfx))
                        resultContainer.Call("ReformatElements", resultContainer);
                };
            }
            else if (PortraitViewportPatch.IsPortrait && NCombatRoom.Instance == null)
            {
                Node[] results = resultContainer.GetChildren().ToArray();
                // The original Claws contract permits at most six cards. Leave other
                // preview types, one-to-three results and larger batches unchanged.
                if (results.Length is >= 4 and <= 6 && results.All(child => child is NCardTransformVfx))
                {
                    Vector2 center = resultContainer.Size * .5f + Vector2.Down * 50f;
                    Vector2 step = NCard.defaultSize + Vector2.One * 25f;
                    for (int i = 0; i < results.Length; i++)
                    {
                        int row = i / 3, column = i % 3;
                        int rowCount = Math.Min(3, results.Length - row * 3);
                        // Move only the existing VFX root. Card scale curves, shine,
                        // fly-to-deck completion and native cleanup stay untouched.
                        ((NCardTransformVfx)results[i]).Position = center + new Vector2(
                            (column - (rowCount - 1) * .5f) * step.X,
                            (row - .5f) * step.Y);
                    }
                }
            }
            return;
        }
        if (__instance is NChooseABundleSelectionScreen bundleScreen && __originalMethod.Name == "_Ready")
        {
            BundleStates.GetValue(bundleScreen, value => new BundleLayoutState(value)).Apply();
            return;
        }
        if (FindBundle(__instance) is BundleLayoutState bundleState)
        {
            if (__instance is NCard) bundleState.RefreshReading();
            else if (__instance is NBackButton or NConfirmButton && __originalMethod.Name == "OnWindowChange")
                bundleState.ButtonDestination((Control)__instance, true);
            else if (__instance is NChooseABundleSelectionScreen) bundleState.Queue();
            return;
        }
        if (__instance is NChooseACardSelectionScreen choice && __originalMethod.Name == "_Ready")
        {
            ChoiceStates.GetValue(choice, value => new ChoiceLayoutState(value)).Apply();
            return;
        }
        if (FindChoice(__instance) is ChoiceLayoutState choiceState)
        {
            if (__instance is NCard) choiceState.RefreshReading();
            else if (__instance is NCardHolder choiceHolder && __originalMethod.Name == "DoCardHoverEffects")
                choiceState.KeepCardScale(choiceHolder);
            else if (__instance is NExhaustPileButton choicePile && __originalMethod.Name == "SetAnimInOutPositions")
                choiceState.SyncPileTarget(choicePile);
            return;
        }
        if (__instance is NDeckCardSelectScreen or NDeckUpgradeSelectScreen or NDeckTransformSelectScreen or NDeckEnchantSelectScreen or NDeckViewScreen or NSimpleCardsViewScreen or NCardPileScreen or NCombatPileCardSelectScreen or NSimpleCardSelectScreen && __originalMethod.Name == "_Ready")
        {
            States.GetValue((Control)__instance, value => new LayoutState(value)).Apply();
            return;
        }
        LayoutState? state = Find(__instance);
        if (state == null)
            return;
        if (__instance is NDeckCardSelectScreen && __originalMethod.Name == "PreviewSelection")
        {
            // The native method queues its 1/.8/.55 scale callback first. This
            // separate deferred call must not be merged into an older Queue.
            Callable.From(state.AfterNativePreviewScale).CallDeferred();
        }
        else if (__instance is NCardGridSelectionScreen or NDeckViewScreen or NCardPileScreen or NTransformPreview)
            state.Queue();
        else if (__instance is NCardGrid grid)
        {
            if (__originalMethod.Name == "InitGrid")
            {
                state.PrepareGridHolders();
                if (state.Active && PortraitViewportPatch.IsPortrait
                    && Ancestors(grid).OfType<NSimpleCardSelectScreen>().FirstOrDefault() is { } simple)
                {
                    // InitGrid creates new holders without AssignCardsToRow's highlight restoration.
                    HashSet<CardModel> selected = (HashSet<CardModel>)SimpleSelectedCards.GetValue(simple)!;
                    foreach (NGridCardHolder holder in grid.CurrentlyDisplayedCardHolders)
                        if (selected.Contains(holder.CardModel)) holder.CardNode!.CardHighlight.AnimShow();
                }
                else if (state.Active && PortraitViewportPatch.IsPortrait
                    && Ancestors(grid).OfType<NDeckTransformSelectScreen>().FirstOrDefault() is { } transform)
                {
                    // Restore presentation after rebuilding holders, never add another selection.
                    HashSet<CardModel> selected = (HashSet<CardModel>)TransformSelectedCards.GetValue(transform)!;
                    foreach (NGridCardHolder holder in grid.CurrentlyDisplayedCardHolders)
                        if (selected.Contains(holder.CardModel)) holder.CardNode!.CardHighlight.AnimShow();
                }
            }
            else if (__originalMethod.Name == "ProcessMouseEvent") state.TrackDrag((InputEvent)__args[0]);
        }
        else if (__instance is NCardHolder holder)
        {
            if (__originalMethod.Name == "OnMousePressed") state.BeginPress(holder, (InputEvent)__args[0]);
            else if (__originalMethod.Name == "DoCardHoverEffects") state.Read(holder);
        }
        else if (__instance is NCard card) state.RefreshCard(card);
        else if (__instance is NBackButton or NConfirmButton && __originalMethod.Name == "OnWindowChange")
            state.ButtonDestination((Control)__instance, true);
        else if (__instance is NExhaustPileButton pile && __originalMethod.Name == "SetAnimInOutPositions")
            state.SyncPeekPileTarget(pile);
    }

    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        CodeInstruction[] body = instructions.ToArray();
        if (__originalMethod.DeclaringType == typeof(NCombatPileCardSelectScreen)
            && __originalMethod.Name is "UpdateConfirmButton" or "CheckIfSelectionComplete")
        {
            // Replace only the source enumerable; native Count, limits and branches stay intact.
            MethodInfo getter = AccessTools.PropertyGetter(typeof(NCardGrid), nameof(NCardGrid.CurrentlyDisplayedCards));
            CodeInstruction[] reads = body.Where(item => item.Calls(getter)).ToArray();
            if (reads.Length != 1)
                throw new InvalidOperationException($"Unexpected native selector card-count reads in {__originalMethod.Name}: {reads.Length}.");
            reads[0].opcode = OpCodes.Call;
            reads[0].operand = AccessTools.Method(typeof(PortraitDeckSelectPatch), nameof(SelectionCandidates));
            return body;
        }
        if (__originalMethod.DeclaringType != typeof(NCardGrid)
            || __originalMethod.Name is not ("AllocateCardHolders" or "ReallocateAll"))
            return body;
        int topCount = 0, bottomCount = 0;
        var result = new List<CodeInstruction>();
        foreach (CodeInstruction item in body)
        {
            if (item.opcode == OpCodes.Ldc_R4 && item.operand is float value && value == 0f)
            {
                // The original virtual window assumes a viewport at y=0. Keep
                // its allocation algorithm and substitute this clipped grid.
                var load = new CodeInstruction(OpCodes.Ldarg_0);
                load.labels.AddRange(item.labels); load.blocks.AddRange(item.blocks);
                result.Add(load);
                result.Add(new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(PortraitDeckSelectPatch), nameof(GridTop))));
                topCount++;
            }
            else
            {
                if (item.Calls(AccessTools.Method(typeof(CanvasItem), nameof(CanvasItem.GetViewportRect))))
                {
                    item.opcode = OpCodes.Call;
                    item.operand = AccessTools.Method(typeof(PortraitDeckSelectPatch), nameof(GridBottomRect));
                    bottomCount++;
                }
                result.Add(item);
            }
        }
        int expectedTop = __originalMethod.Name == "AllocateCardHolders" ? 2 : 1;
        int expectedBottom = __originalMethod.Name == "AllocateCardHolders" ? 1 : 0;
        if (topCount != expectedTop || bottomCount != expectedBottom)
            throw new InvalidOperationException($"Unexpected native grid bounds in {__originalMethod.Name}: {topCount}/{bottomCount}.");
        return result;
    }

    private static IEnumerable<CardModel> SelectionCandidates(NCardGrid grid)
    {
        // The exact native selector owns filtering and refresh; its grid already holds all candidates.
        if (!Entry.IsDisabled && PortraitViewportPatch.IsPortrait
            && Ancestors(grid).OfType<NCombatPileCardSelectScreen>().FirstOrDefault() is { } selector
            && States.TryGetValue(selector, out LayoutState? state) && state.Active)
            return (List<CardModel>)GridCards.GetValue(grid)!;
        return grid.CurrentlyDisplayedCards;
    }

    private static float GridTop(NCardGrid grid) => !Entry.IsDisabled && PortraitViewportPatch.IsPortrait
        && Find(grid)?.Active == true ? grid.GlobalPosition.Y : 0f;
    private static Rect2 GridBottomRect(NCardGrid grid)
    {
        Rect2 rect = grid.GetViewportRect();
        if (!Entry.IsDisabled && PortraitViewportPatch.IsPortrait && Find(grid)?.Active == true)
            rect.Size = new Vector2(rect.Size.X, grid.GetGlobalRect().End.Y);
        return rect;
    }

    private sealed class LayoutState
    {
        private readonly Control _screen;
        private readonly NCardGrid _grid;
        private readonly Control _gridContent;
        private readonly MegaRichTextLabel _prompt;
        private readonly Control _promptRoot;
        private readonly NPeekButton? _peek;
        private readonly NCombatCardPile[] _peekPiles = Array.Empty<NCombatCardPile>();
        private readonly ColorRect _backdrop;
        private readonly ReadingPanel? _reading;
        private readonly Control? _enchantmentDescription;
        private readonly Control? _sortingOptions;
        private readonly HBoxContainer? _sorters;
        private readonly (bool Had, int Value) _sortSeparation;
        private readonly List<Preview> _previews = new();
        private readonly Dictionary<Control, PortraitControlSnapshot> _geometry = new();
        private readonly Dictionary<(Control Node, string Name), Variant> _properties = new();
        private readonly Dictionary<(Control Node, string Name), (bool Had, int Value)> _fonts = new();
        private readonly Dictionary<NCardHolder, Control.MouseFilterEnum> _hitboxFilters = new();
        private readonly Control[] _buttons;
        private Vector2 _viewport;
        private NCardHolder? _pressed;
        private Vector2 _pressPosition;
        private bool _dragged, _queued;
        private CardModel? _readModel;
        public bool Active { get; private set; }

        public LayoutState(Control screen)
        {
            _screen = screen;
            _grid = screen.Get("_grid").As<NCardGrid>();
            _gridContent = _grid.Get("_scrollContainer").As<Control>();
            _prompt = screen.Get(screen is NDeckViewScreen or NSimpleCardsViewScreen or NCardPileScreen ? "_bottomLabel" : "_infoLabel").As<MegaRichTextLabel>();
            _promptRoot = Ancestors(_prompt).OfType<Control>().First(node => node.Name == "BottomText");
            _peek = screen is NDeckViewScreen or NSimpleCardsViewScreen or NCardPileScreen ? null : screen.Get("_peekButton").As<NPeekButton>();
            _backdrop = new ColorRect { Name = "PortraitDeckBackdrop", Color = new Color(.075f, .10f, .09f),
                MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
            screen.AddChild(_backdrop); screen.MoveChild(_backdrop, 0);
            if (screen is NDeckViewScreen)
            {
                // Deck viewing already opens the native Inspect screen on click.
                // Keep sorting inside the original scrolled subtree.
                Control sorter = screen.Get("_obtainedSorter").As<Control>();
                _sorters = sorter.GetParent<HBoxContainer>();
                _sortingOptions = Ancestors(_sorters).OfType<Control>().First(node => node.Name == "SortingOptions");
                _sortSeparation = (_sorters.HasThemeConstantOverride("separation"), _sorters.GetThemeConstant("separation"));
            }
            else if (screen is not NSimpleCardsViewScreen)
            {
                // Result viewing, like the deck, reads full text through native Inspect.
                _reading = new ReadingPanel("PortraitDeckReading", screen, screen as NDeckEnchantSelectScreen, fitToContent: true);
                screen.MoveChild(_reading.Root, _grid.GetIndex() + 1);
                // Reflow after native text shaping or the animated grid finishes sizing.
                if (screen is NCombatPileCardSelectScreen)
                {
                    _reading.Root.Resized += Queue;
                    _gridContent.Resized += Queue;
                }
                // Native combat and simple-grid Peek keep this backdrop as a footer.
                if (screen is NCombatPileCardSelectScreen or NSimpleCardSelectScreen)
                    _peek!.AddTargets(_reading.Root);
                else
                    _peek?.AddTargets(_backdrop, _reading.Root);
            }
            if (screen is NDeckCardSelectScreen)
                _previews.Add(new Preview(this, screen.Get("_previewContainer").As<Control>(),
                    screen.Get("_previewCards").As<HBoxContainer>(), null));
            else if (screen is NDeckUpgradeSelectScreen)
            {
                _previews.Add(new Preview(this, screen.Get("_upgradeSinglePreviewContainer").As<Control>(),
                    null, screen.Get("_singlePreview").As<NUpgradePreview>()));
                _previews.Add(new Preview(this, screen.Get("_upgradeMultiPreviewContainer").As<Control>(),
                    screen.Get("_multiPreview").As<HBoxContainer>(), null));
            }
            else if (screen is NDeckTransformSelectScreen)
                _previews.Add(new Preview(this, screen.Get("_previewContainer").As<Control>(),
                    null, screen.Get("_transformPreview").As<NTransformPreview>()));
            else if (screen is NDeckEnchantSelectScreen)
            {
                _enchantmentDescription = screen.Get("_enchantmentDescriptionContainer").As<Control>();
                _previews.Add(new Preview(this, screen.Get("_enchantSinglePreviewContainer").As<Control>(),
                    null, screen.Get("_singlePreview").As<NEnchantPreview>()));
                _previews.Add(new Preview(this, screen.Get("_enchantMultiPreviewContainer").As<Control>(),
                    screen.Get("_multiPreview").As<HBoxContainer>(), null));
            }
            _buttons = Descendants(screen).Where(node => node is NBackButton or NConfirmButton).Cast<Control>().ToArray();
            if (screen is NCombatPileCardSelectScreen or NSimpleCardSelectScreen)
            {
                // These native nodes exist even when noncombat Simple leaves them disabled.
                NCombatPilesContainer piles = screen.Get("_combatPiles").As<NCombatPilesContainer>();
                _peekPiles = new NCombatCardPile[] { piles.DrawPile, piles.DiscardPile, piles.ExhaustPile };
                // This signal source is a child of the same screen and is freed with its owner.
                _peek!.Connect(NPeekButton.SignalName.Toggled, Callable.From<NPeekButton>(_ => Apply()));
            }
            _screen.GetViewport().SizeChanged += Queue;
            _screen.TreeExiting += () => _screen.GetViewport().SizeChanged -= Queue;
            _screen.VisibilityChanged += Queue;
            _promptRoot.MinimumSizeChanged += () =>
            {
                if (Active) Queue();
            };
        }

        public void Queue()
        {
            if (_queued) return;
            _queued = true;
            Callable.From(() => { _queued = false; Apply(); }).CallDeferred();
        }

        public void BeforeTransformInitialize()
        {
            // Native Initialize derives its card scale from Before.GlobalPosition.X.
            // Restore only this preview; keep the selected grid and native task intact.
            foreach (Preview preview in _previews) preview.Restore();
        }

        public void AfterNativePreviewScale()
        {
            if (!GodotObject.IsInstanceValid(_screen) || !_screen.IsInsideTree()) return;
            foreach (Preview preview in _previews) preview.CaptureNativeScale();
            Apply();
        }

        public void Apply()
        {
            if (!GodotObject.IsInstanceValid(_screen) || !_screen.IsInsideTree()) return;
            if (!PortraitViewportPatch.IsPortrait) { Restore(); return; }
            Vector2 viewport = _screen.GetViewportRect().Size;
            bool changed = !Active || viewport != _viewport;
            Active = true; _viewport = viewport;
            // Hide only original local executing cards while this native choice is pending.
            // Peek still moves the same nodes; completion and cancellation restore their drawing.
            if (_screen is NCombatPileCardSelectScreen
                && !((TaskCompletionSource<IEnumerable<CardModel>>)CombatSelectionCompletion.GetValue(_screen)!).Task.IsCompleted)
                foreach (NCard card in NCombatRoom.Instance!.Ui.PlayContainer.GetChildren().OfType<NCard>()
                    .Where(card => card.Model is { Pile.Type: PileType.Play } model && LocalContext.IsMe(model.Owner)))
                    Saved(card, "visible", false);
            float width = viewport.X - 96;
            bool deckView = _screen is NDeckViewScreen;
            bool resultView = _screen is NSimpleCardsViewScreen;
            // Native hover expands the first row through local Y=459.8. At
            // 1080x1440 reserve a 480-high grid and let the existing reader scroll.
            // The lower bound keeps the reader positive on shorter viewports;
            // it does not alter native card size or the full-hitbox selection gate.
            float readingHeight = Math.Clamp(viewport.Y - 1224, 144, 408);
            float readingTop = viewport.Y - 216 - readingHeight;
            // Peek changes without a viewport resize. Cover only the existing combat footer,
            // leaving the hand above H-180 visible and the selector's native controls in front.
            bool peekFooter = _peekPiles.Length > 0 && _peek!.IsPeeking;
            Place(_backdrop, peekFooter ? new Vector2(0, viewport.Y - 168) : Vector2.Zero,
                peekFooter ? new Vector2(viewport.X, 168) : viewport);
            // Native shared backstops retain their room shading and input ownership.
            // This drawing remains opaque only for the existing combat Peek footer.
            _backdrop.SelfModulate = peekFooter ? Colors.White : Colors.Transparent;
            _backdrop.Visible = peekFooter || _peek?.IsPeeking != true;
            if (changed)
            {
                Place(_promptRoot, new Vector2(48, 336), new Vector2(width, 144));
                // The native footer slab is oversized beside the portrait prompt.
                // Keep its label and save the original drawing for landscape restore.
                foreach (ColorRect slab in _promptRoot.GetChildren().OfType<ColorRect>().Where(node => node.Name == "ColorRect"))
                    Saved(slab, "visible", false);
                Saved(_prompt, "AutoSizeEnabled", false);
                // Wrapping needs the available width, not the native shrink minimum.
                Saved(_prompt, "size_flags_horizontal", (int)Control.SizeFlags.ExpandFill);
                Saved(_prompt, "autowrap_mode", (int)TextServer.AutowrapMode.WordSmart);
                foreach (string font in TextSizes) Font(_prompt, font, 48);
                // The native scroll viewport ends above the separate reading
                // panel. Neither the grid nor any live holder is reparented.
                Place(_grid, new Vector2(48, 504), new Vector2(width, deckView || resultView ? viewport.Y - 720 : readingTop - 528));
                Saved(_grid, "clip_contents", true);
                Remember(_gridContent);
                PrepareGridWidth();
                _grid.Set("_needsReinit", true);
                if (_peek != null)
                    Place(_peek, new Vector2(_peekPiles.Length > 0 ? 576 : (viewport.X - 144) / 2, viewport.Y - 192), new Vector2(144, 144));
                foreach (NCombatCardPile pile in _peekPiles)
                {
                    // Preserve native visibility, counts and click/Peek ownership.
                    Complete(pile.Get("_positionTween").AsGodotObject() as Tween);
                    SyncPeekPileTarget(pile);
                    Place(pile, pile.Get("_showPosition").AsVector2(), new Vector2(96, 96));
                    pile.PivotOffset = Vector2.Zero;
                    pile.Scale = Vector2.One * 1.5f;
                    foreach (Control hotkey in pile.GetChildren().OfType<Control>().Where(node => node.Name == "HotkeyIcon"))
                        Saved(hotkey, "visible", false);
                }
                if (deckView) LayoutSorting(width);
                if (_enchantmentDescription != null)
                {
                    // Keep native Peek visibility ownership; the copied description
                    // scrolls with card text and the original cannot intercept touch.
                    Saved(_enchantmentDescription, "modulate", Colors.Transparent);
                    foreach (Control node in Descendants(_enchantmentDescription).OfType<Control>().Append(_enchantmentDescription))
                        Saved(node, "mouse_filter", (int)Control.MouseFilterEnum.Ignore);
                }
                foreach (Control button in _buttons)
                {
                    Complete(button.Get("_moveTween").AsGodotObject() as Tween);
                    ButtonDestination(button, true);
                }
                if (_screen is NDeckUpgradeSelectScreen or NDeckTransformSelectScreen or NDeckViewScreen or NSimpleCardsViewScreen)
                {
                    Control tick = _screen.Get(deckView || resultView ? "_showUpgrades" : "_viewUpgrades").As<Control>();
                    Control group = Ancestors(tick).OfType<Control>().First(node => node.Name == "ViewUpgrades");
                    // Restore the result toggle minimum before its parent footer geometry.
                    if (resultView) Remember(tick);
                    // Result confirmation occupies the right footer; keep its native upgrade toggle left.
                    Place(group, deckView || resultView ? new Vector2(resultView ? 48 : 384, viewport.Y - 192) : new Vector2(viewport.X - 504, 336),
                        new Vector2(deckView || resultView ? viewport.X - 432 : 456, 144));
                    group.Scale = Vector2.One;
                    Remember(tick); tick.CustomMinimumSize = new Vector2(0, 144);
                    foreach (Label label in Descendants(tick).OfType<Label>())
                    {
                        if (label is MegaLabel) Saved(label, "AutoSizeEnabled", false);
                        Font(label, "font_size", 48);
                    }
                    if (!deckView && !resultView)
                        Place(_promptRoot, new Vector2(48, 336), new Vector2(width - 480, 144));
                }
            }
            // Container minimums settle after shaping. A smaller minimum does
            // not shrink an earlier explicit size, so reflow this owner on that signal.
            float promptHeight = Math.Max(144, _promptRoot.GetCombinedMinimumSize().Y);
            if (_promptRoot.Size.Y != promptHeight)
                _promptRoot.Size = new Vector2(_promptRoot.Size.X, promptHeight);
            PrepareGridHolders();
            // A live pile may change while open. Clear the copied text when its
            // original card leaves that pile; native SetCards still owns the grid.
            if (_readModel != null &&
                ((_screen is NCardPileScreen viewedPile && !viewedPile.Pile.Cards.Contains(_readModel)) ||
                 (_screen is NCombatPileCardSelectScreen && !((List<CardModel>)GridCards.GetValue(_grid)!).Contains(_readModel))))
            {
                _readModel = null;
                _reading!.Sync(Array.Empty<NCard>());
            }
            _reading?.Place(new Vector2(48, readingTop), new Vector2(width, readingHeight));
            if (_peek?.IsPeeking == true) _reading?.Hide();
            // Short combat-pile choices reclaim an empty reader without changing card size.
            // Keep the original holders when every filtered candidate is already allocated.
            if (_screen is NCombatPileCardSelectScreen && viewport.Y < 2160
                && ((List<CardModel>)GridCards.GetValue(_grid)!).Count == _grid.CurrentlyDisplayedCardHolders.Count())
            {
                readingHeight = _reading!.Root.Visible ? _reading.Root.Size.Y : 0;
                readingTop = viewport.Y - 216 - readingHeight;
                _reading.Root.Position = new Vector2(48, readingTop);
                Vector2 gridSize = new Vector2(width, readingTop - 528);
                if (_grid.Size != gridSize)
                {
                    // A height-only resize needs no new rows here. Preserve pending native
                    // rebuilds from other changes and the current press, highlight and scroll.
                    bool needsReinit = _grid.Get("_needsReinit").AsBool();
                    _grid.Size = gridSize;
                    _grid.Set("_needsReinit", needsReinit);
                    _grid.Call("UpdateScrollLimitBottom");
                }
            }
            // Refit small native rows after the reader or animated grid finishes sizing.
            if (_screen is NCombatPileCardSelectScreen) PrepareGridWidth();
            foreach (Preview preview in _previews) preview.Apply(viewport);
        }

        private void LayoutSorting(float width)
        {
            HBoxContainer sorters = _sorters!;
            Place(_sortingOptions!, new Vector2(0, 24), new Vector2(width, 144));
            Remember(sorters);
            sorters.AddThemeConstantOverride("separation", 8);
            float buttonWidth = (width - 24) / 4;
            foreach (NCardViewSortButton sorter in sorters.GetChildren().OfType<NCardViewSortButton>())
            {
                Remember(sorter);
                sorter.CustomMinimumSize = new Vector2(buttonWidth, 144);
                sorter.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                sorter.SizeFlagsVertical = Control.SizeFlags.Fill;
                MegaLabel label = sorter.Get("_label").As<MegaLabel>();
                Place(label.GetParent<Control>(), new Vector2(8, 0), new Vector2(buttonWidth - 16, 144));
                Saved(label, "AutoSizeEnabled", false);
                Saved(label, "size_flags_horizontal", (int)Control.SizeFlags.ExpandFill);
                Saved(label, "size_flags_vertical", (int)Control.SizeFlags.ExpandFill);
                Saved(label, "autowrap_mode", (int)TextServer.AutowrapMode.WordSmart);
                Font(label, "font_size", 42);
            }
            Place(sorters, Vector2.Zero, new Vector2(width, 144));
        }

        public void PrepareGridWidth()
        {
            // Native columns use content width. Limit only complete one- or two-card
            // combat choices so the original grid centers its own holders.
            int count = _screen is NCombatPileCardSelectScreen
                ? ((List<CardModel>)GridCards.GetValue(_grid)!).Count : 0;
            Vector2 cardSize = _grid.Get("_cardSize").AsVector2();
            float rowWidth = count * cardSize.X + (count - 1) * 40;
            float inset = count is 1 or 2 ? (_grid.Size.X - rowWidth) * .5f : 0;
            // Apply final offsets once; transient resets would retrigger Resized each fit.
            _gridContent.OffsetLeft = inset; _gridContent.OffsetRight = -inset;
            if (count is 1 or 2)
            {
                // Preserve native .8-to-1 hover scaling and its original tween.
                // Leave eight pixels at either side when either card is fully hovered.
                float widthScale = (_grid.Size.X - 16) / (rowWidth + NCard.defaultSize.X - cardSize.X);
                // Native top/bottom padding is 80/320, placing the row 120 above center.
                // Keep that row center and leave 24 pixels above a fully hovered card.
                float heightScale = (_grid.Size.Y - 288) / NCard.defaultSize.Y;
                float scale = Math.Clamp(Math.Min(widthScale, heightScale), 1f, 1.7f);
                Saved(_gridContent, "pivot_offset", new Vector2(_gridContent.Size.X * .5f, 80 + cardSize.Y * .5f));
                Saved(_gridContent, "scale", Vector2.One * scale);
            }
            else
            {
                // A live pile that grows beyond two cards returns to its saved native grid.
                if (_properties.TryGetValue((_gridContent, "scale"), out Variant nativeScale))
                    _gridContent.Scale = nativeScale.AsVector2();
                if (_properties.TryGetValue((_gridContent, "pivot_offset"), out Variant nativePivot))
                    _gridContent.PivotOffset = nativePivot.AsVector2();
            }
            // DisplayCards writes native YOffset=100 on every sort. Correct it
            // before InitGrid so the retained sorting row has its own space.
            if (_screen is NDeckViewScreen) Saved(_grid, "YOffset", 144);
        }

        public void PrepareGridHolders()
        {
            if (!Active || !PortraitViewportPatch.IsPortrait) return;
            foreach (NGridCardHolder holder in Descendants(_gridContent).OfType<NGridCardHolder>()) PrepareHolder(holder);
        }

        private void PrepareHolder(NCardHolder holder)
        {
            Control hitbox = holder.Get("_hitbox").As<Control>();
            if (!_hitboxFilters.ContainsKey(holder)) _hitboxFilters.Add(holder, hitbox.MouseFilter);
            // Native holder _GuiInput does not accept the event; Pass lets the
            // same mouse-emulated touch reach the existing grid drag handler.
            hitbox.MouseFilter = Control.MouseFilterEnum.Pass;
        }

        public void RestoreHolder(NCardHolder holder)
        {
            if (_hitboxFilters.Remove(holder, out Control.MouseFilterEnum filter))
                holder.Get("_hitbox").As<Control>().MouseFilter = filter;
            if (_geometry.Remove(holder, out PortraitControlSnapshot snapshot)) snapshot.Restore();
            // Native transform holders are temporary; release their saved hover baseline on exit.
            if (_properties.Remove((holder, "_originalScale"), out Variant originalScale))
                holder.Set("_originalScale", originalScale);
            if (_pressed == holder) _pressed = null;
        }

        public void BeginPress(NCardHolder holder, InputEvent input)
        {
            if (!Active || !PortraitViewportPatch.IsPortrait || input is not InputEventMouseButton button) return;
            Read(holder);
            if (holder is not NGridCardHolder || button.ButtonIndex != MouseButton.Left) return;
            _pressed = holder; _pressPosition = button.GlobalPosition; _dragged = false;
        }

        public void TrackDrag(InputEvent input)
        {
            if (!Active || _pressed == null || input is not InputEventMouseMotion motion) return;
            // Retain this flag after a finger returns to its starting point.
            if (motion.GlobalPosition.DistanceTo(_pressPosition) >= 24) _dragged = true;
        }

        public void BeforeRelease(NCardHolder holder, InputEvent input)
        {
            if (holder is not NGridCardHolder || input is not InputEventMouseButton button) return;
            bool moved = _pressed == holder && (_dragged || button.GlobalPosition.DistanceTo(_pressPosition) >= 24);
            Rect2 hit = holder.Get("_hitbox").As<Control>().GetGlobalRect();
            if (moved || !_grid.GetGlobalRect().Encloses(hit))
                holder.Set("_currentPressedAction", default(Variant));
            // Do not invoke OnCardClicked or alter the selected-card set.
            _pressed = null;
        }

        public void Read(NCardHolder holder)
        {
            if (!Active || !PortraitViewportPatch.IsPortrait || _reading == null || holder is not NGridCardHolder || holder.CardNode == null) return;
            _readModel = holder.CardModel;
            _reading.Sync(new[] { holder.CardNode });
            // Apply owns reader visibility, including the native Peek state.
            Queue();
        }

        public void RefreshCard(NCard card)
        {
            if (!Active || !PortraitViewportPatch.IsPortrait) return;
            // Grid holders retain the base model while their native CardNode
            // switches to an upgraded display model for the preview checkbox.
            // Enchant, pile and simple grids do not show upgrade clones. A recycled holder updates
            // CardNode.Model before its cached CardModel; retain the last read text.
            if (_readModel != null && card.GetParent() is NGridCardHolder holder && holder.CardModel == _readModel
                && (_screen is not (NDeckEnchantSelectScreen or NCardPileScreen or NCombatPileCardSelectScreen or NSimpleCardSelectScreen) || card.Model == _readModel))
                _reading?.Sync(new[] { card });
            foreach (Preview preview in _previews) preview.Refresh(card);
        }

        public void SyncPeekPileTarget(NCombatCardPile pile)
        {
            if (!Active || !PortraitViewportPatch.IsPortrait || _peekPiles.Length == 0) return;
            int index = Array.IndexOf(_peekPiles, pile);
            // The original exhaust intro still runs and still raises Finished.
            Saved(pile, "_showPosition", new Vector2(48 + index * 180, _viewport.Y - 192));
            Saved(pile, "_hidePosition", new Vector2(48 + index * 180, _viewport.Y + 48));
        }

        public void ButtonDestination(Control button, bool snap)
        {
            if (!Active || !PortraitViewportPatch.IsPortrait) return;
            bool back = button is NBackButton;
            Vector2 shown = new(back ? 48 : _viewport.X - 336, _viewport.Y - 192);
            Vector2 hidden = new(back ? -336 : _viewport.X + 48, shown.Y);
            Remember(button);
            button.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
            button.Size = new Vector2(288, 144);
            // Back animates global_position; Confirm animates local position.
            Transform2D parent = button.GetParent<Control>().GetGlobalTransform();
            button.Set("_showPos", back ? parent * shown : shown);
            button.Set("_hidePos", back ? parent * hidden : hidden);
            foreach (TextureRect image in button.GetChildren().OfType<TextureRect>())
            {
                Saved(image, "expand_mode", (int)TextureRect.ExpandModeEnum.IgnoreSize);
                Place(image, Vector2.Zero, button.Size);
            }
            if (snap) button.Position = button.Get("_isEnabled").AsBool() ? shown : hidden;
        }

        public void Restore(bool playingOnly = false)
        {
            if (!Active) return;
            if (playingOnly)
            {
                // Release only external play-pile cards; keep selector geometry and callbacks intact.
                foreach (var key in _properties.Keys.Where(key => key.Node is NCard && key.Name == "visible").ToArray())
                {
                    if (GodotObject.IsInstanceValid(key.Node)) key.Node.Set(key.Name, _properties[key]);
                    _properties.Remove(key);
                }
                return;
            }
            Active = false;
            foreach (Control button in _buttons) Complete(button.Get("_moveTween").AsGodotObject() as Tween);
            foreach (NCombatCardPile pile in _peekPiles) Complete(pile.Get("_positionTween").AsGodotObject() as Tween);
            foreach (var item in _properties)
                if (item.Key.Name != "expand_mode" && GodotObject.IsInstanceValid(item.Key.Node)) item.Key.Node.Set(item.Key.Name, item.Value);
            foreach (var item in _fonts)
                if (GodotObject.IsInstanceValid(item.Key.Node))
                {
                    if (item.Value.Had) item.Key.Node.AddThemeFontSizeOverride(item.Key.Name, item.Value.Value);
                    else item.Key.Node.RemoveThemeFontSizeOverride(item.Key.Name);
                }
            foreach (var item in _geometry) if (GodotObject.IsInstanceValid(item.Key)) item.Value.Restore();
            // Native button art uses FitWidth. Restore its four offsets under
            // IgnoreSize first, so intermediate height cannot inflate width.
            foreach (var item in _properties)
                if (item.Key.Name == "expand_mode" && GodotObject.IsInstanceValid(item.Key.Node)) item.Key.Node.Set(item.Key.Name, item.Value);
            foreach (NCardHolder holder in _hitboxFilters.Keys.ToArray()) if (GodotObject.IsInstanceValid(holder)) RestoreHolder(holder);
            _backdrop.Hide(); _reading?.Hide();
            if (_sorters != null)
            {
                if (_sortSeparation.Had) _sorters.AddThemeConstantOverride("separation", _sortSeparation.Value);
                else _sorters.RemoveThemeConstantOverride("separation");
            }
            foreach (Preview preview in _previews) preview.Restore();
            foreach (Control button in _buttons) button.Call("OnWindowChange");
            foreach (NCombatCardPile pile in _peekPiles)
            {
                // Restore native anchors before recomputing landscape endpoints.
                pile.Call("SetAnimInOutPositions");
                pile.Position = pile.Get("_showPosition").AsVector2();
            }
            _grid.Set("_needsReinit", true);
            _pressed = null;
        }

        private void Remember(Control node)
        {
            if (!_geometry.ContainsKey(node)) _geometry.Add(node, new PortraitControlSnapshot(node));
        }
        private void Place(Control node, Vector2 position, Vector2 size)
        {
            Remember(node); node.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
            node.CustomMinimumSize = Vector2.Zero; node.Position = position; node.Size = size;
        }
        private void Saved(Control node, string name, Variant value)
        {
            if (!_properties.ContainsKey((node, name))) _properties.Add((node, name), node.Get(name));
            node.Set(name, value);
        }
        private void Font(Control node, string name, int value)
        {
            if (!_fonts.ContainsKey((node, name))) _fonts.Add((node, name), (node.HasThemeFontSizeOverride(name), node.GetThemeFontSize(name)));
            if (!node.HasThemeFontSizeOverride(name) || node.GetThemeFontSize(name) != value)
                node.AddThemeFontSizeOverride(name, value);
        }

        private sealed class Preview
        {
            private readonly LayoutState _owner;
            private readonly Control _root;
            private readonly HBoxContainer? _cards;
            private readonly Control? _single;
            private readonly ScrollContainer? _scroll;
            private readonly Control? _stage;
            private readonly ReadingPanel _reading;
            private readonly (bool Had, int Value) _separation;
            private Vector2 _nativeScale = Vector2.One;

            public Preview(LayoutState owner, Control root, HBoxContainer? cards, Control? single)
            {
                _owner = owner; _root = root; _cards = cards; _single = single;
                if (cards != null || single is NTransformPreview)
                {
                    Control content = cards ?? single!;
                    if (cards != null && cards.GetChildCount() != 0)
                        throw new InvalidOperationException("Preview cards must be empty before wrapping.");
                    if (single is NTransformPreview && Descendants(single).OfType<NPreviewCardHolder>().Any())
                        throw new InvalidOperationException("Transform preview cards must be empty before wrapping.");
                    owner.Remember(content);
                    if (cards != null)
                        _separation = (cards.HasThemeConstantOverride("separation"), cards.GetThemeConstant("separation"));
                    _scroll = new ScrollContainer { Name = "PortraitDeckPreviewScroll", FollowFocus = true, ScrollDeadzone = 24 };
                    int index = content.GetIndex();
                    root.AddChild(_scroll); root.MoveChild(_scroll, index);
                    // This happens once while empty. Native Before/After and Cards
                    // remain the same owners when later holders enter or leave.
                    _stage = new Control { Name = "PortraitDeckPreviewStage", MouseFilter = Control.MouseFilterEnum.Pass,
                        SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
                    _scroll.AddChild(_stage);
                    content.Reparent(_stage, false);
                    if (single is NTransformPreview)
                    {
                        foreach (string name in new[] { "_before", "_after", "_arrows" })
                            owner.Remember(single.Get(name).As<Control>());
                        // Initialize must see the same full viewport coordinate space
                        // as its original parent, including before the first portrait pass.
                        _scroll.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
                        _stage.Size = root.Size;
                    }
                }
                // Reuse measured reader sizing so short previews keep native room space.
                _reading = new ReadingPanel("PortraitDeckPreviewReading", root, owner._screen as NDeckEnchantSelectScreen, fitToContent: true);
                root.MoveChild(_reading.Root, Math.Max(0, root.GetChildCount() - 3));
            }

            public void CaptureNativeScale()
            {
                if (_cards != null) _nativeScale = _cards.Scale;
            }

            public void Apply(Vector2 viewport)
            {
                float width = viewport.X - 96;
                // Preserve the original preview black veil above the shared backstop.
                if (_cards != null && _scroll != null && _stage != null)
                {
                    _scroll.MouseFilter = Control.MouseFilterEnum.Stop;
                    _scroll.ClipContents = true;
                    _scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Auto;
                    _scroll.VerticalScrollMode = ScrollContainer.ScrollMode.Disabled;
                    _owner.Place(_scroll, new Vector2(48, 528), new Vector2(width, 552));
                    _cards.Scale = Vector2.One; _cards.PivotOffset = Vector2.Zero;
                    _cards.AddThemeConstantOverride("separation", 384);
                    NPreviewCardHolder[] holders = _cards.GetChildren().OfType<NPreviewCardHolder>().Where(h => !h.IsQueuedForDeletion()).ToArray();
                    _stage.CustomMinimumSize = new Vector2(Math.Max(width, holders.Length * 384), 528);
                    _cards.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
                    foreach (NPreviewCardHolder holder in holders)
                    {
                        _owner.Remember(holder); _owner.PrepareHolder(holder);
                        holder.CustomMinimumSize = Vector2.Zero;
                        holder.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
                        holder.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
                        // HBoxContainer owns child transforms during sorting;
                        // keep its native unit scale and read text below.
                        holder.Scale = Vector2.One;
                    }
                }
                else if (_single is NTransformPreview && _scroll != null && _stage != null)
                {
                    Control before = _single.Get("_before").As<Control>();
                    Control after = _single.Get("_after").As<Control>();
                    Control arrows = _single.Get("_arrows").As<Control>();
                    NPreviewCardHolder[] originals = before.GetChildren().OfType<NPreviewCardHolder>()
                        .Where(holder => !holder.IsQueuedForDeletion() && holder.CardNode != null).ToArray();
                    NPreviewCardHolder[] replacements = after.GetChildren().OfType<NPreviewCardHolder>()
                        .Where(holder => !holder.IsQueuedForDeletion() && holder.CardNode != null).ToArray();
                    // Zero cards is the native Claws contract. Each nonempty pair
                    // keeps a full original card-sized target, with 1.1 hover clearance.
                    float height = Math.Max(552, originals.Length * 528);
                    _scroll.MouseFilter = Control.MouseFilterEnum.Stop;
                    _scroll.ClipContents = true;
                    _scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
                    _scroll.VerticalScrollMode = ScrollContainer.ScrollMode.Auto;
                    _owner.Place(_scroll, new Vector2(48, 528), new Vector2(width, 552));
                    _stage.CustomMinimumSize = new Vector2(width, height);
                    _owner.Place(_single, Vector2.Zero, new Vector2(width, height));
                    _owner.Place(before, new Vector2(width * .25f, 0), Vector2.Zero);
                    _owner.Place(after, new Vector2(width * .75f, 0), Vector2.Zero);
                    _owner.Place(arrows, new Vector2(width * .5f, 264), Vector2.Zero);
                    arrows.Scale = Vector2.One * .6f;
                    foreach (Control control in new[] { _single, before, after })
                        _owner.Saved(control, "mouse_filter", (int)Control.MouseFilterEnum.Pass);
                    foreach (NPreviewCardHolder[] column in new[] { originals, replacements })
                        for (int index = 0; index < column.Length; index++)
                        {
                            NPreviewCardHolder holder = column[index];
                            _owner.Remember(holder);
                            Complete(holder.Get("_hoverTween").AsGodotObject() as Tween);
                            _owner.Saved(holder, "_originalScale", Vector2.One);
                            holder.SetCardScale(Vector2.One);
                            holder.Position = new Vector2(0, 264 + index * 528);
                            // Unknown random outcomes remain noninteractive as in native Initialize.
                            if (holder.Hitbox.MouseFilter != Control.MouseFilterEnum.Ignore)
                                _owner.PrepareHolder(holder);
                        }
                }
                else if (_single != null)
                {
                    _owner.Place(_single, new Vector2(48, 528), new Vector2(width, 552));
                    Control before = _single.Get("_before").As<Control>();
                    Control after = _single.Get("_after").As<Control>();
                    _owner.Place(before, new Vector2(width * .25f, 276), Vector2.Zero);
                    _owner.Place(after, new Vector2(width * .75f, 276), Vector2.Zero);
                    _owner.Place(_single.Get("_arrows").As<Control>(), new Vector2(width * .5f, 276), Vector2.Zero);
                    foreach (NPreviewCardHolder holder in Descendants(_single).OfType<NPreviewCardHolder>())
                    {
                        if (holder.IsQueuedForDeletion()) continue;
                        // Enchant Before/After are empty holder anchors around the
                        // real holders. Scale only the cards, never both layers.
                        if (_single is NEnchantPreview && holder.CardNode == null) continue;
                        _owner.Remember(holder); holder.Scale = Vector2.One * 1.2f;
                    }
                }
                // Populate the existing reader before its content-based visibility and sizing.
                Sync();
                _reading.Place(new Vector2(48, 1104), new Vector2(width, viewport.Y - 1320));
                // Only the active preview reader is drawn; the grid reader returns on cancel.
                if (_root.Visible) _owner._reading?.Hide();
            }

            public void Refresh(NCard card)
            {
                if (Ancestors(card).Contains(_root)) Sync();
            }
            private void Sync()
            {
                if (_single is NTransformPreview)
                {
                    // Original and candidate are paired in display order. Native
                    // ReassignToCard reuses NCard, so cycling does not reset this reader.
                    NCard[] originals = _single.Get("_before").As<Control>().GetChildren().OfType<NPreviewCardHolder>()
                        .Where(holder => !holder.IsQueuedForDeletion() && holder.CardNode != null).Select(holder => holder.CardNode!).ToArray();
                    NCard[] replacements = _single.Get("_after").As<Control>().GetChildren().OfType<NPreviewCardHolder>()
                        .Where(holder => !holder.IsQueuedForDeletion() && holder.CardNode != null).Select(holder => holder.CardNode!).ToArray();
                    _reading.Sync(originals.Zip(replacements, (before, after) => new[] { before, after }).SelectMany(pair => pair).ToArray());
                }
                else
                    _reading.Sync(Descendants(_cards ?? (Control)_single!).OfType<NPreviewCardHolder>()
                        .Where(holder => !holder.IsQueuedForDeletion() && holder.CardNode != null).Select(holder => holder.CardNode!).ToArray());
            }

            public void Restore()
            {
                _reading.Hide();
                if (_single is NTransformPreview && _scroll != null && _stage != null)
                {
                    // Native Initialize uses global X immediately, before a queued
                    // Container sort. Restore this full space synchronously and retain the wrapper.
                    _stage.CustomMinimumSize = Vector2.Zero;
                    _scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
                    _scroll.VerticalScrollMode = ScrollContainer.ScrollMode.Disabled;
                    _scroll.ClipContents = false; _scroll.MouseFilter = Control.MouseFilterEnum.Ignore;
                    _scroll.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
                    _stage.Position = Vector2.Zero; _stage.Size = _root.Size;
                    _scroll.ScrollHorizontal = 0; _scroll.ScrollVertical = 0;
                    foreach (Control control in new[] { _single, _single.Get("_before").As<Control>(),
                        _single.Get("_after").As<Control>(), _single.Get("_arrows").As<Control>() })
                    {
                        _owner._geometry[control].Restore();
                        if (_owner._properties.TryGetValue((control, "mouse_filter"), out Variant filter))
                            control.Set("mouse_filter", filter);
                    }
                    foreach (NPreviewCardHolder holder in Descendants(_single).OfType<NPreviewCardHolder>())
                    {
                        Complete(holder.Get("_hoverTween").AsGodotObject() as Tween);
                        if (_owner._properties.TryGetValue((holder, "_originalScale"), out Variant scale))
                            holder.Set("_originalScale", scale);
                        if (_owner._geometry.TryGetValue(holder, out PortraitControlSnapshot geometry)) geometry.Restore();
                        if (_owner._hitboxFilters.TryGetValue(holder, out Control.MouseFilterEnum filter))
                            holder.Hitbox.MouseFilter = filter;
                    }
                    return;
                }
                if (_cards == null || _scroll == null || _stage == null) return;
                // Keep the wrapper identity so live preview holders never exit
                // the tree. Disabled scroll modes leave original geometry free.
                _scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
                _scroll.VerticalScrollMode = ScrollContainer.ScrollMode.Disabled;
                _scroll.ClipContents = false; _scroll.MouseFilter = Control.MouseFilterEnum.Ignore;
                _scroll.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
                _stage.CustomMinimumSize = Vector2.Zero;
                _scroll.ScrollHorizontal = 0; _scroll.ScrollVertical = 0;
                _cards.Scale = _nativeScale;
                if (_separation.Had) _cards.AddThemeConstantOverride("separation", _separation.Value);
                else _cards.RemoveThemeConstantOverride("separation");
                Callable.From(() =>
                {
                    if (GodotObject.IsInstanceValid(_cards) && !PortraitViewportPatch.IsPortrait)
                        _cards.PivotOffset = _cards.Size / 2;
                }).CallDeferred();
            }
        }
    }

    // This owner has a native row, not NCardGrid. Reuse the display-only reader
    // while leaving its 350 ms selection gate, optional skip and task result intact.
    private sealed class BundleLayoutState
    {
        private readonly NChooseABundleSelectionScreen _screen;
        private readonly NCommonBanner _banner;
        private readonly Control _row, _previewContainer, _previewCards;
        private readonly NCardBundle[] _bundles;
        private readonly NButton[] _buttons;
        private readonly NPeekButton _peek;
        private readonly ColorRect _backdrop;
        private readonly ReadingPanel[] _bundleReading;
        private readonly ReadingPanel _previewReading;
        private readonly Dictionary<Control, PortraitControlSnapshot> _geometry = new();
        private readonly Dictionary<(Control Node, string Name), Variant> _properties = new();
        private readonly (bool Had, int Value) _bannerFont;
        private bool _active, _queued;
        private Vector2 _viewport;

        public BundleLayoutState(NChooseABundleSelectionScreen screen)
        {
            _screen = screen;
            _banner = screen.Get("_banner").As<NCommonBanner>();
            _row = screen.Get("_bundleRow").As<Control>();
            _previewContainer = screen.Get("_bundlePreviewContainer").As<Control>();
            _previewCards = screen.Get("_bundlePreviewCards").As<Control>();
            _bundles = _row.GetChildren().OfType<NCardBundle>().ToArray();
            _buttons = new NButton[] { screen.Get("_previewCancelButton").As<NBackButton>(), screen.Get("_previewConfirmButton").As<NConfirmButton>() };
            _peek = screen.Get("_peekButton").As<NPeekButton>();
            _bannerFont = (_banner.label.HasThemeFontSizeOverride("font_size"), _banner.label.GetThemeFontSize("font_size"));
            _backdrop = new ColorRect { Name = "PortraitBundleBackdrop", Visible = false,
                Color = new Color(.075f, .10f, .09f), MouseFilter = Control.MouseFilterEnum.Ignore };
            screen.AddChild(_backdrop); screen.MoveChild(_backdrop, 0);
            // Readers borrow every original card's formatted text. The bundle,
            // preview holders and selection signals never leave their native owners.
            _bundleReading = _bundles.Select((_, i) => new ReadingPanel("PortraitBundleReading" + i, screen)).ToArray();
            _previewReading = new ReadingPanel("PortraitBundlePreviewReading", screen);
            _peek.AddTargets(_bundleReading.Select(reader => (Control)reader.Root)
                .Append(_previewReading.Root).Append(_backdrop).ToArray());
            _peek.Toggled += _ => Apply();
            Viewport viewport = screen.GetViewport();
            viewport.SizeChanged += Queue;
            screen.VisibilityChanged += Queue;
            screen.TreeExiting += () => viewport.SizeChanged -= Queue;
        }

        public void Queue()
        {
            if (_queued) return;
            _queued = true;
            Callable.From(() => { _queued = false; Apply(); }).CallDeferred();
        }

        public void Apply()
        {
            if (!GodotObject.IsInstanceValid(_screen) || !_screen.IsInsideTree() || _screen.IsQueuedForDeletion()) return;
            if (!PortraitViewportPatch.IsPortrait)
            {
                Restore();
                _backdrop.Hide(); _previewReading.Hide();
                foreach (ReadingPanel reader in _bundleReading) reader.Hide();
                return;
            }
            _active = true;
            _viewport = _screen.GetViewportRect().Size;
            float width = _viewport.X - 96;
            // Finish original position/color tweens normally before mapping their
            // endpoints. No live bundle/card reparenting or replacement is needed.
            Complete(_screen.Get("_cardTween").AsGodotObject() as Tween);
            Complete(_banner.Get("_tween").AsGodotObject() as Tween);
            foreach (NCardBundle bundle in _bundles) Complete(bundle.Get("_cardTween").AsGodotObject() as Tween);
            Place(_backdrop, Vector2.Zero, _viewport);
            _backdrop.Visible = !_peek.IsPeeking;
            Saved(_banner, "expand_mode", (int)TextureRect.ExpandModeEnum.IgnoreSize);
            Saved(_banner.label, "AutoSizeEnabled", false);
            _banner.label.AddThemeFontSizeOverride("font_size", 54);
            Place(_banner, new Vector2(48, 312), new Vector2(width, 144));
            Place(_banner.label, new Vector2(24, 0), new Vector2(width - 48, 144));
            Saved(_banner, "_showPos", _banner.Position);
            Saved(_banner, "_hidePos", _banner.Position + new Vector2(0, 50));
            Place(_row, new Vector2(_viewport.X / 2, 744), Vector2.Zero);
            float columnWidth = (width - 24 * (_bundles.Length - 1)) / _bundles.Length;
            for (int i = 0; i < _bundles.Length; i++)
            {
                Remember(_bundles[i]);
                // Keep native .8/.85 normal and focus scales on the original hitbox.
                _bundles[i].Position = new Vector2(48 + columnWidth / 2 + i * (columnWidth + 24) - _viewport.X / 2, 0);
                _bundleReading[i].Place(new Vector2(48 + i * (columnWidth + 24), 984), new Vector2(columnWidth, _viewport.Y - 1224));
            }
            NPreviewCardHolder[] holders = _previewCards.GetChildren().OfType<NPreviewCardHolder>()
                .Where(holder => !holder.IsQueuedForDeletion() && holder.CardNode != null && holder.IsAncestorOf(holder.CardNode)).ToArray();
            Place(_previewCards, new Vector2(_viewport.X / 2, 744), Vector2.Zero);
            // Native preview centers stay at 400 px; scale their parent so the
            // outer card's original 1.1 hover expansion remains inside the canvas.
            _previewCards.Scale = Vector2.One * Math.Min(1f, width / (Math.Max(0, holders.Length - 1) * 400 + 330));
            _previewReading.Place(new Vector2(48, 984), new Vector2(width, _viewport.Y - 1224));
            RefreshReading();
            Place(_peek, new Vector2((_viewport.X - 144) / 2, _viewport.Y - 192), new Vector2(144, 144));
            foreach (NButton button in _buttons)
            {
                Complete(button.Get("_moveTween").AsGodotObject() as Tween);
                ButtonDestination(button, true);
            }
        }

        public void RefreshReading()
        {
            if (!_active || !PortraitViewportPatch.IsPortrait) return;
            for (int i = 0; i < _bundles.Length; i++)
            {
                _bundleReading[i].Sync(_bundles[i].CardNodes.Where(card => GodotObject.IsInstanceValid(card) && !card.IsQueuedForDeletion()).ToArray());
                _bundleReading[i].Root.Visible = _row.Visible && !_peek.IsPeeking;
            }
            _previewReading.Sync(_previewCards.GetChildren().OfType<NPreviewCardHolder>()
                .Where(holder => !holder.IsQueuedForDeletion() && holder.CardNode != null && holder.IsAncestorOf(holder.CardNode))
                .Select(holder => holder.CardNode!).ToArray());
            _previewReading.Root.Visible = _previewContainer.Visible && !_peek.IsPeeking;
        }

        public void ButtonDestination(Control button, bool snap)
        {
            if (!_active || !PortraitViewportPatch.IsPortrait || !_buttons.Any(action => action == button)) return;
            bool back = button is NBackButton;
            Vector2 shown = new(back ? 48 : _viewport.X - 336, _viewport.Y - 192);
            Vector2 hidden = new(back ? -336 : _viewport.X + 48, shown.Y);
            Remember(button);
            button.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
            button.Size = new Vector2(288, 144);
            // Preserve original local/global tween conventions and native enabled state.
            Transform2D parent = button.GetParent<Control>().GetGlobalTransform();
            button.Set("_showPos", back ? parent * shown : shown);
            button.Set("_hidePos", back ? parent * hidden : hidden);
            foreach (TextureRect image in button.GetChildren().OfType<TextureRect>())
            {
                Saved(image, "expand_mode", (int)TextureRect.ExpandModeEnum.IgnoreSize);
                Place(image, Vector2.Zero, button.Size);
            }
            if (snap) button.Position = button.Get("_isEnabled").AsBool() ? shown : hidden;
        }

        private void Restore()
        {
            if (!_active) return;
            _active = false;
            Complete(_screen.Get("_cardTween").AsGodotObject() as Tween);
            Complete(_banner.Get("_tween").AsGodotObject() as Tween);
            foreach (NCardBundle bundle in _bundles) Complete(bundle.Get("_cardTween").AsGodotObject() as Tween);
            foreach (NButton button in _buttons) Complete(button.Get("_moveTween").AsGodotObject() as Tween);
            if (_bannerFont.Had) _banner.label.AddThemeFontSizeOverride("font_size", _bannerFont.Value);
            else _banner.label.RemoveThemeFontSizeOverride("font_size");
            foreach (var item in _properties)
                if (item.Key.Name != "expand_mode") item.Key.Node.Set(item.Key.Name, item.Value);
            foreach (var item in _geometry) item.Value.Restore();
            foreach (var item in _properties)
                if (item.Key.Name == "expand_mode") item.Key.Node.Set(item.Key.Name, item.Value);
            _banner.Call("OnWindowChange");
            foreach (NButton button in _buttons) button.Call("OnWindowChange");
        }

        private void Remember(Control node)
        {
            if (!_geometry.ContainsKey(node)) _geometry.Add(node, new PortraitControlSnapshot(node));
        }
        private void Place(Control node, Vector2 position, Vector2 size)
        {
            Remember(node); node.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
            node.CustomMinimumSize = Vector2.Zero; node.Position = position; node.Size = size;
        }
        private void Saved(Control node, string name, Variant value)
        {
            if (!_properties.ContainsKey((node, name))) _properties.Add((node, name), node.Get(name));
            node.Set(name, value);
        }
    }

    private sealed class ChoiceLayoutState
    {
        private readonly NChooseACardSelectionScreen _screen;
        private readonly NCommonBanner _banner;
        private readonly Control _row;
        private readonly NGridCardHolder[] _holders;
        private readonly NChoiceSelectionSkipButton _skip;
        private readonly NPeekButton _peek;
        private readonly NCombatCardPile[] _piles;
        private readonly Control _inspectPrompt;
        private readonly bool _inspectVisible;
        private readonly ColorRect _backdrop;
        private readonly ReadingPanel _reading;
        private readonly Dictionary<Control, PortraitControlSnapshot> _geometry = new();
        private readonly Dictionary<(Control Node, string Name), Variant> _properties = new();
        private readonly Dictionary<(Control Node, string Name), (bool Had, int Value)> _fonts = new();
        private bool _active, _queued;
        private Vector2 _viewport;

        public ChoiceLayoutState(NChooseACardSelectionScreen screen)
        {
            _screen = screen;
            _banner = screen.Get("_banner").As<NCommonBanner>();
            _row = screen.Get("_cardRow").As<Control>();
            _holders = _row.GetChildren().OfType<NGridCardHolder>().ToArray();
            _skip = screen.Get("_skipButton").As<NChoiceSelectionSkipButton>();
            _peek = screen.Get("_peekButton").As<NPeekButton>();
            NCombatPilesContainer piles = screen.Get("_combatPiles").As<NCombatPilesContainer>();
            _piles = new NCombatCardPile[] { piles.DrawPile, piles.DiscardPile, piles.ExhaustPile };
            _inspectPrompt = screen.Get("_inspectPrompt").As<Control>();
            _inspectVisible = _inspectPrompt.Visible;
            _backdrop = new ColorRect { Name = "PortraitChoiceBackdrop", Visible = false,
                Color = new Color(.075f, .10f, .09f), MouseFilter = Control.MouseFilterEnum.Ignore };
            screen.AddChild(_backdrop); screen.MoveChild(_backdrop, 0);
            _reading = new ReadingPanel("PortraitChoiceReading", screen, compact: _holders.Length == 2);
            screen.MoveChild(_reading.Root, _row.GetIndex() + 1);
            // The native button still owns which cards, buttons and piles are enabled.
            _peek.AddTargets(_reading.Root);
            _peek.Toggled += _ => Apply();
            Viewport viewport = screen.GetViewport();
            viewport.SizeChanged += Queue;
            screen.VisibilityChanged += Queue;
            screen.TreeExiting += () => viewport.SizeChanged -= Queue;
        }

        private void Queue()
        {
            if (_queued) return;
            _queued = true;
            Callable.From(() => { _queued = false; Apply(); }).CallDeferred();
        }

        public void Apply()
        {
            if (!GodotObject.IsInstanceValid(_screen) || !_screen.IsInsideTree() || _screen.IsQueuedForDeletion()) return;
            if (!PortraitViewportPatch.IsPortrait)
            {
                Restore();
                // Peek can restore its remembered target after landscape restoration.
                _backdrop.Hide(); _reading.Hide();
                _inspectPrompt.Visible = _inspectVisible && !_peek.IsPeeking;
                return;
            }
            _active = true;
            _viewport = _screen.GetViewportRect().Size;
            float width = _viewport.X - 96;
            // Complete the original intros before recording their final native geometry.
            Complete(_screen.Get("_cardTween").AsGodotObject() as Tween);
            Complete(_banner.Get("_tween").AsGodotObject() as Tween);
            Complete(_skip.Get("_animInTween").AsGodotObject() as Tween);
            Place(_backdrop, _peek.IsPeeking ? new Vector2(0, _viewport.Y - 168) : Vector2.Zero,
                _peek.IsPeeking ? new Vector2(_viewport.X, 168) : _viewport);
            _backdrop.Show();
            Saved(_banner, "expand_mode", (int)TextureRect.ExpandModeEnum.IgnoreSize);
            Place(_banner, new Vector2(48, 312), new Vector2(width, 144));
            Place(_banner.label, new Vector2(24, 0), new Vector2(width - 48, 144));
            Saved(_banner.label, "AutoSizeEnabled", false); Font(_banner.label, 54);
            Saved(_banner, "_showPos", _banner.Position);
            Saved(_banner, "_hidePos", _banner.Position + new Vector2(0, 50));
            // Two choices use the available width; native holders and hitboxes keep
            // their original parent and 340-unit spacing. Other counts retain their layout.
            bool pair = _holders.Length == 2;
            float rowScale = pair ? width / (340 + NCard.defaultSize.X)
                : Math.Min(1f, width / (Math.Max(0, _holders.Length - 1) * 340 + 300));
            float rowY = pair ? 492 + NCard.defaultSize.Y * rowScale * .5f : 680;
            Place(_row, new Vector2(_viewport.X / 2, rowY), Vector2.Zero);
            _row.Scale = Vector2.One * rowScale;
            if (pair)
                foreach (NGridCardHolder holder in _holders)
                {
                    if (!_geometry.ContainsKey(holder)) _geometry.Add(holder, new PortraitControlSnapshot(holder));
                    KeepCardScale(holder);
                }
            float readingY = pair ? rowY + NCard.defaultSize.Y * rowScale * .5f + 36 : 920;
            _reading.Place(new Vector2(48, readingY), new Vector2(width, _viewport.Y - 240 - readingY));
            RefreshReading();
            if (_peek.IsPeeking) _reading.Hide();
            _inspectPrompt.Visible = false;
            Place(_skip, new Vector2(_viewport.X - 336, _viewport.Y - 192), new Vector2(288, 144));
            _skip.PivotOffset = _skip.Size / 2;
            Saved(_skip, "_showPosition", _skip.Position);
            TextureRect image = _skip.Get("_image").As<TextureRect>();
            Saved(image, "expand_mode", (int)TextureRect.ExpandModeEnum.IgnoreSize);
            Place(image, Vector2.Zero, _skip.Size);
            MegaLabel label = _skip.Get("_label").As<MegaLabel>();
            Place(label, Vector2.Zero, _skip.Size); Saved(label, "AutoSizeEnabled", false); Font(label, 48);
            Place(_peek, new Vector2(576, _viewport.Y - 192), new Vector2(144, 144));
            foreach (NCombatCardPile pile in _piles)
            {
                Complete(pile.Get("_positionTween").AsGodotObject() as Tween);
                SyncPileTarget(pile);
                Place(pile, pile.Get("_showPosition").AsVector2(), new Vector2(96, 96));
                pile.PivotOffset = Vector2.Zero;
                pile.Scale = Vector2.One * 1.5f;
            }
            foreach (NHotkeyIcon icon in Descendants(_skip).Concat(_piles.SelectMany(Descendants)).OfType<NHotkeyIcon>())
                Saved(icon, "visible", false);
        }

        public void RefreshReading()
        {
            if (_active && PortraitViewportPatch.IsPortrait)
                _reading.Sync(_holders.Where(holder => holder.CardNode != null).Select(holder => holder.CardNode!).ToArray());
        }

        public void KeepCardScale(NCardHolder holder)
        {
            if (!_active || !PortraitViewportPatch.IsPortrait || _holders.Length != 2 || !_holders.Contains(holder)) return;
            // Keep the larger comparison cards stable without replacing native
            // Pressed/AltPressed, hover tips or Inspect. Do not overlap the other card.
            Complete(holder.Get("_hoverTween").AsGodotObject() as Tween);
            holder.Scale = Vector2.One;
        }

        public void SyncPileTarget(NCombatCardPile pile)
        {
            if (!_active || !PortraitViewportPatch.IsPortrait) return;
            int index = Array.IndexOf(_piles, pile);
            Saved(pile, "_showPosition", new Vector2(48 + index * 180, _viewport.Y - 192));
            Saved(pile, "_hidePosition", new Vector2(48 + index * 180, _viewport.Y + 48));
        }

        private void Restore()
        {
            if (!_active) return;
            _active = false;
            Complete(_banner.Get("_tween").AsGodotObject() as Tween);
            Complete(_skip.Get("_animInTween").AsGodotObject() as Tween);
            foreach (NCombatCardPile pile in _piles) Complete(pile.Get("_positionTween").AsGodotObject() as Tween);
            if (_holders.Length == 2)
                foreach (NGridCardHolder holder in _holders) Complete(holder.Get("_hoverTween").AsGodotObject() as Tween);
            foreach (var item in _properties)
                if (item.Key.Name != "expand_mode") item.Key.Node.Set(item.Key.Name, item.Value);
            foreach (var item in _fonts)
            {
                if (item.Value.Had) item.Key.Node.AddThemeFontSizeOverride(item.Key.Name, item.Value.Value);
                else item.Key.Node.RemoveThemeFontSizeOverride(item.Key.Name);
            }
            foreach (var item in _geometry) item.Value.Restore();
            if (_holders.Length == 2)
                foreach (NGridCardHolder holder in _holders)
                {
                    // Native hover creation adds this owner; release its portrait
                    // tip before replaying the current focus in native geometry.
                    MegaCrit.Sts2.Core.Nodes.HoverTips.NHoverTipSet.Remove(holder);
                    holder.Call("DoCardHoverEffects", holder.Get("_isFocused"));
                }
            foreach (var item in _properties)
                if (item.Key.Name == "expand_mode") item.Key.Node.Set(item.Key.Name, item.Value);
            _banner.Call("OnWindowChange");
            foreach (NCombatCardPile pile in _piles)
            {
                pile.Call("SetAnimInOutPositions");
                pile.Position = pile.Get("_showPosition").AsVector2();
            }
        }

        private void Place(Control node, Vector2 position, Vector2 size)
        {
            if (!_geometry.ContainsKey(node)) _geometry.Add(node, new PortraitControlSnapshot(node));
            node.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
            node.CustomMinimumSize = Vector2.Zero; node.Position = position; node.Size = size;
        }
        private void Saved(Control node, string name, Variant value)
        {
            if (!_properties.ContainsKey((node, name))) _properties.Add((node, name), node.Get(name));
            node.Set(name, value);
        }
        private void Font(Control node, int value)
        {
            const string name = "font_size";
            if (!_fonts.ContainsKey((node, name))) _fonts.Add((node, name), (node.HasThemeFontSizeOverride(name), node.GetThemeFontSize(name)));
            node.AddThemeFontSizeOverride(name, value);
        }
    }

    // Display-only text comes from the native cards after their own formatting
    // and upgrade calculation. No cloned card model or selection signal lives here.
    private sealed class ReadingPanel
    {
        public readonly Panel Root;
        private readonly ScrollContainer _scroll;
        private readonly VBoxContainer _content;
        private readonly List<(NCard Card, VBoxContainer Row)> _rows = new();
        private readonly bool _compact;
        private readonly bool _fitToContent;
        private float _maximumHeight;
        private bool _fitQueued;

        public ReadingPanel(string name, Node parent, NDeckEnchantSelectScreen? enchantmentScreen = null, bool compact = false, bool fitToContent = false)
        {
            _compact = compact;
            _fitToContent = fitToContent;
            Root = new Panel { Name = name, Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
            // Reuse the native hover-tip backdrop with its original tiled nine-patch.
            // Keep content padding separate so reader sizing and input stay unchanged.
            Texture2D backdrop = ResourceLoader.Load<Texture2D>("res://images/ui/hover_tip.png")
                ?? throw new InvalidOperationException($"Native hover-tip background failed to load for {name}.");
            Root.AddThemeStyleboxOverride("panel", new StyleBoxTexture
            {
                Texture = backdrop,
                TextureMarginLeft = 55, TextureMarginTop = 43,
                TextureMarginRight = 91, TextureMarginBottom = 32,
                AxisStretchHorizontal = StyleBoxTexture.AxisStretchMode.Tile,
                AxisStretchVertical = StyleBoxTexture.AxisStretchMode.Tile,
                ContentMarginLeft = 0, ContentMarginTop = 0,
                ContentMarginRight = 0, ContentMarginBottom = 0,
            });
            parent.AddChild(Root);
            _scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
                VerticalScrollMode = ScrollContainer.ScrollMode.Auto, ScrollDeadzone = 24 };
            Root.AddChild(_scroll);
            _content = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Pass };
            _content.AddThemeConstantOverride("separation", compact ? 20 : 32);
            _scroll.AddChild(_content);
            if (compact || fitToContent) _content.MinimumSizeChanged += QueueFit;
            if (enchantmentScreen != null)
            {
                // Copy only the native formatted display. CardCmd still owns the
                // enchantment amount, eligible cards and eventual model mutation.
                var summary = new VBoxContainer { Name = "Enchantment", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                    MouseFilter = Control.MouseFilterEnum.Pass };
                _content.AddChild(summary);
                var heading = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
                heading.AddThemeConstantOverride("separation", 16);
                summary.AddChild(heading);
                TextureRect sourceIcon = enchantmentScreen.Get("_enchantmentIcon").As<TextureRect>();
                heading.AddChild(new TextureRect { Texture = sourceIcon.Texture, CustomMinimumSize = new Vector2(72, 72),
                    ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                    MouseFilter = Control.MouseFilterEnum.Ignore });
                MegaLabel sourceTitle = enchantmentScreen.Get("_enchantmentTitle").As<MegaLabel>();
                var title = new Label { Text = sourceTitle.Text, Theme = sourceTitle.Theme,
                    AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                    MouseFilter = Control.MouseFilterEnum.Pass };
                title.AddThemeFontOverride("font", sourceTitle.GetThemeFont("font"));
                title.AddThemeFontSizeOverride("font_size", 48);
                title.AddThemeColorOverride("font_color", sourceTitle.GetThemeColor("font_color"));
                heading.AddChild(title);
                MegaRichTextLabel source = enchantmentScreen.Get("_enchantmentDescription").As<MegaRichTextLabel>();
                var body = new MegaRichTextLabel { Text = source.Text, Theme = source.Theme,
                    AutoSizeEnabled = false, BbcodeEnabled = true, FitContent = true, ScrollActive = false,
                    AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                    MouseFilter = Control.MouseFilterEnum.Pass, CustomEffects = source.CustomEffects.Duplicate() };
                foreach (string font in TextFonts) body.AddThemeFontOverride(font, source.GetThemeFont(font));
                foreach (string font in TextSizes) body.AddThemeFontSizeOverride(font, 48);
                foreach (string color in new[] { "default_color", "font_shadow_color", "font_outline_color" })
                    body.AddThemeColorOverride(color, source.GetThemeColor(color));
                summary.AddChild(body);
            }
        }

        public void Place(Vector2 position, Vector2 size)
        {
            _maximumHeight = size.Y;
            Root.SetAnchorsPreset(Control.LayoutPreset.TopLeft); Root.Position = position;
            Root.Size = _compact || _fitToContent ? new Vector2(size.X, Math.Min(size.Y, Math.Max(120, _content.GetCombinedMinimumSize().Y + 40))) : size;
            _scroll.Position = new Vector2(24, 20); _scroll.Size = Root.Size - new Vector2(48, 40);
            // A native enchantment summary is content even before a card is read.
            // Empty general readers stay hidden; compact choice behavior is unchanged.
            Root.Visible = !_fitToContent || _content.GetChildCount() > 0;
            if (_compact || _fitToContent) QueueFit();
        }

        private void QueueFit()
        {
            if (_fitQueued) return;
            _fitQueued = true;
            Callable.From(() =>
            {
                _fitQueued = false;
                if (!GodotObject.IsInstanceValid(Root) || !Root.IsInsideTree() || Root.IsQueuedForDeletion() || !Root.Visible) return;
                // FitContent settles after the real container width. Short text
                // uses its measured height; long text retains the same native scroll.
                float height = Math.Min(_maximumHeight, Math.Max(120, _content.GetCombinedMinimumSize().Y + 40));
                if (Mathf.IsEqualApprox(Root.Size.Y, height)) return;
                Root.Size = new Vector2(Root.Size.X, height);
                _scroll.Size = Root.Size - new Vector2(48, 40);
            }).CallDeferred();
        }
        public void Hide() => Root.Hide();

        public void Sync(NCard[] cards)
        {
            if (!_rows.Select(item => item.Card).SequenceEqual(cards))
            {
                foreach (var item in _rows) { _content.RemoveChild(item.Row); item.Row.QueueFree(); }
                _rows.Clear();
                foreach (NCard card in cards)
                {
                    var row = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Pass };
                    _content.AddChild(row); _rows.Add((card, row));
                    var title = new Label { Name = "Title", AutowrapMode = TextServer.AutowrapMode.WordSmart,
                        MouseFilter = Control.MouseFilterEnum.Pass };
                    // Fonts and theme stay fixed for this row; native title
                    // color is refreshed below when upgrade previews change.
                    Label original = card.Get("_titleLabel").As<Label>();
                    title.Theme = original.Theme; title.AddThemeFontOverride("font", original.GetThemeFont("font"));
                    title.AddThemeFontSizeOverride("font_size", _compact ? 42 : 54);
                    row.AddChild(title);
                    var cost = new Label { Name = "Cost", MouseFilter = Control.MouseFilterEnum.Pass };
                    Label energy = card.Get("_energyLabel").As<Label>();
                    cost.Theme = energy.Theme; cost.AddThemeFontOverride("font", energy.GetThemeFont("font")); cost.AddThemeFontSizeOverride("font_size", _compact ? 32 : 48);
                    row.AddChild(cost);
                    MegaRichTextLabel source = card.Get("_descriptionLabel").As<MegaRichTextLabel>();
                    var body = new MegaRichTextLabel { Name = "Body", AutoSizeEnabled = false, BbcodeEnabled = true,
                        FitContent = true, ScrollActive = false, MouseFilter = Control.MouseFilterEnum.Pass,
                        SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Theme = source.Theme,
                        CustomEffects = source.CustomEffects.Duplicate() };
                    foreach (string font in TextFonts) body.AddThemeFontOverride(font, source.GetThemeFont(font));
                    foreach (string font in TextSizes) body.AddThemeFontSizeOverride(font, _compact ? 40 : 48);
                    foreach (string color in new[] { "default_color", "font_shadow_color", "font_outline_color" })
                        body.AddThemeColorOverride(color, source.GetThemeColor(color));
                    row.AddChild(body);
                }
                _scroll.ScrollVertical = 0;
            }
            foreach (var item in _rows)
            {
                Label original = item.Card.Get("_titleLabel").As<Label>();
                Label title = item.Row.GetNode<Label>("Title");
                Color titleColor = original.GetThemeColor("font_color");
                if (!title.HasThemeColorOverride("font_color") || title.GetThemeColor("font_color") != titleColor)
                    title.AddThemeColorOverride("font_color", titleColor);
                if (title.Text != original.Text) title.Text = original.Text;
                Label energy = item.Card.Get("_energyLabel").As<Label>(), star = item.Card.Get("_starLabel").As<Label>();
                Label cost = item.Row.GetNode<Label>("Cost");
                // Native cost icons hide absent-cost sentinel labels, including -1.
                bool hasEnergy = item.Card.Get("_energyIcon").As<Control>().Visible;
                bool hasStar = item.Card.Get("_starIcon").As<Control>().Visible;
                string costText = string.Join("    ", new[] { hasEnergy ? "能量 " + energy.Text : "", hasStar ? "星能 " + star.Text : "" }.Where(text => text.Length > 0));
                if (cost.Text != costText) cost.Text = costText;
                MegaRichTextLabel body = item.Row.GetNode<MegaRichTextLabel>("Body");
                string bodyText = item.Card.Get("_descriptionLabel").As<MegaRichTextLabel>().Text;
                if (body.Text != bodyText) body.Text = bodyText;
            }
        }
    }

    private static readonly string[] TextFonts = { "normal_font", "bold_font", "italics_font", "bold_italics_font", "mono_font" };
    private static readonly string[] TextSizes = { "normal_font_size", "bold_font_size", "italics_font_size", "bold_italics_font_size", "mono_font_size" };
    private static IEnumerable<Node> Ancestors(Node node)
    {
        for (Node? parent = node.GetParent(); parent != null; parent = parent.GetParent()) yield return parent;
    }
    private static IEnumerable<Node> Descendants(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            yield return child;
            foreach (Node descendant in Descendants(child)) yield return descendant;
        }
    }
    private static void Complete(Tween? tween)
    {
        if (GodotObject.IsInstanceValid(tween) && tween!.IsValid() && tween.IsRunning()) tween.FastForwardToCompletion();
    }
}
