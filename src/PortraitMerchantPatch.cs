using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Events.Custom;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using MegaCrit.Sts2.addons.mega_text;
using STS2RitsuLib.Patching.Models;
using OpCodes = System.Reflection.Emit.OpCodes;

namespace STS2Portrait.Patches;

/// <summary>Wraps the native rug before any stock node enters the scene tree.</summary>
internal sealed class PortraitMerchantFactoryPatch : IPatchMethod
{
    public static string PatchId => "sts2portrait_merchant_factory";
    public static string Description => "Prepare the native merchant scroll before initialization";
    public static bool IsCritical => true;
    public static ModPatchTarget[] GetTargets() => new[]
    {
        PatchTarget.Method(typeof(NMerchantRoom), "Create"),
    };

    public static void Postfix(NMerchantRoom? __result)
    {
        if (!Entry.IsDisabled && __result != null)
            PortraitMerchantPatch.Prepare(__result);
    }
}

/// <summary>Reflows original stock without replacing purchase or removal models.</summary>
internal sealed class PortraitMerchantPatch : IPatchMethod
{
    public static string PatchId => "sts2portrait_merchant";
    public static string Description => "Arrange the native merchant for portrait touch input";
    public static bool IsCritical => true;
    public static ModPatchTarget[] GetTargets() => new[]
    {
        PatchTarget.Method(typeof(NMerchantRoom), "_Ready"),
        PatchTarget.Method(typeof(NMerchantRoom), "OnActiveScreenUpdated"),
        PatchTarget.Method(typeof(NFakeMerchant), "Initialize"),
        PatchTarget.Method(typeof(NFakeMerchant), "_Ready"),
        PatchTarget.Method(typeof(NFakeMerchant), "OnActiveScreenUpdated"),
        PatchTarget.Method(typeof(NFakeMerchantInventory), "UpdateNavigation"),
        PatchTarget.Method(typeof(NSpeechBubbleVfx), "_Ready"),
        PatchTarget.Method(typeof(NMerchantInventory), "Open"),
        PatchTarget.Method(typeof(NMerchantInventory), "Close"),
        PatchTarget.AsyncMethod(typeof(NMerchantInventory), "DoOpenAnimation"),
        PatchTarget.Method(typeof(NMerchantInventory), "OnActiveScreenUpdated"),
        PatchTarget.Method(typeof(NMerchantInventory), "UpdateNavigation"),
        PatchTarget.Method(typeof(NMerchantCard), "UpdateVisual"),
        PatchTarget.Method(typeof(NMerchantRelic), "UpdateVisual"),
        PatchTarget.Method(typeof(NMerchantPotion), "UpdateVisual"),
        PatchTarget.Method(typeof(NMerchantCardRemoval), "UpdateVisual"),
        PatchTarget.Method(typeof(NMerchantCard), "CreateHoverTip"),
        PatchTarget.Method(typeof(NMerchantRelic), "CreateHoverTip"),
        PatchTarget.Method(typeof(NMerchantPotion), "CreateHoverTip"),
        PatchTarget.Method(typeof(NMerchantCardRemoval), "CreateHoverTip"),
        PatchTarget.Method(typeof(NMerchantRelic), "OnSuccessfulPurchase"),
        PatchTarget.Method(typeof(NMerchantPotion), "OnSuccessfulPurchase"),
        PatchTarget.Method(typeof(NMerchantSlot), "OnPurchaseFailed"),
        PatchTarget.Method(typeof(NMerchantSlot), "OnFocus"),
        PatchTarget.Method(typeof(NMerchantSlot), "OnUnfocus"),
        PatchTarget.Method(typeof(NBackButton), "OnEnable"),
        PatchTarget.Method(typeof(NBackButton), "OnDisable"),
        PatchTarget.Method(typeof(NBackButton), "OnWindowChange"),
        PatchTarget.Method(typeof(NProceedButton), "OnEnable"),
        PatchTarget.Method(typeof(NProceedButton), "OnDisable"),
        PatchTarget.Method(typeof(NMerchantDialogue), "ShowRandom"),
    };

    private static readonly ConditionalWeakTable<Control, LayoutState> States = new();
    private static readonly Vector2 NativeHoverScale = (Vector2)AccessTools.Field(typeof(NMerchantSlot), "_hoverScale").GetValue(null)!;
    private static readonly Vector2 NativeRestingScale = (Vector2)AccessTools.Field(typeof(NMerchantSlot), "_smallScale").GetValue(null)!;
    private static readonly FieldInfo SlotHoverTween = AccessTools.Field(typeof(NMerchantSlot), "_hoverTween");
    private static readonly FieldInfo FailureOrigin = AccessTools.Field(typeof(NMerchantSlot), "_originalVisualPosition");
    private static readonly MethodInfo InitializeTips = AccessTools.Method(typeof(NHoverTipSet), "Init");
    private static readonly FieldInfo ActiveHoverTips = AccessTools.Field(typeof(NHoverTipSet), "_activeHoverTips");

    internal static void Prepare(NMerchantRoom room) => States.Add(room, new LayoutState(room));

    private static LayoutState? StateFor(Node node)
    {
        for (Node? parent = node; parent != null; parent = parent.GetParent())
            if (parent is Control owner && States.TryGetValue(owner, out LayoutState? state))
                return state;
        return null;
    }

    public static bool Prefix(object __instance, MethodBase __originalMethod)
    {
        if (__instance is not Node node || StateFor(node) is not LayoutState state || !state.Active)
            return true;
        if (node is NBackButton back && __originalMethod.Name is "OnEnable" or "OnDisable")
            state.SetBackDestinations(back);
        else if (node is NMerchantSlot slot && __originalMethod.Name == "OnPurchaseFailed")
        {
            // Native wiggle caches X once; refresh that geometric cache after rotation.
            Finish(slot.Get("_purchaseFailedTween").AsGodotObject() as Tween);
            FailureOrigin.SetValue(slot, slot.Get("Visual").As<CanvasItem>().Get("position").AsVector2().X);
        }
        else if (node is NMerchantRelic relic && __originalMethod.Name == "OnSuccessfulPurchase")
        {
            if (relic.Get("_relicNode").AsGodotObject() is NRelic icon)
                relic.Set("_relicNodePosition", icon.Icon.GlobalPosition);
        }
        else if (node is NMerchantPotion potion && __originalMethod.Name == "OnSuccessfulPurchase")
        {
            if (potion.Get("_potionNode").AsGodotObject() is Control icon)
                potion.Set("_potionNodePosition", icon.GlobalPosition);
        }
        else if (node is NMerchantInventory && __originalMethod.Name == "Close")
        {
            state.CancelPurchasePress();
            if (state.IsReading)
            {
                // The existing Back action first returns from reading to stock.
                state.CloseDetails();
                return false;
            }
        }
        return true;
    }

