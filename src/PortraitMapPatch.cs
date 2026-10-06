using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.ControllerInput;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.addons.mega_text;
using STS2RitsuLib.Patching.Models;
using OpCodes = System.Reflection.Emit.OpCodes;

namespace STS2Portrait.Patches;

/// <summary>Improves map presentation without replacing native scrolling or travel.</summary>
internal sealed class PortraitMapPatch : IPatchMethod
{
    public static string PatchId => "sts2portrait_map";
    public static string Description => "Present continuous map paper and accessible portrait map tools";
    public static bool IsCritical => true;
    public static ModPatchTarget[] GetTargets() => new[]
    {
        PatchTarget.Method(typeof(NMapScreen), "_Ready"),
        PatchTarget.Method(typeof(NMapScreen), "SetMap"),
        PatchTarget.Method(typeof(NMapScreen), "Open"),
        PatchTarget.Method(typeof(NMapScreen), "Close"),
        PatchTarget.Method(typeof(NMapScreen), "get_MapLegendX"),
        PatchTarget.Method(typeof(NMapScreen), "OnBackButtonPressed"),
        PatchTarget.Method(typeof(NMapScreen), "OnLegendHotkeyPressed"),
        PatchTarget.Method(typeof(NMapScreen), "OnDrawingToolsHotkeyPressed"),
        PatchTarget.Method(typeof(NMapScreen), "ProcessMouseEvent"),
        PatchTarget.Method(typeof(NMapScreen), "GetNetPositionFromScreenPosition"),
        PatchTarget.Method(typeof(NMouseModeMapDrawingInput), "_Input"),
        PatchTarget.Method(typeof(NMouseModeMapDrawingInput), "ProcessMouseDrawingEvent"),
        PatchTarget.Method(typeof(NMouseHeldMapDrawingInput), "_Ready"),
        PatchTarget.Method(typeof(NMouseHeldMapDrawingInput), "_Input"),
        PatchTarget.Method(typeof(NControllerMapDrawingInput), "_Process"),
        PatchTarget.Method(typeof(NMapScreen), "UpdateScrollPosition"),
        PatchTarget.Method(typeof(NMapScreen), "ProcessControllerEvent"),
        PatchTarget.Method(typeof(NMapScreen), "TryCancelStartOfActAnim"),
        PatchTarget.Method(typeof(NMapScreen), "IsNodeOnScreen"),
        PatchTarget.AsyncMethod(typeof(NMapScreen), "StartOfActAnim"),
        PatchTarget.Method(typeof(NMapPoint), "OnRelease"),
        PatchTarget.Method(typeof(NMapMarker), "SetMapPoint"),
        PatchTarget.Method(typeof(NAncientMapPoint), "_Process"),
    };

    private static readonly ConditionalWeakTable<NMapScreen, LayoutState> States = new();
    private static readonly FieldInfo MapNodes = AccessTools.Field(typeof(NMapScreen), "_mapPointDictionary");
    private static readonly FieldInfo MapRun = AccessTools.Field(typeof(NMapScreen), "_runState");

