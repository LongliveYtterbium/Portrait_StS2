using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using OpCodes = System.Reflection.Emit.OpCodes;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Debug;
using MegaCrit.sts2.Core.Nodes.TopBar;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Potions;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Capstones;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
using MegaCrit.Sts2.addons.mega_text;
using STS2RitsuLib.Patching.Models;
using STS2RitsuLib.TopBar;

namespace STS2Portrait.Patches;

/// <summary>Uses the native top-bar rows and actions at a phone-readable size.</summary>
internal sealed class PortraitTopBarPatch : IPatchMethod
{
    public static string PatchId => "sts2portrait_top_bar";
    public static string Description => "Arrange the native top bar in two portrait rows";
    public static bool IsCritical => true;
    public static ModPatchTarget[] GetTargets() => new[]
    {
        PatchTarget.Method(typeof(NTopBar), "Initialize"),
        PatchTarget.Method(typeof(NTopBar), "AnimHide"),
        PatchTarget.Method(typeof(NTopBar), "AnimShow"),
        PatchTarget.Method(typeof(NRelicInventory), "AnimShow"),
        PatchTarget.Method(typeof(NRelicInventory), "AnimHide"),
        PatchTarget.Method(typeof(NRelicInventory), "ShowImmediately"),
        PatchTarget.Method(typeof(NRelicInventory), "HideImmediately"),
        PatchTarget.Method(typeof(NRelicInventory), "UpdateNavigation"),
        PatchTarget.Method(typeof(NRelicInventory), "GetBottomOfInventory"),
        PatchTarget.Method(typeof(NPotionContainer), "_Ready"),
        PatchTarget.Method(typeof(NPotionContainer), "GrowPotionHolders"),
        PatchTarget.Method(typeof(NPotionHolder), "AddPotion"),
        PatchTarget.Method(typeof(NPotionHolder), "OnPress"),
        PatchTarget.Method(typeof(NPotionHolder), "OnFocus"),
        PatchTarget.Method(typeof(NTopBarModifier), "OnFocus"),
        PatchTarget.Method(typeof(NPotionPopup), "_Ready"),
        PatchTarget.Method(typeof(NHoverTipSet), "Init"),
        PatchTarget.Method(typeof(NCombatUi), "Activate"),
        PatchTarget.Method(typeof(NCombatUi), "Deactivate"),
        PatchTarget.Method(typeof(NMouseCardPlay), "Start"),
        PatchTarget.Method(typeof(NCardPlay), "Cleanup"),
    };

    private const float BarHeight = 312;
    // Audited local v0.111.0 relic effect scopes; preserve native acquisition order within each set.
    // Both-domain relics occur in both sets; context-only and unknown third-party IDs occur in neither.
    private static readonly HashSet<string> BattleRelicIds = new(StringComparer.Ordinal)
    {
        "AKABEKO", "ANCHOR", "ART_OF_WAR", "BAG_OF_MARBLES",
        "BAG_OF_PREPARATION", "BEATING_REMNANT", "BELLOWS", "BELT_BUCKLE",
        "BIG_HAT", "BIG_MUSHROOM", "BIIIG_HUG", "BLACK_BLOOD",
        "BLESSED_ANTLER", "BLOOD_SOAKED_ROSE", "BLOOD_VIAL", "BONE_FLUTE",
        "BONE_TEA", "BOOKMARK", "BOOK_REPAIR_KNIFE", "BOOMING_CONCH",
        "BOUND_PHYLACTERY", "BOWLER_HAT", "BREAD", "BRILLIANT_SCARF",
        "BRIMSTONE", "BRONZE_SCALES", "BURNING_BLOOD", "BURNING_STICKS",
        "BYRDPIP", "CANDELABRA", "CAPTAINS_WHEEL", "CENTENNIAL_PUZZLE",
        "CHANDELIER", "CHARONS_ASHES", "CHEMICAL_X", "CHOICES_PARADOX",
        "CHOSEN_CHEESE", "CLOAK_CLASP", "CRACKED_CORE", "CROSSBOW",
        "DATA_DISK", "DAUGHTER_OF_THE_WIND", "DELICATE_FROND", "DEMON_TONGUE",
        "DIAMOND_DIADEM", "DIVINE_DESTINY", "DIVINE_RIGHT", "DRAGON_FRUIT",
        "ECTOPLASM", "EMBER_TEA", "EMOTION_CHIP", "FAKE_ANCHOR",
        "FAKE_BLOOD_VIAL", "FAKE_HAPPY_FLOWER", "FAKE_ORICHALCUM", "FAKE_SNECKO_EYE",
        "FAKE_STRIKE_DUMMY", "FAKE_VENERABLE_TEA_SET", "FENCING_MANUAL", "FESTIVE_POPPER",
        "FIDDLE", "FORGOTTEN_SOUL", "FUNERARY_MASK", "FUR_COAT",
        "GALACTIC_DUST", "GAMBLING_CHIP", "GAME_PIECE", "GHOST_SEED",
        "GIRYA", "GOLD_PLATED_CABLES", "GORGET", "GREMLIN_HORN",
        "HAND_DRILL", "HAPPY_FLOWER", "HELICAL_DART", "HISTORY_COURSE",
        "HORN_CLEAT", "ICE_CREAM", "INFUSED_CORE", "INTIMIDATING_HELMET",
        "IRON_CLUB", "IVORY_TILE", "JEWELED_MASK", "JOSS_PAPER",
        "KUNAI", "KUSARIGAMA", "LANTERN", "LETTER_OPENER",
        "LIZARD_TAIL", "LOST_WISP", "LUNAR_PASTRY", "MEAT_ON_THE_BONE",
        "MERCURY_HOURGLASS", "METRONOME", "MINIATURE_CANNON", "MINI_REGENT",
        "MR_STRUGGLES", "MUMMIFIED_HAND", "MUSIC_BOX", "MYSTIC_LIGHTER",
        "NINJA_SCROLL", "NUNCHAKU", "ODDLY_SMOOTH_STONE", "ORANGE_DOUGH",
        "ORICHALCUM", "ORNAMENTAL_FAN", "PAELS_BLOOD", "PAELS_EYE",
        "PAELS_FLESH", "PAELS_LEGION", "PAELS_TEARS", "PANTOGRAPH",
        "PAPER_KRANE", "PAPER_PHROG", "PARRYING_SHIELD", "PENDULUM",
        "PEN_NIB", "PERMAFROST", "PETRIFIED_TOAD", "PHILOSOPHERS_STONE",
        "PHYLACTERY_UNBOUND", "POCKETWATCH", "POLLINOUS_CORE", "POWER_CELL",
        "PRISMATIC_GEM", "PUMPKIN_CANDLE", "RADIANT_PEARL", "RAINBOW_RING",
        "RAZOR_TOOTH", "RED_MASK", "RED_SKULL", "REGALITE",
        "REPTILE_TRINKET", "RINGING_TRIANGLE", "RING_OF_THE_DRAKE", "RING_OF_THE_SNAKE",
        "RIPPLE_BASIN", "ROYAL_POISON", "RUINED_HELMET", "RUNIC_CAPACITOR",
        "RUNIC_PYRAMID", "SAI", "SCREAMING_FLAGON", "SEAL_OF_GOLD",
        "SELF_FORMING_CLAY", "SHURIKEN", "SLING_OF_COURAGE", "SNECKO_EYE",
        "SNECKO_SKULL", "SOZU", "SPARKLING_ROUGE", "SPIKED_GAUNTLETS",
        "STONE_CALENDAR", "STONE_CRACKER", "STRIKE_DUMMY", "STURDY_CLAMP",
        "SWORD_OF_JADE", "SYMBIOTIC_VIRUS", "TEA_OF_DISCOURTESY", "THE_ABACUS",
        "THE_BOOT", "THROWING_AXE", "TINGSHA", "TOASTY_MITTENS",
        "TOOLBOX", "TOUGH_BANDAGES", "TUNGSTEN_ROD", "TUNING_FORK",
        "TWISTED_FUNNEL", "UNCEASING_TOP", "UNDYING_SIGIL", "UNSETTLING_LAMP",
        "VAJRA", "VAMBRACE", "VELVET_CHOKER", "VENERABLE_TEA_SET",
        "VERY_HOT_COCOA", "VEXING_PUZZLEBOX", "VITRUVIAN_MINION", "WHISPERING_EARRING",
    };