    public static void Postfix(object __instance, MethodBase __originalMethod)
    {
        // Custom events are initialized by EventModel.SetNode before AddChild.
        // Register this exact owner without borrowing the ordinary room lifecycle.
        if (!Entry.IsDisabled && __instance is NFakeMerchant fake && __originalMethod.Name == "Initialize")
            States.Add(fake, new LayoutState(fake));
        if (__instance is not Node node || StateFor(node) is not LayoutState state)
            return;
        if (node is NMerchantRoom or NFakeMerchant && __originalMethod.Name == "_Ready")
            state.Initialize();
        else if (node is NMerchantRoom or NFakeMerchant && __originalMethod.Name == "OnActiveScreenUpdated")
            state.Queue();
        else if (node is NMerchantSlot hovered && __originalMethod.Name == "CreateHoverTip" &&
            state.Active && !Entry.IsDisabled && PortraitViewportPatch.IsPortrait)
            RemoveStockHover(hovered);
        else if (node is NMerchantSlot slot && __originalMethod.Name == "UpdateVisual" && state.Active)
            state.LayoutSlot(slot);
        else if (node is NMerchantInventory && __originalMethod.Name == "UpdateNavigation" && state.Active)
            state.UpdateNavigation();
        else if (node is NMerchantInventory && __originalMethod.Name is "Open" or "Close")
            state.Queue();
        else if (node is NBackButton back && __originalMethod.Name == "OnWindowChange" && state.Active)
        {
            state.SetBackDestinations(back);
            back.GlobalPosition = back.Get(back.IsEnabled ? "_showPos" : "_hidePos").AsVector2();
        }
        else if (node is NMerchantDialogue dialogue && __originalMethod.Name == "ShowRandom" && state.Active)
            state.LayoutDialogue(dialogue);
        else if (node is NSpeechBubbleVfx && __originalMethod.Name == "_Ready" && state.Active)
            state.RepositionEventSpeech();
    }