    public static bool Prefix(object __instance, MethodBase __originalMethod, object[] __args, out bool __state)
    {
        __state = false;
        if (Entry.IsDisabled)
            return true;
        if (__instance is NMapMarker marker && __originalMethod.Name == "SetMapPoint")
        {
            StateFor(marker)?.PrepareMarker((NMapPoint)__args[0]);
            return true;
        }
        if (__instance is NMouseModeMapDrawingInput drawing &&
            __originalMethod.Name == "ProcessMouseDrawingEvent" &&
            __args[0] is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } mouse)
        {
            LayoutState? owner = StateFor(drawing);
            // Drawing _Input runs before GUI buttons. Reject only a new press
            // over this map's visible controls; releases still stop native lines.
            return owner == null || !owner.PortraitMapVisible || !owner.BlocksDrawingStart(mouse.GlobalPosition);
        }
        if (__instance is NMapPoint point && __originalMethod.Name == "OnRelease")
        {
            LayoutState? owner = StateFor(point);
            // Visibility is an additional UI condition; the original method
            // still owns every travel, tutorial, vote and drawing restriction.
            return owner == null || !owner.PortraitMapVisible || owner.FullyVisible(point);
        }
        if (__instance is not NMapScreen screen || !States.TryGetValue(screen, out LayoutState? state))
            return true;
        switch (__originalMethod.Name)
        {
            case "SetMap":
                state.BeforeSetMap();
                break;
            case "Open":
                __state = !screen.IsOpen;
                // Set the back button's native tween destinations before Enable.
                state.Apply();
                if (__state)
                    state.Panels(false, false);
                break;
            case "TryCancelStartOfActAnim":
                __state = screen.Get("_actAnimTween").AsGodotObject() != null && screen.Get("_canInterruptAnim").AsBool();
                break;
            case "UpdateScrollPosition":
                if (state.PortraitGeometry)
                {
                    state.UpdatePan((double)__args[0]);
                    return false;
                }
                break;
            case "OnBackButtonPressed":
                if (state.ClosePanel())
                    return false;
                break;
            case "OnLegendHotkeyPressed":
                state.Panels(true, false);
                break;
            case "OnDrawingToolsHotkeyPressed":
                state.Panels(false, true);
                break;
        }
        return true;
    }

    public static void Postfix(object __instance, MethodBase __originalMethod, object[] __args, bool __state)
    {
        if (Entry.IsDisabled)
            return;
        if (__instance is NMapMarker marker)
        {
            StateFor(marker)?.RememberMarker((NMapPoint)__args[0]);
            return;
        }
        // Async targets are state-machine instances, not NMapScreen nodes.
        if (__instance is not NMapScreen screen)
            return;
        LayoutState state = States.GetValue(screen, value => new LayoutState(value));
        switch (__originalMethod.Name)
        {
            case "_Ready": state.Queue(); break;
            case "SetMap":
                // Open can be called in the same native stack: do not defer this.
                state.Apply();
                state.CenterCurrent(true);
                break;
            case "Open":
                if (__state && !(screen.Get("_actAnimTween").AsGodotObject() is Tween tween && tween.IsRunning()))
                    state.CenterCurrent(true);
                state.Queue();
                break;
            case "Close": state.Panels(false, false); break;
            case "ProcessControllerEvent": state.FollowController((InputEvent)__args[0]); break;
            case "TryCancelStartOfActAnim":
                if (__state)
                    state.CenterStart(false);
                break;
        }
    }

    private static LayoutState? StateFor(Node node)
    {
        for (Node? current = node; current != null; current = current.GetParent())
            if (current is NMapScreen screen)
                return States.TryGetValue(screen, out LayoutState? state) ? state : null;
        return null;
    }

    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        CodeInstruction[] body = instructions.ToArray();
        Type? owner = __originalMethod.DeclaringType;
        string name = __originalMethod.Name;
        if (owner == typeof(NAncientMapPoint) && name == "_Process")
        {
            MethodInfo getter = AccessTools.PropertyGetter(typeof(Control), nameof(Control.Scale));
            MethodInfo setter = AccessTools.PropertySetter(typeof(Control), nameof(Control.Scale));
            if (body.Count(instruction => instruction.Calls(getter)) != 1 ||
                body.Count(instruction => instruction.Calls(setter)) != 2)
                throw new InvalidOperationException("Expected one ancient pulse scale read and two writes.");
            // Keep the original pulse and lerp in their native scalar space.
            foreach (CodeInstruction instruction in body)
            {
                if (instruction.Calls(getter))
                    instruction.operand = AccessTools.Method(typeof(PortraitMapPatch), nameof(AncientNativeScale));
                else if (instruction.Calls(setter))
                    instruction.operand = AccessTools.Method(typeof(PortraitMapPatch), nameof(AncientDisplayScale));
            }
            return body;
        }
        int inverseCount = owner == typeof(NControllerMapDrawingInput) && name == "_Process" ? 2 :
            (owner == typeof(NMapScreen) && name is "ProcessMouseEvent" or "GetNetPositionFromScreenPosition") ||
            (owner == typeof(NMouseModeMapDrawingInput) && name is "_Input" or "ProcessMouseDrawingEvent") ||
            (owner == typeof(NMouseHeldMapDrawingInput) && name is "_Ready" or "_Input") ? 1 : 0;
        if (inverseCount != 0)
        {
            MethodInfo inverse = AccessTools.Method(typeof(Transform2D), nameof(Transform2D.Inverse));
            int actual = body.Count(instruction => instruction.Calls(inverse));
            if (actual != inverseCount)
                throw new InvalidOperationException($"Expected {inverseCount} map inverse calls in {owner!.Name}.{name}, found {actual}.");
            List<CodeInstruction> mapped = new();
            foreach (CodeInstruction instruction in body)
            {
                if (instruction.Calls(inverse))
                {
                    // Native IL has the Transform2D address on the stack.
                    // Add this map input's owner without changing event flow.
                    CodeInstruction load = new(OpCodes.Ldarg_0);
                    load.labels.AddRange(instruction.labels);
                    load.blocks.AddRange(instruction.blocks);
                    instruction.labels.Clear();
                    instruction.blocks.Clear();
                    mapped.Add(load);
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = AccessTools.Method(typeof(PortraitMapPatch), nameof(MapInputInverse));
                }
                mapped.Add(instruction);
            }
            return mapped;
        }
        if (__originalMethod.Name == "IsNodeOnScreen")
            return new[]
            {
                new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Ldarg_1),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(PortraitMapPatch), nameof(NodeOnScreen))),
                new CodeInstruction(OpCodes.Ret),
            };
        if (__originalMethod.Name == "MoveNext" && __originalMethod.DeclaringType!.Name.Contains("<StartOfActAnim>"))
            return MapAnimationEndpoints(body, __originalMethod.DeclaringType);
        if (__originalMethod.Name == "get_MapLegendX")
        {
            // Keep both native opening/closing tweens and their completion signals;
            // replace only their shared horizontal destination calculation.
            CodeInstruction factor = body.Single(instruction => instruction.opcode == OpCodes.Ldc_R4 &&
                instruction.operand is float value && value == 0.8f);
            factor.opcode = OpCodes.Call;
            factor.operand = AccessTools.Method(typeof(PortraitMapPatch), nameof(LegendFraction));
        }
        else if (__originalMethod.Name == "Open")
        {
            // Replace the caller's two endpoints as well: a previously compiled
            // Open can retain an inlined copy of the original tiny getter.
            MethodInfo getter = AccessTools.PropertyGetter(typeof(NMapScreen), "MapLegendX");
            CodeInstruction[] endpoints = body.Where(instruction => instruction.Calls(getter)).ToArray();
            if (endpoints.Length != 2)
                throw new InvalidOperationException($"Expected 2 map legend endpoints in Open, found {endpoints.Length}.");
            foreach (CodeInstruction endpoint in endpoints)
            {
                endpoint.opcode = OpCodes.Call;
                endpoint.operand = AccessTools.Method(typeof(PortraitMapPatch), nameof(LegendX));
            }
        }
        return body;
    }

    private static float LegendFraction() => !Entry.IsDisabled && PortraitViewportPatch.IsPortrait ? 216f / 1080f : 0.8f;
    private static float LegendX(NMapScreen screen) => screen.Size.X * LegendFraction();

    private static Transform2D MapInputInverse(ref Transform2D transform, Node owner)
    {
        // Inverse only transposes the basis; the portrait map has a scaled
        // basis and needs the affine inverse. Preserve native behavior elsewhere.
        return !Entry.IsDisabled && StateFor(owner) is { PortraitGeometry: true }
            ? transform.AffineInverse() : transform.Inverse();
    }

    private static Vector2 AncientNativeScale(NAncientMapPoint point) =>
        !Entry.IsDisabled && StateFor(point) is { PortraitGeometry: true } state
            ? point.Scale / state.PointScaleFactor : point.Scale;

    private static void AncientDisplayScale(NAncientMapPoint point, Vector2 value) =>
        point.Scale = !Entry.IsDisabled && StateFor(point) is { PortraitGeometry: true } state
            ? value * state.PointScaleFactor : value;

    private static bool NodeOnScreen(NMapScreen screen, NMapPoint point)
    {
        if (!Entry.IsDisabled && States.TryGetValue(screen, out LayoutState? state) && state.PortraitMapVisible)
            return state.FullyVisible(point);
        float y = point.GlobalPosition.Y;
        return y > 0 && y < screen.Size.Y;
    }

    private static Vector2 MapAnimationStart(NMapScreen screen) =>
        !Entry.IsDisabled && States.TryGetValue(screen, out LayoutState? state) && state.PortraitGeometry
            ? state.AnimationStart() : new Vector2(0, 1800);

    private static Vector2 MapAnimationEnd(NMapScreen screen) =>
        !Entry.IsDisabled && States.TryGetValue(screen, out LayoutState? state) && state.PortraitGeometry
            ? state.StartTarget() : new Vector2(0, -600);

    private static float MapAnimationEndY(NMapScreen screen) => MapAnimationEnd(screen).Y;

    private static IEnumerable<CodeInstruction> MapAnimationEndpoints(CodeInstruction[] body, Type stateMachine)
    {
        FieldInfo owner = stateMachine.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(field => field.FieldType == typeof(NMapScreen));
        ConstructorInfo vector = typeof(Vector2).GetConstructor(new[] { typeof(float), typeof(float) })!;
        List<CodeInstruction> result = new();
        int starts = 0, ends = 0, tweenEnds = 0;
        for (int i = 0; i < body.Length; i++)
        {
            bool localEndpoint = i + 3 < body.Length &&
                (body[i].opcode == OpCodes.Ldloca || body[i].opcode == OpCodes.Ldloca_S) &&
                body[i + 1].opcode == OpCodes.Ldc_R4 && body[i + 1].operand is float lx && lx == 0 &&
                body[i + 2].opcode == OpCodes.Ldc_R4 && body[i + 2].operand is float ly && ly == -600 &&
                body[i + 3].opcode == OpCodes.Call && Equals(body[i + 3].operand, vector);
            bool endpoint = i + 2 < body.Length && body[i].opcode == OpCodes.Ldc_R4 &&
                body[i].operand is float x && x == 0 && body[i + 1].opcode == OpCodes.Ldc_R4 &&
                body[i + 1].operand is float y && (y == 1800 || y == -600) &&
                body[i + 2].opcode == OpCodes.Newobj && Equals(body[i + 2].operand, vector);
            if (localEndpoint)
            {
                ends++;
                CodeInstruction load = new(OpCodes.Ldarg_0);
                load.labels.AddRange(body[i].labels);
                load.blocks.AddRange(body[i].blocks);
                result.Add(load);
                result.Add(new CodeInstruction(OpCodes.Ldfld, owner));
                result.Add(new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(PortraitMapPatch), nameof(MapAnimationEnd))));
                result.Add(new CodeInstruction(body[i].opcode == OpCodes.Ldloca_S ? OpCodes.Stloc_S : OpCodes.Stloc, body[i].operand));
                i += 3;
            }
            else if (endpoint)
            {
                bool start = (float)body[i + 1].operand == 1800;
                if (start) starts++; else ends++;
                CodeInstruction load = new(OpCodes.Ldarg_0);
                load.labels.AddRange(body[i].labels);
                load.blocks.AddRange(body[i].blocks);
                result.Add(load);
                result.Add(new CodeInstruction(OpCodes.Ldfld, owner));
                result.Add(new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(PortraitMapPatch),
                    start ? nameof(MapAnimationStart) : nameof(MapAnimationEnd))));
                i += 2;
            }
            else if (body[i].opcode == OpCodes.Ldc_R4 && body[i].operand is float value && value == -600)
            {
                tweenEnds++;
                CodeInstruction load = new(OpCodes.Ldarg_0);
                load.labels.AddRange(body[i].labels);
                load.blocks.AddRange(body[i].blocks);
                result.Add(load);
                result.Add(new CodeInstruction(OpCodes.Ldfld, owner));
                result.Add(new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(PortraitMapPatch), nameof(MapAnimationEndY))));
            }
            else result.Add(body[i]);
        }
        if (starts != 1 || ends != 1 || tweenEnds != 1)
            throw new InvalidOperationException($"Unexpected map animation endpoints: start={starts}, end={ends}, tween={tweenEnds}.");
        // Only endpoints change; delays, easing, interrupt callback and await stay native.
        return result;
    }

    private sealed class LayoutState
    {
        private readonly NMapScreen _screen;
        private readonly Control _map;
        private readonly Dictionary<MapCoord, NMapPoint> _nodes;
        private readonly Vector2 _nativeMapScale;
        private readonly Vector2 _nativeMapPivot;
        private readonly Vector2 _markerOffset;
        private readonly Dictionary<Control, MapGeometry> _mapGeometry = new();
        private readonly Dictionary<NNormalMapPoint, Vector2> _normalPivots = new();
        private NMapPoint? _markerPoint;
        private Rect2 _readingArea;
        private Vector2 _canvasSize;
        private Vector2 _panMin;
        private Vector2 _panMax;
        private bool _hasGeometry;
        public bool PortraitGeometry { get; private set; }
        public Vector2 PointScaleFactor => Vector2.One / _map.Scale;
        public bool PortraitMapVisible => PortraitGeometry && _screen.IsVisibleInTree();

        // Store geometry only. Node state, tint, icon pulse scale and travel
        // progress must never be restored from an earlier map snapshot.
        private readonly record struct MapGeometry(Control Node, float Left, float Top, float Right, float Bottom,
            float OffsetLeft, float OffsetTop, float OffsetRight, float OffsetBottom, Vector2 Pivot, Vector2? StaticScale)
        {
            public MapGeometry(Control node) : this(node, node.AnchorLeft, node.AnchorTop, node.AnchorRight, node.AnchorBottom,
                node.OffsetLeft, node.OffsetTop, node.OffsetRight, node.OffsetBottom, node.PivotOffset,
                node is NNormalMapPoint or NBossMapPoint ? node.Scale : null) { }

            public void Restore()
            {
                Node.AnchorLeft = Left; Node.AnchorTop = Top; Node.AnchorRight = Right; Node.AnchorBottom = Bottom;
                Node.OffsetLeft = OffsetLeft; Node.OffsetTop = OffsetTop; Node.OffsetRight = OffsetRight; Node.OffsetBottom = OffsetBottom;
                Node.PivotOffset = Pivot;
                // These two roots have no native scale animation. Icon pulse
                // and the ancient root pulse are restored independently.
                if (StaticScale is Vector2 scale)
                    Node.Scale = scale;
            }
        }
        private readonly NMapBg _background;
        private readonly Control _legend;
        private readonly VBoxContainer _legendItems;
        private readonly Control _tools;
        private readonly NMapShareButton _share;
        private readonly NBackButton _back;
        private readonly Vector2 _backOffset;
        private readonly ColorRect _paper;
        private readonly Panel _footer;
        private readonly Panel _legendSurface;
        private readonly Panel _backSurface;
        private readonly Label _backLabel;
        private readonly Button _legendButton;
        private readonly Button _toolsButton;
        private readonly Dictionary<Control, PortraitControlSnapshot> _original = new();
        private readonly Dictionary<(Control Control, string Name), Variant> _properties = new();
        private readonly Dictionary<Label, (bool Had, int Size, bool? Auto)> _fonts = new();
        private bool _active;
        private bool _queued;
        private float _height;

        public LayoutState(NMapScreen screen)
        {
            _screen = screen;
            _map = screen.Get("_mapContainer").As<Control>();
            _nodes = (Dictionary<MapCoord, NMapPoint>)MapNodes.GetValue(screen)!;
            _nativeMapScale = _map.Scale;
            _nativeMapPivot = _map.PivotOffset;
            _markerOffset = screen.Get("_marker").As<NMapMarker>().Get("_posOffset").AsVector2();
            _background = screen.Get("_mapBgContainer").As<NMapBg>();
            _legend = screen.Get("_mapLegend").As<Control>();
            _legendItems = screen.Get("_legendItems").As<VBoxContainer>();
            _tools = screen.Get("_drawingTools").As<Control>();
            _back = screen.Get("_backButton").As<NBackButton>();
            _share = screen.GetChildren().OfType<NMapShareButton>().Single();
            _backOffset = _back.Get("_posOffset").AsVector2();
            _paper = new ColorRect
            {
                Name = "PortraitMapPaper", Visible = false,
                Color = new Color(0.32f, 0.29f, 0.22f, 1f), MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            screen.AddChild(_paper);
            screen.MoveChild(_paper, 0);
            _paper.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            _footer = Surface(screen, "PortraitMapFooter", false);
            // Keep the footer's input shield while letting the underlying map background show through.
            _footer.AddThemeStyleboxOverride("panel", new StyleBoxEmpty());
            screen.MoveChild(_footer, _legend.GetIndex());
            _footer.MouseFilter = Control.MouseFilterEnum.Stop;
            _legendSurface = Surface(_legend, "PortraitLegendSurface", true);
            // The native TextureRect has no nine-slice data; retain its torn edges with art-based slices.
            _legendSurface.Material = _legend.Material;
            _legendSurface.AddThemeStyleboxOverride("panel", new StyleBoxTexture
            {
                Texture = ((TextureRect)_legend).Texture,
                TextureMarginLeft = 96, TextureMarginTop = 40, TextureMarginRight = 48, TextureMarginBottom = 64,
                AxisStretchHorizontal = StyleBoxTexture.AxisStretchMode.Stretch,
                AxisStretchVertical = StyleBoxTexture.AxisStretchMode.Stretch,
                ContentMarginLeft = 0, ContentMarginTop = 0, ContentMarginRight = 0, ContentMarginBottom = 0,
            });
            _legend.MoveChild(_legendSurface, 0);
            _legendSurface.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            _backSurface = Surface(_back, "PortraitMapBackSurface", false);
            _backSurface.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            _backLabel = new Label
            {
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            _backLabel.AddThemeFontSizeOverride("font_size", 48);
            _backSurface.AddChild(_backLabel);
            _backLabel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            _legendButton = Toggle("PortraitLegendButton");
            _toolsButton = Toggle("PortraitDrawingToolsButton");
            // Reuse the game's drawing icon rather than introducing an untranslated label.
            _toolsButton.Icon = _tools.FindChildren("*", "", true, false).OfType<NMapDrawButton>()
                .Single().GetChildren().OfType<TextureRect>().Single().Texture;
            _toolsButton.ExpandIcon = true;
            _legendButton.Pressed += () => Panels(!_legend.Visible, false);
            _toolsButton.Pressed += () => Panels(false, !_tools.Visible);
            Window window = screen.GetWindow();
            window.SizeChanged += Queue;
            screen.TreeExiting += () => window.SizeChanged -= Queue;
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
            bool portrait = PortraitViewportPatch.IsPortrait;
            Vector2I window = _screen.GetWindow().Size;
            float height = (float)Math.Round(1080.0 * window.Y / window.X);
            bool spaceChanged = _active != portrait || (_active && height != _height);
            if (spaceChanged)
            {
                // End the native gesture before changing its coordinate space.
                // This also covers portrait entry, which hides the tools below.
                StopDrawingMode();
                // A resized coordinate space invalidates finite tween endpoints.
                // Complete them normally, retaining their callbacks and signals.
                foreach (Tween? tween in new[]
                {
                    _screen.Get("_tween").AsGodotObject() as Tween,
                    _back.Get("_moveTween").AsGodotObject() as Tween,
                })
                    if (tween != null && tween.IsValid() && tween.IsRunning())
                        tween.FastForwardToCompletion();
            }
            ApplyMapGeometry(portrait);
            if (!portrait)
            {
                if (_active)
                    Restore();
                return;
            }
            bool entering = !_active;
            _active = true;
            _height = height;
            _paper.Show();
            _footer.Show();
            Place(_footer, new Vector2(0, height - 216), new Vector2(1080, 216));
            foreach (TextureRect sheet in _background.GetChildren().OfType<TextureRect>())
                Saved(sheet, "stretch_mode", (int)TextureRect.StretchModeEnum.KeepAspectCovered);
            // Points are centered on the canvas. Keep the paper's native offset
            // from that center (-1620 at height 1080), including the ancient node.
            Remember(_background);
            _background.Position = new Vector2(_background.Position.X, height * 0.5f - 2160f);
            _background.Get("_drawings").As<NMapDrawings>().RepositionBasedOnBackground(_background);

            // Keep the original six legend controls and their highlight signals.
            // Only their panel is collapsible; no map point or path is reparented.
            Remember(_legend);
            Saved(_legend, "expand_mode", (int)TextureRect.ExpandModeEnum.IgnoreSize);
            _legend.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
            // Fit the native six-row paper to its contents instead of a full-screen sheet.
            _legend.Size = new Vector2(648, 624);
            _legend.Position = new Vector2(spaceChanged ? 216 : _legend.Position.X, height - 864);
            Saved(_legend, "self_modulate", Colors.Transparent);
            Saved(_legend, "mouse_filter", (int)Control.MouseFilterEnum.Stop);
            _legendSurface.Show();
            Label header = _legend.GetChildren().OfType<Label>().Single();
            Place(header, new Vector2(24, 12), new Vector2(600, 96));
            Font(header, 48);
            _legendButton.Text = header.Text;
            Place(_legendItems, new Vector2(48, 108), new Vector2(552, 492));
            foreach (NMapLegendItem item in _legendItems.GetChildren().OfType<NMapLegendItem>())
            {
                Remember(item);
                item.CustomMinimumSize = new Vector2(552, 80);
                TextureRect icon = item.GetChildren().OfType<TextureRect>().Single();
                Place(icon, new Vector2(24, 8), new Vector2(64, 64));
                Label text = item.GetChildren().OfType<Label>().Single();
                Place(text, new Vector2(120, 0), new Vector2(408, 80));
                Font(text, 48);
            }

            Place(_tools, new Vector2(48, height - 396), new Vector2(984, 156));
            HBoxContainer toolRow = _tools.GetChildren().OfType<HBoxContainer>().Single();
            Place(toolRow, new Vector2(276, 6), new Vector2(432, 144));
            foreach (NButton button in toolRow.GetChildren().OfType<NButton>())
            {
                Remember(button);
                button.CustomMinimumSize = new Vector2(144, 144);
                foreach (TextureRect icon in button.GetChildren().OfType<TextureRect>())
                    Place(icon, new Vector2(24, 24), new Vector2(96, 96));
            }
            Place(_legendButton, new Vector2(336, height - 192), new Vector2(216, 144));
            Place(_toolsButton, new Vector2(576, height - 192), new Vector2(216, 144));
            _legendButton.Show();
            _toolsButton.Show();
            Place(_share, new Vector2(816, height - 192), new Vector2(216, 144));
            foreach (Label label in _share.FindChildren("*", "", true, false).OfType<Label>())
                Font(label, 42);
            Remember(_back);
            _back.Size = new Vector2(264, 144);
            _back.Set("_posOffset", new Vector2(-48, 192));
            _back.Set("_showPos", new Vector2(48, height - 192));
            _back.Set("_hidePos", new Vector2(-320, height - 192));
            if (spaceChanged)
                _back.Position = _back.Get("_isEnabled").AsBool() ? new Vector2(48, height - 192) : new Vector2(-320, height - 192);
            // Restore native Back artwork and its original hover/press animation owners.
            foreach (Control art in _back.GetChildren().OfType<Control>().Where(node => node != _backSurface))
                Saved(art, "visible", true);
            foreach (Control hotkey in _back.FindChildren("*", "", true, false).OfType<Control>().Where(node => node.Name == "HotkeyIcon"))
                Saved(hotkey, "visible", false);
            _backLabel.Text = new LocString("main_menu_ui", "MULTIPLAYER_WARNING_POPUP.back").GetFormattedText();
            _backSurface.Hide();
            // Native keyboard hotkeys can still open the panels through Prefix.
            if (entering)
            {
                Saved(_legend, "visible", false);
                Saved(_tools, "visible", false);
            }
        }

        public void BeforeSetMap()
        {
            FinishMapTweens();
            RestorePointGeometry();
            _mapGeometry.Clear();
            _normalPivots.Clear();
            _markerPoint = null;
            _map.Scale = _nativeMapScale;
            _map.PivotOffset = _nativeMapPivot;
            PortraitGeometry = false;
            _hasGeometry = false;
        }

        private void ApplyMapGeometry(bool portrait)
        {
            if (_nodes.Count == 0 || (_hasGeometry && PortraitGeometry == portrait && _canvasSize == _screen.Size))
                return;
            FinishMapTweens();
            Vector2 normalizedView = Vector2.Zero, normalizedTarget = Vector2.Zero;
            if (_hasGeometry)
            {
                Transform2D inverse = _map.GetTransform().AffineInverse();
                normalizedView = inverse * _readingArea.GetCenter() - _canvasSize * 0.5f;
                normalizedTarget = inverse * (_readingArea.GetCenter() -
                    (_screen.Get("_targetDragPos").AsVector2() - _map.Position)) - _canvasSize * 0.5f;
            }
            RestorePointGeometry();
            _map.Scale = _nativeMapScale;
            _map.PivotOffset = _nativeMapPivot;
            _readingArea = portrait ? new Rect2(48, 336, _screen.Size.X - 96, _screen.Size.Y - 576)
                : new Rect2(Vector2.Zero, _screen.Size);
            if (portrait)
            {
                NMapPoint[] points = _nodes.Values.ToArray();
                NNormalMapPoint[] normals = points.OfType<NNormalMapPoint>().ToArray();
                Dictionary<NMapPoint, Vector2> centers = points.ToDictionary(point => point, LocalCenter);
                Dictionary<NMapPoint, Vector2> targets = points.ToDictionary(point => point, point =>
                    point is NNormalMapPoint ? new Vector2(144, 144) :
                    point is NAncientMapPoint ? point.Size * 1.05f : point.Size * point.Scale);
                Vector2 scale = new(1.6f, 1.6f);
                for (int i = 0; i < points.Length; i++)
                    for (int j = i + 1; j < points.Length; j++)
                    {
                        Vector2 distance = (centers[points[i]] - centers[points[j]]).Abs();
                        Vector2 halfSizes = (targets[points[i]] + targets[points[j]]) * 0.5f;
                        if (distance.X > 0)
                            scale.X = Mathf.Min(scale.X, (_readingArea.Size.X - halfSizes.X) / distance.X);
                        // Native rows are separated before jitter. Stretch only Y
                        // if another floor would otherwise overlap a 144-high hit.
                        if (points[i].Point.coord.row != points[j].Point.coord.row)
                            scale.Y = Mathf.Max(scale.Y, halfSizes.Y / distance.Y);
                    }
                foreach (NMapPoint point in points)
                {
                    SaveMapGeometry(point);
                    // Cancel the parent's axis ratio at the real node root.
                    // Native icon rotations, pulses, votes and circles stay round.
                    point.Scale /= scale;
                }
                foreach (NNormalMapPoint normal in normals)
                {
                    float width = 144;
                    foreach (NNormalMapPoint other in normals)
                        if (other != normal && other.Point.coord.row == normal.Point.coord.row)
                            width = Mathf.Min(width, scale.X * Mathf.Abs(centers[normal].X - centers[other].X));
                    // The installed seven-column generators imply width >=96.3
                    // at 984 content width. Report any map outside that contract.
                    if (width < 96)
                        GD.PushWarning($"Map node {normal.Point.coord} has a {width:F2}-wide portrait hit area; native seven-column bounds were exceeded.");
                    Vector2 oldSize = normal.Size;
                    Vector2 center = normal.Position + oldSize * 0.5f;
                    Vector2 oldPivot = normal.PivotOffset;
                    _normalPivots.TryAdd(normal, oldPivot);
                    Control icon = normal.Get("_iconContainer").As<Control>();
                    SaveMapGeometry(icon);
                    SaveMapGeometry(normal.VoteContainer);
                    normal.Size = new Vector2(width, 144);
                    normal.PivotOffset = normal.Size * 0.5f;
                    normal.Position = center - normal.Size * 0.5f;
                    icon.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
                    icon.Position = (normal.Size - oldSize) * 0.5f;
                    icon.Size = oldSize;
                    normal.VoteContainer.Position += normal.PivotOffset - oldPivot;
                    MoveCenteredVfx(normal, normal.PivotOffset - oldPivot);
                }
                // Paths and the drawing texture keep one affine parent transform.
                // No point center, route, drawing buffer or saved coordinate moves.
                _map.PivotOffset = Vector2.Zero;
                _map.Scale = scale;
                NMapMarker marker = _screen.Get("_marker").As<NMapMarker>();
                SaveMapGeometry(marker);
                // Resize the texture rectangle, leaving native scale tweens intact.
                Saved(marker, "expand_mode", (int)TextureRect.ExpandModeEnum.IgnoreSize);
                Saved(marker, "stretch_mode", (int)TextureRect.StretchModeEnum.Scale);
                marker.Size /= scale;
                marker.PivotOffset /= scale;
            }
            PortraitGeometry = portrait;
            Vector2 newCanvas = _screen.Size;
            if (_hasGeometry)
            {
                _map.Position += _readingArea.GetCenter() -
                    _map.GetTransform() * (normalizedView + newCanvas * 0.5f);
                _screen.Set("_targetDragPos", _map.Position + _readingArea.GetCenter() -
                    _map.GetTransform() * (normalizedTarget + newCanvas * 0.5f));
            }
            _canvasSize = newCanvas;
            bool firstMap = !_hasGeometry;
            _hasGeometry = true;
            if (portrait)
            {
                ComputePanBounds();
                ValidateHitAreas();
                if (firstMap)
                    CenterCurrent(true);
                else
                {
                    _map.Position = ClampPan(_map.Position);
                    _screen.Set("_targetDragPos", ClampPan(_screen.Get("_targetDragPos").AsVector2()));
                }
            }
            RepositionMarker();
        }

        private void SaveMapGeometry(Control control)
        {
            if (!_mapGeometry.ContainsKey(control))
                _mapGeometry.Add(control, new MapGeometry(control));
        }

        private void RestorePointGeometry()
        {
            if (PortraitGeometry)
                foreach (NAncientMapPoint ancient in _nodes.Values.OfType<NAncientMapPoint>())
                    ancient.Scale /= PointScaleFactor;
            foreach (var item in _normalPivots)
                if (GodotObject.IsInstanceValid(item.Key))
                    MoveCenteredVfx(item.Key, item.Value - item.Key.PivotOffset);
            NMapMarker marker = _screen.Get("_marker").As<NMapMarker>();
            TextureRect.ExpandModeEnum markerExpand = marker.ExpandMode;
            // FitWidth must not turn intermediate restored heights into a
            // persistent minimum width while the four offsets change in order.
            marker.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;
            foreach (MapGeometry geometry in _mapGeometry.Values)
                if (GodotObject.IsInstanceValid(geometry.Node))
                    geometry.Restore();
            marker.ExpandMode = markerExpand;
        }

        private static void MoveCenteredVfx(NNormalMapPoint point, Vector2 delta)
        {
            // Covers both existing circles and effects created after portrait
            // entry using the then-current native PivotOffset.
            foreach (Node child in point.GetChildren())
                if (child is NMapCircleVfx or NMapNodeSelectVfx)
                    child.Set("position", child.Get("position").AsVector2() + delta);
        }

        private Vector2 LocalCenter(NMapPoint point) =>
            _map.GetGlobalTransform().AffineInverse() * point.GetGlobalRect().GetCenter();

        private Vector2 TargetFor(NMapPoint point, bool showAhead = false)
        {
            Vector2 focus = _readingArea.GetCenter();
            if (showAhead)
                // Keep the current room near the foot of the map, with future routes above.
                // Leave room for the real hit rectangle and keep oversized nodes centered.
                focus.Y = Mathf.Max(focus.Y, _readingArea.End.Y - Mathf.Max(144, point.GetGlobalRect().Size.Y * 0.5f + 48));
            return _map.Position + focus - _map.GetTransform() * LocalCenter(point);
        }

        private NMapPoint CurrentPoint()
        {
            RunState run = (RunState)MapRun.GetValue(_screen)!;
            return run.CurrentMapCoord is MapCoord coordinate && _nodes.TryGetValue(coordinate, out NMapPoint? current)
                ? current : _screen.Get("_startingPointNode").As<NMapPoint>();
        }

        private void ComputePanBounds()
        {
            Vector2[] targets = _nodes.Values.Select(point => TargetFor(point)).ToArray();
            _panMin = new Vector2(targets.Min(value => value.X), targets.Min(value => value.Y));
            // Retain the original centered search range and allow the lower current-room framing.
            _panMax = new Vector2(targets.Max(value => value.X), _nodes.Values.Max(point => TargetFor(point, true).Y));
            Rect2 extent = _nodes.Values.Select(point => point.GetGlobalRect()).Aggregate((a, b) => a.Merge(b));
            Vector2 centered = _map.Position + _screen.GlobalPosition + _readingArea.GetCenter() - extent.GetCenter();
            // Every route fits horizontally; only the vertical camera can move.
            _panMin.X = _panMax.X = centered.X;
            if (extent.Size.Y <= _readingArea.Size.Y)
                _panMin.Y = _panMax.Y = centered.Y;
        }

        private Vector2 ClampPan(Vector2 value) => new(
            Mathf.Clamp(value.X, _panMin.X, _panMax.X), Mathf.Clamp(value.Y, _panMin.Y, _panMax.Y));

        private void ValidateHitAreas()
        {
            NMapPoint[] nodes = _nodes.Values.ToArray();
            for (int i = 0; i < nodes.Length; i++)
            {
                Rect2 rect = nodes[i].GetGlobalRect();
                if (rect.Size.X > _readingArea.Size.X || rect.Size.Y > _readingArea.Size.Y)
                    throw new InvalidOperationException($"Map node {nodes[i].Point.coord} cannot fit the portrait reading area: {rect.Size}.");
                if (nodes[i] is NNormalMapPoint &&
                    (rect.Size.X <= 0 || rect.Size.X > 144.02f || Mathf.Abs(rect.Size.Y - 144) > 0.02f))
                    throw new InvalidOperationException($"Unexpected map hit area for {nodes[i].Point.coord}: {rect.Size}.");
                for (int j = 0; j < i; j++)
                    if (rect.Grow(-0.01f).Intersects(nodes[j].GetGlobalRect().Grow(-0.01f)))
                        throw new InvalidOperationException($"Portrait map hit areas overlap: {nodes[i].Point.coord} and {nodes[j].Point.coord}.");
            }
        }

        public bool FullyVisible(NMapPoint point) =>
            new Rect2(_screen.GlobalPosition + _readingArea.Position, _readingArea.Size).Encloses(point.GetGlobalRect());

        public bool BlocksDrawingStart(Vector2 globalPoint) =>
            (_tools.IsVisibleInTree() && _tools.GetGlobalRect().HasPoint(globalPoint)) ||
            (_footer.IsVisibleInTree() && _footer.GetGlobalRect().HasPoint(globalPoint)) ||
            (_legend.IsVisibleInTree() && _legend.GetGlobalRect().HasPoint(globalPoint));

        public void UpdatePan(double delta)
        {
            // Invoked by the original _Process, retaining its visibility,
            // start-of-act tween and share-screenshot guards.
            Vector2 target = _screen.Get("_targetDragPos").AsVector2();
            _map.Position = _map.Position.Lerp(target, Mathf.Min((float)delta * 15, 1));
            if (_map.Position.DistanceSquaredTo(target) < 0.25f)
                _map.Position = target;
            if (!_screen.Get("_isDragging").AsBool())
                _screen.Set("_targetDragPos", target.Lerp(ClampPan(target), Mathf.Min((float)delta * 12, 1)));
            // The native map _Process runs under the live NGame instance.
            NGame.Instance!.RemoteCursorContainer.ForceUpdateAllCursors();
        }

        public void CenterCurrent(bool immediate)
        {
            if (!PortraitGeometry)
                return;
            Vector2 target = ClampPan(TargetFor(CurrentPoint(), true));
            _screen.Set("_targetDragPos", target);
            if (immediate)
                _map.Position = target;
        }

        public Vector2 StartTarget() => ClampPan(TargetFor(_screen.Get("_startingPointNode").As<NMapPoint>(), true));

        public Vector2 AnimationStart()
        {
            NMapPoint top = _nodes.Values.Where(point => point is NBossMapPoint).MinBy(point => LocalCenter(point).Y)!;
            Vector2 target = TargetFor(top);
            target.X = StartTarget().X;
            return ClampPan(target);
        }

        public void CenterStart(bool immediate)
        {
            if (!PortraitGeometry)
                return;
            Vector2 target = StartTarget();
            _screen.Set("_targetDragPos", target);
            if (immediate)
                _map.Position = target;
        }

        public void FollowController(InputEvent input)
        {
            if (!PortraitGeometry || !(input.IsActionPressed(MegaInput.left) ||
                input.IsActionPressed(MegaInput.right) || input.IsActionPressed(MegaInput.select)))
                return;
            // Godot applies directional focus after _Input. Read the resulting
            // real node on the deferred turn instead of centering the old focus.
            Callable.From(() =>
            {
                if (!GodotObject.IsInstanceValid(_screen) || !PortraitMapVisible)
                    return;
                NMapPoint point = _screen.GetViewport().GuiGetFocusOwner() is NMapPoint focused && _nodes.ContainsValue(focused)
                    ? focused : CurrentPoint();
                _screen.Set("_targetDragPos", ClampPan(TargetFor(point)));
            }).CallDeferred();
        }

        public void RememberMarker(NMapPoint point) => _markerPoint = point;

        public void PrepareMarker(NMapPoint point)
        {
            if (!PortraitGeometry)
                return;
            NMapMarker marker = _screen.Get("_marker").As<NMapMarker>();
            Rect2 rect = point.GetGlobalRect();
            Vector2 top = _map.GetGlobalTransform().AffineInverse() *
                new Vector2(rect.GetCenter().X, rect.Position.Y);
            Vector2 nativeTop = point.Position + new Vector2(point.Size.X * 0.5f, 0);
            // The original marker method still creates and completes its tween.
            Saved(marker, "_posOffset", _markerOffset * PointScaleFactor + top - nativeTop);
        }

        private void RepositionMarker()
        {
            NMapMarker marker = _screen.Get("_marker").As<NMapMarker>();
            if (!marker.Visible)
                return;
            NMapPoint point = GodotObject.IsInstanceValid(_markerPoint) ? _markerPoint! : CurrentPoint();
            if (PortraitGeometry)
                PrepareMarker(point);
            else
                marker.Set("_posOffset", _markerOffset);
            marker.Position = point.Position + new Vector2(point.Size.X * 0.5f, 0) + marker.Get("_posOffset").AsVector2();
        }

        private void FinishMapTweens()
        {
            foreach (Tween? tween in new[]
            {
                _screen.Get("_actAnimTween").AsGodotObject() as Tween,
                _screen.Get("_tween").AsGodotObject() as Tween,
                _screen.Get("_marker").As<NMapMarker>().Get("_tween").AsGodotObject() as Tween,
            })
                if (tween != null && tween.IsValid() && tween.IsRunning())
                    tween.FastForwardToCompletion();
        }

        public void Panels(bool legend, bool tools)
        {
            if (!_active)
                return;
            if (!tools)
                StopDrawingMode();
            _legend.Visible = legend;
            _tools.Visible = tools;
        }

        private void StopDrawingMode()
        {
            // Use live owned nodes instead of wrapping a possibly disposed field.
            // Native StopDrawing preserves strokes and emits Finished normally.
            foreach (NMapDrawingInput input in _screen.GetChildren().OfType<NMapDrawingInput>())
                if (!input.IsQueuedForDeletion())
                    input.StopDrawing();
        }

        public bool ClosePanel()
        {
            if (!_active || (!_legend.Visible && !_tools.Visible))
                return false;
            Panels(false, false);
            return true;
        }

        private void Restore()
        {
            _active = false;
            foreach (var item in _properties)
                item.Key.Control.Set(item.Key.Name, item.Value);
            foreach (var item in _fonts)
            {
                if (item.Value.Had)
                    item.Key.AddThemeFontSizeOverride("font_size", item.Value.Size);
                else
                    item.Key.RemoveThemeFontSizeOverride("font_size");
                if (item.Key is MegaLabel mega && item.Value.Auto.HasValue)
                    mega.AutoSizeEnabled = item.Value.Auto.Value;
            }
            foreach (PortraitControlSnapshot snapshot in _original.Values)
                snapshot.Restore();
            // Recompute the current native paper and drawing origin after restoring bounds.
            _background.Call("OnWindowChange");
            _back.Set("_posOffset", _backOffset);
            _back.Call("OnWindowChange");
            _legend.Position = new Vector2(_screen.Size.X * 0.8f, _legend.Position.Y);
            foreach (CanvasItem item in new CanvasItem[] { _paper, _footer, _legendSurface, _backSurface, _legendButton, _toolsButton })
                item.Hide();
        }

        private void Font(Label label, int size)
        {
            if (!_fonts.ContainsKey(label))
                _fonts.Add(label, (label.HasThemeFontSizeOverride("font_size"), label.GetThemeFontSize("font_size"),
                    label is MegaLabel mega ? mega.AutoSizeEnabled : null));
            if (label is MegaLabel autoLabel)
                autoLabel.AutoSizeEnabled = false;
            label.AddThemeFontSizeOverride("font_size", size);
        }

        private void Remember(Control control)
        {
            if (!_original.ContainsKey(control))
                _original.Add(control, new PortraitControlSnapshot(control));
        }

        private void Place(Control control, Vector2 position, Vector2 size)
        {
            Remember(control);
            control.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
            control.Position = position;
            control.Size = size;
        }

        private void Saved(Control control, string name, Variant value)
        {
            if (!_properties.ContainsKey((control, name)))
                _properties.Add((control, name), control.Get(name));
            control.Set(name, value);
        }

        private Button Toggle(string name)
        {
            Button button = new() { Name = name, Visible = false, FocusMode = Control.FocusModeEnum.None };
            button.AddThemeFontSizeOverride("font_size", 48);
            // Bare actions reuse native share lettering on the existing map/footer background.
            Label nativeLabel = _share.Get("_label").As<Label>();
            button.AddThemeFontOverride("font", nativeLabel.GetThemeFont("font"));
            button.AddThemeColorOverride("font_color", nativeLabel.GetThemeColor("font_color"));
            button.AddThemeColorOverride("font_outline_color", nativeLabel.GetThemeColor("font_outline_color"));
            button.AddThemeConstantOverride("outline_size", nativeLabel.GetThemeConstant("outline_size"));
            button.AddThemeColorOverride("font_hover_color", StsColors.cream);
            button.AddThemeColorOverride("font_pressed_color", StsColors.cream);
            button.AddThemeStyleboxOverride("normal", new StyleBoxEmpty());
            button.AddThemeStyleboxOverride("disabled", new StyleBoxEmpty());
            button.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
            // Use the real native Back paper only for action feedback, with its original press tint.
            Texture2D paper = _back.Get("_buttonImage").As<TextureRect>().Texture;
            // Preserve the native atlas padding canvas in both feedback states.
            button.AddThemeStyleboxOverride("hover", new StyleBoxTexture
            {
                Texture = paper,
                RegionRect = new Rect2(Vector2.Zero, paper.GetSize()),
                ContentMarginLeft = 0, ContentMarginTop = 0, ContentMarginRight = 0, ContentMarginBottom = 0,
            });
            button.AddThemeStyleboxOverride("pressed", new StyleBoxTexture
            {
                Texture = paper, ModulateColor = Colors.Gray,
                RegionRect = new Rect2(Vector2.Zero, paper.GetSize()),
                ContentMarginLeft = 0, ContentMarginTop = 0, ContentMarginRight = 0, ContentMarginBottom = 0,
            });
            _screen.AddChild(button);
            return button;
        }

        private static Panel Surface(Control parent, string name, bool light)
        {
            Panel panel = new() { Name = name, Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
            panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
            {
                BgColor = light ? new Color(0.72f, 0.70f, 0.58f) : new Color(0.09f, 0.12f, 0.09f),
                BorderColor = new Color(0.62f, 0.53f, 0.34f), BorderWidthTop = 2, BorderWidthBottom = 2,
                BorderWidthLeft = 2, BorderWidthRight = 2,
                CornerRadiusTopLeft = 16, CornerRadiusTopRight = 16,
                CornerRadiusBottomLeft = 16, CornerRadiusBottomRight = 16,
            });
            parent.AddChild(panel);
            return panel;
        }
    }
}