    private static readonly HashSet<string> NonBattleRelicIds = new(StringComparer.Ordinal)
    {
        "ALCHEMICAL_COFFER", "AMETHYST_AUBERGINE", "ARCANE_SCROLL", "ARCHAIC_TOOTH",
        "ASTROLABE", "BEAUTIFUL_BRACELET", "BING_BONG", "BLACK_STAR",
        "BOOK_OF_FIVE_RINGS", "BOWLER_HAT", "CALLING_BELL", "CAULDRON",
        "CLAWS", "CURSED_PEARL", "DARKSTONE_PERIAPT", "DINGY_RUG",
        "DISTINGUISHED_CAPE", "DOLLYS_MIRROR", "DOWSING_ROD", "DRAGON_FRUIT",
        "DREAM_CATCHER", "DRIFTWOOD", "DUSTY_TOME", "ECTOPLASM",
        "ELECTRIC_SHRYMP", "EMPTY_CAGE", "ETERNAL_FEATHER", "FAKE_LEES_WAFFLE",
        "FAKE_MANGO", "FISHING_ROD", "FRAGRANT_MUSHROOM", "FRESNEL_LENS",
        "FROZEN_EGG", "FUR_COAT", "GIRYA", "GLASS_EYE",
        "GLITTER", "GNARLED_HAMMER", "GOLDEN_COMPASS", "GOLDEN_PEARL",
        "HEFTY_TABLET", "JEWELRY_BOX", "JUZU_BRACELET", "KALEIDOSCOPE",
        "KIFUDA", "LARGE_CAPSULE", "LASTING_CANDY", "LAVA_LAMP",
        "LAVA_ROCK", "LEAD_PAPERWEIGHT", "LEAFY_POULTICE", "LEES_WAFFLE",
        "LIZARD_TAIL", "LOOMING_FRUIT", "LORDS_PARASOL", "LOST_COFFER",
        "LUCKY_FYSH", "MANGO", "MASSIVE_SCROLL", "MAW_BANK",
        "MEAL_TICKET", "MEAT_CLEAVER", "MEMBERSHIP_CARD", "MINIATURE_TENT",
        "MOLTEN_EGG", "NEOWS_BONES", "NEOWS_SACRIFICE", "NEOWS_TALISMAN",
        "NEOWS_TORMENT", "NEW_LEAF", "NUTRITIOUS_OYSTER", "NUTRITIOUS_SOUP",
        "OLD_COIN", "ORRERY", "PAELS_CLAW", "PAELS_GROWTH",
        "PAELS_HORN", "PAELS_TOOTH", "PAELS_WING", "PANDORAS_BOX",
        "PEAR", "PHIAL_HOLSTER", "PLANISPHERE", "POMANDER",
        "POTION_BELT", "PRAYER_WHEEL", "PRECARIOUS_SHEARS", "PRECISE_SCISSORS",
        "PRESERVED_FOG", "PRISMATIC_GEM", "PUMPKIN_CANDLE", "PUNCH_DAGGER",
        "REGAL_PILLOW", "ROYAL_STAMP", "SAND_CASTLE", "SCROLL_BOXES",
        "SEA_GLASS", "SERE_TALON", "SHOVEL", "SIGNET_RING",
        "SILKEN_TRESS", "SILVER_CRUCIBLE", "SMALL_CAPSULE", "SOZU",
        "STONE_HUMIDIFIER", "STORYBOOK", "STRAWBERRY", "SWORD_OF_STONE",
        "TANXS_WHISTLE", "THE_COURIER", "TINY_MAILBOX", "TOUCH_OF_OROBAS",
        "TOXIC_EGG", "TOY_BOX", "TRI_BOOMERANG", "TUNGSTEN_ROD",
        "WAR_HAMMER", "WAR_PAINT", "WHETSTONE", "WHITE_BEAST_STATUE",
        "WHITE_STAR", "WING_CHARM", "WINGED_BOOTS", "WONGOS_MYSTERY_TICKET",
        "YUMMY_COOKIE",
    };
    private static readonly ConditionalWeakTable<NTopBar, LayoutState> States = new();
    private static readonly ConditionalWeakTable<NPotionContainer, ScrollContainer> PotionScrolls = new();
    private static readonly ConditionalWeakTable<NTopBar, ScrollContainer> ModifierScrolls = new();
    private static readonly FieldInfo ActiveHoverTips = AccessTools.Field(typeof(NHoverTipSet), "_activeHoverTips");

    public static bool Prefix(Node __instance, MethodBase __originalMethod, object[] __args)
    {
        if (!Entry.IsDisabled && __instance is NTopBar bar && __originalMethod.Name == "Initialize")
        {
            // Wrap the empty native row before Initialize creates modifier controls.
            // The original HBox and its navigation children keep their identities.
            HBoxContainer modifiers = bar.Get("_modifiersContainer").As<HBoxContainer>();
            if (modifiers.GetChildCount() != 0)
                throw new InvalidOperationException("Modifier scroll must precede native modifier initialization.");
            ScrollContainer scroll = new()
            {
                Name = "PortraitModifierScroll", Visible = modifiers.Visible,
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
                VerticalScrollMode = ScrollContainer.ScrollMode.Disabled, ClipContents = false,
                MouseFilter = Control.MouseFilterEnum.Ignore, FollowFocus = false, ScrollDeadzone = 20,
                SizeFlagsHorizontal = modifiers.SizeFlagsHorizontal,
                SizeFlagsVertical = modifiers.SizeFlagsVertical,
            };
            scroll.AddThemeStyleboxOverride("panel", new StyleBoxEmpty());
            Node parent = modifiers.GetParent();
            int index = modifiers.GetIndex();
            parent.AddChild(scroll);
            parent.MoveChild(scroll, index);
            modifiers.Reparent(scroll, false);
            ModifierScrolls.Add(bar, scroll);
            // Native Initialize controls whether the modifier slot exists at all.
            modifiers.VisibilityChanged += () => scroll.Visible = modifiers.Visible;
            scroll.ScrollStarted += () =>
            {
                foreach (NTopBarModifier modifier in modifiers.GetChildren().OfType<NTopBarModifier>())
                    NHoverTipSet.Remove(modifier);
            };
        }
        if (!Entry.IsDisabled && __instance is NHoverTipSet tips && __originalMethod.Name == "Init" &&
            __args[0] is Control owner && owner.GetParent() is NPotionPopup popup)
            FindState(popup)?.WrapPotionDetails(popup, tips);
        // The inherited press handler still records the press. Only defer the
        // holder's SetInputAsHandled to its scroll parent so touch can bubble.
        return Entry.IsDisabled || __instance is not NPotionHolder holder ||
            __originalMethod.Name != "OnPress" || !PortraitViewportPatch.IsPortrait ||
            FindState(holder)?.Manages(holder) != true;
    }

    public static void Postfix(Node __instance, MethodBase __originalMethod)
    {
        if (Entry.IsDisabled || __instance is NHoverTipSet)
            return;
        string method = __originalMethod.Name;
        if (__instance is NPotionContainer container && method == "_Ready")
        {
            // Wrap before Initialize creates holders: reparenting a filled row
            // would cancel the holders' and popup's native lifetime tokens.
            Control holders = container.Get("_potionHolders").As<Control>();
            ScrollContainer scroll = new()
            {
                Name = "PortraitPotionScroll", HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
                VerticalScrollMode = ScrollContainer.ScrollMode.Disabled, ClipContents = false,
                MouseFilter = Control.MouseFilterEnum.Ignore, FollowFocus = false, ScrollDeadzone = 20,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            };
            scroll.AddThemeStyleboxOverride("panel", new StyleBoxEmpty());
            holders.GetParent().AddChild(scroll);
            holders.Reparent(scroll, false);
            PotionScrolls.Add(container, scroll);
            scroll.ScrollStarted += () => FindState(container)?.CancelPotionPresses();
            return;
        }
        if (__instance is NTopBar bar && method == "Initialize")
        {
            States.GetValue(bar, value => new LayoutState(value)).Queue();
            return;
        }
        LayoutState? state = FindState(__instance);
        if (state == null)
            return;
        if (__instance is NTopBar && method is "AnimHide" or "AnimShow")
            state.SetBarHidden(method == "AnimHide");
        else if (__instance is NTopBarModifier modifier && method == "OnFocus")
            state.ArrangeModifierHoverTip(modifier);
        else if (__instance is NRelicInventory)
        {
            if (method == "UpdateNavigation") state.ArrangeRelicNavigation();
            else if (method != "GetBottomOfInventory") state.Queue();
        }
        else if (__instance is NPotionPopup popup)
        {
            state.ArrangePopup(popup);
            // Let FitContent propagate its first measured height before the
            // following layout pass, without refreshing for unrelated hover tips.
            state.Queue();
        }
        else if (__instance is NCombatUi ui)
            state.AttachCombat(ui);
        else if (__instance is NPotionHolder holder && method == "AddPotion")
            state.RememberNewPotion(holder);
        else if (__instance is NPotionHolder focused && method == "OnFocus")
            state.RemoveHoverTip(focused);
        else if (__instance is NMouseCardPlay play && method == "Start")
        {
            // Cleanup queues deletion; InCardPlay clears only after that deletion.
            play.TreeExited += state.Queue;
            state.Queue();
        }
        else
            state.Queue();
    }

    private static LayoutState? FindState(Node node)
    {
        for (Node? current = node; current != null; current = current.GetParent())
            if (current is NTopBar bar)
                return States.TryGetValue(bar, out LayoutState? state) ? state : null;
        NTopBar? topBar = NRun.Instance?.GlobalUi.TopBar;
        return topBar != null && States.TryGetValue(topBar, out LayoutState? active) ? active : null;
    }

    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        CodeInstruction[] body = instructions.ToArray();
        if (__originalMethod.DeclaringType == typeof(NRelicInventory))
        {
            if (__originalMethod.Name is "AnimShow" or "AnimHide")
            {
                // Both immediate methods use local Y. Use that same space for
                // native tweens inside the portrait scroll, retaining completion.
                CodeInstruction property = body.Single(instruction => instruction.opcode == OpCodes.Ldstr &&
                    instruction.operand is string name && name == "global_position:y");
                property.opcode = OpCodes.Call;
                property.operand = AccessTools.Method(typeof(PortraitTopBarPatch), nameof(RelicTweenProperty));
            }
            else if (__originalMethod.Name == "GetBottomOfInventory")
            {
                // Map sharing and multiplayer read the old parent's coordinates.
                // The portrait bar remains 312 high even with zero relics.
                var result = new List<CodeInstruction>();
                foreach (CodeInstruction instruction in body)
                {
                    if (instruction.opcode == OpCodes.Ret)
                    {
                        var instance = new CodeInstruction(OpCodes.Ldarg_0);
                        instance.labels.AddRange(instruction.labels);
                        instruction.labels.Clear();
                        result.Add(instance);
                        result.Add(new CodeInstruction(OpCodes.Call,
                            AccessTools.Method(typeof(PortraitTopBarPatch), nameof(RelicInventoryBottom))));
                    }
                    result.Add(instruction);
                }
                return result;
            }
            return body;
        }
        if (__originalMethod.DeclaringType != typeof(NTopBar) || __originalMethod.Name != "AnimHide")
            return body;