    private static void RemoveStockHover(NMerchantSlot slot)
    {
        // Keep native Init/seen-state effects, then retire only this stock tip.
        // Hide synchronously because native Remove queues the scene for deletion.
        var tips = (Dictionary<Control, NHoverTipSet>)ActiveHoverTips.GetValue(null)!;
        if (tips.TryGetValue(slot, out NHoverTipSet? tip))
            tip.Hide();
        NHoverTipSet.Remove(slot);
    }

    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        List<CodeInstruction> body = instructions.ToList();
        Type? type = __originalMethod.DeclaringType;
        string name = __originalMethod.Name;
        if (type == typeof(NMerchantSlot) && name is "OnFocus" or "OnUnfocus")
        {
            FieldInfo field = AccessTools.Field(type, name == "OnFocus" ? "_hoverScale" : "_smallScale");
            int[] matches = body.Select((item, index) => (item, index))
                .Where(pair => pair.item.LoadsField(field)).Select(pair => pair.index).ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException($"Expected one merchant hover scale in {name}, found {matches.Length}.");
            body.InsertRange(matches[0] + 1, new[]
            {
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(PortraitMerchantPatch), nameof(SlotScale))),
            });
        }
        else if (type == typeof(NProceedButton) && name is "OnEnable" or "OnDisable")
        {
            // B18 already rewrites the position getter. Compose at the unchanged
            // Vector2 conversion, preserving both owners in either patch order.
            MethodInfo conversion = AccessTools.Method(typeof(Variant), "op_Implicit", new[] { typeof(Vector2) });
            int[] matches = body.Select((item, index) => (item, index))
                .Where(pair => pair.item.Calls(conversion)).Select(pair => pair.index).ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException($"Expected one proceed vector conversion in {name}, found {matches.Length}.");
            CodeInstruction owner = new(OpCodes.Ldarg_0);
            owner.labels.AddRange(body[matches[0]].labels);
            body[matches[0]].labels.Clear();
            body.InsertRange(matches[0], new[]
            {
                owner, new CodeInstruction(name == "OnDisable" ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(PortraitMerchantPatch), nameof(ProceedDestination))),
            });
        }
        else
        {
            bool opening = name == "MoveNext" && type != null && type.DeclaringType == typeof(NMerchantInventory)
                && type.Name.StartsWith("<DoOpenAnimation>", StringComparison.Ordinal);
            bool closing = type == typeof(NMerchantInventory) && name == "Close";
            bool pointing = type == typeof(NMerchantInventory) && name == "OnActiveScreenUpdated";
            if (!opening && !closing && !pointing)
                return body;
            float value = closing ? -1000 : 80;
            int[] matches = body.Select((item, index) => (item, index))
                .Where(pair => pair.item.opcode == OpCodes.Ldc_R4 && Equals(pair.item.operand, value))
                .Select(pair => pair.index).ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException($"Expected one merchant Y={value} in {type}.{name}, found {matches.Length}.");
            List<CodeInstruction> addition = new() { new CodeInstruction(OpCodes.Ldarg_0) };
            if (opening)
                addition.Add(new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(type!, "<>4__this")));
            addition.Add(new CodeInstruction(closing ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0));
            addition.Add(new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(PortraitMerchantPatch), nameof(RugDestination))));
            body.InsertRange(matches[0] + 1, addition);
        }
        return body;
    }

    private static Vector2 SlotScale(Vector2 native, NMerchantSlot slot) =>
        !Entry.IsDisabled && StateFor(slot) is { Active: true } ? Vector2.One : native;

    private static float RugDestination(float native, NMerchantInventory inventory, bool hidden) =>
        !Entry.IsDisabled && StateFor(inventory) is { Active: true } state ? (hidden ? -state.ContentHeight - 48 : 0) : native;

    private static Vector2 ProceedDestination(Vector2 native, NProceedButton button, bool hidden) =>
        !Entry.IsDisabled && StateFor(button) is { Active: true }
            ? new Vector2(button.GetViewportRect().Size.X - 432, button.GetViewportRect().Size.Y + (hidden ? 48 : -192)) : native;

    private static void Finish(Tween? tween)
    {
        if (tween != null && tween.IsValid() && tween.IsRunning())
            tween.FastForwardToCompletion();
    }

    private sealed class LayoutState
    {
        private readonly Control _room;
        private readonly bool _fakeMerchant;
        private NMerchantButton _merchantButton = null!;
        private readonly NMerchantInventory _inventory;
        private readonly Control _rug;
        private readonly ScrollContainer _scroll;
        private readonly Control _content;
        private readonly Dictionary<Control, PortraitControlSnapshot> _geometry = new();
        private readonly Dictionary<(GodotObject Node, string Name), Variant> _properties = new();
        private readonly Dictionary<(Control Node, string Name), (bool Had, int Size)> _fonts = new();
        private readonly Dictionary<NMerchantSlot, Panel> _surfaces = new();
        private readonly Dictionary<NMerchantSlot, Button> _previews = new();
        private readonly Dictionary<NButton, Panel> _buttonSurfaces = new();
        private Label _backLabel = null!;
        private NMerchantSlot[] _slots = Array.Empty<NMerchantSlot>();
        private Button _open = null!;
        private NBackButton _back = null!;
        private NProceedButton _proceed = null!;
        private Label _removalTitle = null!;
        private MegaRichTextLabel _removalBody = null!;
        private Panel _dialogueSurface = null!;
        // Only the ordinary room replaces its verified static environment draw.
        private TextureRect? _portraitBackdrop;
        private Node2D? _merchantEnvironmentSpine;
        private Callable _merchantEnvironmentFilter;
        private readonly Dictionary<GodotObject, Variant> _merchantEnvironmentAttachments = new();
        private MegaLabel _proceedLabel = null!;
        private Node _proceedLabelParent = null!;
        private int _proceedLabelIndex;
        private Vector2 _viewport;
        private bool _ready;
        private bool _queued;
        private bool _applying;
        private bool _previewDragged;
        private Label _purchaseHint = null!;
        private Panel _detailSurface = null!;
        private ScrollContainer _detailScroll = null!;
        private NHoverTipSet? _detailTips;
        private readonly Dictionary<Control, CardReading> _detailCards = new();
        private sealed record CardReading(VBoxContainer Text, Label Title, Label Cost, MegaRichTextLabel Body);

        public bool IsReading => _detailTips != null;

        public bool Active { get; private set; }
        public float ContentHeight { get; private set; }

        public LayoutState(Control room)
        {
            _room = room;
            _fakeMerchant = room is NFakeMerchant;
            if (room.IsInsideTree())
                throw new InvalidOperationException("Merchant scroll must be prepared before the room enters the tree.");
            _inventory = room.FindChildren("*", "", true, false).OfType<NMerchantInventory>().Single();
            _rug = _inventory.GetChildren().OfType<Control>().Single(node => node.Name == "SlotsContainer");
            int index = _rug.GetIndex();
            _scroll = new ScrollContainer
            {
                Name = "PortraitMerchantScroll", FollowFocus = true, ScrollDeadzone = 24,
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
                VerticalScrollMode = ScrollContainer.ScrollMode.Disabled,
                ClipContents = false, MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            _content = new Control { Name = "PortraitMerchantContent", MouseFilter = Control.MouseFilterEnum.Ignore };
            // The plain content retains the native rug tween's independent Y.
            _inventory.AddChild(_scroll);
            _inventory.MoveChild(_scroll, index);
            _scroll.AddChild(_content);
            _rug.Reparent(_content, false);
            _scroll.ScrollStarted += CancelPurchasePress;
        }

        public void Initialize()
        {
            _ready = true;
            _slots = _inventory.GetAllSlots().ToArray();
            _back = _inventory.Get("_backButton").As<NBackButton>();
            // Both known owners retain their native navigation and event actions.
            _proceed = _room is NMerchantRoom merchantRoom ? merchantRoom.ProceedButton : _room.Get("_proceedButton").As<NProceedButton>();
            _merchantButton = _room is NFakeMerchant fake ? fake.MerchantButton : ((NMerchantRoom)_room).MerchantButton;
            if (!_fakeMerchant)
            {
                Control scene = _room.GetChildren().OfType<Control>().Single(node => node.Name == "SceneContainer");
                Control background = scene.GetChildren().OfType<Control>().Single(node => node.Name == "BgContainer");
                _merchantEnvironmentSpine = background.GetChildren().OfType<Node2D>()
                    .Single(node => node.GetClass() == "SpineSprite" &&
                        node.Name.ToString().Contains("Spine", StringComparison.Ordinal));
                Texture2D texture = ResourceLoader.Load<Texture2D>("res://STS2Portrait/portrait/merchant-room-portrait-v1.png")
                    ?? throw new InvalidOperationException("Portrait merchant background is missing from the STS2Portrait resource pack.");
                _portraitBackdrop = new TextureRect
                {
                    Name = "PortraitMerchantBackdrop", Visible = false, Texture = texture,
                    ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                    StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
                    MouseFilter = Control.MouseFilterEnum.Ignore,
                };
                // Keep the live actor and prop scene above this passive full-page image.
                _room.AddChild(_portraitBackdrop);
                _room.MoveChild(_portraitBackdrop, scene.GetIndex());
                _merchantEnvironmentFilter = Callable.From<GodotObject>(_ => ClearMerchantBackground());
            }
            _proceedLabel = _proceed.Get("_label").As<MegaLabel>();
            _proceedLabelParent = _proceedLabel.GetParent();
            _proceedLabelIndex = _proceedLabel.GetIndex();
            MegaLabel priceSource = _slots[0].Get("_costLabel").As<MegaLabel>();
            NMerchantDialogue dialogue = _inventory.Get("_merchantDialogue").As<NMerchantDialogue>();
            MegaRichTextLabel richSource = dialogue.Get("_label").As<MegaRichTextLabel>();
            foreach (NMerchantSlot slot in _slots)
            {
                _surfaces.Add(slot, Surface(slot, "PortraitStockSurface", false));
                if (slot is NMerchantCard or NMerchantRelic or NMerchantPotion)
                {
                    Button preview = new()
                    {
                        Name = "PortraitStockPreview", Text = "查看", Visible = false,
                        MouseFilter = Control.MouseFilterEnum.Pass,
                    };
                    // Keep the native price font and outline legible without a permanent preview backdrop.
                    preview.AddThemeFontOverride("font", priceSource.GetThemeFont("font"));
                    preview.AddThemeFontSizeOverride("font_size", 48);
                    preview.AddThemeColorOverride("font_color", priceSource.GetThemeColor("font_color"));
                    preview.AddThemeColorOverride("font_outline_color", priceSource.GetThemeColor("font_outline_color"));
                    preview.AddThemeConstantOverride("outline_size", priceSource.GetThemeConstant("outline_size"));
                    preview.AddThemeStyleboxOverride("normal", new StyleBoxEmpty());
                    preview.AddThemeStyleboxOverride("disabled", new StyleBoxEmpty());
                    if (_fakeMerchant)
                    {
                        preview.AddThemeStyleboxOverride("hover", SurfaceStyle(true));
                        preview.AddThemeStyleboxOverride("pressed", SurfaceStyle(true));
                    }
                    else
                    {
                        // Reuse only the native eye artwork for the existing stock-reading action.
                        preview.Text = string.Empty;
                        preview.Icon = ResourceLoader.Load<Texture2D>("res://images/atlases/ui_atlas.sprites/peek_button.tres")
                            ?? throw new InvalidOperationException("Native eye icon failed to load for portrait shop inspection.");
                        preview.ExpandIcon = true;
                        preview.IconAlignment = HorizontalAlignment.Center;
                        preview.AddThemeConstantOverride("icon_max_width", 64);
                        preview.AddThemeColorOverride("icon_normal_color", new Color(0.9f, 0.9f, 0.9f));
                        preview.AddThemeColorOverride("icon_hover_color", Colors.White);
                        preview.AddThemeColorOverride("icon_focus_color", Colors.White);
                        preview.AddThemeColorOverride("icon_pressed_color", Colors.Gray);
                        preview.AddThemeColorOverride("icon_hover_pressed_color", Colors.Gray);
                        preview.AddThemeColorOverride("icon_disabled_color", new Color(1, 1, 1, 0.4f));
                        StyleBoxEmpty previewStyle = new();
                        foreach (string state in new[] { "hover", "pressed", "hover_pressed", "focus" })
                            preview.AddThemeStyleboxOverride(state, previewStyle);
                    }
                    preview.ButtonDown += () => _previewDragged = false;
                    preview.Pressed += () =>
                    {
                        if (!_previewDragged && Active && _inventory.IsOpen && !_inventory.Get("_isInputBlocked").AsBool() && slot.Entry.IsStocked)
                        {
                            if (slot is NMerchantCard)
                                slot.Call("OnPreview");
                            else
                                OpenDetails(slot);
                        }
                    };
                    slot.AddChild(preview);
                    _previews.Add(slot, preview);
                }
            }
            _purchaseHint = new Label
            {
                Name = "PortraitPurchaseHint",
                Text = _fakeMerchant ? "点商品购买 · 点「查看」读详情" : "点商品购买 · 点眼睛读详情",
                Visible = false,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            _purchaseHint.AddThemeFontOverride("font", priceSource.GetThemeFont("font"));
            _purchaseHint.AddThemeFontSizeOverride("font_size", 42);
            _inventory.AddChild(_purchaseHint);
            _detailSurface = Surface(_inventory, "PortraitMerchantReading", false);
            // Keep native input blocking above the reading state as well as stock.
            _inventory.MoveChild(_detailSurface, _inventory.Get("_inputBlocker").As<Control>().GetIndex());
            _detailScroll = new ScrollContainer
            {
                Name = "PortraitMerchantReadingScroll", MouseFilter = Control.MouseFilterEnum.Stop,
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
                VerticalScrollMode = ScrollContainer.ScrollMode.Auto, ScrollDeadzone = 24,
            };
            _detailSurface.AddChild(_detailScroll);
            // The original merchant button owns the upper-body Spine artwork.
            // Mirror its action instead of moving that artwork away from the lower body.
            _open = new Button { Name = "PortraitOpenMerchant", Text = "查看商品", Visible = false };
            _open.AddThemeFontOverride("font", priceSource.GetThemeFont("font"));
            _open.AddThemeFontSizeOverride("font_size", 48);
            _open.AddThemeStyleboxOverride("normal", SurfaceStyle(true));
            _open.AddThemeStyleboxOverride("hover", SurfaceStyle(true));
            _open.AddThemeStyleboxOverride("pressed", SurfaceStyle(false));
            _open.MouseEntered += () => { if (_merchantButton.IsEnabled) _merchantButton.Call("OnFocus"); };
            _open.MouseExited += () => _merchantButton.Call("OnUnfocus");
            _open.Pressed += () =>
            {
                if (Active && _merchantButton.Visible && _merchantButton.IsEnabled)
                    _merchantButton.Call("OnRelease");
            };
            _room.AddChild(_open);
            _room.MoveChild(_open, _inventory.GetIndex());
            foreach (NButton button in new NButton[] { _back, _proceed })
                // Both native navigation actions use the existing blue action paper.
                _buttonSurfaces.Add(button, Surface(button, "PortraitMerchantAction", true));
            _backLabel = new Label
            {
                Name = "PortraitBackLabel", Text = "返回", Visible = false,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            _backLabel.AddThemeFontOverride("font", priceSource.GetThemeFont("font"));
            _backLabel.AddThemeFontSizeOverride("font_size", 48);
            _back.AddChild(_backLabel);
            // The custom inventory has six relics and no removal service.
            if (!_fakeMerchant)
            {
                NMerchantCardRemoval removal = _slots.OfType<NMerchantCardRemoval>().Single();
                _removalTitle = new Label { Text = new LocString("merchant_room", "MERCHANT.cardRemovalService.title").GetFormattedText(), Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
                _removalTitle.AddThemeFontOverride("font", priceSource.GetThemeFont("font"));
                _removalTitle.AddThemeFontSizeOverride("font_size", 48);
                removal.AddChild(_removalTitle);
                LocString description = new("merchant_room", "MERCHANT.cardRemovalService.description");
                description.Add("Amount", MerchantCardRemovalEntry.PriceIncrease);
                _removalBody = new MegaRichTextLabel
                {
                    Name = "PortraitRemovalDescription", Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore,
                    AutoSizeEnabled = false, BbcodeEnabled = true, FitContent = true, ScrollActive = false,
                    Size = new Vector2(672, 1), Theme = richSource.Theme, CustomEffects = richSource.CustomEffects.Duplicate(),
                };
                // MegaRichTextLabel asserts native font overrides during Ready.
                foreach (string font in new[] { "normal_font", "bold_font", "italics_font", "bold_italics_font", "mono_font" })
                    _removalBody.AddThemeFontOverride(font, richSource.GetThemeFont(font));
                foreach (string font in RichFonts)
                    _removalBody.AddThemeFontSizeOverride(font, 48);
                removal.AddChild(_removalBody);
                _removalBody.Text = description.GetFormattedText();
                _removalBody.MinimumSizeChanged += Queue;
            }
            _dialogueSurface = Surface(dialogue.Get("_dialogueBox").As<Node2D>(), "PortraitMerchantSpeech", false);
            dialogue.Get("_label").As<MegaRichTextLabel>().MinimumSizeChanged += Queue;
            Window window = _room.GetWindow();
            window.SizeChanged += Queue;
            _room.Resized += Queue;
            _room.TreeExiting += () =>
            {
                window.SizeChanged -= Queue;
                RestoreMerchantBackground();
            };
            Apply();
        }

        public void Queue()
        {
            if (!_ready || _queued)
                return;
            _queued = true;
            Callable.From(() =>
            {
                _queued = false;
                if (GodotObject.IsInstanceValid(_room) && _room.IsInsideTree())
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
                Vector2 viewport = _room.GetViewportRect().Size;
                bool portrait = !Entry.IsDisabled && PortraitViewportPatch.IsPortrait;
                bool resized = _viewport != viewport || Active != portrait;
                if (resized)
                {
                    Finish(_inventory.Get("_inventoryTween").AsGodotObject() as Tween);
                    Finish(_back.Get("_moveTween").AsGodotObject() as Tween);
                    Finish(_back.Get("_hoverTween").AsGodotObject() as Tween);
                    Finish(_proceed.Get("_animTween").AsGodotObject() as Tween);
                    Finish(_proceed.Get("_hoverTween").AsGodotObject() as Tween);
                    foreach (NMerchantSlot slot in _slots)
                    {
                        // NMerchantCard has a separate field with the same name.
                        Finish(SlotHoverTween.GetValue(slot) as Tween);
                        Finish(slot.Get("_purchaseFailedTween").AsGodotObject() as Tween);
                    }
                }
                _viewport = viewport;
                if (!portrait)
                {
                    if (Active)
                        Restore();
                    Place(_scroll, Vector2.Zero, viewport, false);
                    _content.CustomMinimumSize = viewport;
                    _content.Size = viewport;
                    if (resized)
                    {
                        _rug.Position = new Vector2(_rug.Position.X, _inventory.IsOpen ? 80 : -1000);
                        _back.Call("OnWindowChange");
                        _proceed.Position = _proceed.Get(_proceed.IsEnabled ? "ShowPos" : "HidePos").AsVector2();
                    }
                    return;
                }
                if (!Active)
                {
                    // A stock tip may already be visible when rotating into portrait.
                    foreach (NMerchantSlot slot in _slots)
                        RemoveStockHover(slot);
                }
                Active = true;
                if (_fakeMerchant)
                {
                    // The native centered background and fixed foreground coincide only
                    // in the original design canvas. Map their shared scene together.
                    Control scene = _room.GetChildren().OfType<Control>().Single(node => node.Name == "SceneContainer");
                    Vector2 native = NGame.devResolution;
                    Vector2 artSize = new(viewport.X, viewport.Y - 312 - 384);
                    float artScale = Mathf.Max(artSize.X / native.X, artSize.Y / native.Y);
                    // Center the crop on the real merchant hitbox, preserving its
                    // position within the original scene and native potion targeting.
                    float focusX = (_merchantButton.AnchorLeft + _merchantButton.AnchorRight) * native.X * 0.5f
                        + (_merchantButton.OffsetLeft + _merchantButton.OffsetRight) * 0.5f;
                    float artX = Mathf.Clamp(viewport.X * 0.5f - focusX * artScale, viewport.X - native.X * artScale, 0);
                    Place(scene, new Vector2(artX, 312 + (artSize.Y - native.Y * artScale) * 0.5f), native);
                    scene.PivotOffset = Vector2.Zero;
                    scene.Scale = Vector2.One * artScale;
                    Saved(scene, "clip_contents", true);
                    RepositionEventSpeech();
                }
                else
                {
                    // The ordinary scene keeps its native actor, hitbox and VFX
                    // geometry; verified environment draws yield to the portrait page.
                    // Align the expanded floor with the unchanged native actor feet.
                    // Extra cover depth replaces the original horizontal crop, not actor geometry.
                    Place(_portraitBackdrop!, Vector2.Zero, viewport + new Vector2(0, 448), false);
                    _portraitBackdrop!.Show();
                    if (!_merchantEnvironmentSpine!.IsConnected("before_world_transforms_change", _merchantEnvironmentFilter))
                        _merchantEnvironmentSpine.Connect("before_world_transforms_change", _merchantEnvironmentFilter);
                }
                // Keep all stock positions visible; only item details scroll.
                ContentHeight = viewport.Y - 636;
                _content.CustomMinimumSize = new Vector2(984, ContentHeight);
                _content.Size = _content.CustomMinimumSize;
                _scroll.ScrollVertical = 0;
                _scroll.VerticalScrollMode = ScrollContainer.ScrollMode.Disabled;
                Place(_scroll, new Vector2(48, 396), new Vector2(984, ContentHeight), false);
                _scroll.ClipContents = true;
                // Closed content and its scrollbar must not cover the native opener.
                Saved(_scroll, "visible", _inventory.IsOpen && !IsReading);
                _scroll.MouseFilter = _inventory.IsOpen ? Control.MouseFilterEnum.Pass : Control.MouseFilterEnum.Ignore;
                _content.MouseFilter = Control.MouseFilterEnum.Pass;
                Saved(_rug, "self_modulate", Colors.Transparent);
                Saved(_rug, "mouse_filter", (int)Control.MouseFilterEnum.Pass);
                Saved(_inventory.MerchantHand.GetParent(), "visible", false);
                foreach (Control group in _rug.GetChildren().OfType<Control>().Where(node => node.Name == "CharacterCards" || node.Name == "ColorlessCards" || node.Name == "Relics" || node.Name == "Potions"))
                {
                    Place(group, Vector2.Zero, Vector2.Zero);
                    Saved(group, "mouse_filter", (int)Control.MouseFilterEnum.Pass);
                }
                foreach (NMerchantSlot slot in _slots)
                    LayoutSlot(slot);
                // Keep ongoing native open/close animation unless the coordinate system changed.
                float rugY = resized ? (_inventory.IsOpen ? 0 : -ContentHeight - 48) : _rug.Position.Y;
                Place(_rug, new Vector2(0, rugY), new Vector2(984, ContentHeight));
                LayoutActions(resized);
                Place(_purchaseHint, new Vector2(48, 324), new Vector2(984, 60), false);
                _purchaseHint.Visible = _inventory.IsOpen && !IsReading;
                _detailSurface.Visible = _inventory.IsOpen && IsReading;
                if (IsReading)
                    LayoutDetails();
                LayoutDialogue(_inventory.Get("_merchantDialogue").As<NMerchantDialogue>());
                UpdateNavigation();
            }
            finally { _applying = false; }
        }

        public void LayoutSlot(NMerchantSlot slot)
        {
            if (!Active || !_ready)
                return;
            // The card grid uses four columns. Six accessories share one row,
            // leaving removal and the native Back action visible on short screens.
            float accessoryHeight = _viewport.Y < 1600 ? 180 : 216;
            const float removalHeight = 144;
            float cardHeight = (ContentHeight - accessoryHeight - removalHeight - 36) / 2;
            float accessoryY = cardHeight * 2 + 24;
            int index;
            Vector2 size;
            Vector2 position;
            if (_fakeMerchant)
            {
                // Six native fake relics fill three columns and two complete rows.
                index = Array.IndexOf(_slots, slot);
                size = new Vector2(312, (ContentHeight - 24) / 2);
                position = new Vector2(index % 3 * 336, index / 3 * (size.Y + 24));
            }
            else if (slot is NMerchantCard)
            {
                index = Array.IndexOf(_slots.Where(item => item is NMerchantCard).ToArray(), slot);
                size = new Vector2(234, cardHeight);
                position = new Vector2(index % 4 * 250, index / 4 * (cardHeight + 12));
            }
            else if (slot is NMerchantRelic or NMerchantPotion)
            {
                index = Array.IndexOf(_slots.Where(item => item is NMerchantRelic or NMerchantPotion).ToArray(), slot);
                size = new Vector2(154, accessoryHeight);
                position = new Vector2(index * 166, accessoryY);
            }
            else
            {
                size = new Vector2(984, removalHeight);
                position = new Vector2(0, accessoryY + accessoryHeight + 12);
            }
            Place(slot, position, size);
            slot.Scale = Vector2.One;
            // Native stock artwork, price rows and hitboxes are siblings of this passive skin.
            // Keep the service backdrop while stock renders directly on the scene.
            bool cardStock = slot is NMerchantCard;
            Place(_surfaces[slot], cardStock ? new Vector2(0, size.Y - 136) : Vector2.Zero,
                cardStock ? new Vector2(size.X, 136) : size, false);
            _surfaces[slot].Visible = slot is not (NMerchantCard or NMerchantRelic or NMerchantPotion);
            bool removal = slot is NMerchantCardRemoval;
            Place(slot.Hitbox, new Vector2(8, 8), new Vector2(size.X - 16, removal ? size.Y - 16 : size.Y - (_fakeMerchant ? 120 : 88)));
            slot.Hitbox.Scale = Vector2.One;
            if (slot.Entry.IsStocked && !(removal && slot.Get("_isUnavailable").AsBool()))
                Saved(slot.Hitbox, "mouse_filter", (int)Control.MouseFilterEnum.Pass);
            MegaLabel cost = slot.Get("_costLabel").As<MegaLabel>();
            Control costContainer = (Control)cost.GetParent();
            // Release the old font minimum before narrowing the native price row.
            Font(cost, "font_size", _fakeMerchant ? 48 : 36);
            Place(costContainer, removal ? new Vector2(792, 48) : new Vector2(8, size.Y - (_fakeMerchant ? 188 : 128)),
                new Vector2(removal ? 168 : size.X - 16, _fakeMerchant ? 72 : 48));
            Saved(costContainer, "mouse_filter", (int)Control.MouseFilterEnum.Ignore);
            foreach (Control decoration in costContainer.GetChildren().OfType<Control>())
                Saved(decoration, "mouse_filter", (int)Control.MouseFilterEnum.Ignore);
            if (slot is NMerchantCard card)
            {
                float artHeight = size.Y - 144;
                // Use more of each stock column while preserving the complete card frame.
                float artScale = Math.Min(0.72f, artHeight / 454);
                float artY = 8 + artHeight / 2;
                float saleY = 32;
                if (!_fakeMerchant)
                {
                    // Group ordinary stock without changing the fake merchant's existing geometry.
                    float captionGap = Math.Min(20, size.Y - 454 * artScale - 128);
                    float artTop = Math.Max(8, (size.Y - 454 * artScale - captionGap - 120) / 2);
                    artY = artTop + 454 * artScale / 2;
                    // Follow the native sale tag's vertical offset from the card center.
                    saleY = artY - 153.154f * artScale;
                    Place(costContainer, new Vector2(8, artTop + 454 * artScale + captionGap), new Vector2(size.X - 16, 48));
                }
                Control holder = card.Get("_cardHolder").As<Control>();
                Place(holder, new Vector2(117, artY), Vector2.Zero);
                holder.Scale = Vector2.One * artScale;
                Saved(card.Get("_saleVisual").As<Node2D>(), "position", new Vector2(186, saleY));
                Saved(card.Get("_saleVisual").As<Node2D>(), "scale", Vector2.One * 0.5f);
            }
            else if (slot is NMerchantRelic relic)
            {
                Control holder = relic.Get("_relicHolder").As<Control>();
                if (_fakeMerchant)
                {
                    // Large fake-shop icons have a native upward price offset.
                    // Preserve their real size and restore that offset on rotation.
                    if (relic.Get("_relicNode").AsGodotObject() is NRelic nativeRelic)
                    {
                        float iconSize = Math.Min(180, size.Y - 208);
                        Saved(nativeRelic.Icon, "position", Vector2.Zero);
                        Place(holder, new Vector2((size.X - iconSize) / 2, 8 + (size.Y - 208 - iconSize) / 2), nativeRelic.Size);
                        holder.Scale = Vector2.One * (iconSize / nativeRelic.Icon.Size.X);
                    }
                }
                else
                {
                    float iconSize = Math.Min(72, size.Y - 136);
                    Place(holder, new Vector2((size.X - iconSize) / 2, 8), new Vector2(64, 64));
                    holder.Scale = Vector2.One * (iconSize / 64);
                }
            }
            else if (slot is NMerchantPotion potion)
            {
                Control holder = potion.Get("_potionHolder").As<Control>();
                float iconSize = Math.Min(72, size.Y - 136);
                Place(holder, new Vector2((size.X - iconSize) / 2, 8), new Vector2(40, 40));
                holder.PivotOffset = Vector2.Zero;
                holder.Scale = Vector2.One * (iconSize / 40);
            }
            else
            {
                Saved(slot.Get("_removalVisual").As<Sprite2D>(), "position", new Vector2(66, 72));
                // Native 470x592 removal frames and their 16px shadow fit this 144-high row.
                Saved(slot.Get("_removalVisual").As<Sprite2D>(), "scale", Vector2.One * 0.20f);
                _removalTitle.AddThemeFontSizeOverride("font_size", 36);
                Place(_removalTitle, new Vector2(144, 8), new Vector2(600, 48), false);
                foreach (string font in RichFonts)
                    Font(_removalBody, font, 28);
                Place(_removalBody, new Vector2(144, 60), new Vector2(612, 80), false);
                _removalTitle.Show();
                _removalBody.Show();
            }
            if (_previews.TryGetValue(slot, out Button? preview))
            {
                preview.AddThemeFontSizeOverride("font_size", _fakeMerchant ? 42 : 32);
                Place(preview, new Vector2(8, !_fakeMerchant && slot is NMerchantCard ? costContainer.Position.Y + 52 : size.Y - (_fakeMerchant ? 104 : 76)),
                    new Vector2(size.X - 16, _fakeMerchant ? 96 : 68), false);
                preview.Disabled = !slot.Entry.IsStocked;
                preview.Show();
            }
        }

        private void OpenDetails(NMerchantSlot slot)
        {
            IEnumerable<IHoverTip> source = slot.Entry switch
            {
                MerchantPotionEntry potion => potion.Model!.HoverTips,
                MerchantRelicEntry relic => relic.Model!.HoverTips,
                _ => throw new InvalidOperationException("Only merchant potions and relics use this reading view."),
            };
            CancelPurchasePress();
            CloseDetails();
            _detailSurface.Show();
            NHoverTipSet tips = PreloadManager.Cache.GetScene("res://scenes/ui/hover_tip_set.tscn")
                .Instantiate<NHoverTipSet>(PackedScene.GenEditState.Disabled);
            _detailTips = tips;
            _detailScroll.AddChild(tips);
            // Reuse native content construction and seen-state rules. This owned
            // reading scene is not registered as a transient global mouse hover.
            InitializeTips.Invoke(tips, new object[] { _detailSurface, source });
            foreach (Control control in tips.FindChildren("*", "Control", true, false).OfType<Control>())
                control.MouseFilter = Control.MouseFilterEnum.Ignore;
            VFlowContainer text = tips.Get("_textHoverTipContainer").As<VFlowContainer>();
            foreach (Control tip in text.GetChildren().OfType<Control>())
                tip.MinimumSizeChanged += Queue;
            NHoverTipCardContainer cards = tips.Get("_cardHoverTipContainer").As<NHoverTipCardContainer>();
            foreach (Control shell in cards.GetChildren().OfType<Control>())
            {
                NCard card = shell.GetChildren().OfType<NCard>().Single();
                MegaRichTextLabel nativeBody = card.Get("_descriptionLabel").As<MegaRichTextLabel>();
                Label nativeTitle = card.Get("_titleLabel").As<Label>();
                VBoxContainer reading = new()
                {
                    Name = "PortraitMerchantCardReading", MouseFilter = Control.MouseFilterEnum.Ignore,
                    CustomMinimumSize = new Vector2(560, 0), Size = new Vector2(560, 0),
                };
                reading.AddThemeConstantOverride("separation", 12);
                Label title = new()
                {
                    Theme = nativeTitle.Theme, MouseFilter = Control.MouseFilterEnum.Ignore,
                    AutowrapMode = TextServer.AutowrapMode.WordSmart,
                };
                title.AddThemeFontOverride("font", nativeTitle.GetThemeFont("font"));
                title.AddThemeFontSizeOverride("font_size", 48);
                Label cost = new()
                {
                    Theme = nativeTitle.Theme, MouseFilter = Control.MouseFilterEnum.Ignore,
                    AutowrapMode = TextServer.AutowrapMode.WordSmart,
                };
                cost.AddThemeFontOverride("font", nativeTitle.GetThemeFont("font"));
                cost.AddThemeFontSizeOverride("font_size", 42);
                MegaRichTextLabel body = new()
                {
                    Theme = nativeBody.Theme, AutoSizeEnabled = false, BbcodeEnabled = true,
                    FitContent = true, ScrollActive = false, AutowrapMode = TextServer.AutowrapMode.WordSmart,
                    MouseFilter = Control.MouseFilterEnum.Ignore,
                    CustomMinimumSize = new Vector2(560, 0), Size = new Vector2(560, 0),
                    CustomEffects = nativeBody.CustomEffects.Duplicate(),
                };
                // Native fonts are required before MegaRichTextLabel enters the tree.
                foreach (string font in new[] { "normal_font", "bold_font", "italics_font", "bold_italics_font", "mono_font" })
                    body.AddThemeFontOverride(font, nativeBody.GetThemeFont(font));
                foreach (string font in RichFonts)
                    body.AddThemeFontSizeOverride(font, 48);
                shell.AddChild(reading);
                reading.AddChild(title);
                reading.AddChild(cost);
                reading.AddChild(body);
                reading.MinimumSizeChanged += Queue;
                _detailCards.Add(shell, new CardReading(reading, title, cost, body));
            }
            _detailScroll.ScrollVertical = 0;
            _back.TryGrabFocus();
            Queue();
        }

        public void CloseDetails()
        {
            if (_detailTips == null)
                return;
            // Only this reading view's generated nodes are freed. Original stock
            // and its model/entry subscriptions never leave the scene tree.
            _detailTips.Hide();
            _detailTips.QueueFree();
            _detailTips = null;
            _detailCards.Clear();
            _detailSurface.Hide();
            Queue();
        }

        private void LayoutDetails()
        {
            NHoverTipSet tips = _detailTips!;
            // Reserve the native scrollbar width even when it is hidden, so height changes cannot rewrap text.
            float width = 984 - 48 - _detailScroll.GetVScrollBar().GetCombinedMinimumSize().X;
            VFlowContainer text = tips.Get("_textHoverTipContainer").As<VFlowContainer>();
            Control[] textTips = text.GetChildren().OfType<Control>().ToArray();
            foreach (Control tip in textTips)
            {
                tip.CustomMinimumSize = new Vector2(width, 0);
                foreach (MegaLabel title in tip.FindChildren("*", "Label", true, false).OfType<MegaLabel>())
                {
                    title.AutoSizeEnabled = false;
                    title.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                    title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                    if (title.GetThemeFontSize("font_size") != 48)
                        title.AddThemeFontSizeOverride("font_size", 48);
                }
                foreach (MegaRichTextLabel body in tip.FindChildren("*", "RichTextLabel", true, false).OfType<MegaRichTextLabel>())
                {
                    body.AutoSizeEnabled = false;
                    body.FitContent = true;
                    body.ScrollActive = false;
                    body.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                    body.CustomMinimumSize = new Vector2(width - 80, 0);
                    foreach (string font in RichFonts)
                        if (body.GetThemeFontSize(font) != 48)
                            body.AddThemeFontSizeOverride(font, 48);
                }
            }
            float textHeight = textTips.Sum(tip => tip.GetCombinedMinimumSize().Y) +
                Math.Max(0, textTips.Length - 1) * text.GetThemeConstant("v_separation");
            Place(text, Vector2.Zero, new Vector2(width, textHeight), false);
            text.CustomMinimumSize = new Vector2(width, textHeight);
            text.Alignment = FlowContainer.AlignmentMode.Begin;
            text.ReverseFill = false;
            NHoverTipCardContainer cards = tips.Get("_cardHoverTipContainer").As<NHoverTipCardContainer>();
            float cardHeight = 0;
            foreach (var item in _detailCards)
            {
                Control shell = item.Key;
                CardReading reading = item.Value;
                NCard card = shell.GetChildren().OfType<NCard>().Single();
                float readingWidth = width - 263;
                Place(reading.Text, new Vector2(263, 0), new Vector2(readingWidth, 0), false);
                reading.Text.CustomMinimumSize = new Vector2(readingWidth, 0);
                reading.Title.Size = new Vector2(readingWidth, 0);
                reading.Body.CustomMinimumSize = new Vector2(readingWidth, 0);
                string titleText = card.Get("_titleLabel").As<Label>().Text;
                if (reading.Title.Text != titleText)
                    reading.Title.Text = titleText;
                Label energy = card.Get("_energyLabel").As<Label>();
                Label star = card.Get("_starLabel").As<Label>();
                string costText = string.Join("    ", new[]
                {
                    card.Get("_energyIcon").As<Control>().Visible ? "能量 " + energy.Text : "",
                    card.Get("_starIcon").As<Control>().Visible ? "星能 " + star.Text : "",
                }.Where(value => value.Length > 0));
                if (reading.Cost.Text != costText)
                    reading.Cost.Text = costText;
                string bodyText = card.Get("_descriptionLabel").As<MegaRichTextLabel>().Text;
                if (reading.Body.Text != bodyText)
                    reading.Body.Text = bodyText;
                float rowHeight = Math.Max(323, reading.Text.GetCombinedMinimumSize().Y);
                Place(shell, new Vector2(0, cardHeight), new Vector2(width, rowHeight), false);
                reading.Text.QueueSort();
                cardHeight += rowHeight + 24;
            }
            if (cardHeight > 0)
                cardHeight -= 24;
            Place(cards, new Vector2(0, textHeight + (cardHeight > 0 ? 24 : 0)), new Vector2(width, cardHeight), false);
            cards.Scale = Vector2.One;
            float height = textHeight + (cardHeight > 0 ? 24 + cardHeight : 0);
            Place(tips, Vector2.Zero, new Vector2(width, height), false);
            tips.CustomMinimumSize = new Vector2(width, height);
            // Short descriptions sit near the existing Back row; long content retains native scrolling.
            float surfaceHeight = Math.Clamp(height + 48, 216, _viewport.Y - 576);
            Place(_detailSurface, new Vector2(48, _viewport.Y - 240 - surfaceHeight),
                new Vector2(984, surfaceHeight), false);
            Place(_detailScroll, new Vector2(24, 24), _detailSurface.Size - new Vector2(48, 48), false);
            text.QueueSort();
            _detailScroll.QueueSort();
        }

        public void SetBackDestinations(NBackButton back)
        {
            back.Set("_showPos", _inventory.GlobalPosition + new Vector2(48, _viewport.Y - 192));
            back.Set("_hidePos", _inventory.GlobalPosition + new Vector2(48, _viewport.Y + 48));
        }

        private void LayoutActions(bool reposition)
        {
            // Keep one native event-paper browse action above the original red proceed arrow.
            Place(_open, new Vector2((_viewport.X - 600) * 0.5f, _viewport.Y - 384), new Vector2(600, 128), false);
            // The event deliberately hides the merchant after its combat branch.
            _open.Visible = !_inventory.IsOpen && _merchantButton.Visible;
            _open.Disabled = !_merchantButton.IsEnabled;
            foreach (NButton button in new NButton[] { _back, _proceed })
            {
                Remember(button);
                // Restore the native Back paper and arrow with a thumb-sized
                // hit area; its existing Close/reading branch and tweens remain.
                bool nativeBack = button == _back;
                foreach (Control decoration in button.GetChildren().OfType<Control>().Where(node => node.Name == "HotkeyIcon" || node.Name == "Outline" || node.Name == "MerchantSelectionReticle"))
                    Saved(decoration, "visible", decoration.Name == "Outline");
                Saved(button.Get("_buttonImage").As<Control>(), "visible", true);
                Vector2 destination = button.Position;
                if (reposition)
                    destination = new Vector2(nativeBack ? 48 : _viewport.X - 432,
                        _viewport.Y + (button.IsEnabled ? -192 : 48));
                Place(button, destination, new Vector2(nativeBack ? 264 : 384, 144));
                if (!nativeBack)
                    button.PivotOffset = new Vector2(192, 72);
                Place(_buttonSurfaces[button], Vector2.Zero, button.Size, false);
                _buttonSurfaces[button].Hide();
                if (nativeBack)
                {
                    Place(_backLabel, Vector2.Zero, button.Size, false);
                    _backLabel.Text = IsReading ? "返回商品" : "返回";
                    _backLabel.Hide();
                }
            }
            SetBackDestinations(_back);
            Remember(_proceedLabel);
            // Native label stays on its original arrow image, with original material and feedback.
            if (_proceedLabel.GetParent() != _proceedLabelParent)
                _proceedLabel.Reparent(_proceedLabelParent, false);
            Place(_proceedLabel, Vector2.Zero, ((Control)_proceedLabelParent).Size);
            Font(_proceedLabel, "font_size", 48);
        }

        private static readonly string[] RichFonts = { "normal_font_size", "bold_font_size", "italics_font_size", "bold_italics_font_size", "mono_font_size" };

        public void LayoutDialogue(NMerchantDialogue dialogue)
        {
            Saved(dialogue, "position", new Vector2(48, 336));
            Saved(dialogue.Get("_bubble").As<Sprite2D>(), "visible", false);
            MegaRichTextLabel label = dialogue.Get("_label").As<MegaRichTextLabel>();
            foreach (string font in RichFonts)
                Font(label, font, 48);
            Saved(label, "fit_content", true);
            Saved(label, "scroll_active", false);
            float textHeight = Math.Max(96, label.GetCombinedMinimumSize().Y);
            Control margin = (Control)label.GetParent();
            Place(margin, Vector2.Zero, new Vector2(984, textHeight + 48));
            Saved(margin, "mouse_filter", (int)Control.MouseFilterEnum.Ignore);
            Place(label, new Vector2(24, 24), new Vector2(936, textHeight));
            Saved(label, "mouse_filter", (int)Control.MouseFilterEnum.Ignore);
            Place(_dialogueSurface, Vector2.Zero, new Vector2(984, label.Size.Y + 48), false);
            _dialogueSurface.Show();
        }

        public void CancelPurchasePress()
        {
            if (!Active)
                return;
            _previewDragged = true;
            foreach (NMerchantSlot slot in _slots)
            {
                // The next real native press resets this flag; ScrollEnded must not.
                slot.Set("_ignoreMouseRelease", true);
                slot.Call("ClearHoverTip");
            }
        }

        public void UpdateNavigation()
        {
            NMerchantSlot[] slots = _slots.Where(slot => slot.Visible && slot.Entry.IsStocked).ToArray();
            foreach (NMerchantSlot slot in slots)
            {
                foreach ((Vector2 direction, string property) in new[]
                {
                    (Vector2.Left, "focus_neighbor_left"), (Vector2.Right, "focus_neighbor_right"),
                    (Vector2.Up, "focus_neighbor_top"), (Vector2.Down, "focus_neighbor_bottom"),
                })
                {
                    NMerchantSlot? next = slots.Where(other => other != slot && (other.GlobalPosition - slot.GlobalPosition).Dot(direction) > 1)
                        .MinBy(other => (other.GlobalPosition - slot.GlobalPosition).LengthSquared());
                    slot.Set(property, (next ?? slot).GetPath());
                }
            }
        }

        public void RepositionEventSpeech()
        {
            if (!_fakeMerchant)
                return;
            // Native dialogue uses an unscaled left offset. Keep its original
            // bubble and lifetime, placing it beside the portrait merchant instead.
            bool portrait = Active && !Entry.IsDisabled && PortraitViewportPatch.IsPortrait;
            Vector2 position = _merchantButton.GlobalPosition;
            position.X += portrait ? _merchantButton.Size.X * _merchantButton.GetGlobalTransform().Scale.X * 0.5f
                : -_merchantButton.Size.X;
            // These native bubbles free themselves; never add them to snapshots.
            foreach (NSpeechBubbleVfx bubble in _merchantButton.GetParent().GetChildren().OfType<NSpeechBubbleVfx>())
                bubble.GlobalPosition = position;
        }

        private void ClearMerchantBackground()
        {
            if (!Active || Entry.IsDisabled || !PortraitViewportPatch.IsPortrait)
                return;
            // Native animation has applied before this signal. The verified bottom
            // skeleton's horizontal backdrop and cropped curtain yield to the portrait tent.
            if (_merchantEnvironmentAttachments.Count == 0)
            {
                GodotObject skeleton = _merchantEnvironmentSpine!.Call("get_skeleton").AsGodotObject();
                foreach (Variant value in skeleton.Call("get_slots").AsGodotArray())
                {
                    GodotObject slot = value.AsGodotObject();
                    string name = slot.Call("get_data").AsGodotObject().Call("get_name").AsString();
                    // Exact native 4.2.36 slots 0, 17 and 21; all 19 prop slots remain live.
                    if (name is "bg" or "purple_curtain" or "purple_curtain_highlights")
                        _merchantEnvironmentAttachments.Add(slot, slot.Call("get_attachment"));
                }
            }
            // Clear draw references only; native bone updates and signals continue.
            foreach (GodotObject slot in _merchantEnvironmentAttachments.Keys)
                slot.Call("set_attachment", default(Variant));
        }

        private void RestoreMerchantBackground()
        {
            // Disconnect before restoring the native reference on landscape or exit.
            if (_merchantEnvironmentSpine != null && GodotObject.IsInstanceValid(_merchantEnvironmentSpine) &&
                _merchantEnvironmentSpine.IsConnected("before_world_transforms_change", _merchantEnvironmentFilter))
                _merchantEnvironmentSpine.Disconnect("before_world_transforms_change", _merchantEnvironmentFilter);
            foreach (var entry in _merchantEnvironmentAttachments)
                if (GodotObject.IsInstanceValid(entry.Key))
                    entry.Key.Call("set_attachment", entry.Value);
            _merchantEnvironmentAttachments.Clear();
            _portraitBackdrop?.Hide();
        }

        private void Restore()
        {
            RestoreMerchantBackground();
            CloseDetails();
            _purchaseHint.Hide();
            CancelPurchasePress();
            Active = false;
            _scroll.ScrollVertical = 0;
            _scroll.VerticalScrollMode = ScrollContainer.ScrollMode.Disabled;
            _scroll.ClipContents = false;
            _scroll.MouseFilter = Control.MouseFilterEnum.Ignore;
            _content.MouseFilter = Control.MouseFilterEnum.Ignore;
            _open.Hide();
            foreach (Panel panel in _surfaces.Values.Concat(_buttonSurfaces.Values).Append(_dialogueSurface))
                panel.Hide();
            foreach (Button preview in _previews.Values)
                preview.Hide();
            _backLabel.Hide();
            if (!_fakeMerchant)
            {
                _removalTitle.Hide();
                _removalBody.Hide();
            }
            _proceedLabel.Reparent(_proceedLabelParent, false);
            _proceedLabelParent.MoveChild(_proceedLabel, _proceedLabelIndex);
            foreach (var entry in _properties.Reverse())
                if (GodotObject.IsInstanceValid(entry.Key.Node))
                    entry.Key.Node.Set(entry.Key.Name, entry.Value);
            foreach (var entry in _fonts)
                if (entry.Value.Had)
                    entry.Key.Node.AddThemeFontSizeOverride(entry.Key.Name, entry.Value.Size);
                else
                    entry.Key.Node.RemoveThemeFontSizeOverride(entry.Key.Name);
            foreach (PortraitControlSnapshot snapshot in _geometry.Values)
                snapshot.Restore();
            RepositionEventSpeech();
            foreach (NMerchantSlot slot in _slots)
            {
                FailureOrigin.SetValue(slot, null);
                slot.Scale = slot.Get("_isHovered").AsBool() ? NativeHoverScale : NativeRestingScale;
                // Do not revive a removal service that became unavailable in portrait.
                if (slot is NMerchantCardRemoval && slot.Get("_isUnavailable").AsBool())
                    slot.Hitbox.MouseFilter = Control.MouseFilterEnum.Ignore;
                if (!slot.Entry.IsStocked)
                    slot.MouseFilter = Control.MouseFilterEnum.Ignore;
            }
            _inventory.Call("UpdateNavigation");
        }

        private static Panel Surface(Node owner, string name, bool primary)
        {
            Panel panel = new() { Name = name, Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
            panel.AddThemeStyleboxOverride("panel", SurfaceStyle(primary));
            owner.AddChild(panel);
            owner.MoveChild(panel, 0);
            return panel;
        }

        // Reuse the original paper actions and tiled hover-tip frames without adding hitbox padding.
        private static StyleBoxTexture SurfaceStyle(bool primary) => new()
        {
            Texture = ResourceLoader.Load<Texture2D>(primary
                ? "res://images/packed/common_ui/event_button.png" : "res://images/ui/hover_tip.png"),
            TextureMarginLeft = primary ? 192 : 55, TextureMarginTop = primary ? 50 : 43,
            TextureMarginRight = primary ? 192 : 91, TextureMarginBottom = primary ? 50 : 32,
            AxisStretchHorizontal = primary ? StyleBoxTexture.AxisStretchMode.Stretch : StyleBoxTexture.AxisStretchMode.Tile,
            AxisStretchVertical = primary ? StyleBoxTexture.AxisStretchMode.Stretch : StyleBoxTexture.AxisStretchMode.Tile,
            ContentMarginLeft = 0, ContentMarginTop = 0,
            ContentMarginRight = 0, ContentMarginBottom = 0,
        };

        private void Remember(Control control)
        {
            if (!_geometry.ContainsKey(control))
                _geometry.Add(control, new PortraitControlSnapshot(control));
        }

        private void Place(Control control, Vector2 position, Vector2 size, bool remember = true)
        {
            if (remember)
                Remember(control);
            control.CustomMinimumSize = Vector2.Zero;
            control.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
            control.Position = position;
            control.Size = size;
        }

        private void Saved(GodotObject node, string name, Variant value)
        {
            if (!_properties.ContainsKey((node, name)))
                _properties.Add((node, name), node.Get(name));
            node.Set(name, value);
        }

        private void Font(Control node, string name, int size)
        {
            if (!_fonts.ContainsKey((node, name)))
                _fonts.Add((node, name), (node.HasThemeFontSizeOverride(name), node.GetThemeFontSize(name)));
            Saved(node, "AutoSizeEnabled", false);
            if (node.GetThemeFontSize(name) != size)
                node.AddThemeFontSizeOverride(name, size);
        }
    }
}
