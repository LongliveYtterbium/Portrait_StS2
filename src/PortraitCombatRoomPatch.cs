using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Animation;
using MegaCrit.Sts2.Core.Bindings.MegaSpine;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Models.Encounters;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Orbs;
using MegaCrit.Sts2.Core.Nodes.Screens.Capstones;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using MegaCrit.Sts2.Core.Nodes.Vfx.Backgrounds;
using MegaCrit.Sts2.addons.mega_text;
using STS2RitsuLib.Patching.Models;
using OpCodes = System.Reflection.Emit.OpCodes;

namespace STS2Portrait.Patches;

/// <summary>Places complete native creatures in portrait battle lanes.</summary>
internal sealed class PortraitCombatRoomPatch : IPatchMethod
{
    public static string PatchId => "sts2portrait_combat_room";
    public static string Description => "Arrange creatures above the portrait hand area";
    public static bool IsCritical => true;
    public static ModPatchTarget[] GetTargets() => new[]
    {
        // The forged blade is a sibling of player Visuals, with its own orbit update.
        PatchTarget.Method(typeof(NSovereignBladeVfx), "_Ready"),
        PatchTarget.Method(typeof(NSovereignBladeVfx), "_Process"),
        // Keep native orb queue animation and replace only its portrait destination.
        PatchTarget.Method(typeof(NOrbManager), "TweenLayout"),
        PatchTarget.Method(typeof(NOrbManager), "ClearOrbs"),
        PatchTarget.Method(typeof(NOrb), "_Ready"),
        PatchTarget.Method(typeof(NCreature), "SetOrbManagerPosition"),
        // Wake the verified player skeleton before either native trigger entry.
        PatchTarget.Method(typeof(NCreature), "SetAnimationTrigger", new[] { typeof(string) }),
        PatchTarget.Method(typeof(NCreature), "ImmediatelySetIdle"),
        PatchTarget.Method(typeof(NCombatRoom), "_Ready"),
        PatchTarget.Method(typeof(NCombatRoom), "CreateEnemyNodes"),
        PatchTarget.Method(typeof(NCombatRoom), "RemoveCreatureNode"),
        PatchTarget.Method(typeof(NCombatRoom), "AdjustCreatureScaleForAspectRatio"),
        PatchTarget.Method(typeof(NCombatRoom), "AddCreature"),
        PatchTarget.Method(typeof(NCombatRoom), "SetUpBackground"),
        PatchTarget.Method(typeof(NCombatSceneContainer), "OnWindowChange"),
        // Apply pet visibility after all native drawing nodes exist, including mid-combat summons.
        PatchTarget.Method(typeof(NCreature), "_Ready"),
        PatchTarget.Method(typeof(NCreature), "UpdateBounds", new[] { typeof(Node) }),
        PatchTarget.Method(typeof(NCreature), "ToggleIsInteractable"),
        PatchTarget.Method(typeof(NCreature), "StartReviveAnim"),
        PatchTarget.Method(typeof(NPowerContainer), "UpdatePositions"),
        PatchTarget.Method(typeof(NHealthBar), "SetHpBarContainerSizeWithOffsetsImmediately"),
        PatchTarget.Method(typeof(NHealthBar), "RefreshForeground"),
        // Mirror pet HP at the original HP event boundary, without another polling loop.
        PatchTarget.Method(typeof(NHealthBar), "RefreshValues"),
        PatchTarget.Method(typeof(NHealthBar), "UpdateLayoutForCreatureBounds"),
        PatchTarget.Method(typeof(NIntent), "_Ready"),
        PatchTarget.Method(typeof(NIntent), "UpdateVisuals"),
        PatchTarget.Method(typeof(NCreature), "ShowHoverTips", new[] { typeof(IEnumerable<IHoverTip>) }),
        PatchTarget.Method(typeof(NMouseCardPlay), "Start"),
    };

    private static readonly ConditionalWeakTable<NCombatRoom, LayoutState> States = new();
    private static readonly FieldInfo NativePlayerAnimator = AccessTools.Field(typeof(NCreature), "_spineAnimator");
    private static readonly FieldInfo NativeAnimatorState = AccessTools.Field(typeof(CreatureAnimator), "_currentState");
    private static readonly FieldInfo NativeOrbNodes = AccessTools.Field(typeof(NOrbManager), "_orbs");
    private static readonly FieldInfo ActiveHoverTips = AccessTools.Field(typeof(NHoverTipSet), "_activeHoverTips");
    private static readonly string[] DetailFonts = { "normal_font", "bold_font", "italics_font", "bold_italics_font", "mono_font" };
    private static readonly string[] DetailFontSizes = { "normal_font_size", "bold_font_size", "italics_font_size", "bold_italics_font_size", "mono_font_size" };

    public static bool Prefix(Node __instance, MethodBase __originalMethod, object[] __args)
    {
        if (Entry.IsDisabled)
            return true;
        // Skip only the blade's pure orbit calculation. Native attack, forge and
        // removal tweens keep running with their original callbacks.
        if (__instance is NSovereignBladeVfx && __originalMethod.Name == "_Process")
            return !PortraitViewportPatch.IsPortrait || FindState(__instance)?.Active != true
                || __instance.GetParent() is not NCreature { Entity.IsPlayer: true };
        if (__instance is NCreature animatedPlayer
            && __originalMethod.Name is "SetAnimationTrigger" or "ImmediatelySetIdle")
            FindState(animatedPlayer)?.WakeIdlePlayer(animatedPlayer);
        if (__instance is NMouseCardPlay)
            FindState(__instance)?.CloseDetails();
        // Wrap the original container while it is empty, before encounter slots
        // or creatures can subscribe to their native tree lifecycle.
        if (__instance is NCombatRoom room)
        {
            if (__originalMethod.Name == "CreateEnemyNodes")
                States.GetValue(room, value => new LayoutState(value));
            // Removal only changes the native lists and waits for death. Restoring
            // its departing creature here would visibly jump the death animation.
            if (__originalMethod.Name != "RemoveCreatureNode"
                && States.TryGetValue(room, out LayoutState? state))
                state.RestoreGeometry();
        }
        else if (__instance is NCombatSceneContainer && FindState(__instance) is LayoutState backgroundState)
            backgroundState.RestoreBackground();
        else if (__originalMethod.Name == "SetHpBarContainerSizeWithOffsetsImmediately"
            && __instance is NHealthBar health && FindState(health) is LayoutState healthState
            && healthState.GetHealthWidth(health) is float healthWidth)
            __args[0] = new Vector2(healthWidth, 36);
        else if (__instance is NPowerContainer powers)
            FindState(powers)?.PlaceEnemyPowers(powers);
        return true;
    }

    // Ritsu's graft display also aligns HP and block to the creature hitbox.
    // Keep its calculations, then place the resulting display in our strip.
    [HarmonyAfter("com.ritsukage.sts2-RitsuLib.framework-core")]
    public static void Postfix(Node __instance, MethodBase __originalMethod, object[] __args)
    {
        if (Entry.IsDisabled)
            return;
        if (__instance is NMouseCardPlay
            || (__instance is NCreature && __originalMethod.Name is "SetAnimationTrigger" or "ImmediatelySetIdle"))
            return;
        if (__instance is NSovereignBladeVfx)
        {
            // New blades may be forged after room layout. Do not queue per-frame work.
            if (__originalMethod.Name == "_Ready") FindState(__instance)?.Queue();
            return;
        }
        if (__instance is NCombatRoom room)
        {
            if (__originalMethod.Name == "_Ready")
                States.GetValue(room, value => new LayoutState(value)).Queue();
            else if (States.TryGetValue(room, out LayoutState? state))
            {
                if (__originalMethod.Name == "RemoveCreatureNode")
                    state.ForgetCreature((NCreature)__args[0]);
                state.Queue();
            }
        }
        else if (__instance is NOrbManager manager)
            FindState(manager)?.OrbLayoutChanged(manager);
        else if (__instance is NOrb orb)
            FindState(orb)?.ApplyOrb(orb);
        else if (__instance is NCreature creature)
        {
            LayoutState? state = FindState(creature);
            if (__originalMethod.Name == "SetOrbManagerPosition" && creature.OrbManager is { } nativeOrbs)
                state?.PlaceOrbs(nativeOrbs);
            else if (__originalMethod.Name == "ShowHoverTips")
                state?.HideEnemyTip(creature);
            else if (__originalMethod.Name is "ToggleIsInteractable" or "StartReviveAnim")
            {
                state?.NativeEnemyInputChanged(creature);
                // Native pet revival and interaction refresh can show the HP display again.
                if (creature.Entity.Monster is Osty) state?.ApplyPlayer(creature);
            }
            else
            {
                state?.ApplyPlayer(creature);
                state?.ApplyEnemy(creature);
            }
        }
        else if (__instance is NPowerContainer powers)
        {
            LayoutState? state = FindState(powers);
            state?.ApplyPowerRow(powers);
            state?.PlaceEnemyPowers(powers);
        }
        else if (__instance is NHealthBar health)
        {
            LayoutState? state = FindState(health);
            state?.PlaceHealth(health);
            if (__originalMethod.Name == "RefreshValues"
                && health.GetParent() is NCreatureStateDisplay display
                && display.GetParent() is NCreature { Entity.Monster: Osty } pet)
                state?.ApplyPlayer(pet);
        }
        else if (__instance is NIntent intent && __originalMethod.Name == "UpdateVisuals")
        {
            // Native intents can be reused between numeric and icon-only moves.
            // Reflow only a changed slot shape, not every damage-value refresh.
            if (PortraitViewportPatch.IsPortrait)
            {
                Control holder = intent.GetChildren().OfType<Control>().Single(node => node.Name == "IntentHolder");
                RichTextLabel value = holder.GetChildren().OfType<RichTextLabel>().Single(node => node.Name == "Value");
                LayoutState? state = FindState(intent);
                float width = state?.FourEnemyLayout == true ? 72
                    : state?.FiveEnemyLayout == true || string.IsNullOrEmpty(value.Text) ? 96 : 144;
                if (intent.CustomMinimumSize.X != width)
                    state?.Queue();
            }
        }
        else
        {
            // Intent nodes are created after the room's Ready during combat setup,
            // and can be replaced each turn. Adapt each real creation boundary.
            Node? parent = __instance.GetParent();
            while (parent != null && parent is not NCombatRoom)
                parent = parent.GetParent();
            if (parent is NCombatRoom owner && States.TryGetValue(owner, out LayoutState? state))
                state.Queue();
        }
    }