        // Replace only the destination. The native tween, easing, focus changes,
        // interruption rules and Finished signal remain owned by NTopBar.
        CodeInstruction target = body.Single(instruction => instruction.opcode == OpCodes.Ldc_R4 &&
            instruction.operand is float value && value == -100f);
        target.opcode = OpCodes.Call;
        target.operand = AccessTools.Method(typeof(PortraitTopBarPatch), nameof(HiddenY));
        return body;
    }

    private static float HiddenY() => !Entry.IsDisabled && PortraitViewportPatch.IsPortrait ? -BarHeight : -100f;

    private static string RelicTweenProperty() => !Entry.IsDisabled && PortraitViewportPatch.IsPortrait
        ? "position:y" : "global_position:y";

    private static Vector2 RelicInventoryBottom(Vector2 native, NRelicInventory inventory) =>
        !Entry.IsDisabled && PortraitViewportPatch.IsPortrait && FindState(inventory) is LayoutState state
            ? state.RelicInventoryBottom : native;

    private sealed class LayoutState
    {
        private readonly NTopBar _bar;
        private readonly HBoxContainer _left;
        private readonly HBoxContainer _right;
        private readonly TextureRect _background;
        private readonly Control _save;
        private readonly Control _padding;
        private readonly Control _roomIcons;
        private readonly Label _goldPopup;
        private readonly Panel _surface;
        private readonly Panel[] _buttonSurfaces;
        private readonly NPotionContainer _potions;
        private readonly Control _potionShell;
        private readonly HBoxContainer _holders;
        private readonly ScrollContainer _scroll;
        private readonly HBoxContainer _modifiers;
        private readonly ScrollContainer _modifierScroll;
        private readonly Label[] _debugLabels;
        private readonly NMapScreen _map;
        private readonly NOverlayStack _overlays;
        private readonly NCapstoneContainer _capstones;
        private readonly NRelicInventory _relicInventory;
        private readonly ScrollContainer _relicScroll;
        private readonly Control _relicContent;
        private readonly Control _relicParent;
        private NRelicInventoryHolder[] _relicOrder = Array.Empty<NRelicInventoryHolder>();
        private readonly HashSet<NRelicInventoryHolder> _relicFocusTracked = new();
        private bool _relicPortrait;
        private readonly Button _relicSortButton;
        private int _combatRelicSortMode = 1;
        private int _nonCombatRelicSortMode = 2;
        private bool? _relicSortContext;
        private readonly Node _timerParent;
        private readonly int _timerIndex;
        private readonly PortraitControlSnapshot _timerOriginal;
        private NCombatUi? _combat;
        private bool _barHidden;
        private readonly Dictionary<Control, PortraitControlSnapshot> _original = new();
        private readonly Dictionary<(Control Control, string Name), Variant> _properties = new();
        private readonly Dictionary<(Control Control, string Name), (bool Had, int Value)> _constants = new();
        private readonly Dictionary<Label, (bool Had, int Size, bool? Auto)> _fonts = new();
        private readonly Dictionary<(RichTextLabel Control, string Name), (bool Had, int Size)> _richFonts = new();
        private readonly Dictionary<NPotionPopup, PotionDetails> _details = new();
        private bool _active;
        private bool _queued;

        // One native popup owns one native tip set. These are only its layout
        // nodes; all text, cards and model lifetimes remain in the original set.
        private sealed record PotionDetails(NHoverTipSet Tips, ScrollContainer Scroll, Control Content,
            VFlowContainer Text, NHoverTipCardContainer Cards)
        {
            public bool WatchingMinimums { get; set; }
            public Dictionary<Control, PotionCardReading> CardReading { get; } = new();
        }

        // Each native card-tip shell owns only a presentation mirror. No card
        // model, card node or potion action is duplicated by this reading row.
        private sealed record PotionCardReading(Panel Surface, VBoxContainer Content,
            Label Title, Label Cost, MegaRichTextLabel Body);

        public LayoutState(NTopBar bar)
        {
            _bar = bar;
            _left = bar.GetChildren().OfType<HBoxContainer>().Single(node => node.Name == "LeftAlignedStuff");
            _right = ModTopBarLayout.GetRightAlignedContainer(bar) as HBoxContainer
                ?? throw new InvalidOperationException("Portrait top bar requires the native right HBoxContainer.");
            _background = bar.GetChildren().OfType<TextureRect>().Single(node => node.Name == "BgImage");
            _save = _right.GetChildren().OfType<Control>().Single(node => node.Name == "SaveIndicator");
            _padding = _right.GetChildren().OfType<Control>().Single(node => node.Name == "Padding");
            _roomIcons = bar.FloorIcon.GetParent<Control>();
            _goldPopup = bar.GetChildren().OfType<Label>().Single(node => node.Name == "GoldPopup");
            _surface = AddSurface(bar, "PortraitTopBarSurface", false);
            _buttonSurfaces = new Control[] { bar.Map, bar.Deck, bar.Pause }
                .Select(button => AddSurface(button, "PortraitActionSurface", true)).ToArray();
            _potions = bar.PotionContainer;
            _potionShell = _left.GetChildren().OfType<Control>().Single(node => node.Name == "PotionMarginifier");
            _holders = _potions.Get("_potionHolders").As<HBoxContainer>();
            _scroll = PotionScrolls.GetValue(_potions, _ =>
                throw new InvalidOperationException("Potion scroll must be installed before top-bar initialization."));
            _modifiers = bar.Get("_modifiersContainer").As<HBoxContainer>();
            _modifierScroll = ModifierScrolls.GetValue(bar, _ =>
                throw new InvalidOperationException("Modifier scroll must precede top-bar initialization."));
            // Only the active run owns these debug labels; the menu manager is untouched.
            NDebugInfoLabelManager debug = NRun.Instance!.GetChildren().OfType<NDebugInfoLabelManager>()
                .Single(node => node.Name.ToString().Contains("DebugInfo", StringComparison.OrdinalIgnoreCase));
            _debugLabels = new[] { debug.Get("_releaseInfo").As<Label>(),
                debug.Get("_moddedWarning").As<Label>(), debug.Get("_seed").AsGodotObject() as Label }
                .OfType<Label>().ToArray();
            foreach (Label label in _debugLabels)
                label.VisibilityChanged += HidePortraitDebugInfo;
            _map = NRun.Instance!.GlobalUi.MapScreen;
            _map.VisibilityChanged += Queue;
            // Reward and selection overlays can cover a still-visible combat UI.
            _overlays = NRun.Instance!.GlobalUi.Overlays;
            _overlays.Changed += Queue;
            // Deck view and other capstones are separate from the overlay stack.
            // Their native Changed signal fires after the active screen changes.
            _capstones = NRun.Instance!.GlobalUi.CapstoneContainer;
            _capstones.Changed += Queue;
            ActiveScreenContext.Instance.Updated += Queue;
            _timerParent = bar.Timer.GetParent();
            _timerIndex = bar.Timer.GetIndex();
            _timerOriginal = new PortraitControlSnapshot(bar.Timer);
            _relicInventory = NRun.Instance!.GlobalUi.RelicInventory;
            // NGlobalUi initializes TopBar before filling its native inventory.
            // Wrapping this empty node once does not cancel any holder animation.
            if (_relicInventory.RelicNodes.Count != 0)
                throw new InvalidOperationException("Relic scroll must precede native inventory initialization.");
            _relicParent = _relicInventory.GetParent<Control>();
            _relicScroll = new ScrollContainer
            {
                Name = "PortraitRelicScroll", HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
                VerticalScrollMode = ScrollContainer.ScrollMode.Disabled, ClipContents = false,
                MouseFilter = Control.MouseFilterEnum.Ignore, ScrollDeadzone = 24, FollowFocus = false,
            };
            _relicScroll.AddThemeStyleboxOverride("panel", new StyleBoxEmpty());
            _relicContent = new Control
            {
                Name = "PortraitRelicContent", MouseFilter = Control.MouseFilterEnum.Pass,
            };
            int inventoryIndex = _relicInventory.GetIndex();
            _relicParent.AddChild(_relicScroll);
            _relicParent.MoveChild(_relicScroll, inventoryIndex);
            _relicScroll.AddChild(_relicContent);
            _relicInventory.Reparent(_relicContent, false);
            _relicScroll.ScrollStarted += CancelRelicPresses;
            // One native button cycles only this context's three stable orders.
            _relicSortButton = new Button
            {
                Name = "PortraitRelicSort", Visible = false, Text = "战斗\n优先",
                MouseFilter = Control.MouseFilterEnum.Stop, FocusMode = Control.FocusModeEnum.None,
                ActionMode = BaseButton.ActionModeEnum.Release,
            };
            // Reuse the native font resource so Chinese labels retain its fallback chain.
            Label goldLabel = bar.Gold.GetChildren().OfType<Label>().Single(node => node.Name == "GoldLabel");
            _relicSortButton.AddThemeFontOverride("font", goldLabel.GetThemeFont("font"));
            _relicSortButton.AddThemeFontSizeOverride("font_size", 28);
            // Reuse the native sorting glyph within the existing three-mode touch target.
            _relicSortButton.Icon = ResourceLoader.Load<Texture2D>("res://images/atlases/ui_atlas.sprites/sort_descending.tres")
                ?? throw new InvalidOperationException("Native sorting icon failed to load for portrait relics.");
            _relicSortButton.ExpandIcon = true;
            _relicSortButton.AddThemeConstantOverride("icon_max_width", 32);
            _relicSortButton.AddThemeConstantOverride("h_separation", 4);
            _relicSortButton.AddThemeColorOverride("icon_normal_color", goldLabel.GetThemeColor("font_color"));
            _relicSortButton.AddThemeColorOverride("icon_hover_color", Colors.White);
            _relicSortButton.AddThemeColorOverride("icon_pressed_color", Colors.Gray);
            _relicSortButton.AddThemeColorOverride("icon_hover_pressed_color", Colors.Gray);
            // Match native top-bar text over the scene; the same large sort hit area remains.
            _relicSortButton.AddThemeColorOverride("font_color", goldLabel.GetThemeColor("font_color"));
            _relicSortButton.AddThemeColorOverride("font_hover_color", Colors.White);
            _relicSortButton.AddThemeColorOverride("font_pressed_color", Colors.Gray);
            _relicSortButton.AddThemeColorOverride("font_hover_pressed_color", Colors.Gray);
            _relicSortButton.AddThemeColorOverride("font_outline_color", goldLabel.GetThemeColor("font_outline_color"));
            _relicSortButton.AddThemeConstantOverride("outline_size", 4);
            StyleBoxEmpty sortStyle = new()
            {
                // Leave room for the small native icon without widening the existing button.
                ContentMarginLeft = 0, ContentMarginRight = 0, ContentMarginTop = 8, ContentMarginBottom = 8,
            };
            foreach (string state in new[] { "normal", "hover", "pressed", "hover_pressed", "disabled", "focus" })
                _relicSortButton.AddThemeStyleboxOverride(state, sortStyle);
            _bar.AddChild(_relicSortButton);
            _relicSortButton.Pressed += CycleRelicSort;
            _relicInventory.RelicsChanged += RelicsChanged;
            ArrangeLandscapeRelicShell();
            AttachCombat(NCombatRoom.Instance?.Ui);
            Window window = bar.GetWindow();
            window.SizeChanged += Queue;
            bar.ItemRectChanged += AlignDetachedTopBarInfo;
            bar.TreeExiting += () =>
            {
                window.SizeChanged -= Queue;
                foreach (Label label in _debugLabels)
                    if (GodotObject.IsInstanceValid(label))
                        label.VisibilityChanged -= HidePortraitDebugInfo;
                bar.ItemRectChanged -= AlignDetachedTopBarInfo;
                _map.VisibilityChanged -= Queue;
                _overlays.Changed -= Queue;
                _capstones.Changed -= Queue;
                ActiveScreenContext.Instance.Updated -= Queue;
                _relicInventory.RelicsChanged -= RelicsChanged;
                AttachCombat(null);
            };
        }

        public Vector2 RelicInventoryBottom => new(_relicScroll.Position.X, BarHeight);

        private void RelicsChanged()
        {
            // Native removal updates navigation before our deferred layout runs.
            // Drop detached holders now while preserving the remaining visual order.
            _relicOrder = _relicOrder.Where(holder => _relicInventory.RelicNodes.Contains(holder)).ToArray();
            // Read the current context during layout, including room transitions
            // that do not add or remove a relic.
            Queue();
        }

        private void CycleRelicSort()
        {
            if (!_active || !_relicSortButton.IsVisibleInTree() || _relicSortButton.Disabled) return;
            bool combat = GodotObject.IsInstanceValid(_combat) && _combat!.IsVisibleInTree();
            if (combat) _combatRelicSortMode = (_combatRelicSortMode + 1) % 3;
            else _nonCombatRelicSortMode = (_nonCombatRelicSortMode + 1) % 3;
            CancelRelicPresses();
            _relicScroll.ScrollHorizontal = 0;
            Queue();
        }

        private void CancelRelicPresses()
        {
            if (!_active) return;
            foreach (NRelicInventoryHolder holder in _relicInventory.RelicNodes)
            {
                holder.Set("_isPressed", false);
                NHoverTipSet.Remove(holder);
            }
        }

        public void ArrangeRelicNavigation()
        {
            if (!_active || _relicOrder.Length == 0) return;
            for (int i = 0; i < _relicOrder.Length; i++)
            {
                NRelicInventoryHolder holder = _relicOrder[i];
                Saved(holder, "focus_neighbor_left", _relicOrder[(i + _relicOrder.Length - 1) % _relicOrder.Length].GetPath());
                Saved(holder, "focus_neighbor_right", _relicOrder[(i + 1) % _relicOrder.Length].GetPath());
            }
        }

        private void ArrangeLandscapeRelicShell()
        {
            // A plain content Control preserves the inventory's native offsets;
            // a ScrollContainer directly containing the inventory would zero Y.
            _relicScroll.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            _relicScroll.CustomMinimumSize = Vector2.Zero;
            _relicScroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
            _relicScroll.MouseFilter = Control.MouseFilterEnum.Ignore;
            _relicScroll.ClipContents = false;
            _relicScroll.ScrollHorizontal = 0;
            _relicScroll.ZIndex = 0;
            _relicScroll.Show();
            _relicContent.CustomMinimumSize = _relicParent.Size;
            _relicContent.Size = _relicParent.Size;
            _relicContent.MouseFilter = Control.MouseFilterEnum.Ignore;
        }

        private void AlignDetachedTopBarInfo()
        {
            if (!_active) return;
            // Top-level controls leave HBox sorting but still follow the bar's
            // native show/hide tween through its rectangle-change signal.
            _roomIcons.GlobalPosition = _bar.GlobalPosition + new Vector2(24, 104);
            _save.GlobalPosition = _bar.GlobalPosition + new Vector2(528, 112);
            _modifierScroll.GlobalPosition = _bar.GlobalPosition + new Vector2(600, 12);
        }

        private void ArrangeRelics()
        {
            bool combat = GodotObject.IsInstanceValid(_combat) && _combat!.IsVisibleInTree();
            int mode = combat ? _combatRelicSortMode : _nonCombatRelicSortMode;
            // OrderByDescending is stable: native acquisition order breaks ties.
            // The original RelicNodes list and inspect-screen model order stay native.
            _relicOrder = mode == 0 ? _relicInventory.RelicNodes.ToArray()
                : _relicInventory.RelicNodes.OrderByDescending(holder =>
                    (mode == 1 ? BattleRelicIds : NonBattleRelicIds).Contains(holder.Relic.Model.Id.Entry)).ToArray();
            if (_relicSortContext != combat)
            {
                _relicScroll.ScrollHorizontal = 0;
                _relicSortContext = combat;
            }
            bool changingSpace = !_relicPortrait;
            if (!_relicPortrait)
            {
                FinishTween(_relicInventory, "_curTween");
                FinishTween(_relicInventory, "_debugHideTween");
                foreach (NRelicInventoryHolder holder in _relicInventory.RelicNodes)
                {
                    FinishTween(holder, "_hoverTween");
                    FinishTween(holder, "_obtainedTween");
                }
                bool hidden = _relicInventory.Position.Y < _relicInventory.Get("_originalPos").AsVector2().Y - 1;
                Saved(_relicInventory, "_originalPos", Vector2.Zero);
                Remember(_relicInventory);
                // Stop native full-rect anchors before shrinking the parent;
                // GrowBoth otherwise shifts Y when minimum height is enforced.
                _relicInventory.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
                _relicInventory.Position = new Vector2(0, hidden ? -158 : 0);
                _relicPortrait = true;
            }
            float width = (combat ? 984 : 624) - 108;
            int columns = (_relicOrder.Length + 1) / 2;
            float inventoryWidth = Math.Max(78, columns * 86 - 8);
            // Native inventory precedes TopBar, whose portrait surface is taller.
            _relicScroll.ZIndex = 1;
            _relicScroll.HorizontalScrollMode = ScrollContainer.ScrollMode.ShowNever;
            _relicScroll.ClipContents = true;
            bool dragging = combat && (_combat!.Hand.InCardPlay || _combat.Hand.IsInCardSelection);
            _relicScroll.MouseFilter = dragging ? Control.MouseFilterEnum.Ignore : Control.MouseFilterEnum.Stop;
            NGame game = NGame.Instance!;
            _relicScroll.Visible = !_barHidden && _overlays.ScreenCount == 0 && !_capstones.InUse &&
                game.InspectRelicScreen?.Visible != true && game.InspectCardScreen?.Visible != true;
            float contentWidth = Math.Max(width, inventoryWidth);
            _relicContent.MouseFilter = dragging ? Control.MouseFilterEnum.Ignore : Control.MouseFilterEnum.Pass;
            float nativeY = _relicInventory.Position.Y;
            Saved(_relicInventory, "mouse_filter", (int)(dragging ? Control.MouseFilterEnum.Ignore : Control.MouseFilterEnum.Pass));
            Constant(_relicInventory, "h_separation", 8);
            Constant(_relicInventory, "v_separation", 0);
            // The real native node is a fixed HFlowContainer. Keep its orientation;
            // even/odd visual rows make each column show consecutive sorted relics.
            for (int childIndex = 0; childIndex < _relicOrder.Length; childIndex++)
            {
                int orderIndex = childIndex < columns ? childIndex * 2 : (childIndex - columns) * 2 + 1;
                NRelicInventoryHolder holder = _relicOrder[orderIndex];
                // Move in final child-index order so later moves cannot undo an earlier one.
                if (holder.GetIndex() != childIndex) _relicInventory.MoveChild(holder, childIndex);
                Remember(holder);
                holder.CustomMinimumSize = new Vector2(78, 60);
                holder.Size = new Vector2(78, 60);
                holder.Scale = Vector2.One;
                Saved(holder, "mouse_filter", (int)(dragging ? Control.MouseFilterEnum.Ignore : Control.MouseFilterEnum.Pass));
                Saved(holder, "_ignoreDragThreshold", 24f);
                Place(holder.Relic, new Vector2(6, 6), new Vector2(48, 48));
                // Newly acquired animations own Icon.Position between layout changes.
                if (changingSpace || !_original.ContainsKey(holder.Relic.Icon))
                {
                    Place(holder.Relic.Icon, Vector2.Zero, new Vector2(48, 48));
                    holder.Relic.Icon.PivotOffset = new Vector2(24, 24);
                    Saved(holder, "_originalIconPosition", Vector2.Zero);
                }
                Place(holder.Relic.Outline, Vector2.Zero, new Vector2(48, 48));
                holder.Relic.Outline.PivotOffset = new Vector2(24, 24);
                Saved(holder.Relic, "mouse_filter", (int)Control.MouseFilterEnum.Ignore);
                Saved(holder.Relic.Icon, "mouse_filter", (int)Control.MouseFilterEnum.Ignore);
                Saved(holder.Relic.Outline, "mouse_filter", (int)Control.MouseFilterEnum.Ignore);
                Label amount = holder.Get("_amountLabel").As<Label>();
                PhoneFont(amount, 28);
                // Keep the original live counter inside its own row; its actual
                // font height, not the artwork size, determines the bottom inset.
                float amountHeight = amount.GetCombinedMinimumSize().Y;
                Place(amount, new Vector2(4 - holder.Relic.Position.X,
                    holder.Size.Y - holder.Relic.Position.Y - 4 - amountHeight),
                    new Vector2(holder.Size.X - 8, amountHeight));
                Saved(amount, "mouse_filter", (int)Control.MouseFilterEnum.Ignore);
                Saved(amount, "horizontal_alignment", (int)HorizontalAlignment.Right);
                if (_relicFocusTracked.Add(holder))
                {
                    Action focused = () =>
                    {
                        if (_active) _relicScroll.EnsureControlVisible(holder);
                    };
                    holder.FocusEntered += focused;
                    holder.TreeExiting += () =>
                    {
                        holder.FocusEntered -= focused;
                        _relicFocusTracked.Remove(holder);
                    };
                }
            }
            ArrangeRelicNavigation();
            // Keep flow width separate from viewport width: even two relics must
            // wrap into two rows. Update child minimums before sizing their parents.
            Place(_relicInventory, new Vector2(0, nativeY), new Vector2(inventoryWidth, 120));
            _relicContent.CustomMinimumSize = new Vector2(contentWidth, 120);
            _relicContent.Size = new Vector2(contentWidth, 120);
            Place(_relicScroll, new Vector2(48, 180), new Vector2(width, 120));
            Place(_relicSortButton, new Vector2(48 + width + 12, 180), new Vector2(96, 120));
            _relicSortButton.Text = mode == 1 ? "战斗\n优先" : mode == 2 ? "场外\n优先" : "获得\n顺序";
            _relicSortButton.Visible = _relicScroll.Visible;
            _relicSortButton.Disabled = dragging;
            _relicSortButton.MouseFilter = dragging ? Control.MouseFilterEnum.Ignore : Control.MouseFilterEnum.Stop;
            _relicInventory.QueueSort();
            _relicScroll.QueueSort();
        }

        public bool Manages(NPotionHolder holder) => holder.GetParent() == _holders;

        public void RememberNewPotion(NPotionHolder holder)
        {
            if (_active && Manages(holder) && holder.Potion != null &&
                _properties.TryGetValue((holder, "_potionScale"), out Variant nativeScale))
            {
                // AddPotion used the portrait holder scale. Store the native
                // creation scale before this new node receives its first snapshot.
                Vector2 shownScale = holder.Potion.Scale;
                holder.Potion.Scale = nativeScale.AsVector2();
                Remember(holder.Potion);
                holder.Potion.Scale = shownScale;
            }
            Queue();
        }

        public void ArrangeModifierHoverTip(NTopBarModifier modifier)
        {
            if (!_active || !PortraitViewportPatch.IsPortrait || modifier.GetParent() != _modifiers)
                return;
            var tips = (Dictionary<Control, NHoverTipSet>)ActiveHoverTips.GetValue(null)!;
            if (!tips.TryGetValue(modifier, out NHoverTipSet? tip))
                return;
            // Native modifier tips already live in the global hover container.
            // Move the same tip below the portrait HUD after native OnFocus;
            // its registry owner, artwork and removal callbacks remain native.
            tip.GlobalPosition = new Vector2(tip.GlobalPosition.X, _bar.GlobalPosition.Y + BarHeight + 24);
            tip.SetAlignment(modifier, HoverTipAlignment.None);
        }

        public void RemoveHoverTip(NPotionHolder holder)
        {
            if (!PortraitViewportPatch.IsPortrait || !Manages(holder))
                return;
            var tips = (Dictionary<Control, NHoverTipSet>)ActiveHoverTips.GetValue(null)!;
            if (tips.TryGetValue(holder, out NHoverTipSet? tip))
                tip.Hide();
            NHoverTipSet.Remove(holder);
        }

        public void WrapPotionDetails(NPotionPopup popup, NHoverTipSet tips)
        {
            if (popup.GetParent() is not NPotionHolder holder || !Manages(holder))
                return;
            VFlowContainer text = tips.Get("_textHoverTipContainer").As<VFlowContainer>();
            NHoverTipCardContainer cards = tips.Get("_cardHoverTipContainer").As<NHoverTipCardContainer>();
            ScrollContainer scroll = new()
            {
                Name = "PortraitPotionDetails", MouseFilter = Control.MouseFilterEnum.Ignore,
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
                VerticalScrollMode = ScrollContainer.ScrollMode.Disabled, ClipContents = false,
                FollowFocus = false, ScrollDeadzone = 20,
            };
            scroll.AddThemeStyleboxOverride("panel", new StyleBoxEmpty());
            Control content = new() { Name = "PotionDetailsContent", MouseFilter = Control.MouseFilterEnum.Ignore };
            tips.AddChild(scroll);
            scroll.AddChild(content);
            // Init has not populated either container yet. In particular, no
            // live NCard leaves the tree, cancels its token or loses a tween.
            text.Reparent(content, false);
            cards.Reparent(content, false);
            _details.Add(popup, new PotionDetails(tips, scroll, content, text, cards));
            popup.TreeExiting += () => _details.Remove(popup);
            // Native Remove frees the tips before the popup's exit tween ends.
            // Stop retaining their controls at the actual owning tree boundary.
            tips.TreeExiting += () => _details.Remove(popup);
            text.SortChildren += () =>
            {
                if (_active || PortraitViewportPatch.IsPortrait ||
                    !GodotObject.IsInstanceValid(popup) || popup.IsMarkedForRemoval ||
                    !_original.TryGetValue(text, out PortraitControlSnapshot original))
                    return;
                // VFlow's width minimum is cached by its native sort. Restore
                // its bounds and align only after that cache uses landscape text.
                original.Restore();
                AlignPotionTip(popup);
            };
        }

        public void AttachCombat(NCombatUi? combat)
        {
            if (_combat == combat)
                return;
            if (GodotObject.IsInstanceValid(_combat))
            {
                _combat!.VisibilityChanged -= Queue;
                _combat.Hand.ModeChanged -= Queue;
            }
            _combat = combat;
            if (combat != null)
            {
                combat.VisibilityChanged += Queue;
                combat.Hand.ModeChanged += Queue;
                combat.TreeExiting += () =>
                {
                    if (_combat == combat)
                        AttachCombat(null);
                };
            }
            Queue();
        }

        public void SetBarHidden(bool hidden)
        {
            _barHidden = hidden;
            Queue();
        }

        public void CancelPotionPresses()
        {
            if (!_active)
                return;
            foreach (NPotionHolder holder in _holders.GetChildren().OfType<NPotionHolder>())
                holder.Set("_isPressed", false);
            // The native full-belt feedback temporarily animates HBox.Position.
            // Complete it normally before the scroll container owns that position.
            FinishTween(_potions, "_potionsFullTween");
            _scroll.QueueSort();
        }

        public void Queue()
        {
            if (_queued)
                return;
            _queued = true;
            Callable.From(() =>
            {
                _queued = false;
                if (GodotObject.IsInstanceValid(_bar) && _bar.IsInsideTree())
                    Apply();
            }).CallDeferred();
        }

        private void Apply()
        {
            bool portrait = PortraitViewportPatch.IsPortrait;
            if (_active != portrait)
            {
                // A running tween captured the old coordinate-space destination.
                // Finish it normally before mapping its endpoint to the new layout.
                Tween? tween = _bar.Get("_hideTween").AsGodotObject() as Tween;
                if (tween != null && tween.IsValid() && tween.IsRunning())
                    tween.FastForwardToCompletion();
                bool hidden = _bar.Position.Y < -1;
                _bar.Position = new Vector2(_bar.Position.X, hidden ? HiddenY() : 0);
            }
            if (!portrait)
            {
                if (_active)
                    Restore();
                else
                    // Later landscape resize notifications must also re-align
                    // the same native tips after their own container sort.
                    foreach (PotionDetails details in _details.Values)
                        details.Text.QueueSort();
                ArrangeLandscapeRelicShell();
                return;
            }
            _active = true;
            HidePortraitDebugInfo();
            // Reuse the native worn stone strip at the first row, with its original texture/material.
            // The relic rows show the room beneath them instead of an opaque 312-high custom slab.
            Saved(_background, "visible", true);
            Saved(_background, "mouse_filter", (int)Control.MouseFilterEnum.Ignore);
            Saved(_background, "expand_mode", (int)TextureRect.ExpandModeEnum.IgnoreSize);
            Saved(_background, "stretch_mode", (int)TextureRect.StretchModeEnum.KeepAspectCovered);
            Place(_background, Vector2.Zero, new Vector2(1080, 180));
            _background.PivotOffset = Vector2.Zero;
            _background.Scale = Vector2.One;
            _surface.Hide();
            // Keep the native timer under the bar's hide/show transform. Its
            // plain Control has no exit cancellation; the child Timer keeps ticking.
            if (_bar.Timer.GetParent() != _bar) _bar.Timer.Reparent(_bar, false);
            _bar.Timer.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
            _bar.Timer.Position = new Vector2(344, 112);
            _bar.Timer.Size = new Vector2(320, 80);
            _bar.Timer.Scale = Vector2.One * 0.5f;
            foreach (Label label in _bar.Timer.GetChildren().OfType<Label>())
                PhoneFont(label, 72);

            // Let the original HBoxes sort the stat and action controls. Their
            // buttons keep their parents, hotkeys and focus-neighbor paths.
            Remember(_left);
            Remember(_right);
            Saved(_roomIcons, "top_level", true);
            // Keep the native room, floor and boss controls at phone-readable scale.
            // Their original HBox still owns ordering and their focus handlers keep the tips.
            Place(_roomIcons, new Vector2(24, 104), new Vector2(296, 64));
            _roomIcons.Scale = Vector2.One;
            Constant(_roomIcons, "separation", 12);
            Control roomShell = _bar.RoomIcon.GetParent<Control>();
            Control floorShell = _bar.FloorIcon.GetChildren().OfType<MarginContainer>()
                .Single(node => node.Name == "FloorIconPositioner");
            foreach (Control shell in new[] { roomShell, floorShell, _bar.BossIcon })
            {
                Remember(shell);
                shell.CustomMinimumSize = new Vector2(shell == floorShell ? 48 : 64, 64);
                foreach (string edge in new[] { "left", "right", "top", "bottom" })
                    Constant(shell, "margin_" + edge, 0);
            }
            Remember(_bar.RoomIcon);
            _bar.RoomIcon.CustomMinimumSize = new Vector2(64, 64);
            foreach (Control owner in new Control[] { _bar.RoomIcon, floorShell, _bar.BossIcon })
            {
                TextureRect icon = owner.GetChildren().OfType<TextureRect>().Single();
                Remember(icon);
                icon.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
                icon.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
                Place(icon, new Vector2(8, 8), new Vector2(48, 48));
                // Place clears minimums; native MarginContainers need this after placement.
                icon.CustomMinimumSize = new Vector2(48, 48);
            }
            Label floorNumber = _bar.FloorIcon.GetChildren().OfType<Label>()
                .Single(node => node.Name == "FloorNumLabel");
            PhoneFont(floorNumber, 40);
            floorNumber.CustomMinimumSize = new Vector2(64, 64);
            Constant(_left, "separation", 12);
            Constant(_right, "separation", 12);
            foreach (Control child in _left.GetChildren().OfType<Control>())
            {
                Remember(child);
                child.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            }
            Number(_bar.Hp, "HpLabel", 192, 40);
            Number(_bar.Gold, "GoldLabel", 144, 40);
            Remember(_save);
            Saved(_save, "top_level", true);
            // The native save label extends beyond its old 48-wide shell. Keep
            // its text between the timer and modifiers without changing its fade.
            Place(_save, new Vector2(528, 112), new Vector2(168, 48));
            MegaRichTextLabel saveLabel = _save.GetChildren().OfType<MegaRichTextLabel>()
                .Single(node => node.Name == "Label");
            Saved(saveLabel, "fit_content", false);
            Saved(saveLabel, "clip_contents", true);
            Place(saveLabel, Vector2.Zero, new Vector2(168, 48));
            Remember(_padding);
            Saved(_padding, "visible", false);
            _padding.CustomMinimumSize = Vector2.Zero;
            _padding.Size = new Vector2(0, 80);
            foreach (Control button in new Control[] { _bar.Map, _bar.Deck, _bar.Pause })
            {
                Remember(button);
                button.CustomMinimumSize = new Vector2(100, 120);
                button.SizeFlagsHorizontal = Control.SizeFlags.Fill;
                button.SizeFlagsVertical = Control.SizeFlags.Fill;
                // Map/Pause have native MarginContainer bases. Zero margins let
                // their inner Control fill the same 100-by-120 input rectangle.
                foreach (string edge in new[] { "left", "right", "top", "bottom" })
                    Constant(button, "margin_" + edge, 0);
                Control visual = button.GetChildren().OfType<Control>().Single(node => node.Name == "Control");
                Remember(visual);
                visual.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
                TextureRect icon = visual.GetChildren().OfType<TextureRect>().Single(node => node.Name == "Icon");
                Place(icon, new Vector2(18, 24), new Vector2(64, 64));
                icon.PivotOffset = new Vector2(32, 32);
            }
            // Native map/deck/settings icons already carry hover, press and open-state feedback.
            foreach (Panel panel in _buttonSurfaces)
                panel.Hide();
            Label deckCount = _bar.Deck.GetChildren().OfType<Label>().Single(node => node.Name == "DeckCardCount");
            Place(deckCount, new Vector2(44, 72), new Vector2(48, 40));
            PhoneFont(deckCount, 32);
            ArrangePotions();
            ArrangeRelics();
            ArrangeModifiers();
            // Restored native children must release their old minimums first.
            Place(_left, new Vector2(24, 12), new Vector2(696, 92));
            Place(_right, new Vector2(732, 12), new Vector2(324, 120));
            AlignDetachedTopBarInfo();
            _left.QueueSort();
            _right.QueueSort();
            Callable.From(() =>
            {
                if (_active && GodotObject.IsInstanceValid(_bar) && _bar.IsInsideTree())
                {
                    // Only move the popup horizontally; its native gold animation
                    // owns Y and may already be running during a window resize.
                    Remember(_goldPopup);
                    _goldPopup.Position = new Vector2(_bar.Gold.Position.X + 24 + 54, _goldPopup.Position.Y);
                }
            }).CallDeferred();
        }

        private void HidePortraitDebugInfo()
        {
            if (!_active) return;
            // Native debug input may re-show these labels; preserve their original
            // visibility once and keep pure debug text outside the portrait UI.
            foreach (Label label in _debugLabels)
                if (GodotObject.IsInstanceValid(label))
                    Saved(label, "visible", false);
        }

        private void ArrangeModifiers()
        {
            // The stat row ends at X=589. This 120-wide slot ends at X=720,
            // leaving the native Map button's X=732 input rectangle clear.
            Saved(_modifierScroll, "top_level", true);
            Saved(_modifierScroll, "mouse_filter", (int)Control.MouseFilterEnum.Stop);
            Saved(_modifierScroll, "follow_focus", true);
            _modifierScroll.HorizontalScrollMode = ScrollContainer.ScrollMode.ShowNever;
            _modifierScroll.ClipContents = true;
            Place(_modifierScroll, new Vector2(600, 12), new Vector2(120, 120));
            _modifierScroll.CustomMinimumSize = new Vector2(120, 120);
            Remember(_modifiers);
            _modifiers.CustomMinimumSize = new Vector2(0, 120);
            _modifiers.SizeFlagsHorizontal = Control.SizeFlags.Fill;
            _modifiers.SizeFlagsVertical = Control.SizeFlags.Fill;
            Saved(_modifiers, "mouse_filter", (int)Control.MouseFilterEnum.Pass);
            Constant(_modifiers, "separation", 12);
            foreach (NTopBarModifier modifier in _modifiers.GetChildren().OfType<NTopBarModifier>())
            {
                Remember(modifier);
                modifier.CustomMinimumSize = new Vector2(96, 120);
                modifier.SizeFlagsVertical = Control.SizeFlags.Fill;
                // NClickableControl has no press action here; pass the gesture to
                // the scroll while retaining its native focus and hover-tip signals.
                Saved(modifier, "mouse_filter", (int)Control.MouseFilterEnum.Pass);
                TextureRect icon = modifier.GetChildren().OfType<TextureRect>()
                    .Single(node => node.Name.ToString().Contains("Icon", StringComparison.OrdinalIgnoreCase));
                Place(icon, new Vector2(16, 28), new Vector2(64, 64));
            }
            _modifiers.QueueSort();
            _modifierScroll.QueueSort();
        }

        private void Number(Control owner, string name, float width, int fontSize = 48)
        {
            Label label = owner.GetChildren().OfType<Label>().Single(node => node.Name == name);
            Remember(label);
            PhoneFont(label, fontSize);
            label.CustomMinimumSize = new Vector2(width, 80);
        }

        private void PhoneFont(Label label, int fontSize = 48)
        {
            Remember(label);
            if (!_fonts.ContainsKey(label))
                _fonts.Add(label, (label.HasThemeFontSizeOverride("font_size"), label.GetThemeFontSize("font_size"),
                    label is MegaLabel mega ? mega.AutoSizeEnabled : null));
            // HP/gold changes call SetTextAutoSize again; keep the phone font
            // readable without replacing the game's text or update signals.
            if (label is MegaLabel autoLabel)
                autoLabel.AutoSizeEnabled = false;
            label.AddThemeFontSizeOverride("font_size", fontSize);
        }

        private void ArrangePotions()
        {
            float height = _bar.GetViewportRect().Size.Y;
            bool combat = GodotObject.IsInstanceValid(_combat) && _combat!.IsVisibleInTree();
            // Native Inspect updates this context after opening and only after
            // its close tween hides it. Read cached screens without creating them.
            NGame game = NGame.Instance!;
            bool inspecting = game.InspectRelicScreen?.Visible == true || game.InspectCardScreen?.Visible == true;
            bool visible = !inspecting && !_barHidden && !_map.IsVisibleInTree() && _overlays.ScreenCount == 0 &&
                !_capstones.InUse && !(combat && (_combat!.Hand.InCardPlay || _combat.Hand.IsInCardSelection));
            Saved(_potionShell, "top_level", true);
            Saved(_potionShell, "visible", visible);
            // Outside combat, a 624-wide native relic row and a 24 gap precede
            // the two visible potion slots; additional items use native scrolling.
            // Combat retains its original three-slot, 480-wide lower belt.
            float beltWidth = combat ? 480 : 312;
            // Combat shares one compact row; retain each 144-wide touch slot.
            float beltHeight = combat ? 120 : 144;
            Remember(_potionShell);
            foreach (Control margin in new[] { _potionShell, _potions, (Control)_scroll.GetParent() })
            {
                Remember(margin);
                margin.CustomMinimumSize = Vector2.Zero;
                foreach (string edge in new[] { "left", "right", "top", "bottom" })
                    Constant(margin, "margin_" + edge, 0);
            }
            Saved(_scroll, "mouse_filter", (int)Control.MouseFilterEnum.Stop);
            _scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.ShowNever;
            _scroll.ClipContents = true;
            _scroll.CustomMinimumSize = new Vector2(beltWidth, beltHeight);
            // Lower child minimums before setting the shell size; otherwise its
            // old combat width is clamped into the noncombat offsets.
            // Reduce holder minimums before the enclosing margins read their height.
            foreach (NPotionHolder holder in _holders.GetChildren().OfType<NPotionHolder>())
            {
                bool first = !_original.ContainsKey(holder);
                Remember(holder);
                holder.CustomMinimumSize = new Vector2(144, beltHeight);
                if (first)
                    holder.FocusEntered += () =>
                    {
                        if (_active)
                            _scroll.EnsureControlVisible(holder);
                    };
            }
            // All portrait heights share this row with the 480-wide player strip.
            Place(_potionShell, new Vector2(combat ? 552 : 696,
                combat ? height - 744 : 156), new Vector2(beltWidth, beltHeight));
            Remember(_holders);
            Saved(_holders, "mouse_filter", (int)Control.MouseFilterEnum.Pass);
            _holders.SizeFlagsHorizontal = Control.SizeFlags.Fill;
            _holders.SizeFlagsVertical = Control.SizeFlags.Fill;
            Constant(_holders, "separation", 24);
            foreach (NPotionHolder holder in _holders.GetChildren().OfType<NPotionHolder>())
            {
                FinishTween(holder, "_hoverTween");
                RemoveHoverTip(holder);
                holder.SizeFlagsVertical = Control.SizeFlags.Fill;
                Saved(holder, "mouse_filter", (int)Control.MouseFilterEnum.Pass);
                Saved(holder, "_potionScale", Vector2.One * 1.4f);
                Control empty = holder.Get("_emptyIcon").As<Control>();
                Place(empty, new Vector2(46, (beltHeight - 52) * .5f), new Vector2(52, 52));
                empty.PivotOffset = new Vector2(26, 26);
                empty.Scale = Vector2.One * 1.4f;
                Control reticle = holder.Get("_selectionReticle").As<Control>();
                Place(reticle, Vector2.Zero, new Vector2(144, beltHeight));
                if (holder.Potion != null)
                {
                    Place(holder.Potion, new Vector2(42, (beltHeight - 60) * .5f), new Vector2(60, 60));
                    holder.Potion.PivotOffset = new Vector2(30, 30);
                    holder.Potion.Scale = Vector2.One * 1.4f;
                }
                // The native field retains freed popups; query live children before
                // any Variant conversion, excluding an old popup still fading out.
                NPotionPopup? popup = holder.GetChildren().OfType<NPotionPopup>()
                    .SingleOrDefault(candidate => !candidate.IsMarkedForRemoval);
                if (GodotObject.IsInstanceValid(popup) && !popup!.IsMarkedForRemoval)
                {
                    if (visible)
                        ArrangePopup(popup);
                    else
                        popup.Remove();
                }
            }
            _holders.QueueSort();
            _scroll.QueueSort();
        }

        public void ArrangePopup(NPotionPopup popup)
        {
            if (!PortraitViewportPatch.IsPortrait || popup.IsMarkedForRemoval ||
                popup.GetParent() is not NPotionHolder holder || !Manages(holder))
                return;
            // Keep the complete native popup in its owner subtree, but give it
            // an independent canvas transform and input rectangle outside clipping.
            FinishTween(popup, "_tween");
            Saved(popup, "top_level", true);
            Saved(popup, "z_index", 20);
            // The native shell must not intercept the separate detail scroller;
            // its child buttons and global outside-release handler stay native.
            Saved(popup, "mouse_filter", (int)Control.MouseFilterEnum.Ignore);
            Vector2 viewport = _bar.GetViewportRect().Size;
            float width = viewport.X - 96;
            // Scale the original 259x239 two-action scene uniformly; each hit area is 354x129.
            const float actionScale = 1.5f;
            const float actionWidth = 259 * actionScale;
            const float actionHeight = 239 * actionScale;
            float spaceAbove = holder.GlobalPosition.Y - actionHeight - 24 - (BarHeight + 12);
            float spaceBelow = viewport.Y - 48 - (holder.GlobalPosition.Y + holder.Size.Y + 24 + actionHeight + 24);
            bool above = spaceAbove >= spaceBelow;
            float actionTop = above ? holder.GlobalPosition.Y - actionHeight : holder.GlobalPosition.Y + holder.Size.Y + 24;
            float available = above ? spaceAbove : spaceBelow;
            // Measure within the available side, then shrink the outside-cancel
            // rectangle to the actual description plus its nearby action row.
            Place(popup, new Vector2(48, above ? BarHeight + 12 : actionTop),
                new Vector2(width, available + actionHeight + 24));
            _details.TryGetValue(popup, out PotionDetails? details);
            if (details != null)
                ArrangeDetails(popup, details, available);
            float detailHeight = details == null ? 0 : Math.Min(available, details.Content.GetCombinedMinimumSize().Y);
            float gap = detailHeight > 0 ? 24 : 0;
            float popupTop = above ? actionTop - gap - detailHeight : actionTop;
            Place(popup, new Vector2(48, popupTop), new Vector2(width, detailHeight + gap + actionHeight));
            Control panel = popup.Get("_popupContainer").As<Control>();
            // Keep the native pointed popup close to the selected bottle, inside the viewport.
            float actionX = Math.Clamp(holder.GlobalPosition.X + holder.Size.X * .5f - 48 - actionWidth * .5f,
                0, width - actionWidth);
            Place(panel, new Vector2(actionX, above ? detailHeight + gap : 0), new Vector2(actionWidth, actionHeight));
            Saved(panel, "self_modulate", Colors.White);
            // Above the bottle, mirror only the paper so its point still faces its owner.
            Saved(panel, "flip_v", above);
            Control use = popup.Get("_useButton").As<Control>();
            Control discard = popup.Get("_discardButton").As<Control>();
            Place(use, new Vector2(above ? 12 : 11, above ? 10 : 50) * actionScale,
                new Vector2(236, 86) * actionScale);
            Place(discard, new Vector2(above ? 11 : 12, above ? 103 : 143) * actionScale,
                new Vector2(236, 86) * actionScale);
            Texture2D nativeTop = ResourceLoader.Load<Texture2D>("res://images/potions/potion_pop_up/potion_popup_top.png")
                ?? throw new InvalidOperationException("Native potion top highlight failed to load.");
            Texture2D nativeBottom = ResourceLoader.Load<Texture2D>("res://images/potions/potion_pop_up/potion_popup_bottom.png")
                ?? throw new InvalidOperationException("Native potion bottom highlight failed to load.");
            foreach (Control button in new[] { use, discard })
            {
                // Hide only our flat decoration; original focus/disable tweens own this background.
                Panel? surface = button.GetChildren().OfType<Panel>()
                    .SingleOrDefault(child => child.Name == "PortraitPotionActionSurface");
                if (surface != null) Saved(surface, "visible", false);
                TextureRect background = button.Get("_background").As<TextureRect>();
                Saved(background, "self_modulate", Colors.White);
                Saved(background, "mouse_filter", (int)Control.MouseFilterEnum.Ignore);
                bool topShape = (button == use) != above;
                Saved(background, "texture", topShape ? nativeTop : nativeBottom);
                Saved(background, "flip_v", above);
                Place(background, new Vector2(topShape ? -2 : -3, above ? -4 : topShape ? -39 : -3) * actionScale,
                    new Vector2(241, topShape ? 129 : 93) * actionScale);
                Panel? feedback = background.GetChildren().OfType<Panel>()
                    .SingleOrDefault(child => child.Name == "PortraitPotionActionFeedback");
                if (feedback != null) Saved(feedback, "visible", false);
            }
            foreach (Label label in popup.FindChildren("*", "Label", true, false).OfType<Label>())
                PhoneFont(label);
            Place(popup.Get("_hoverTipBounds").As<Control>(), Vector2.Zero, popup.Size);
            if (details != null)
            {
                Vector2 detailSize = new(width, detailHeight);
                Place(details.Tips, details.Tips.Position, detailSize);
                details.Tips.GlobalPosition = popup.GlobalPosition +
                    (above ? Vector2.Zero : new Vector2(0, actionHeight + gap));
                Place(details.Scroll, Vector2.Zero, detailSize);
                details.Scroll.QueueSort();
            }
        }

        private void ArrangeDetails(NPotionPopup popup, PotionDetails details, float availableHeight)
        {
            Place(details.Tips, details.Tips.Position, new Vector2(popup.Size.X, availableHeight));
            details.Tips.GlobalPosition = popup.GlobalPosition;
            Saved(details.Tips, "z_index", 20);
            Place(details.Scroll, Vector2.Zero, details.Tips.Size);
            Saved(details.Scroll, "mouse_filter", (int)Control.MouseFilterEnum.Stop);
            Saved(details.Scroll, "vertical_scroll_mode", (int)ScrollContainer.ScrollMode.Auto);
            Saved(details.Scroll, "clip_contents", true);
            Saved(details.Content, "mouse_filter", (int)Control.MouseFilterEnum.Pass);
            float width = details.Scroll.Size.X - details.Scroll.GetVScrollBar().GetCombinedMinimumSize().X;
            // Capture the native flow bounds before changing any descendant
            // minimum; their larger portrait fonts can grow the parent itself.
            Remember(details.Text);
            Control[] textTips = details.Text.GetChildren().OfType<Control>().ToArray();
            foreach (Control tip in textTips)
            {
                Remember(tip);
                tip.CustomMinimumSize = new Vector2(width, 0);
                foreach (Label title in tip.FindChildren("*", "Label", true, false).OfType<Label>())
                {
                    PhoneFont(title);
                    Saved(title, "autowrap_mode", (int)TextServer.AutowrapMode.WordSmart);
                    title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                }
                foreach (MegaRichTextLabel body in tip.FindChildren("*", "RichTextLabel", true, false).OfType<MegaRichTextLabel>())
                {
                    Remember(body);
                    Saved(body, "AutoSizeEnabled", false);
                    Saved(body, "fit_content", true);
                    Saved(body, "scroll_active", false);
                    Saved(body, "autowrap_mode", (int)TextServer.AutowrapMode.WordSmart);
                    body.CustomMinimumSize = new Vector2(width - 80, 0);
                    foreach (string name in new[] { "normal_font_size", "bold_font_size", "italics_font_size", "bold_italics_font_size", "mono_font_size" })
                    {
                        if (!_richFonts.ContainsKey((body, name)))
                            _richFonts.Add((body, name), (body.HasThemeFontSizeOverride(name), body.GetThemeFontSize(name)));
                        body.AddThemeFontSizeOverride(name, 48);
                    }
                }
                if (!details.WatchingMinimums)
                    tip.MinimumSizeChanged += Queue;
            }
            details.WatchingMinimums = true;
            float textHeight = textTips.Sum(tip => tip.GetCombinedMinimumSize().Y) +
                Math.Max(0, textTips.Length - 1) * details.Text.GetThemeConstant("v_separation");
            Place(details.Text, Vector2.Zero, new Vector2(width, textHeight));
            details.Text.CustomMinimumSize = new Vector2(width, textHeight);
            Saved(details.Text, "alignment", (int)FlowContainer.AlignmentMode.Begin);
            Saved(details.Text, "reverse_fill", false);
            Remember(details.Cards);
            details.Cards.Scale = Vector2.One;
            float cardsHeight = 0;
            foreach (Control shell in details.Cards.GetChildren().OfType<Control>())
            {
                // The native scene has one 0.75-scale NCard per 239x323 shell.
                // Keep that original card intact and read its computed labels.
                NCard card = shell.GetChildren().OfType<NCard>().Single();
                MegaRichTextLabel source = card.Get("_descriptionLabel").As<MegaRichTextLabel>();
                if (!details.CardReading.TryGetValue(shell, out PotionCardReading? reading))
                {
                    Panel surface = AddSurface(shell, "PortraitPotionCardSurface", true);
                    VBoxContainer content = new()
                    {
                        Name = "PortraitPotionCardText", MouseFilter = Control.MouseFilterEnum.Ignore,
                    };
                    content.AddThemeConstantOverride("separation", 12);
                    Label title = new()
                    {
                        Name = "PortraitPotionCardTitle", MouseFilter = Control.MouseFilterEnum.Ignore,
                        AutowrapMode = TextServer.AutowrapMode.WordSmart,
                        SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                    };
                    Label cost = new()
                    {
                        Name = "PortraitPotionCardCost", MouseFilter = Control.MouseFilterEnum.Ignore,
                        AutowrapMode = TextServer.AutowrapMode.WordSmart,
                        SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                    };
                    MegaRichTextLabel body = new()
                    {
                        Name = "PortraitPotionCardDescription", Theme = source.Theme,
                        AutoSizeEnabled = false, BbcodeEnabled = true, FitContent = true,
                        ScrollActive = false, AutowrapMode = TextServer.AutowrapMode.WordSmart,
                        MouseFilter = Control.MouseFilterEnum.Ignore,
                        SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                        CustomEffects = source.CustomEffects.Duplicate(),
                    };
                    // MegaRichTextLabel requires explicit native fonts before
                    // entering the tree. Preserve all source BBCode and effects.
                    foreach (string font in new[] { "normal_font", "bold_font", "italics_font", "bold_italics_font", "mono_font" })
                        body.AddThemeFontOverride(font, source.GetThemeFont(font));
                    foreach (string size in new[] { "normal_font_size", "bold_font_size", "italics_font_size", "bold_italics_font_size", "mono_font_size" })
                        body.AddThemeFontSizeOverride(size, 48);
                    foreach (string color in new[] { "default_color", "font_shadow_color", "font_outline_color" })
                        body.AddThemeColorOverride(color, source.GetThemeColor(color));
                    surface.AddChild(content);
                    content.AddChild(title);
                    content.AddChild(cost);
                    content.AddChild(body);
                    reading = new PotionCardReading(surface, content, title, cost, body);
                    details.CardReading.Add(shell, reading);
                    content.MinimumSizeChanged += Queue;
                }
                // Fix the reading width before copying text, so the first shape
                // never measures a complete title against a zero-width control.
                float readingWidth = width - 239 - 24 - 48;
                reading.Content.Position = new Vector2(24, 24);
                reading.Content.CustomMinimumSize = new Vector2(readingWidth, 0);
                reading.Content.Size = new Vector2(readingWidth, 0);
                reading.Title.Size = new Vector2(readingWidth, 0);
                reading.Cost.Size = new Vector2(readingWidth, 0);
                reading.Body.CustomMinimumSize = new Vector2(readingWidth, 0);
                Label sourceTitle = card.Get("_titleLabel").As<Label>();
                reading.Title.Theme = sourceTitle.Theme;
                reading.Title.AddThemeFontOverride("font", sourceTitle.GetThemeFont("font"));
                reading.Title.AddThemeFontSizeOverride("font_size", 54);
                reading.Title.AddThemeColorOverride("font_color", sourceTitle.GetThemeColor("font_color"));
                reading.Title.Text = sourceTitle.Text;
                Label energy = card.Get("_energyLabel").As<Label>();
                Label star = card.Get("_starLabel").As<Label>();
                reading.Cost.Theme = energy.Theme;
                reading.Cost.AddThemeFontOverride("font", energy.GetThemeFont("font"));
                reading.Cost.AddThemeFontSizeOverride("font_size", 42);
                reading.Cost.Text = string.Join("    ", new[]
                {
                    energy.IsVisibleInTree() ? "能量 " + energy.Text : "",
                    star.IsVisibleInTree() ? "星能 " + star.Text : "",
                }.Where(text => text.Length > 0));
                reading.Body.Text = source.Text;
                float rowHeight = Math.Max(323, reading.Content.GetCombinedMinimumSize().Y + 48);
                Place(shell, new Vector2(0, cardsHeight), new Vector2(width, rowHeight));
                Place(reading.Surface, new Vector2(263, 0), new Vector2(width - 263, rowHeight));
                reading.Surface.Show();
                reading.Content.QueueSort();
                cardsHeight += rowHeight + 24;
            }
            bool hasCards = cardsHeight > 0;
            if (hasCards)
                cardsHeight -= 24;
            Place(details.Cards, new Vector2(0, textHeight + (hasCards ? 24 : 0)),
                new Vector2(width, cardsHeight));
            Remember(details.Content);
            details.Content.CustomMinimumSize = new Vector2(width,
                textHeight + (hasCards ? 24 + cardsHeight : 0));
            details.Text.QueueSort();
            details.Scroll.QueueSort();
        }

        private static void AlignPotionTip(NPotionPopup popup)
        {
            // Native creation aligns once before this layout runs. Reuse that
            // exact tip instance and its native screen-overflow correction.
            Control bounds = popup.Get("_hoverTipBounds").As<Control>();
            var tips = (Dictionary<Control, NHoverTipSet>)ActiveHoverTips.GetValue(null)!;
            if (tips.TryGetValue(bounds, out NHoverTipSet? tip))
                tip.SetAlignment(bounds, HoverTipAlignment.Right);
        }

        private static void FinishTween(Node node, string field)
        {
            if (node.Get(field).AsGodotObject() is Tween tween && tween.IsValid() && tween.IsRunning())
                tween.FastForwardToCompletion();
        }

        private void Restore()
        {
            FinishTween(_relicInventory, "_curTween");
            FinishTween(_relicInventory, "_debugHideTween");
            foreach (NRelicInventoryHolder holder in _relicInventory.RelicNodes)
            {
                FinishTween(holder, "_hoverTween");
                FinishTween(holder, "_obtainedTween");
            }
            bool relicsHidden = _relicInventory.Position.Y < -1;
            _active = false;
            _relicPortrait = false;
            _relicSortButton.Hide();
            for (int i = 0; i < _relicInventory.RelicNodes.Count; i++)
                _relicInventory.MoveChild(_relicInventory.RelicNodes[i], i);
            // This snapshot is separate from Remember: its normal TreeExiting
            // cleanup must not discard the original timer bounds on reparent.
            _bar.Timer.Reparent(_timerParent, false);
            _timerParent.MoveChild(_bar.Timer, _timerIndex);
            _timerOriginal.Restore();
            foreach (NPotionHolder holder in _holders.GetChildren().OfType<NPotionHolder>())
                FinishTween(holder, "_hoverTween");
            _scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
            _scroll.ClipContents = false;
            _scroll.ScrollHorizontal = 0;
            _scroll.CustomMinimumSize = Vector2.Zero;
            // Disabled axes forward the original HBox minimum to its old parent.
            // Keep this empty-row wrapper for the entire run, avoiding modifier exits.
            _modifierScroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
            _modifierScroll.ClipContents = false;
            _modifierScroll.ScrollHorizontal = 0;
            _modifierScroll.CustomMinimumSize = Vector2.Zero;
            // Dismiss tips before restoring modifier geometry. The next native
            // focus recreates them at the original landscape position.
            foreach (NTopBarModifier modifier in _modifiers.GetChildren().OfType<NTopBarModifier>())
                NHoverTipSet.Remove(modifier);
            foreach (var item in _properties)
                item.Key.Control.Set(item.Key.Name, item.Value);
            foreach (var item in _constants)
                if (item.Value.Had)
                    item.Key.Control.AddThemeConstantOverride(item.Key.Name, item.Value.Value);
                else
                    item.Key.Control.RemoveThemeConstantOverride(item.Key.Name);
            foreach (var item in _fonts)
            {
                if (item.Value.Had)
                    item.Key.AddThemeFontSizeOverride("font_size", item.Value.Size);
                else
                    item.Key.RemoveThemeFontSizeOverride("font_size");
                if (item.Key is MegaLabel mega && item.Value.Auto.HasValue)
                    mega.AutoSizeEnabled = item.Value.Auto.Value;
            }
            foreach (var item in _richFonts)
                if (item.Value.Had)
                    item.Key.Control.AddThemeFontSizeOverride(item.Key.Name, item.Value.Size);
                else
                    item.Key.Control.RemoveThemeFontSizeOverride(item.Key.Name);
            foreach (PortraitControlSnapshot snapshot in _original.Values)
                snapshot.Restore();
            foreach (PotionDetails details in _details.Values)
            {
                // Keep the empty-layout wrapper for this popup's lifetime; its
                // native NCard children must not exit/re-enter on rotation.
                foreach (PotionCardReading reading in details.CardReading.Values)
                    reading.Surface.Hide();
                details.Scroll.ScrollVertical = 0;
                details.Scroll.QueueSort();
                details.Text.QueueSort();
            }
            ArrangeLandscapeRelicShell();
            if (relicsHidden) _relicInventory.HideImmediately();
            else _relicInventory.ShowImmediately();
            _relicInventory.Call("UpdateNavigation");
            _surface.Hide();
            foreach (Panel panel in _buttonSurfaces)
                panel.Hide();
            _left.QueueSort();
            _right.QueueSort();
            _scroll.QueueSort();
            _modifiers.QueueSort();
            _modifierScroll.QueueSort();
        }

        private void Remember(Control control)
        {
            if (!_original.ContainsKey(control))
            {
                _original.Add(control, new PortraitControlSnapshot(control));
                // Potions and popups are transient native nodes. Never retain a
                // freed instance for a later orientation restore.
                control.TreeExiting += () =>
                {
                    _original.Remove(control);
                    foreach (var key in _properties.Keys.Where(key => key.Control == control).ToArray())
                        _properties.Remove(key);
                    foreach (var key in _constants.Keys.Where(key => key.Control == control).ToArray())
                        _constants.Remove(key);
                    if (control is Label label)
                        _fonts.Remove(label);
                    foreach (var key in _richFonts.Keys.Where(key => key.Control == control).ToArray())
                        _richFonts.Remove(key);
                };
            }
        }

        private void Place(Control control, Vector2 position, Vector2 size)
        {
            Remember(control);
            control.CustomMinimumSize = Vector2.Zero;
            control.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
            control.Position = position;
            control.Size = size;
        }

        private void Saved(Control control, string name, Variant value)
        {
            Remember(control);
            if (!_properties.ContainsKey((control, name)))
                _properties.Add((control, name), control.Get(name));
            control.Set(name, value);
        }

        private void Constant(Control control, string name, int value)
        {
            if (!_constants.ContainsKey((control, name)))
                _constants.Add((control, name), (control.HasThemeConstantOverride(name), control.GetThemeConstant(name)));
            control.AddThemeConstantOverride(name, value);
        }

        private static Panel AddSurface(Control parent, string name, bool action)
        {
            // Decorative surfaces never intercept the native control's input.
            Panel panel = new() { Name = name, Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
            StyleBoxFlat style = new()
            {
                BgColor = action ? new Color(0.16f, 0.19f, 0.15f, 0.95f) : new Color(0.075f, 0.095f, 0.075f, 0.96f),
                BorderColor = new Color(0.62f, 0.53f, 0.34f, 0.85f),
                BorderWidthBottom = 2,
                BorderWidthTop = action ? 2 : 0, BorderWidthLeft = action ? 2 : 0, BorderWidthRight = action ? 2 : 0,
                CornerRadiusTopLeft = action ? 18 : 0, CornerRadiusTopRight = action ? 18 : 0,
                CornerRadiusBottomLeft = action ? 18 : 0, CornerRadiusBottomRight = action ? 18 : 0,
            };
            panel.AddThemeStyleboxOverride("panel", style);
            parent.AddChild(panel);
            parent.MoveChild(panel, 0);
            if (action)
                panel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            return panel;
        }
    }
}