    private static LayoutState? FindState(Node node)
    {
        for (Node? parent = node; parent != null; parent = parent.GetParent())
            if (parent is NCombatRoom room && States.TryGetValue(room, out LayoutState? state))
                return state;
        return null;
    }

    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        List<CodeInstruction> body = instructions.ToList();
        if (__originalMethod.DeclaringType != typeof(NOrbManager) || __originalMethod.Name != "TweenLayout")
            return body;
        MethodInfo vectorVariant = AccessTools.Method(typeof(Variant), "op_Implicit", new[] { typeof(Vector2) });
        MethodInfo item = AccessTools.PropertyGetter(typeof(List<NOrb>), "Item");
        int[] vectors = body.Select((instruction, index) => (instruction, index))
            .Where(value => value.instruction.Calls(vectorVariant)).Select(value => value.index).ToArray();
        int[] items = body.Select((instruction, index) => (instruction, index))
            .Where(value => value.instruction.Calls(item)).Select(value => value.index).ToArray();
        if (vectors.Length != 1 || items.Length != 1 || vectors[0] != items[0] + 4
            || items[0] == 0 || body[items[0] + 1].opcode != OpCodes.Ldstr
            || !Equals(body[items[0] + 1].operand, "position")
            || (body[items[0] - 1].opcode != OpCodes.Ldloc_S && body[items[0] - 1].opcode != OpCodes.Ldloc))
            throw new InvalidOperationException("Expected one native orb position tween with its list index and Vector2 destination.");
        CodeInstruction destination = body[vectors[0]];
        CodeInstruction owner = new(OpCodes.Ldarg_0);
        owner.labels.AddRange(destination.labels);
        owner.blocks.AddRange(destination.blocks);
        destination.labels.Clear();
        destination.blocks.Clear();
        // Reuse the original loop local rather than assuming its IL slot number.
        CodeInstruction indexLoad = body[items[0] - 1];
        body.InsertRange(vectors[0], new[]
        {
            owner, new CodeInstruction(indexLoad.opcode, indexLoad.operand),
            new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(PortraitCombatRoomPatch), nameof(OrbTarget))),
        });
        return body;
    }

    private static Vector2 OrbTarget(Vector2 original, NOrbManager manager, int index) =>
        !Entry.IsDisabled && FindState(manager) is LayoutState state
            ? state.OrbTarget(original, manager, index) : original;

    private sealed class LayoutState
    {
        private readonly NCombatRoom _room;
        private readonly Control _allies;
        private readonly Control _enemies;
        private readonly ScrollContainer _enemyScroll;
        private readonly Control _enemyContent;
        private readonly HashSet<Control> _passingEnemyControls = new();
        private int _enemyScrollPosition;
        private float _enemyModelHeight = 96;
        private float _enemyCellScale = 1;
        private int _enemyPowerRows = 1;
        private bool _closeEnemyRow;
        // Ordinary four-enemy short rows share this cell shape across native HUD refreshes.
        public bool FourEnemyLayout { get; private set; }
        public bool FiveEnemyLayout { get; private set; }
        private readonly Dictionary<Control, (NCreature Creature, Control.GuiInputEventHandler Handler, Action Exiting)> _detailInputs = new();
        private readonly Dictionary<NHoverTipSet, (NCreature Creature, bool Visible)> _hiddenEnemyTips = new();
        private readonly ColorRect _detailRoot;
        private readonly Panel _detailPanel;
        private readonly ScrollContainer _detailScroll;
        private readonly VBoxContainer _detailContent;
        private readonly Button _detailBack;
        private readonly NOverlayStack _overlays;
        private readonly NCapstoneContainer _capstones;
        private readonly NMapScreen _map;
        private readonly NTargetManager _targets;
        private readonly CombatManager _combat;
        private NCreature? _readingCreature;
        private NOrb? _readingOrb;
        private NOrbManager? _orbManager;
        private int _orbSlotCount;
        private Vector2 _orbLayoutSize;
        private readonly Dictionary<NOrb, (Control.GuiInputEventHandler Handler, Action Exiting)> _orbInputs = new();
        private Control? _detailPress;
        private Vector2 _detailPressPosition;
        private Vector2 _outsidePressPosition;
        private bool _outsidePressed;
        private bool _detailFitQueued;
        private int _detailGeneration;
        private readonly Dictionary<Control, PortraitControlSnapshot> _geometry = new();
        private readonly Dictionary<Control, PortraitControlSnapshot> _annotations = new();
        private readonly Dictionary<Control, (bool Had, int Font, bool? Auto)> _fonts = new();
        private readonly Dictionary<RichTextLabel, HorizontalAlignment> _alignments = new();
        private readonly Dictionary<(Node Node, string Name), Variant> _properties = new();
        private readonly Dictionary<NCreature, PlayerStrip> _playerStrips = new();
        private readonly Dictionary<NCreature, (MegaSprite Sprite, Node2D Body, Callable Completed, Action Exiting, int Generation)> _idlePlayers = new();
        private int _idleGeneration;
        private PortraitControlSnapshot? _backgroundOriginal;
        // Keep one display quad and restore only the native base texture, not its children.
        private TextureRect? _portraitBattleBackground;
        private TextureRect? _backgroundBaseImage;
        private Texture2D? _backgroundBaseTexture;
        private bool _backgroundActive;
        private Vector2 _backgroundCenterOffset;
        private NKaiserCrabBossBackground? _kaiserRig;
        private Transform2D _kaiserFrame;
        private float _kaiserFit;
        private bool _placingPlayer;
        private bool _placingEnemy;
        private bool _compact;
        private bool _active;
        private bool _queued;
        private bool _reflowingLandscape;

        public bool Active => _active;

        public LayoutState(NCombatRoom room)
        {
            _room = room;
            _allies = room.SceneContainer.GetChildren().OfType<Control>().Single(node => node.Name == "AllyContainer");
            _enemies = room.SceneContainer.GetChildren().OfType<Control>().Single(node => node.Name == "EnemyContainer");
            if (_enemies.GetChildCount() != 0)
                throw new InvalidOperationException("Portrait enemy scroll must be prepared before native enemies are created.");
            int index = _enemies.GetIndex();
            Node owner = _enemies.Owner;
            PortraitControlSnapshot original = new(_enemies);
            _enemyScroll = new ScrollContainer
            {
                Name = "PortraitEnemyScroll", FollowFocus = true, ScrollDeadzone = 24,
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
                VerticalScrollMode = ScrollContainer.ScrollMode.Disabled,
                ClipContents = false, MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            _enemyScroll.AddThemeStyleboxOverride("panel", new StyleBoxEmpty());
            _enemyContent = new Control
            {
                Name = "PortraitEnemyContent", MouseFilter = Control.MouseFilterEnum.Ignore,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            };
            room.SceneContainer.AddChild(_enemyScroll);
            room.SceneContainer.MoveChild(_enemyScroll, index);
            _enemyScroll.AddChild(_enemyContent);
            // Never reparent this container again. Native creature ExitTree
            // cancels animation tasks even when the same object is reattached.
            _enemies.Reparent(_enemyContent, false);
            _enemies.Owner = owner;
            RestoreEnemyViewport();
            original.Restore();
            room.Resized += Queue;
            Window window = room.GetWindow();
            window.SizeChanged += Queue;
            room.TreeExiting += () => window.SizeChanged -= Queue;

            // The existing global tooltip canvas already sits above combat UI.
            // This owned display survives native hover removal, but dies with its room.
            _detailRoot = new ColorRect
            {
                Name = "PortraitEnemyDetails", Color = new Color(0, 0, 0, 0.65f),
                MouseFilter = Control.MouseFilterEnum.Stop, Visible = false,
                // Match the potion belt's independent GUI root, so the modal
                // intercepts touches as well as drawing above its native popup.
                TopLevel = true, ZIndex = 30,
            };
            NGame.Instance!.HoverTipsContainer!.AddChild(_detailRoot);
            _detailPanel = new Panel { Name = "Reading", MouseFilter = Control.MouseFilterEnum.Stop };
            // Reuse the original tiled hover-tip paper; reader padding and fit remain separate.
            StyleBoxTexture detailStyle = new()
            {
                Texture = ResourceLoader.Load<Texture2D>("res://images/ui/hover_tip.png")
                    ?? throw new InvalidOperationException("Native enemy-details paper failed to load."),
                TextureMarginLeft = 55, TextureMarginTop = 43,
                TextureMarginRight = 91, TextureMarginBottom = 32,
                AxisStretchHorizontal = StyleBoxTexture.AxisStretchMode.Tile,
                AxisStretchVertical = StyleBoxTexture.AxisStretchMode.Tile,
                ContentMarginLeft = 0, ContentMarginTop = 0,
                ContentMarginRight = 0, ContentMarginBottom = 0,
            };
            _detailPanel.AddThemeStyleboxOverride("panel", detailStyle);
            _detailRoot.AddChild(_detailPanel);
            _detailScroll = new ScrollContainer
            {
                Name = "DescriptionScroll", Position = new Vector2(24, 24), ScrollDeadzone = 24,
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
                VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
                MouseFilter = Control.MouseFilterEnum.Stop, ClipContents = true,
            };
            _detailScroll.AddThemeStyleboxOverride("panel", new StyleBoxEmpty());
            _detailPanel.AddChild(_detailScroll);
            _detailContent = new VBoxContainer
            {
                Name = "OriginalDescriptions", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                MouseFilter = Control.MouseFilterEnum.Pass,
            };
            _detailContent.AddThemeConstantOverride("separation", 24);
            _detailScroll.AddChild(_detailContent);
            _detailBack = new Button
            {
                Name = "ReturnToCombat", Text = "", FocusMode = Control.FocusModeEnum.None,
                ExpandIcon = true, IconAlignment = HorizontalAlignment.Center,
                Icon = ResourceLoader.Load<Texture2D>("res://images/atlases/compressed.sprites/back_button_arrow.tres")
                    ?? throw new InvalidOperationException("Native enemy-details Back arrow failed to load."),
            };
            _detailBack.AddThemeFontSizeOverride("font_size", 48);
            _detailBack.AddThemeConstantOverride("icon_max_width", 80);
            // Duplicate only the native paper resource so portrait padding never mutates the atlas cache.
            var backPaper = (AtlasTexture)(ResourceLoader.Load<AtlasTexture>("res://images/atlases/ui_atlas.sprites/back_button.tres")
                ?? throw new InvalidOperationException("Native enemy-details Back paper failed to load.")).Duplicate();
            float backPadding = backPaper.Region.Size.X * 144f / 264f - backPaper.Region.Size.Y;
            backPaper.Margin = new Rect2(0, backPadding * .5f, 0, backPadding);
            StyleBoxTexture backStyle = new()
            {
                Texture = backPaper,
                // Preserve the duplicate atlas canvas instead of clipping its padded paper.
                RegionRect = new Rect2(Vector2.Zero, backPaper.GetSize()),
                ContentMarginLeft = 0, ContentMarginTop = 0,
                ContentMarginRight = 0, ContentMarginBottom = 0,
            };
            foreach (string name in new[] { "normal", "focus" })
                _detailBack.AddThemeStyleboxOverride(name, backStyle);
            var hoverStyle = (StyleBoxTexture)backStyle.Duplicate();
            hoverStyle.ModulateColor = new Color(1.1f, 1.1f, 1.1f);
            _detailBack.AddThemeStyleboxOverride("hover", hoverStyle);
            var pressedStyle = (StyleBoxTexture)backStyle.Duplicate();
            pressedStyle.ModulateColor = new Color(.7f, .7f, .7f);
            _detailBack.AddThemeStyleboxOverride("pressed", pressedStyle);
            _detailRoot.AddChild(_detailBack);
            _detailBack.Pressed += CloseDetails;
            _detailRoot.GuiInput += OnDetailsOutsideInput;
            _detailContent.MinimumSizeChanged += QueueDetailFit;
            _enemyScroll.ScrollStarted += CancelDetailPress;
            _overlays = NRun.Instance!.GlobalUi.Overlays;
            _capstones = NRun.Instance.GlobalUi.CapstoneContainer;
            _map = NMapScreen.Instance!;
            _targets = NTargetManager.Instance;
            _combat = CombatManager.Instance;
            _overlays.Changed += CloseDetails;
            _capstones.Changed += CloseDetails;
            _map.Opened += CloseDetails;
            _targets.TargetingBegan += CloseDetails;
            _combat.StateTracker.CombatStateChanged += OnDetailStateChanged;
            _combat.CombatEnded += OnDetailCombatEnded;
            room.Ui.Hand.ModeChanged += CloseDetails;
            ActiveScreenContext.Instance.Updated += CloseDetails;
            window.SizeChanged += CloseDetails;
            room.TreeExiting += () =>
            {
                // Release the portrait display binding before the original room exits.
                RestoreBackground();
                CloseDetails();
                RestoreEnemyTips();
                foreach (var input in _detailInputs)
                    if (GodotObject.IsInstanceValid(input.Key))
                    {
                        input.Key.TreeExiting -= input.Value.Exiting;
                        input.Key.GuiInput -= input.Value.Handler;
                    }
                _detailInputs.Clear();
                foreach (var input in _orbInputs)
                    if (GodotObject.IsInstanceValid(input.Key))
                    {
                        input.Key.TreeExiting -= input.Value.Exiting;
                        input.Key.GuiInput -= input.Value.Handler;
                    }
                _orbInputs.Clear();
                foreach (NCreature player in _idlePlayers.Keys.ToArray()) ReleaseIdlePlayer(player);
                _enemyScroll.ScrollStarted -= CancelDetailPress;
                _overlays.Changed -= CloseDetails;
                _capstones.Changed -= CloseDetails;
                _map.Opened -= CloseDetails;
                _targets.TargetingBegan -= CloseDetails;
                _combat.StateTracker.CombatStateChanged -= OnDetailStateChanged;
                _combat.CombatEnded -= OnDetailCombatEnded;
                room.Ui.Hand.ModeChanged -= CloseDetails;
                ActiveScreenContext.Instance.Updated -= CloseDetails;
                window.SizeChanged -= CloseDetails;
                if (GodotObject.IsInstanceValid(_detailRoot)) _detailRoot.QueueFree();
            };
        }

        public void Queue()
        {
            if (_queued || _reflowingLandscape)
                return;
            _queued = true;
            Callable.From(() =>
            {
                _queued = false;
                if (GodotObject.IsInstanceValid(_room) && _room.IsInsideTree())
                    Apply();
            }).CallDeferred();
        }

        public void RestoreGeometry()
        {
            if (!_active)
                return;
            _enemyScrollPosition = _enemyScroll.ScrollVertical;
            RestoreEnemyViewport();
            foreach (var pair in _geometry)
                if (GodotObject.IsInstanceValid(pair.Key))
                    pair.Value.Restore();
            _active = false;
        }

        private void Apply()
        {
            RestoreGeometry();
            ApplyBackground();
            if (!PortraitViewportPatch.IsPortrait)
            {
                RestoreEnemyViewport();
                RestoreEnemyInput();
                CloseDetails();
                RestoreEnemyTips();
                RestoreAnnotations();
                _enemyScrollPosition = 0;
                _compact = false;
                // Recalculate against the fully restored parent sizes and native
                // bounds. Suppress only this call's synchronous requeue cycle.
                _reflowingLandscape = true;
                try
                {
                    _room.Call("AdjustCreatureScaleForAspectRatio");
                }
                finally
                {
                    _reflowingLandscape = false;
                }
                return;
            }
            float height = _room.GetViewportRect().Size.Y;
            bool compact = height >= 1440 && height < 1800;
            if (_compact != compact)
                RestoreAnnotations();
            _compact = compact;

            // Snapshots are refreshed only after native geometry has been restored
            // and the actual native method has had a chance to update it.
            _geometry.Clear();
            _geometry.Add(_allies, new PortraitControlSnapshot(_allies));
            _geometry.Add(_enemies, new PortraitControlSnapshot(_enemies));
            NCreature[] creatures = _room.CreatureNodes.Where(GodotObject.IsInstanceValid).ToArray();
            foreach (NCreature creature in creatures)
                _geometry.Add(creature, new PortraitControlSnapshot(creature));
            // The native list excludes evoked nodes still fading in the tree.
            _orbManager = creatures.Select(creature => creature.OrbManager)
                .FirstOrDefault(manager => manager?.IsLocal == true);
            _orbSlotCount = _orbManager == null ? 0 : ((List<NOrb>)NativeOrbNodes.GetValue(_orbManager)!).Count;
            _allies.Scale = Vector2.One;
            _enemies.Scale = Vector2.One;
            _active = true;

            Vector2 viewport = _room.GetViewportRect().Size;
            NCreature[] enemies = creatures.Where(creature => creature.Entity.IsEnemy).ToArray();
            PlaceEnemies(enemies, viewport);
            PlaceKaiserRig(viewport);

            NCreature[] players = creatures.Where(creature => creature.Entity.IsPlayer).ToArray();
            for (int index = 0; index < players.Length; index++)
            {
                NCreature player = players[index];
                Vector2 oldPosition = player.Position;
                Vector2 position = new(48, viewport.Y - 744 - index * 144);
                player.GlobalPosition = _room.GetGlobalTransform() * position;
                Vector2 shift = player.Position - oldPosition;
                // Keep native owner-relative pet offsets. Their animations and
                // original entity references remain attached to the same nodes.
                foreach (NCreature pet in creatures.Where(creature => creature.Entity.PetOwner == player.Entity.Player))
                    pet.Position += shift;
            }

            foreach (NCreature creature in creatures)
                EnlargeAnnotations(creature);
            // Player-side pets own independent visuals and health displays.
            foreach (NCreature creature in creatures)
                ApplyPlayer(creature);
            foreach (NCreature enemy in enemies)
                ApplyEnemy(enemy);
        }

        public void RestoreBackground()
        {
            // Texture restoration must not depend on the annotation snapshot dictionary.
            if (_portraitBattleBackground != null && GodotObject.IsInstanceValid(_portraitBattleBackground))
                _portraitBattleBackground.Hide();
            if (_backgroundBaseImage != null && GodotObject.IsInstanceValid(_backgroundBaseImage))
                _backgroundBaseImage.Texture = _backgroundBaseTexture;
            _backgroundBaseImage = null;
            _backgroundBaseTexture = null;
            if (!_backgroundActive)
                return;
            _backgroundOriginal?.Restore();
            _backgroundActive = false;
        }

        private void ApplyBackground()
        {
            bool wasActive = _backgroundActive;
            RestoreBackground();
            if (!PortraitViewportPatch.IsPortrait)
            {
                if (wasActive)
                    _room.SceneContainer.Call("OnWindowChange");
                return;
            }
            Control background = _room.Get("BgContainer").As<Control>();
            Control? layer = Descendants(background).OfType<Control>().FirstOrDefault(node => node.Name == "Layer_00");
            if (layer == null)
                return; // Background assets are installed after the room's Ready.
            TextureRect? image = Descendants(layer).OfType<TextureRect>()
                .OrderByDescending(node => node.Size.X * node.Size.Y).FirstOrDefault();
            if (image == null || image.Size.X <= 0 || image.Size.Y <= 0)
                throw new InvalidOperationException("The combat base layer has no measurable TextureRect for portrait cover.");
            _backgroundOriginal = new PortraitControlSnapshot(background);
            // Preserve the authored background origin before portrait cover.
            // Kaiser targets use encounter-center coordinates, without camera zoom.
            _backgroundCenterOffset = background.Position - _room.SceneContainer.Size * 0.5f;
            // Measure the real base artwork, not the zero-sized background root.
            // Keep every original layer, shader and particle under the same owner.
            Rect2 source = TransformedBounds(background.GetGlobalTransform().AffineInverse() * image.GetGlobalTransform(), image.Size);
            CanvasItem parent = background.GetParent<CanvasItem>();
            Rect2 target = TransformedBounds(parent.GetGlobalTransform().AffineInverse() * _room.GetGlobalTransform(), _room.GetViewportRect().Size);
            float scale = Math.Max(target.Size.X / source.Size.X, target.Size.Y / source.Size.Y);
            background.PivotOffset = Vector2.Zero;
            background.Scale = Vector2.One * scale;
            background.Position = target.GetCenter() - source.GetCenter() * scale;
            _backgroundActive = true;

            // Match the eight verified native static bases; all other backgrounds keep their native display.
            string? portraitPath = image.Texture?.ResourcePath switch
            {
                "res://images/rooms/overgrowth/overgrowth_00.png" => "res://STS2Portrait/portrait/battle/overgrowth_00-portrait-v1.png",
                "res://images/rooms/underdocks/underdocks_00.png" => "res://STS2Portrait/portrait/battle/underdocks_00-portrait-v1.png",
                "res://images/rooms/hive/hive_00.png" => "res://STS2Portrait/portrait/battle/hive_00-portrait-v1.png",
                "res://images/rooms/glory/glory_00.png" => "res://STS2Portrait/portrait/battle/glory_00-portrait-v1.png",
                "res://images/rooms/false_queen/false_queen_bg.png" => "res://STS2Portrait/portrait/battle/false_queen_bg-portrait-v1.png",
                "res://images/rooms/overgrowth_boss/vantom/vantom_bg_bottom.png" => "res://STS2Portrait/portrait/battle/vantom_bg_bottom-portrait-v1.png",
                "res://images/rooms/waterfall_giant_bg/waterfallgiant_bg_4.png" => "res://STS2Portrait/portrait/battle/waterfallgiant_bg_4-portrait-v1.png",
                "res://images/rooms/architect_victory/architect_victory_bg.png" => "res://STS2Portrait/portrait/battle/architect_victory_bg-portrait-v1.png",
                _ => null,
            };
            if (portraitPath == null) return;
            Texture2D portrait = ResourceLoader.Load<Texture2D>(portraitPath)
                ?? throw new InvalidOperationException($"Portrait battle background failed to load: {portraitPath}.");
            if (_portraitBattleBackground == null)
            {
                _portraitBattleBackground = new TextureRect
                {
                    Name = "PortraitBattleBackground", Visible = false,
                    MouseFilter = Control.MouseFilterEnum.Ignore,
                    ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                    StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
                };
                background.AddChild(_portraitBattleBackground);
                background.MoveChild(_portraitBattleBackground, 0);
            }
            // Clear only the original quad's draw texture: its particles, rig and input owners remain live.
            _backgroundBaseImage = image;
            _backgroundBaseTexture = image.Texture;
            image.Texture = null;
            _portraitBattleBackground.Texture = portrait;
            Rect2 portraitBounds = TransformedBounds(background.GetGlobalTransform().AffineInverse()
                * _room.GetGlobalTransform(), _room.GetViewportRect().Size);
            _portraitBattleBackground.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
            _portraitBattleBackground.Position = portraitBounds.Position;
            _portraitBattleBackground.Size = portraitBounds.Size;
            _portraitBattleBackground.Show();
        }

        private static Rect2 TransformedBounds(Transform2D transform, Vector2 size)
        {
            Rect2 bounds = new(transform * Vector2.Zero, Vector2.Zero);
            return bounds.Expand(transform * new Vector2(size.X, 0)).Expand(transform * size)
                .Expand(transform * new Vector2(0, size.Y));
        }

        public void ApplyPlayer(NCreature player)
        {
            if (!_active || !PortraitViewportPatch.IsPortrait || _placingPlayer)
                return;
            if (player.Entity.Monster is Osty)
            {
                // Keep the original pet and animator for native summon, damage and revival callbacks.
                ApplyIdlePlayer(player);
                Saved(Descendants(player).OfType<NCreatureStateDisplay>().Single(), "visible", false);
                Saved(player.IntentContainer, "visible", false);
                Saved(player.Hitbox, "visible", false);
                // The native pet display fades on death; keep its gameplay HP readable
                // in the owner strip while the original hidden display retains its callbacks.
                NCreature owner = _room.GetCreatureNode(player.Entity.PetOwner!.Creature)!;
                if (_playerStrips.TryGetValue(owner, out PlayerStrip? ownerStrip))
                {
                    ownerStrip.OstyHealth.Text = $"{player.Entity.CurrentHp}/{player.Entity.MaxHp}";
                    // Keep the native red fill proportional, including an empty bar after death.
                    float fraction = player.Entity.MaxHp > 0 ? (float)player.Entity.CurrentHp / player.Entity.MaxHp : 0;
                    ownerStrip.OstyFill.Size = new Vector2(218 * fraction, 38);
                    ownerStrip.OstyFill.Visible = player.Entity.CurrentHp > 0;
                    ownerStrip.OstyIcon.Show();
                    ownerStrip.OstyBar.Show();
                }
                return;
            }
            if (!player.Entity.IsPlayer) return;
            _placingPlayer = true;
            try
            {
                NCreatureStateDisplay display = Descendants(player).OfType<NCreatureStateDisplay>().Single();
                NHealthBar health = Descendants(display).OfType<NHealthBar>().Single();
                NPowerContainer powers = Descendants(player).OfType<NPowerContainer>().Single();
                if (!_playerStrips.TryGetValue(player, out PlayerStrip? strip))
                {
                    strip = new PlayerStrip(player, powers);
                    _playerStrips.Add(player, strip);
                }
                // Keep Defect's verified path and reuse its native lifecycle for Silent.
                // Only drawing leaves are hidden; native actions wake the original skeleton.
                if (player.Entity.Player!.Character is Defect or Silent) ApplyIdlePlayer(player);
                else Saved(player.Visuals, "visible", false);
                // This independent VFX root is not beneath Visuals. Restore its
                // original visibility through the same annotation snapshot.
                foreach (NSovereignBladeVfx blade in player.GetChildren().OfType<NSovereignBladeVfx>())
                {
                    Saved(blade, "visible", false);
                    // This visual-only skeleton has no completion-driven game state.
                    // Manual stops its native internal update; original tweens still
                    // perform forge/attack cleanup. Restore the original mode on exit.
                    Saved(blade.Get("_spineNode").As<Node2D>(), "update_mode", 2);
                }
                Remember(player.Hitbox);
                player.Hitbox.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
                player.Hitbox.Position = Vector2.Zero;
                // A shared player/potion row preserves width while reclaiming enemy height.
                player.Hitbox.Size = new Vector2(480, 120);
                player.Scale = _room.GetGlobalTransform().Scale / player.GetParent<CanvasItem>().GetGlobalTransform().Scale;
                Control reticle = player.Get("_selectionReticle").As<Control>();
                Place(reticle, Vector2.Zero, player.Hitbox.Size);
                reticle.PivotOffset = reticle.Size * 0.5f;
                strip.Surface.Size = player.Hitbox.Size;
                strip.Avatar.Position = new Vector2(0, 12);
                strip.Avatar.Size = Vector2.One * 96;
                strip.Scroll.Position = new Vector2(336, 0);
                strip.Scroll.Size = new Vector2(144, 120);
                // Remove only the added leaf frame; avatar, native health and powers remain visible.
                strip.Surface.Hide();
                strip.Avatar.Show();
                strip.Scroll.Show();
                // Ready's delayed entry tween captured the old display origin.
                // Finish it with its native callbacks before storing the new one.
                Tween? displayTween = display.Get("_showHideTween").AsGodotObject() as Tween;
                if (displayTween != null && displayTween.IsValid() && displayTween.IsRunning())
                    displayTween.FastForwardToCompletion();
                float healthWidth = 204;
                Place(display, new Vector2(108, 0), new Vector2(healthWidth, 120));
                Saved(display, "_originalPosition", display.Position);
                Place(health, Vector2.Zero, new Vector2(healthWidth, 120));
                // Use the immediate native width entry so HP fill caches and
                // poison/doom foregrounds agree with the current portrait width.
                Remember(health.HpBarContainer);
                Saved(health, "_expectedMaxFgWidth", health.Get("_expectedMaxFgWidth"));
                health.HpBarContainer.Position = new Vector2(0, 12);
                health.UpdateWidthRelativeToReferenceValue(player.Entity.MaxHp, healthWidth);
                Control block = Descendants(health).OfType<Control>().Single(node => node.Name == "BlockContainer");
                Place(block, new Vector2(0, 60), new Vector2(60, 60));
                Saved(health, "_originalBlockPosition", block.Position);
                PlaceHealth(health);
                Control hpHitbox = Descendants(display).OfType<Control>().Single(node => node.Name == "HpBarHitbox");
                Saved(hpHitbox, "mouse_filter", (int)Control.MouseFilterEnum.Ignore);
                Control nameplate = Descendants(display).OfType<Control>().Single(node => node.Name == "NameplateContainer");
                Saved(nameplate, "visible", false);
                Remember(powers);
                if (powers.GetParent() != strip.Scroll)
                    powers.Reparent(strip.Scroll, false);
                ApplyPowerRow(powers);
                if (player.OrbManager is { IsLocal: true } manager)
                {
                    PlaceOrbs(manager);
                    Vector2 viewport = _room.GetViewportRect().Size;
                    if (_orbSlotCount > 0 && _orbLayoutSize != viewport)
                    {
                        // Recompute the original tween once per layout space; never
                        // replace ClearOrbs after it has emptied the native list.
                        _orbLayoutSize = viewport;
                        manager.Call("TweenLayout");
                    }
                }
            }
            finally
            {
                _placingPlayer = false;
            }
        }

        private void ApplyIdlePlayer(NCreature player)
        {
            NCreatureVisuals visuals = player.Visuals;
            MegaSprite sprite = visuals.SpineBody!;
            Node2D body = visuals.Body;
            // Keep animation apply/events alive while suppressing only real drawing.
            foreach (CanvasItem child in body.GetChildren().OfType<CanvasItem>()
                .Where(node => node.GetClass() == "SpineMesh2D" || node.Name == "SparkSlot"
                    // Osty also renders blue fire beneath a native slot, outside the mesh leaves.
                    || (player.Entity.Monster is Osty && node.GetClass() == "SpineSlotNode" && node.Name == "Flame")))
                Saved(child, "visible", false);
            if (visuals.FormVfxHolder is Control forms) Saved(forms, "visible", false);
            Saved(body, "visible", true);
            Saved(visuals, "visible", true);
            if (_idlePlayers.ContainsKey(player)) return;
            Saved(body, "update_mode", body.Get("update_mode"));
            void OnCompleted(GodotObject _, GodotObject __, GodotObject entry)
            {
                if (!_active || !PortraitViewportPatch.IsPortrait || Entry.IsDisabled) return;
                // The signal owns this wrapper. Read values now; never dispose it
                // or retain it across the deferred boundary.
                MegaTrackEntry completed = new(entry);
                string name = completed.GetAnimationName();
                // A dead Osty remains in the tree for the next summon; stop its terminal dead loop too.
                if (!completed.IsLoop() || (name is not ("idle_loop" or "low_health_loop" or "relaxed_loop")
                    && !(player.Entity.Monster is Osty && name == "dead_loop"))) return;
                int generation = _idlePlayers[player].Generation;
                Callable.From(() =>
                {
                    if (!_active || !PortraitViewportPatch.IsPortrait || Entry.IsDisabled
                        || !_idlePlayers.TryGetValue(player, out var live) || live.Generation != generation
                        || !GodotObject.IsInstanceValid(player) || !player.IsInsideTree()) return;
                    CreatureAnimator animator = (CreatureAnimator)NativePlayerAnimator.GetValue(player)!;
                    AnimState managed = (AnimState)NativeAnimatorState.GetValue(animator)!;
                    // The original callback must already have settled at a terminal
                    // idle; a completed cast with an idle queued is insufficient.
                    if (managed.Id != name || !managed.IsLooping || managed.GetNextState() != null) return;
                    MegaAnimationState state = live.Sprite.GetAnimationState();
                    using MegaTrackEntry? current = state.GetCurrent(0);
                    if (current == null || !current.IsLoop() || current.GetAnimationName() != name) return;
                    IReadOnlyList<string> queue = state.GetQueuedAnimationNames(0);
                    if (queue.Count != 1 || queue[0] != name) return;
                    // Native update_mode is Process=0, Physics=1, Manual=2, confirmed
                    // by this game's property metadata. No synthetic completion.
                    live.Body.Set("update_mode", 2);
                }).CallDeferred();
            }
            Callable callback = Callable.From<GodotObject, GodotObject, GodotObject>(OnCompleted);
            void OnExiting() => ReleaseIdlePlayer(player);
            _idlePlayers.Add(player, (sprite, body, callback, OnExiting, ++_idleGeneration));
            sprite.ConnectAnimationCompleted(callback);
            body.TreeExiting += OnExiting;
        }

        public void WakeIdlePlayer(NCreature player)
        {
            if (!_active || !PortraitViewportPatch.IsPortrait || !_idlePlayers.TryGetValue(player, out var live)) return;
            // Use a room-wide epoch so a retired callback cannot match a new
            // registration after rotation or the direct ImmediatelySetIdle path.
            live.Generation = ++_idleGeneration;
            _idlePlayers[player] = live;
            live.Body.Set("update_mode", _properties[(live.Body, "update_mode")]);
        }

        private void ReleaseIdlePlayer(NCreature player)
        {
            if (!_idlePlayers.Remove(player, out var live)) return;
            if (!GodotObject.IsInstanceValid(live.Body)) return;
            live.Body.TreeExiting -= live.Exiting;
            live.Sprite.DisconnectAnimationCompleted(live.Completed);
            live.Body.Set("update_mode", _properties[(live.Body, "update_mode")]);
        }

        public void OrbLayoutChanged(NOrbManager manager)
        {
            if (!manager.IsLocal) return;
            _orbManager = manager;
            int count = ((List<NOrb>)NativeOrbNodes.GetValue(manager)!).Count;
            bool changed = count != _orbSlotCount;
            _orbSlotCount = count;
            PlaceOrbs(manager);
            if (changed) Queue();
        }

        public Vector2 OrbTarget(Vector2 original, NOrbManager manager, int index)
        {
            if (!_active || !PortraitViewportPatch.IsPortrait || !manager.IsLocal) return original;
            int count = ((List<NOrb>)NativeOrbNodes.GetValue(manager)!).Count;
            float pitch = Math.Min(192, 984f / count);
            return new Vector2((984 - count * pitch) * .5f + index * pitch, 0);
        }

        public void PlaceOrbs(NOrbManager manager)
        {
            if (!_active || !PortraitViewportPatch.IsPortrait || !manager.IsLocal) return;
            List<NOrb> nodes = (List<NOrb>)NativeOrbNodes.GetValue(manager)!;
            if (nodes.Count == 0) return;
            // Preserve the complete native subtree and its VFX subscriptions.
            // This row ends 24 units above the shared player/potion strip.
            Place(manager, new Vector2(0, -168), new Vector2(984, 144));
            manager.Scale = Vector2.One;
            Saved(manager, "mouse_filter", (int)Control.MouseFilterEnum.Ignore);
            Control orbs = manager.Get("_orbContainer").As<Control>();
            Place(orbs, Vector2.Zero, new Vector2(984, 144));
            Saved(orbs, "mouse_filter", (int)Control.MouseFilterEnum.Ignore);
            foreach (NOrb orb in nodes) ApplyOrb(orb);
        }

        public void ApplyOrb(NOrb orb)
        {
            if (!_active || !PortraitViewportPatch.IsPortrait) return;
            Node? owner = orb.GetParent();
            while (owner != null && owner is not NOrbManager) owner = owner.GetParent();
            if (owner is not NOrbManager { IsLocal: true } manager) return;
            // Ready precedes insertion into the native node list; capacity is
            // already committed before AddSlotAnim creates any of these nodes.
            int count = manager.GetParent<NCreature>().Entity.Player!.PlayerCombatState!.OrbQueue.Capacity;
            if (count == 0) return;
            float pitch = Math.Min(192, 984f / count);
            bool narrow = pitch < 168;
            Place(orb, orb.Position, new Vector2(pitch, 144));
            orb.PivotOffset = Vector2.Zero;
            orb.Scale = Vector2.One;
            Control bounds = orb.Get("_bounds").As<Control>();
            Place(bounds, Vector2.Zero, orb.Size);
            Saved(bounds, "mouse_filter", (int)Control.MouseFilterEnum.Pass);
            Control labels = orb.Get("_labelContainer").As<Control>();
            MegaLabel[] lines = labels.GetChildren().OfType<MegaLabel>().ToArray();
            foreach (MegaLabel label in lines)
            {
                Remember(label);
                label.CustomMinimumSize = Vector2.Zero;
                Font(label, narrow ? 32 : 36);
            }
            // Reserve both real label minima even when native rules hide one.
            // Dark keeps both values; Plasma keeps its original hidden values.
            float textHeight = lines.Sum(label => label.GetCombinedMinimumSize().Y);
            Place(labels, narrow ? new Vector2(0, 144 - textHeight) : new Vector2(96, 0),
                narrow ? new Vector2(pitch, textHeight) : new Vector2(pitch - 96, 144));
            Saved(labels, "alignment", 1);
            Saved(labels, "theme_override_constants/separation", 0);
            Saved(labels, "mouse_filter", (int)Control.MouseFilterEnum.Ignore);
            float diameter = narrow ? Math.Min(72, 144 - textHeight) : 96;
            Vector2 center = narrow ? new Vector2(pitch * .5f, (144 - textHeight) * .5f) : new Vector2(48, 72);
            Control visual = orb.Get("_visualContainer").As<Control>();
            Place(visual, center, Vector2.Zero);
            visual.Scale = Vector2.One * (diameter / 64);
            Saved(visual, "mouse_filter", (int)Control.MouseFilterEnum.Ignore);
            Control outline = orb.Get("_outline").As<Control>();
            // The native entry tween owns Outline.Scale; do not snapshot its
            // transient zero scale and later restore an invisible empty slot.
            Saved(outline, "position", center - Vector2.One * diameter * .5f);
            Saved(outline, "size", Vector2.One * diameter);
            Saved(outline, "pivot_offset", Vector2.One * diameter * .5f);
            Saved(outline, "mouse_filter", (int)Control.MouseFilterEnum.Ignore);
            Control reticle = orb.Get("_selectionReticle").As<Control>();
            Place(reticle, Vector2.Zero, orb.Size);
            reticle.PivotOffset = orb.Size * .5f;
            Saved(reticle, "mouse_filter", (int)Control.MouseFilterEnum.Ignore);
            CpuParticles2D flash = orb.GetChildren().OfType<CpuParticles2D>().Single(node => node.Name == "Flash");
            Saved(flash, "position", center);
            TrackOrbInput(orb);
        }

        private bool CanReadOrb(NOrb orb)
        {
            return !Entry.IsDisabled && _active && PortraitViewportPatch.IsPortrait
                && GodotObject.IsInstanceValid(_room) && _room.IsInsideTree()
                && GodotObject.IsInstanceValid(orb) && orb.IsInsideTree() && !orb.IsQueuedForDeletion()
                && _orbManager != null && GodotObject.IsInstanceValid(_orbManager) && _orbManager.IsLocal
                && ((List<NOrb>)NativeOrbNodes.GetValue(_orbManager)!).Contains(orb) && _combat.IsInProgress
                && !_room.Ui.Hand.InCardPlay && !_room.Ui.Hand.IsInCardSelection && !_targets.IsInSelection
                && _overlays.ScreenCount == 0 && !_capstones.InUse && !_map.IsOpen
                && ActiveScreenContext.Instance.GetCurrentScreen() == _room;
        }

        private void TrackOrbInput(NOrb orb)
        {
            if (_orbInputs.ContainsKey(orb)) return;
            Control.GuiInputEventHandler handler = input => OnOrbInput(orb, input);
            void OnTreeExiting()
            {
                if (_readingOrb == orb) CloseDetails();
                if (_detailPress == orb) CancelDetailPress();
                orb.GuiInput -= handler;
                orb.TreeExiting -= OnTreeExiting;
                _orbInputs.Remove(orb);
                // Channel replaces empty nodes and Evoke retires live nodes.
                // Do not retain their snapshots for the rest of a long battle.
                HashSet<Node> retired = Descendants(orb).Append(orb).ToHashSet();
                foreach (Control control in _annotations.Keys.Where(retired.Contains).ToArray()) _annotations.Remove(control);
                foreach (Control control in _fonts.Keys.Where(retired.Contains).ToArray()) _fonts.Remove(control);
                foreach (var key in _properties.Keys.Where(value => retired.Contains(value.Node)).ToArray()) _properties.Remove(key);
            }
            _orbInputs.Add(orb, (handler, OnTreeExiting));
            orb.GuiInput += handler;
            orb.TreeExiting += OnTreeExiting;
        }

        private void OnOrbInput(NOrb orb, InputEvent input)
        {
            // Use the same native mouse-emulated touch path as the hand.
            // Holding is read-only; moving or releasing cancels a pending hold.
            if (input is InputEventMouseMotion motion)
            {
                if (_detailPress == orb && (orb.GetGlobalTransformWithCanvas() * motion.Position)
                    .DistanceTo(_detailPressPosition) > 24) CancelDetailPress();
                return;
            }
            if (input is not InputEventMouseButton { ButtonIndex: MouseButton.Left } mouse) return;
            CancelDetailPress();
            if (!mouse.Pressed || _readingCreature != null || _readingOrb != null || !CanReadOrb(orb)) return;
            _detailPress = orb;
            _detailPressPosition = orb.GetGlobalTransformWithCanvas() * mouse.Position;
            int generation = _detailGeneration;
            _room.GetTree().CreateTimer(.35).Timeout += () =>
            {
                if (generation == _detailGeneration && _detailPress == orb && CanReadOrb(orb))
                    OpenDetails(null, orb);
            };
        }

        public float? GetHealthWidth(NHealthBar health)
        {
            if (!_active || !PortraitViewportPatch.IsPortrait)
                return null;
            for (Node? node = health; node != null; node = node.GetParent())
                if (node is NCreature creature)
                    return !_room.CreatureNodes.Contains(creature) ? null
                        : creature.Entity.IsPlayer ? 204f
                        : creature.Entity.IsEnemy ? (FourEnemyLayout ? 144f : FiveEnemyLayout ? 216f : 264f) : null;
            return null;
        }

        public void PlaceHealth(NHealthBar health)
        {
            if (GetHealthWidth(health) == null)
                return;
            bool enemy = health.GetParent() is NCreatureStateDisplay display
                && display.GetParent() is NCreature creature && creature.Entity.IsEnemy;
            // Both vanilla bounds updates and Ritsu's forecast/resize postfixes
            // write these positions. Change geometry only after those owners;
            // calling Refresh or Resize here would recurse into their patches.
            Remember(health.HpBarContainer);
            health.HpBarContainer.Position = new Vector2(0, enemy ? 0 : 12);
            Control block = Descendants(health).OfType<Control>().Single(node => node.Name == "BlockContainer");
            Remember(block);
            block.Position = enemy ? new Vector2(FourEnemyLayout ? 156 : FiveEnemyLayout ? 228 : 276, -12) : new Vector2(0, 60);
            Saved(health, "_originalBlockPosition", block.Position);
            if (enemy)
            {
                // Label minimum height includes the real font metrics; the bar's
                // 36-unit rectangle is not the full extent of its HP text.
                Label hp = Descendants(health.HpBarContainer).OfType<Label>().Single(node => node.Name == "HpLabel");
                Font(hp, 36);
                float hpHeight = Math.Max(54, hp.GetCombinedMinimumSize().Y);
                Place(hp, new Vector2(0, (36 - hpHeight) * 0.5f), new Vector2(FourEnemyLayout ? 144 : FiveEnemyLayout ? 216 : 264, hpHeight));
                Label amount = Descendants(block).OfType<Label>().Single(node => node.Name == "BlockLabel");
                Font(amount, 36);
                float blockHeight = Math.Max(60, amount.GetCombinedMinimumSize().Y);
                Place(amount, new Vector2(-12, (72 - blockHeight) * 0.5f), new Vector2(96, blockHeight));
            }
            else
            {
                // Measure real text height before fitting HP and block into the compact strip.
                Label hp = Descendants(health.HpBarContainer).OfType<Label>().Single(node => node.Name == "HpLabel");
                Font(hp, 36);
                float hpHeight = Math.Max(54, hp.GetCombinedMinimumSize().Y);
                Place(hp, new Vector2(0, (36 - hpHeight) * 0.5f), new Vector2(204, hpHeight));
                Label amount = Descendants(block).OfType<Label>().Single(node => node.Name == "BlockLabel");
                Font(amount, 36);
                float blockHeight = Math.Max(60, amount.GetCombinedMinimumSize().Y);
                Place(amount, new Vector2(-12, (60 - blockHeight) * 0.5f), new Vector2(84, blockHeight));
            }
        }

        public void ApplyPowerRow(NPowerContainer powers)
        {
            if (!_active || !PortraitViewportPatch.IsPortrait)
                return;
            PlayerStrip? strip = _playerStrips.Values.FirstOrDefault(value => value.Powers == powers);
            if (strip == null || powers.GetParent() != strip.Scroll)
                return;
            // A native horizontal ScrollContainer keeps every original power
            // reachable; no status is hidden or replaced by an overflow count.
            NPower[] nodes = powers.GetChildren().OfType<NPower>().ToArray();
            powers.CustomMinimumSize = new Vector2(Math.Max(strip.Scroll.Size.X, nodes.Length * 168 - 24), 120);
            powers.Size = powers.CustomMinimumSize;
            for (int index = 0; index < nodes.Length; index++)
            {
                NPower power = nodes[index];
                Place(power, new Vector2(index * 168, 0), new Vector2(144, 120));
                TextureRect icon = Descendants(power).OfType<TextureRect>().Single(node => node.Name == "Icon");
                Place(icon, new Vector2(36, 0), new Vector2(72, 72));
                Label amount = Descendants(power).OfType<Label>().Single(node => node.Name == "AmountLabel");
                Place(amount, new Vector2(0, 66), new Vector2(144, 54));
                Font(amount, 36);
            }
            strip.Scroll.QueueSort();
        }

        public void PlaceEnemyPowers(NPowerContainer powers)
        {
            if (!_active || !PortraitViewportPatch.IsPortrait
                || powers.GetParent() is not NCreatureStateDisplay display
                || display.GetParent() is not NCreature creature || !creature.Entity.IsEnemy
                || !_room.CreatureNodes.Contains(creature))
                return;
            // Four-enemy cells retain five native status targets per row; other cells keep six.
            // Reserve every complete row before allocating the native artwork height.
            int powersPerRow = FourEnemyLayout ? 5 : 6;
            Place(powers, new Vector2(FourEnemyLayout ? 0 : FiveEnemyLayout ? 12 : -12,
                FourEnemyLayout || FiveEnemyLayout ? 64 : 48), new Vector2(powersPerRow * 48, 0));
            NPower[] nodes = powers.GetChildren().OfType<NPower>()
                .Where(power => creature.Entity.Powers.Contains(power.Model)).ToArray();
            for (int index = 0; index < nodes.Length; index++)
                Place(nodes[index], new Vector2((index % powersPerRow) * 48, (index / powersPerRow) * 48), new Vector2(48, 40));
            // Creature updates its model list before native Add/Remove callbacks.
            // Eggs can already reserve one row when the mother gains her first visible power.
            int powerRows = Math.Max(1, _room.CreatureNodes.Where(node => node.Entity.IsEnemy)
                .Select(node => (node.Entity.Powers.Count(power => power.IsVisible) + powersPerRow - 1) / powersPerRow).DefaultIfEmpty().Max());
            bool shortFiveOvicopter = FiveEnemyLayout && _room.GetViewportRect().Size.Y < 2160
                && creature.Entity.Monster is Ovicopter
                && creature.Entity.CombatState?.Encounter is not FabricatorNormal
                && _room.CreatureNodes.Count(node => node.Entity.IsEnemy) == 5
                && _room.CreatureNodes.Count(node => node.Entity.IsEnemy && node.Entity.IsAlive) == 5;
            float unusedPowerRow = shortFiveOvicopter && !creature.Entity.Powers.Any(power => power.IsVisible) ? 48 : 0;
            float displayHeight = 64 + powerRows * 48 - unusedPowerRow;
            // ApplyEnemy writes this height before updating powers, so the queued reflow converges.
            if (powerRows != _enemyPowerRows || (shortFiveOvicopter && display.Size.Y != displayHeight)) Queue();
            // A power can be added without recreating intents or the creature.
            // Native Add calls this layout entry after its new node is ready.
            foreach (NPower power in powers.GetChildren().OfType<NPower>())
            {
                if (power.MouseFilter == Control.MouseFilterEnum.Stop)
                {
                    _passingEnemyControls.Add(power);
                    power.MouseFilter = Control.MouseFilterEnum.Pass;
                }
                TrackDetailInput(creature, power);
            }
        }

        public void ApplyEnemy(NCreature creature)
        {
            if (!_active || !PortraitViewportPatch.IsPortrait
                || !creature.Entity.IsEnemy || _placingEnemy || !_room.CreatureNodes.Contains(creature))
                return;
            _placingEnemy = true;
            try
            {
                // Stack intent, native artwork, HP and powers around one center.
                // The short viewport retains the complete HP row below a larger model.
                float modelWidth = FourEnemyLayout ? 208 : FiveEnemyLayout ? 280 : 408;
                // Reuse only the empty first power row of a live mother in a genuine short five.
                float unusedPowerRow = FiveEnemyLayout && _room.GetViewportRect().Size.Y < 2160
                    && creature.Entity.Monster is Ovicopter
                    && creature.Entity.CombatState?.Encounter is not FabricatorNormal
                    && _room.CreatureNodes.Count(node => node.Entity.IsEnemy) == 5
                    && _room.CreatureNodes.Count(node => node.Entity.IsEnemy && node.Entity.IsAlive) == 5
                    && !creature.Entity.Powers.Any(power => power.IsVisible) ? 48 : 0;
                float modelHeight = _enemyModelHeight + unusedPowerRow;
                const float intentHeight = 152;
                // Normalize the encounter camera, then apply the complete-cell
                // fit when full information stacks need less vertical space.
                creature.Scale = _room.GetGlobalTransform().Scale / creature.GetParent<CanvasItem>().GetGlobalTransform().Scale
                    * _enemyCellScale;
                NCreatureVisuals visuals = creature.Visuals;
                Control bounds = visuals.Bounds;
                Rect2 local = TransformedBounds(visuals.GetGlobalTransform().AffineInverse() * bounds.GetGlobalTransform(), bounds.Size);
                Vector2 originalScale = _properties.TryGetValue((visuals, "scale"), out Variant nativeScale)
                    ? nativeScale.AsVector2() : visuals.Scale;
                Vector2 nativeSize = local.Size * originalScale.Abs();
                // Only an ordinary single enemy gets a larger native-scale cap.
                // Existing width and height limits still constrain large artwork.
                float scaleCap = _closeEnemyRow && _room.CreatureNodes.Count(node => node.Entity.IsEnemy) == 1 ? 1.8f : 1.5f;
                float fit = Math.Min(scaleCap, Math.Min(modelWidth / nativeSize.X, modelHeight / nativeSize.Y));
                Vector2 scale = originalScale * fit;
                Saved(visuals, "scale", scale);
                Saved(visuals, "position", new Vector2(-local.GetCenter().X * scale.X, -local.End.Y * scale.Y));
                // A width-limited single row can be much shorter than its model
                // budget. Keep its touch target and intent beside the fitted bounds.
                float fittedHeight = _closeEnemyRow ? local.Size.Y * Math.Abs(scale.Y) : modelHeight;
                float hitHeight = Math.Max(144, fittedHeight);
                Vector2 hitSize = new(modelWidth, hitHeight);
                Place(creature.Hitbox, new Vector2(-modelWidth * 0.5f, _closeEnemyRow ? -hitHeight : -modelHeight), hitSize);
                Control reticle = creature.Get("_selectionReticle").As<Control>();
                Place(reticle, creature.Hitbox.Position, hitSize);
                reticle.PivotOffset = hitSize * 0.5f;

                NIntent[] intents = creature.IntentContainer.GetChildren().OfType<NIntent>().ToArray();
                foreach (NIntent intent in intents)
                {
                    Remember(intent);
                    Control holder = intent.GetChildren().OfType<Control>().Single(node => node.Name == "IntentHolder");
                    RichTextLabel value = holder.GetChildren().OfType<RichTextLabel>().Single(node => node.Name == "Value");
                    bool numeric = !string.IsNullOrEmpty(value.Text);
                    intent.CustomMinimumSize = new Vector2(FourEnemyLayout ? 72 : FiveEnemyLayout ? 96 : numeric ? 144 : 96, intentHeight);
                    Remember(holder);
                    holder.Scale = Vector2.One;
                    holder.PivotOffset = Vector2.Zero;
                    // Keep the 72-unit icon and native bob. Four enemies fit three 72-unit
                    // slots plus two 8-unit gaps; five enemies retain their 96-unit slots.
                    Sprite2D sprite = holder.GetChildren().OfType<Sprite2D>().Single(node => node.Name == "Intent");
                    Saved(sprite, "position", new Vector2(FourEnemyLayout ? 36 : FiveEnemyLayout ? 48 : numeric ? 72 : 48, 54));
                    Saved(sprite, "scale", Vector2.One);
                    // Scale only the label to preserve both the normal font and
                    // native inline BBCode sizes, without enlarging the artwork.
                    // Native repeat counts use an inline size of 18. Preserve
                    // the whole-label scale so the multiplier remains 27 units.
                    Font(value, FourEnemyLayout || FiveEnemyLayout ? 22 : 28);
                    Place(value, new Vector2(0, FourEnemyLayout || FiveEnemyLayout ? 92 : 84),
                        FourEnemyLayout ? new Vector2(48, 36) : FiveEnemyLayout ? new Vector2(64, 36) : new Vector2(96, 42));
                    value.Scale = Vector2.One * 1.5f;
                }
                int intentGap = FourEnemyLayout || FiveEnemyLayout ? 8 : 12;
                Saved(creature.IntentContainer, "theme_override_constants/separation", intentGap);
                float intentWidth = Math.Max(FourEnemyLayout ? 72 : 96, intents.Sum(intent => intent.CustomMinimumSize.X)
                    + Math.Max(0, intents.Length - 1) * intentGap);
                float intentModelHeight = _closeEnemyRow ? Math.Min(modelHeight, fittedHeight + 12) : modelHeight;
                Place(creature.IntentContainer, new Vector2(-intentWidth * 0.5f, -intentModelHeight - intentHeight), new Vector2(intentWidth, intentHeight));
                NCreatureStateDisplay display = Descendants(creature).OfType<NCreatureStateDisplay>().Single();
                Tween? tween = display.Get("_showHideTween").AsGodotObject() as Tween;
                if (tween != null && tween.IsValid() && tween.IsRunning())
                    tween.FastForwardToCompletion();
                // Center the complete HP/block strip inside the narrower cell.
                Place(display, new Vector2(FourEnemyLayout ? -120 : FiveEnemyLayout ? -156 : -132, 8),
                    new Vector2(FourEnemyLayout ? 240 : FiveEnemyLayout ? 312 : 360,
                        (FourEnemyLayout || FiveEnemyLayout ? 64 : 48) + _enemyPowerRows * 48 - unusedPowerRow));
                Saved(display, "_originalPosition", display.Position);
                NHealthBar health = Descendants(display).OfType<NHealthBar>().Single();
                Place(health, Vector2.Zero, new Vector2(FourEnemyLayout ? 240 : FiveEnemyLayout ? 312 : 360, 72));
                Remember(health.HpBarContainer);
                Saved(health, "_expectedMaxFgWidth", health.Get("_expectedMaxFgWidth"));
                health.UpdateWidthRelativeToReferenceValue(creature.Entity.MaxHp, FourEnemyLayout ? 144 : FiveEnemyLayout ? 216 : 264);
                Control block = Descendants(health).OfType<Control>().Single(node => node.Name == "BlockContainer");
                Place(block, new Vector2(FourEnemyLayout ? 156 : FiveEnemyLayout ? 228 : 276, -12), new Vector2(72, 72));
                PlaceHealth(health);
                Control hpHitbox = Descendants(display).OfType<Control>().Single(node => node.Name == "HpBarHitbox");
                Saved(hpHitbox, "mouse_filter", (int)Control.MouseFilterEnum.Ignore);
                Control nameplate = Descendants(display).OfType<Control>().Single(node => node.Name == "NameplateContainer");
                // The details reader still copies the native name text. Hiding
                // both original nodes also survives vanilla hover opacity tweens.
                Saved(nameplate, "visible", false);
                Label name = nameplate.GetChildren().OfType<Label>().Single();
                Saved(name, "visible", false);
                NPowerContainer powers = Descendants(display).OfType<NPowerContainer>().Single();
                PlaceEnemyPowers(powers);
                powers.Call("UpdatePositions");
                PassEnemyInput(creature);
                TrackDetailInput(creature, creature.Hitbox);
                foreach (Control control in Descendants(creature).OfType<Control>().Where(node => node is NIntent or NPower))
                    TrackDetailInput(creature, control);
                PlaceKaiserTarget(creature, local, originalScale);
            }
            finally
            {
                _placingEnemy = false;
            }
        }

        private void PlaceKaiserRig(Vector2 viewport)
        {
            _kaiserRig = _room.Background == null ? null : Descendants(_room.Background)
                .OfType<NKaiserCrabBossBackground>().SingleOrDefault(node => node.Name.ToString().Contains("KaiserCrab", StringComparison.Ordinal));
            if (_kaiserRig == null)
                return;
            Node2D body = _kaiserRig.GetChildren().OfType<Node2D>().Single(node => node.Name == "Visuals");
            Transform2D original = _properties.TryGetValue((_kaiserRig, "transform"), out Variant saved)
                ? saved.AsTransform2D() : _kaiserRig.Transform;
            Transform2D reference = new Transform2D(0, _backgroundCenterOffset) * original;
            // B46 measured the complete, visible three-track idle skeleton. Keep
            // this full reference after arm death; live pose bounds would rezoom.
            Rect2 spineBounds = new(-2812.9355f, -1546.5363f, 5778.3315f, 2479.337f);
            Rect2 source = TransformedBounds(reference * body.Transform
                * new Transform2D(0, spineBounds.Position), spineBounds.Size);
            float available = viewport.Y - 744 - 336 - (_orbSlotCount > 0 ? 168 : 0);
            // Reserve side room for the native HP row's right-hand block display.
            Rect2 target = new(new Vector2(72, 488), new Vector2(936, available - 152 - 144));
            if (target.Size.Y <= 0)
                throw new InvalidOperationException("Kaiser portrait stage has no room for its native model and annotations.");
            _kaiserFit = Math.Min(target.Size.X / source.Size.X, target.Size.Y / source.Size.Y);
            _kaiserFrame = new Transform2D(0, Vector2.One * _kaiserFit, 0,
                target.GetCenter() - source.GetCenter() * _kaiserFit);
            // Only the shared rig cancels parent cover/camera. Its Spine, bone
            // children, particles and native animation callbacks keep their owner.
            Saved(_kaiserRig, "transform", _kaiserRig.GetParent<CanvasItem>().GetGlobalTransform().AffineInverse()
                * _room.GetGlobalTransform() * _kaiserFrame * reference);
            _enemyScrollPosition = 0;
            _enemyScroll.ScrollVertical = 0;
            _enemyScroll.VerticalScrollMode = ScrollContainer.ScrollMode.Disabled;
            _enemyScroll.Size = new Vector2(984, available);
            _enemyContent.CustomMinimumSize = _enemyScroll.Size;
            _enemyContent.Size = _enemyScroll.Size;
            _enemyContent.Position = Vector2.Zero;
            _enemyScroll.QueueSort();
        }

        private void PlaceKaiserTarget(NCreature creature, Rect2 nativeBounds, Vector2 nativeScale)
        {
            if (_kaiserRig == null || !GodotObject.IsInstanceValid(_kaiserRig)
                || creature.Entity.SlotName is not ("crusher" or "rocket"))
                return;
            // Slot identity fixes the surviving arm to its original side. Keep
            // this after ApplyEnemy because native UpdateBounds re-enters it.
            Control slots = _room.Get("EncounterSlots").As<Control>();
            Marker2D marker = slots.GetChildren().OfType<Marker2D>()
                .Single(node => node.Name == creature.Entity.SlotName);
            creature.GlobalPosition = _room.GetGlobalTransform() * (_kaiserFrame * (slots.Position + marker.Position));
            NCreatureVisuals visuals = creature.Visuals;
            Vector2 nativePosition = _properties[(visuals, "position")].AsVector2();
            Vector2 scale = nativeScale * _kaiserFit;
            Saved(visuals, "position", nativePosition * _kaiserFit);
            Saved(visuals, "scale", scale);
            Rect2 bounds = TransformedBounds(new Transform2D(0, scale, 0, nativePosition * _kaiserFit)
                * new Transform2D(0, nativeBounds.Position), nativeBounds.Size);
            Vector2 touchSize = new(Math.Max(240, bounds.Size.X), Math.Max(144, bounds.Size.Y));
            Vector2 touchPosition = bounds.GetCenter() - touchSize * 0.5f;
            Place(creature.Hitbox, touchPosition, touchSize);
            Control reticle = creature.Get("_selectionReticle").As<Control>();
            Place(reticle, touchPosition, touchSize);
            reticle.PivotOffset = touchSize * 0.5f;
            Control intents = creature.IntentContainer;
            Place(intents, new Vector2(bounds.GetCenter().X - intents.Size.X * 0.5f,
                bounds.Position.Y - 164), intents.Size);
            NCreatureStateDisplay display = Descendants(creature).OfType<NCreatureStateDisplay>().Single();
            Place(display, new Vector2(bounds.GetCenter().X - 132, bounds.End.Y + 8), display.Size);
            Saved(display, "_originalPosition", display.Position);
        }

        private sealed class PlayerStrip
        {
            public readonly Panel Surface;
            public readonly TextureRect Avatar;
            public readonly TextureRect OstyIcon;
            public readonly Control OstyBar;
            public readonly NinePatchRect OstyFill;
            public readonly Label OstyHealth;
            public readonly ScrollContainer Scroll;
            public readonly NPowerContainer Powers;
            public readonly Node PowerParent;
            public readonly int PowerIndex;

            public PlayerStrip(NCreature player, NPowerContainer powers)
            {
                Powers = powers;
                PowerParent = powers.GetParent();
                PowerIndex = powers.GetIndex();
                Surface = new Panel { Name = "PortraitPlayerSurface", Size = new Vector2(480, 120), MouseFilter = Control.MouseFilterEnum.Ignore };
                Surface.AddThemeStyleboxOverride("panel", new StyleBoxFlat
                {
                    BgColor = new Color(0.075f, 0.095f, 0.075f, 0.97f),
                    BorderColor = new Color(0.35f, 0.38f, 0.27f),
                    BorderWidthLeft = 2, BorderWidthTop = 2, BorderWidthRight = 2, BorderWidthBottom = 2,
                    CornerRadiusTopLeft = 18, CornerRadiusTopRight = 18,
                    CornerRadiusBottomLeft = 18, CornerRadiusBottomRight = 18,
                });
                player.AddChild(Surface);
                player.MoveChild(Surface, 0);
                Avatar = new TextureRect
                {
                    Name = "PortraitPlayerAvatar", Position = new Vector2(0, 12), Size = new Vector2(96, 96),
                    // ApplyPlayer constructs this only after Entity.IsPlayer.
                    Texture = player.Entity.Player!.Character.IconTexture,
                    ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                    MouseFilter = Control.MouseFilterEnum.Ignore,
                };
                player.AddChild(Avatar);
                // Keep the pet row above the avatar, leaving block and player powers in place.
                // Reuse the original starter-relic icon and HP font without drawing the pet.
                Label nativeHp = Descendants(player).OfType<Label>().Single(node => node.Name == "HpLabel");
                OstyIcon = new TextureRect
                {
                    Name = "PortraitOstyIcon", Position = new Vector2(0, -84), Size = new Vector2(72, 72),
                    Texture = ModelDb.Relic<BoundPhylactery>().Icon,
                    ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                    MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false,
                };
                player.AddChild(OstyIcon);
                // Reuse the native nine-patch background, clipping mask and red fill.
                // These passive leaves share the existing HP refresh and have no animation owner.
                Control nativeBar = nativeHp.GetParent<Control>();
                NinePatchRect background = Descendants(nativeBar).OfType<NinePatchRect>().Single(node => node.Name == "HpBackground");
                NinePatchRect mask = Descendants(nativeBar).OfType<NinePatchRect>().Single(node => node.Name == "Mask");
                NinePatchRect foreground = Descendants(nativeBar).OfType<NinePatchRect>().Single(node => node.Name == "HpForeground");
                OstyBar = new Control
                {
                    Name = "PortraitOstyBar", Position = new Vector2(84, -66), Size = new Vector2(228, 36),
                    MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false,
                };
                player.AddChild(OstyBar);
                NinePatchRect barBackground = (NinePatchRect)background.Duplicate(0);
                OstyBar.AddChild(barBackground);
                barBackground.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
                barBackground.MouseFilter = Control.MouseFilterEnum.Ignore;
                NinePatchRect barMask = new NinePatchRect
                {
                    Name = "PortraitOstyMask", Position = new Vector2(5, 3), Size = new Vector2(218, 30),
                    Texture = mask.Texture, PatchMarginLeft = mask.PatchMarginLeft, PatchMarginRight = mask.PatchMarginRight,
                    ClipChildren = CanvasItem.ClipChildrenMode.Only, MouseFilter = Control.MouseFilterEnum.Ignore,
                };
                OstyBar.AddChild(barMask);
                OstyFill = (NinePatchRect)foreground.Duplicate(0);
                OstyFill.Name = "PortraitOstyFill";
                barMask.AddChild(OstyFill);
                OstyFill.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
                OstyFill.Position = new Vector2(0, -4);
                OstyFill.SelfModulate = new Color("F1373E");
                OstyFill.MouseFilter = Control.MouseFilterEnum.Ignore;
                OstyHealth = new Label
                {
                    Name = "PortraitOstyHealth", Position = new Vector2(0, -18), Size = new Vector2(228, 72),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    MouseFilter = Control.MouseFilterEnum.Ignore,
                };
                OstyHealth.AddThemeFontOverride("font", nativeHp.GetThemeFont("font"));
                OstyHealth.AddThemeFontSizeOverride("font_size", 36);
                OstyHealth.AddThemeColorOverride("font_color", nativeHp.GetThemeColor("font_color"));
                OstyHealth.AddThemeColorOverride("font_outline_color", new Color("900000"));
                OstyHealth.AddThemeConstantOverride("outline_size", 4);
                OstyBar.AddChild(OstyHealth);
                Scroll = new ScrollContainer
                {
                    Name = "PortraitPlayerPowers", Position = new Vector2(336, 0), Size = new Vector2(144, 120),
                    HorizontalScrollMode = ScrollContainer.ScrollMode.ShowNever,
                    VerticalScrollMode = ScrollContainer.ScrollMode.Disabled,
                    ClipContents = true,
                };
                player.AddChild(Scroll);
            }
        }

        private void RestoreEnemyViewport()
        {
            // Identity wrappers preserve the original half-parent anchors and
            // encounter coordinates in landscape and before native layout calls.
            _enemyContent.CustomMinimumSize = Vector2.Zero;
            _enemyScroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
            _enemyScroll.VerticalScrollMode = ScrollContainer.ScrollMode.Disabled;
            _enemyScroll.ClipContents = false;
            _enemyScroll.MouseFilter = Control.MouseFilterEnum.Ignore;
            _enemyScroll.ScrollHorizontal = 0;
            _enemyScroll.ScrollVertical = 0;
            _enemyScroll.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
            _enemyScroll.PivotOffset = Vector2.Zero;
            _enemyScroll.Scale = Vector2.One;
            _enemyScroll.Position = Vector2.Zero;
            _enemyScroll.Size = _room.SceneContainer.Size;
            _enemyContent.Position = Vector2.Zero;
            _enemyContent.Size = _room.SceneContainer.Size;
            _enemyScroll.QueueSort();
        }

        private void PlaceEnemies(NCreature[] enemies, Vector2 viewport)
        {
            const float width = 984;
            float top = 336;
            // Fabricator keeps its five native slots even before all bots exist.
            // Reuse the compact five-enemy cells for ordinary short three-enemy rows.
            bool fabricatorLayout = enemies.Any(creature => creature.Entity.CombatState?.Encounter is FabricatorNormal);
            // Four ordinary enemies use one complete row only in the short viewport.
            FourEnemyLayout = enemies.Length == 4 && viewport.Y < 2160 && !fabricatorLayout
                && !enemies.Any(creature => creature.Entity.CombatState?.Encounter is KaiserCrabBoss)
                && enemies.All(creature =>
                {
                    // Reuse ApplyEnemy's native bounds transform and saved scale.
                    // Admit only shapes whose narrower fit preserves the old two-row body budget.
                    NCreatureVisuals visuals = creature.Visuals;
                    Control bounds = visuals.Bounds;
                    Rect2 local = TransformedBounds(visuals.GetGlobalTransform().AffineInverse() * bounds.GetGlobalTransform(), bounds.Size);
                    Vector2 originalScale = _properties.TryGetValue((visuals, "scale"), out Variant nativeScale)
                        ? nativeScale.AsVector2() : visuals.Scale;
                    Vector2 nativeSize = local.Size * originalScale.Abs();
                    float oldBodyBudget = Math.Clamp((viewport.Y - 1104) * 0.5f, 408, 600) - 256;
                    return nativeSize.X * oldBodyBudget <= nativeSize.Y * 208;
                });
            FiveEnemyLayout = fabricatorLayout || enemies.Length == 5
                || (enemies.Length == 3 && viewport.Y < 2160
                    && !enemies.Any(creature => creature.Entity.CombatState?.Encounter is KaiserCrabBoss));
            int columns = FourEnemyLayout ? 4 : FiveEnemyLayout ? 3 : 2;
            int rows = fabricatorLayout ? 2 : (enemies.Length + columns - 1) / columns;
            // Move ordinary one/two and compact three/four-enemy rows closer to the hand.
            // Fabricator, genuine five-enemy rows and the shared Kaiser rig keep their frame.
            _closeEnemyRow = rows == 1 && !fabricatorLayout && enemies.Length != 5
                && !enemies.Any(creature => creature.Entity.CombatState?.Encounter is KaiserCrabBoss);
            // An orb row must not turn previously visible three/four enemies into
            // a scrolling list. Reuse the same complete-cell fit for those rows.
            bool fitOrbRows = _orbSlotCount > 0 && rows > 1;
            // Keep every five-enemy slot visible above the separate orb row.
            float playerTop = viewport.Y - 744 - (_orbSlotCount > 0 ? 168 : 0);
            float available = playerTop - top - (_closeEnemyRow ? 24 : 0);
            int powersPerRow = FourEnemyLayout ? 5 : 6;
            _enemyPowerRows = Math.Max(1, enemies
                .Select(node => (node.Entity.Powers.Count(power => power.IsVisible) + powersPerRow - 1) / powersPerRow).DefaultIfEmpty().Max());
            float powerOverflow = (_enemyPowerRows - 1) * 48;
            float availableRow = (available - Math.Max(0, rows - 1) * 24) / Math.Max(1, rows);
            float availableSlot = availableRow - powerOverflow;
            // Compact cells fit the complete intent/HP/status stack without the old floor.
            // The four-enemy row reuses the same vertical extent as five-enemy cells.
            float slotHeight = FourEnemyLayout || FiveEnemyLayout ? Math.Min(availableSlot, 600)
                : Math.Clamp(availableSlot, 408, rows <= 1 ? 720 : 600);
            _enemyModelHeight = slotHeight - (FourEnemyLayout || FiveEnemyLayout ? 272 : 256);
            _enemyCellScale = 1;
            float rowHeight = slotHeight + powerOverflow;
            if (FourEnemyLayout || FiveEnemyLayout || fitOrbRows)
            {
                // Fit the complete original stack, including every status row.
                // This also avoids negative artwork height in shorter windows.
                _enemyModelHeight = Math.Max(72, _enemyModelHeight);
                // 152 intent + 8 display offset + 64 before powers + 48 first row.
                float fullHeight = _enemyModelHeight + (FourEnemyLayout || FiveEnemyLayout ? 272 : 256) + powerOverflow;
                rowHeight = Math.Min(availableRow, fullHeight);
                _enemyCellScale = rowHeight / fullHeight;
            }
            float contentHeight = rows == 0 ? 0 : rows * rowHeight + (rows - 1) * 24;
            // Keep native death animations alive. A departing node must not
            // enlarge a no-scroll compact viewport into the player's HUD.
            foreach (NCreature departing in _enemies.GetChildren().OfType<NCreature>()
                .Where(value => !FourEnemyLayout && !FiveEnemyLayout && !fitOrbRows && !_room.CreatureNodes.Contains(value)))
            {
                NPowerContainer powers = Descendants(departing).OfType<NPowerContainer>().Single();
                float bottom = powers.GetChildren().OfType<NPower>()
                    .Select(power => power.Position.Y + 48).DefaultIfEmpty(48).Max();
                contentHeight = Math.Max(contentHeight, departing.Position.Y
                    + powers.GetParent<Control>().Position.Y + powers.Position.Y + bottom);
            }
            float height = Math.Min(contentHeight, available);
            if (height < 0)
                throw new InvalidOperationException("Portrait enemy viewport has no height above the player strip.");
            // Move the whole native enemy subtree, including HP, powers and input,
            // into unused room. Its bottom keeps 24 units clear of player/orb UI.
            if (_closeEnemyRow) top += available - height;
            _enemyScroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
            _enemyScroll.VerticalScrollMode = !FourEnemyLayout && !FiveEnemyLayout && !fitOrbRows && contentHeight > height
                ? ScrollContainer.ScrollMode.ShowNever : ScrollContainer.ScrollMode.Disabled;
            if (FourEnemyLayout || FiveEnemyLayout || fitOrbRows)
                _enemyScrollPosition = 0;
            _enemyScroll.ClipContents = true;
            _enemyScroll.MouseFilter = enemies.Length == 0
                ? Control.MouseFilterEnum.Ignore : Control.MouseFilterEnum.Stop;
            _enemyScroll.Scale = _room.GetGlobalTransform().Scale / _room.SceneContainer.GetGlobalTransform().Scale;
            _enemyScroll.Position = _room.SceneContainer.GetGlobalTransform().AffineInverse()
                * (_room.GetGlobalTransform() * new Vector2((viewport.X - width) * 0.5f, top));
            _enemyScroll.Size = new Vector2(width, height);
            _enemyContent.CustomMinimumSize = new Vector2(width, contentHeight);
            _enemyContent.Size = _enemyContent.CustomMinimumSize;
            _enemyContent.Position = Vector2.Zero;
            _enemies.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
            _enemies.Position = Vector2.Zero;
            _enemies.Size = Vector2.Zero;
            for (int index = 0; index < enemies.Length; index++)
            {
                int row = index / columns;
                int column = index % columns;
                int rowCount = Math.Min(columns, enemies.Length - row * columns);
                if (fabricatorLayout)
                {
                    // Map native slot identities, not the changing creature-list order.
                    // Bots fill clockwise from the boss's left; deaths leave empty cells.
                    (row, column, rowCount) = enemies[index].Entity.SlotName switch
                    {
                        "bot1" => (1, 0, 3),
                        "bot2" => (0, 0, 2),
                        "fabricator" => (1, 1, 3),
                        "bot3" => (0, 1, 2),
                        "bot4" => (1, 2, 3),
                        _ => throw new InvalidOperationException("Fabricator portrait layout requires a native encounter slot.")
                    };
                }
                float centerX = FourEnemyLayout ? column * 248 + 120
                    : FiveEnemyLayout
                        ? (width - (rowCount * 320 + (rowCount - 1) * 12)) * 0.5f + column * 332 + 160
                        : (enemies.Length == 1 ? 252 : column * 504) + 240;
                // Move the foot by the same borrowed height; the intent top and complete cell stay fixed.
                float unusedPowerRow = FiveEnemyLayout && viewport.Y < 2160 && !fabricatorLayout
                    && enemies.Length == 5 && enemies.All(creature => creature.Entity.IsAlive)
                    && enemies[index].Entity.Monster is Ovicopter
                    && !enemies[index].Entity.Powers.Any(power => power.IsVisible) ? 48 : 0;
                Vector2 foot = new(centerX, row * (rowHeight + 24)
                    + (152 + _enemyModelHeight + unusedPowerRow) * _enemyCellScale);
                enemies[index].GlobalPosition = _enemyContent.GetGlobalTransform() * foot;
            }
            _enemyScroll.QueueSort();
            // The original container publishes its new range on its queued sort.
            // Restore the user's scroll only after that owner has laid out content.
            Callable.From(() =>
            {
                if (_active && PortraitViewportPatch.IsPortrait && GodotObject.IsInstanceValid(_enemyScroll))
                    _enemyScroll.ScrollVertical = _enemyScrollPosition;
            }).CallDeferred();
        }

        private void PassEnemyInput(NCreature creature)
        {
            // Keep every native hover/focus handler, but allow a touchscreen drag
            // to bubble from these native enemy controls to the one scroll owner.
            foreach (Control control in Descendants(creature).OfType<Control>().Prepend(creature))
                if (control.MouseFilter == Control.MouseFilterEnum.Stop)
                {
                    _passingEnemyControls.Add(control);
                    control.MouseFilter = Control.MouseFilterEnum.Pass;
                }
        }

        public void NativeEnemyInputChanged(NCreature creature)
        {
            // Native Toggle/Revive just wrote this filter. Forget any older Pass
            // mapping; in particular, never restore a disabled/dead Ignore to Stop.
            if (!creature.Entity.IsAlive || creature.Hitbox.MouseFilter == Control.MouseFilterEnum.Ignore)
            {
                if (_readingCreature == creature) CloseDetails();
                if (_detailPress != null && _detailInputs.TryGetValue(_detailPress, out var input) && input.Creature == creature)
                    CancelDetailPress();
            }
            _passingEnemyControls.Remove(creature.Hitbox);
            if (_active && PortraitViewportPatch.IsPortrait && creature.Entity.IsEnemy
                && _room.CreatureNodes.Contains(creature))
                PassEnemyInput(creature);
        }

        private void RestoreEnemyInput()
        {
            foreach (Control control in _passingEnemyControls)
                if (GodotObject.IsInstanceValid(control) && control.MouseFilter == Control.MouseFilterEnum.Pass)
                    control.MouseFilter = Control.MouseFilterEnum.Stop;
            _passingEnemyControls.Clear();
        }

        public void ForgetCreature(NCreature creature)
        {
            // Removal leaves the original death animation alive. Stop restoring
            // this node and its children; do not free, reparent, or await them here.
            // Defer the next size calculation until the original child has left.
            // Queued-for-deletion nodes still need their final visible frame.
            if (_readingCreature == creature) CloseDetails();
            ReleaseIdlePlayer(creature);
            foreach (var input in _detailInputs.Where(pair => pair.Value.Creature == creature).ToArray())
            {
                if (_detailPress == input.Key) CancelDetailPress();
                if (GodotObject.IsInstanceValid(input.Key))
                {
                    // End both subscriptions before the native death animation frees the node.
                    input.Key.TreeExiting -= input.Value.Exiting;
                    input.Key.GuiInput -= input.Value.Handler;
                }
                _detailInputs.Remove(input.Key);
            }
            creature.TreeExiting += Queue;
            HashSet<Node> retired = Descendants(creature).Append(creature).ToHashSet();
            foreach (Control control in _geometry.Keys.Where(value => !GodotObject.IsInstanceValid(value) || retired.Contains(value)).ToArray())
                _geometry.Remove(control);
            foreach (Control control in _annotations.Keys.Where(value => !GodotObject.IsInstanceValid(value) || retired.Contains(value)).ToArray())
                _annotations.Remove(control);
            foreach (Control control in _fonts.Keys.Where(value => !GodotObject.IsInstanceValid(value) || retired.Contains(value)).ToArray())
                _fonts.Remove(control);
            foreach (RichTextLabel control in _alignments.Keys.Where(value => !GodotObject.IsInstanceValid(value) || retired.Contains(value)).ToArray())
                _alignments.Remove(control);
            foreach (var key in _properties.Keys.Where(value => !GodotObject.IsInstanceValid(value.Node) || retired.Contains(value.Node)).ToArray())
                _properties.Remove(key);
            _passingEnemyControls.RemoveWhere(value => !GodotObject.IsInstanceValid(value) || retired.Contains(value));
        }

        private bool CanReadCreature(NCreature creature)
        {
            return !Entry.IsDisabled && _active && PortraitViewportPatch.IsPortrait
                && GodotObject.IsInstanceValid(creature) && creature.IsInsideTree() && !creature.IsQueuedForDeletion()
                && _room.CreatureNodes.Contains(creature) && creature.Entity.IsEnemy && creature.Entity.IsAlive
                && creature.Hitbox.MouseFilter != Control.MouseFilterEnum.Ignore && _combat.IsInProgress
                && !_room.Ui.Hand.InCardPlay && !_room.Ui.Hand.IsInCardSelection && !_targets.IsInSelection
                && _overlays.ScreenCount == 0 && !_capstones.InUse && !_map.IsOpen
                && ActiveScreenContext.Instance.GetCurrentScreen() == _room;
        }

        private void TrackDetailInput(NCreature creature, Control control)
        {
            if (_detailInputs.ContainsKey(control)) return;
            Control.GuiInputEventHandler handler = input => OnEnemyDetailInput(creature, control, input);
            void OnTreeExiting()
            {
                if (_detailPress == control) CancelDetailPress();
                control.GuiInput -= handler;
                // A surviving node may re-enter; do not retain its previous exit callback.
                control.TreeExiting -= OnTreeExiting;
                _detailInputs.Remove(control);
            }
            _detailInputs.Add(control, (creature, handler, OnTreeExiting));
            control.GuiInput += handler;
            control.TreeExiting += OnTreeExiting;
        }

        private void OnEnemyDetailInput(NCreature creature, Control control, InputEvent input)
        {
            // Touch follows the same native mouse-emulation path as card play.
            // Never accept the press: the parent must still receive scroll gestures.
            if (input is InputEventMouseMotion motion)
            {
                if (_detailPress == control &&
                    (control.GetGlobalTransformWithCanvas() * motion.Position).DistanceTo(_detailPressPosition) > 24)
                    CancelDetailPress();
                return;
            }
            if (input is not InputEventMouseButton { ButtonIndex: MouseButton.Left } mouse) return;
            Vector2 position = control.GetGlobalTransformWithCanvas() * mouse.Position;
            if (mouse.Pressed)
            {
                CancelDetailPress();
                if (_readingCreature == null && _readingOrb == null && CanReadCreature(creature))
                {
                    _detailPress = control;
                    _detailPressPosition = position;
                }
                return;
            }
            bool open = _detailPress == control && position.DistanceTo(_detailPressPosition) <= 24
                && new Rect2(Vector2.Zero, control.Size).HasPoint(mouse.Position) && CanReadCreature(creature);
            CancelDetailPress();
            if (!open) return;
            int generation = _detailGeneration;
            // Finish native GUI dispatch before hiding or replacing any active tip.
            Callable.From(() =>
            {
                if (GodotObject.IsInstanceValid(_room) && _room.IsInsideTree()
                    && generation == _detailGeneration && CanReadCreature(creature)) OpenDetails(creature);
            }).CallDeferred();
        }

        private void CancelDetailPress()
        {
            _detailPress = null;
            _detailGeneration++;
        }

        public void HideEnemyTip(NCreature creature)
        {
            if (!PortraitViewportPatch.IsPortrait || !creature.Entity.IsEnemy || !_room.CreatureNodes.Contains(creature)) return;
            var tips = (Dictionary<Control, NHoverTipSet>)ActiveHoverTips.GetValue(null)!;
            if (!tips.TryGetValue(creature.Hitbox, out NHoverTipSet? tip)) return;
            if (!_hiddenEnemyTips.ContainsKey(tip))
            {
                _hiddenEnemyTips.Add(tip, (creature, tip.Visible));
                tip.TreeExiting += () => _hiddenEnemyTips.Remove(tip);
            }
            // Native focus, unfocus, state-refresh and owner-exit cleanup are intact.
            tip.Hide();
        }

        private void RestoreEnemyTips()
        {
            foreach (var pair in _hiddenEnemyTips)
                if (GodotObject.IsInstanceValid(pair.Key) && !pair.Key.IsQueuedForDeletion()
                    && GodotObject.IsInstanceValid(pair.Value.Creature))
                {
                    pair.Key.Visible = pair.Value.Visible;
                    pair.Key.SetAlignment(pair.Value.Creature.Hitbox, HoverTip.GetHoverTipAlignment(pair.Value.Creature, 0.5f));
                }
            _hiddenEnemyTips.Clear();
        }

        private void OpenDetails(NCreature? creature, NOrb? orb = null)
        {
            CloseDetails();
            // Both native owners generate localized, current descriptions. Reuse
            // this one reader; it never creates a replacement card or orb model.
            Control owner;
            if (orb != null)
            {
                owner = orb.Get("_bounds").As<Control>();
                // Native OnFocus uses Dictionary.Add; retire an existing hover
                // before asking that same owner to generate fresh current values.
                NHoverTipSet.Remove(owner);
                orb.Call("OnFocus");
            }
            else
            {
                creature!.ShowHoverTips(creature.Entity.HoverTips);
                owner = creature.Hitbox;
            }
            var tips = (Dictionary<Control, NHoverTipSet>)ActiveHoverTips.GetValue(null)!;
            if (!tips.TryGetValue(owner, out NHoverTipSet? tip)) return;
            VFlowContainer nativeText = tip.Get("_textHoverTipContainer").As<VFlowContainer>();
            NCard[] cards = Descendants(tip.Get("_cardHoverTipContainer").As<Node>()).OfType<NCard>().ToArray();
            if (nativeText.GetChildCount() == 0 && cards.Length == 0) return;
            Vector2 viewport = _room.GetViewportRect().Size;
            _detailRoot.Position = Vector2.Zero;
            _detailRoot.Size = viewport;
            _detailPanel.Position = new Vector2(48, 336);
            _detailPanel.Size = new Vector2(viewport.X - 96, viewport.Y - 552);
            _detailScroll.Size = _detailPanel.Size - new Vector2(48, 48);
            // Center the native Back while keeping the original close callback and modal input.
            _detailBack.Position = new Vector2((viewport.X - 264) * .5f, viewport.Y - 192);
            _detailBack.Size = new Vector2(264, 144);
            Label name = creature != null
                ? Descendants(creature).OfType<Label>().Single(node => node.Name == "NameplateLabel")
                : Descendants(nativeText.GetChild(0)).OfType<Label>().Single(node => node.Name == "Title");
            if (creature != null) AddDetailTitle(name, _detailContent, 54);
            _detailBack.Theme = name.Theme;
            _detailBack.AddThemeFontOverride("font", name.GetThemeFont("font"));
            foreach (Control block in nativeText.GetChildren().OfType<Control>())
            {
                Label title = Descendants(block).OfType<Label>().Single(node => node.Name == "Title");
                MegaRichTextLabel body = Descendants(block).OfType<MegaRichTextLabel>().Single(node => node.Name == "Description");
                TextureRect icon = Descendants(block).OfType<TextureRect>().Single(node => node.Name == "Icon");
                VBoxContainer section = new() { MouseFilter = Control.MouseFilterEnum.Pass, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                section.AddThemeConstantOverride("separation", 12);
                _detailContent.AddChild(section);
                HBoxContainer heading = new() { MouseFilter = Control.MouseFilterEnum.Pass };
                heading.AddThemeConstantOverride("separation", 12);
                section.AddChild(heading);
                if (icon.Texture != null)
                    heading.AddChild(new TextureRect
                    {
                        Texture = icon.Texture, Modulate = icon.Modulate, CustomMinimumSize = new Vector2(60, 60),
                        ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                        MouseFilter = Control.MouseFilterEnum.Pass,
                    });
                if (title.Visible && title.Text.Length > 0) AddDetailTitle(title, heading, 48);
                AddDetailBody(body, section);
            }
            foreach (NCard card in cards)
            {
                AddDetailTitle(card.Get("_titleLabel").As<Label>(), _detailContent, 48);
                foreach (string cost in new[] { "energy", "star" })
                    if (card.Get("_" + cost + "Icon").As<Control>().Visible)
                    {
                        Label source = card.Get("_" + cost + "Label").As<Label>();
                        Label value = AddDetailTitle(source, _detailContent, 48);
                        value.Text = (cost == "energy" ? "能量 " : "星能 ") + source.Text;
                    }
                AddDetailBody(card.Get("_descriptionLabel").As<MegaRichTextLabel>(), _detailContent);
            }
            _readingCreature = creature;
            _readingOrb = orb;
            // The copied reader survives pointer motion; the native owner remains
            // responsible for normal hover creation and removal outside reading.
            if (orb != null) NHoverTipSet.Remove(owner);
            _detailScroll.ScrollVertical = 0;
            _detailRoot.MoveToFront();
            _detailRoot.Show();
            QueueDetailFit();
        }

        private Label AddDetailTitle(Label source, Control parent, int fontSize)
        {
            Label label = new()
            {
                Text = source.Text, Theme = source.Theme, AutowrapMode = TextServer.AutowrapMode.WordSmart,
                Size = new Vector2(_detailScroll.Size.X, 1), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                MouseFilter = Control.MouseFilterEnum.Pass,
            };
            label.AddThemeFontOverride("font", source.GetThemeFont("font"));
            label.AddThemeFontSizeOverride("font_size", fontSize);
            label.AddThemeColorOverride("font_color", source.GetThemeColor("font_color"));
            parent.AddChild(label);
            return label;
        }

        private void AddDetailBody(MegaRichTextLabel source, Control parent)
        {
            MegaRichTextLabel body = new()
            {
                AutoSizeEnabled = false, BbcodeEnabled = true, FitContent = true, ScrollActive = false,
                AutowrapMode = TextServer.AutowrapMode.WordSmart, Theme = source.Theme,
                CustomEffects = source.CustomEffects.Duplicate(), Size = new Vector2(_detailScroll.Size.X, 1),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Pass,
            };
            foreach (string font in DetailFonts) body.AddThemeFontOverride(font, source.GetThemeFont(font));
            foreach (string font in DetailFontSizes) body.AddThemeFontSizeOverride(font, 48);
            foreach (string color in new[] { "default_color", "font_outline_color", "font_shadow_color" })
                body.AddThemeColorOverride(color, source.GetThemeColor(color));
            parent.AddChild(body);
            body.Text = source.Text;
        }

        private void QueueDetailFit()
        {
            if ((_readingCreature == null && _readingOrb == null) || _detailFitQueued) return;
            _detailFitQueued = true;
            Callable.From(() =>
            {
                _detailFitQueued = false;
                if (!GodotObject.IsInstanceValid(_room) || !_room.IsInsideTree()
                    || (_readingCreature == null && _readingOrb == null)) return;
                if (_readingCreature != null ? !CanReadCreature(_readingCreature) : !CanReadOrb(_readingOrb!))
                { CloseDetails(); return; }
                Vector2 viewport = _room.GetViewportRect().Size;
                float height = Math.Min(viewport.Y - 552, Math.Max(192, _detailContent.GetCombinedMinimumSize().Y + 48));
                Vector2 size = new(viewport.X - 96, height);
                Vector2 scrollSize = size - new Vector2(48, 48);
                // Let container width and FitContent settle; equal sizes never
                // enqueue another layout. No fixed content minimum clips the bar.
                if (_detailPanel.Size != size) _detailPanel.Size = size;
                if (_detailScroll.Size != scrollSize) _detailScroll.Size = scrollSize;
                _detailPanel.Position = new Vector2(48, viewport.Y - 216 - height);
            }).CallDeferred();
        }

        private void OnDetailsOutsideInput(InputEvent input)
        {
            if (input is InputEventMouseMotion motion && _outsidePressed
                && motion.Position.DistanceTo(_outsidePressPosition) > 24) _outsidePressed = false;
            if (input is not InputEventMouseButton { ButtonIndex: MouseButton.Left } mouse) return;
            if (mouse.Pressed)
            {
                _outsidePressed = !_detailPanel.GetRect().HasPoint(mouse.Position)
                    && !_detailBack.GetRect().HasPoint(mouse.Position);
                _outsidePressPosition = mouse.Position;
            }
            else
            {
                bool close = _outsidePressed && mouse.Position.DistanceTo(_outsidePressPosition) <= 24
                    && !_detailPanel.GetRect().HasPoint(mouse.Position) && !_detailBack.GetRect().HasPoint(mouse.Position);
                _outsidePressed = false;
                if (close) CloseDetails();
            }
        }

        public void CloseDetails()
        {
            CancelDetailPress();
            _outsidePressed = false;
            _readingCreature = null;
            _readingOrb = null;
            if (!GodotObject.IsInstanceValid(_detailRoot)) return;
            _detailRoot.Hide();
            foreach (Node child in _detailContent.GetChildren())
            {
                _detailContent.RemoveChild(child);
                child.QueueFree();
            }
        }

        private void OnDetailStateChanged(CombatState _) => CloseDetails();
        private void OnDetailCombatEnded(CombatRoom _) => CloseDetails();

        private void EnlargeAnnotations(NCreature creature)
        {
            // Expand only the existing display nodes. All numbers, status icons,
            // intent types and updates still come from the original game.
            foreach (Control control in Descendants(creature).OfType<Control>())
            {
                if (control.Name == "HpBarContainer")
                {
                    Remember(control);
                    control.Size = new Vector2(control.Size.X, 36);
                }
                else if (control.Name == "HpLabel" && control is Label)
                {
                    Font(control, 42);
                    Remember(control);
                    control.OffsetTop = -12;
                    control.OffsetBottom = 12;
                }
                else if (control.Name == "BlockLabel" && control is Label)
                {
                    Font(control, 42);
                    Remember(control);
                    control.OffsetLeft = -48;
                    control.OffsetRight = 48;
                }
                else if (control.Name == "PowerContainer")
                {
                    Remember(control);
                    control.Position = new Vector2(control.Position.X, 54);
                    // The native container caches its position for status changes.
                    // Reuse its public bounds entry rather than writing private state.
                    ((NPowerContainer)control).SetCreatureBounds(creature.Hitbox);
                }
                else if (control.Name == "NameplateContainer" && !creature.Entity.IsPlayer)
                {
                    Remember(control);
                    control.Position = new Vector2(control.Position.X, -84);
                    control.Size = new Vector2(control.Size.X, 60);
                    Label name = control.GetChildren().OfType<Label>().Single();
                    Font(name, 42);
                    Remember(name);
                    name.OffsetTop = 0;
                    name.OffsetBottom = 0;
                    Control background = control.GetChildren().OfType<TextureRect>().Single();
                    Remember(background);
                    background.OffsetTop = -6;
                    background.OffsetBottom = 6;
                }
            }
            foreach (Control intent in creature.IntentContainer.GetChildren().OfType<NIntent>())
            {
                Remember(intent);
                intent.CustomMinimumSize = new Vector2(96, 112);
                Control holder = intent.GetChildren().OfType<Control>().Single(node => node.Name == "IntentHolder");
                Remember(holder);
                // The native holder's floating animation changes Position only.
                // Its display scale increases the icon without touching hitboxes.
                holder.Scale = Vector2.One * 1.5f;
                foreach (RichTextLabel value in Descendants(intent).OfType<RichTextLabel>().Where(node => node.Name == "Value"))
                {
                    Font(value, 28);
                    Remember(value);
                    value.Position = new Vector2(-16, 42);
                    value.Size = new Vector2(96, 44);
                    if (!_alignments.ContainsKey(value))
                        _alignments.Add(value, value.HorizontalAlignment);
                    value.HorizontalAlignment = HorizontalAlignment.Center;
                }
            }
        }

        private void Font(Control control, int size)
        {
            string key = control is RichTextLabel ? "normal_font_size" : "font_size";
            if (!_fonts.ContainsKey(control))
                _fonts.Add(control, (control.HasThemeFontSizeOverride(key), control.GetThemeFontSize(key),
                    control is MegaLabel label ? label.AutoSizeEnabled : null));
            if (control is MegaLabel mega)
                mega.AutoSizeEnabled = false;
            control.AddThemeFontSizeOverride(key, size);
        }

        private void Remember(Control control)
        {
            if (!_annotations.ContainsKey(control))
                _annotations.Add(control, new PortraitControlSnapshot(control));
        }

        private void Place(Control control, Vector2 position, Vector2 size)
        {
            Remember(control);
            control.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
            control.Position = position;
            control.Size = size;
        }

        private void Saved(Node node, string name, Variant value)
        {
            if (!_properties.ContainsKey((node, name)))
                _properties.Add((node, name), node.Get(name));
            node.Set(name, value);
        }

        private void RestoreAnnotations()
        {
            // End callbacks before restoring visibility/mode and clearing snapshots.
            foreach (NCreature player in _idlePlayers.Keys.ToArray()) ReleaseIdlePlayer(player);
            // Recalculate native fill offsets once after restoring their width.
            // Merely restoring the container leaves damage measured in portrait units.
            NHealthBar[] resizedHealth = _annotations.Keys.OfType<NHealthBar>()
                .Where(GodotObject.IsInstanceValid).ToArray();
            foreach (PlayerStrip strip in _playerStrips.Values)
            {
                if (!GodotObject.IsInstanceValid(strip.Powers))
                    continue;
                if (strip.Powers.GetParent() != strip.PowerParent)
                {
                    strip.Powers.Reparent(strip.PowerParent, false);
                    strip.PowerParent.MoveChild(strip.Powers, strip.PowerIndex);
                }
                strip.Surface.Hide();
                strip.Avatar.Hide();
                strip.OstyIcon.Hide();
                strip.OstyBar.Hide();
                strip.Scroll.Hide();
            }
            // Restore original text minima before the authored control sizes.
            foreach (var pair in _fonts)
            {
                if (!GodotObject.IsInstanceValid(pair.Key))
                    continue;
                string key = pair.Key is RichTextLabel ? "normal_font_size" : "font_size";
                if (pair.Value.Had)
                    pair.Key.AddThemeFontSizeOverride(key, pair.Value.Font);
                else
                    pair.Key.RemoveThemeFontSizeOverride(key);
                if (pair.Key is MegaLabel mega && pair.Value.Auto.HasValue)
                    mega.AutoSizeEnabled = pair.Value.Auto.Value;
            }
            foreach (var pair in _annotations)
                if (GodotObject.IsInstanceValid(pair.Key))
                    pair.Value.Restore();
            foreach (var item in _properties)
                if (GodotObject.IsInstanceValid(item.Key.Node))
                    item.Key.Node.Set(item.Key.Name, item.Value);
            foreach (var pair in _alignments)
                if (GodotObject.IsInstanceValid(pair.Key))
                    pair.Key.HorizontalAlignment = pair.Value;
            // Restore the native status-position cache as well as its geometry.
            foreach (NCreature creature in _room.CreatureNodes.Where(GodotObject.IsInstanceValid))
                foreach (NPowerContainer powers in Descendants(creature).OfType<NPowerContainer>())
                    powers.SetCreatureBounds(creature.Hitbox);
            foreach (NHealthBar health in resizedHealth)
            {
                // Invalidate only the display cache so the original refresh also
                // updates middleground/forecast; combat HP and max HP are untouched.
                health.Set("_maxHpOnLastRefresh", -1);
                health.RefreshValues();
                if (health.Get("_middlegroundTween").AsGodotObject() is Tween tween &&
                    tween.IsValid() && tween.IsRunning())
                    tween.FastForwardToCompletion();
            }
            if (_orbManager != null && GodotObject.IsInstanceValid(_orbManager)
                && ((List<NOrb>)NativeOrbNodes.GetValue(_orbManager)!).Count > 0)
            {
                _orbManager.GetParent<NCreature>().Call("SetOrbManagerPosition");
                _orbManager.Call("TweenLayout");
            }
            _orbLayoutSize = Vector2.Zero;
            _annotations.Clear();
            _properties.Clear();
            _fonts.Clear();
            _alignments.Clear();
        }

        private static IEnumerable<Node> Descendants(Node node)
        {
            foreach (Node child in node.GetChildren())
            {
                yield return child;
                foreach (Node descendant in Descendants(child))
                    yield return descendant;
            }
        }
    }
}
