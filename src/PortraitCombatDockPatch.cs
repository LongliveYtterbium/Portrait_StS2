using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.addons.mega_text;
using STS2RitsuLib.Patching.Models;
using OpCodes = System.Reflection.Emit.OpCodes;

namespace STS2Portrait.Patches;

/// <summary>Arranges native combat counters and actions in the portrait footer.</summary>
internal sealed class PortraitCombatDockPatch : IPatchMethod
{
    public static string PatchId => "sts2portrait_combat_dock";
    public static string Description => "Keep combat counters and turn actions in a touch-sized footer";
    public static bool IsCritical => true;
    public static ModPatchTarget[] GetTargets() => new[]
    {
        PatchTarget.Method(typeof(NCombatUi), "_Ready"),
        PatchTarget.Method(typeof(NCombatUi), "Activate"),
        PatchTarget.Method(typeof(NCombatCardPile), "AnimIn"),
        PatchTarget.Method(typeof(NCombatCardPile), "AnimOut"),
        PatchTarget.Method(typeof(NExhaustPileButton), "SetAnimInOutPositions"),
        PatchTarget.Method(typeof(NEndTurnButton), "AnimIn"),
        PatchTarget.Method(typeof(NEndTurnButton), "AnimOut"),
        PatchTarget.Method(typeof(NEndTurnButton), "OnEnable"),
        PatchTarget.Method(typeof(NEndTurnButton), "OnDisable"),
        PatchTarget.Method(typeof(NEndTurnButton), "OnFocus"),
    };

    private static readonly ConditionalWeakTable<NCombatUi, LayoutState> States = new();
    private static readonly FieldInfo ActiveHoverTips = AccessTools.Field(typeof(NHoverTipSet), "_activeHoverTips");

    private static LayoutState? FindState(Node node)
    {
        // Native piles have a container parent; the end-turn button belongs
        // directly to CombatUi. Resolve ownership by type, not a scene path.
        for (Node? owner = node; owner != null; owner = owner.GetParent())
            if (owner is NCombatUi ui && States.TryGetValue(ui, out LayoutState? state))
                return state;
        return null;
    }

    public static void Prefix(Node __instance, MethodBase __originalMethod)
    {
        if (Entry.IsDisabled)
            return;
        LayoutState? state = FindState(__instance);
        if (__instance is NCombatUi && __originalMethod.Name == "Activate")
            state?.BeforeActivate();
        else if (__instance is NCombatCardPile pile && __originalMethod.Name is "AnimIn" or "AnimOut")
            state?.PreparePileAnimation(pile, __originalMethod.Name == "AnimOut");
    }

    public static void Postfix(Node __instance, MethodBase __originalMethod)
    {
        if (Entry.IsDisabled)
            return;
        if (__instance is NCombatUi ui)
        {
            if (__originalMethod.Name == "_Ready")
                States.GetValue(ui, value => new LayoutState(value)).Queue();
            else if (States.TryGetValue(ui, out LayoutState? state))
                state.AfterActivate();
        }
        else if (__instance is NExhaustPileButton pile && __originalMethod.Name == "SetAnimInOutPositions")
            FindState(pile)?.SyncPileTargets(pile);
        else if (__instance is NEndTurnButton button && __originalMethod.Name is "OnEnable" or "OnDisable")
            FindState(button)?.UpdateEndTurnSkin();
        else if (__instance is NEndTurnButton focused && __originalMethod.Name == "OnFocus")
        {
            // Native OnFocus moves this tip after its original overflow correction.
            // None preserves that placement and reruns the native two-axis correction.
            var tips = (Dictionary<Control, NHoverTipSet>)ActiveHoverTips.GetValue(null)!;
            if (tips.TryGetValue(focused, out NHoverTipSet? tip))
            {
                if (PortraitViewportPatch.IsPortrait)
                    tip.SetAlignment(focused, HoverTipAlignment.None);
                // Follow in both layouts so a focused tip tracks the footer across viewport changes.
                tip.SetFollowOwner();
            }
        }
    }

    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        CodeInstruction[] body = instructions.ToArray();
        if (__originalMethod.DeclaringType != typeof(NEndTurnButton)
            || __originalMethod.Name is not ("AnimIn" or "AnimOut"))
            return body;

        bool hide = __originalMethod.Name == "AnimOut";
        MethodInfo getter = AccessTools.PropertyGetter(typeof(NEndTurnButton), hide ? "HidePos" : "ShowPos");
        CodeInstruction[] calls = body.Where(instruction => instruction.Calls(getter)).ToArray();
        if (calls.Length != 1)
            throw new InvalidOperationException($"Expected one end-turn destination in {__originalMethod.Name}, found {calls.Length}.");
        // Patch the actual animation caller, not only a tiny getter that the
        // runtime may already have inlined. Preserve the original tween chain.
        calls[0].opcode = OpCodes.Call;
        calls[0].operand = AccessTools.Method(typeof(PortraitCombatDockPatch), hide ? nameof(EndTurnHidden) : nameof(EndTurnShown));
        return body;
    }

    private static Vector2 EndTurnShown(NEndTurnButton button) => !Entry.IsDisabled && PortraitViewportPatch.IsPortrait
        ? new Vector2(720, button.GetViewportRect().Size.Y - 144)
        : button.Get("ShowPos").AsVector2();

    private static Vector2 EndTurnHidden(NEndTurnButton button) => !Entry.IsDisabled && PortraitViewportPatch.IsPortrait
        ? new Vector2(720, button.GetViewportRect().Size.Y + 48)
        : button.Get("HidePos").AsVector2();

    private sealed class LayoutState
    {
        private readonly NCombatUi _ui;
        private readonly NCombatCardPile[] _piles;
        private readonly Dictionary<Control, PortraitControlSnapshot> _geometry = new();
        private readonly Dictionary<(Control Node, string Name), Variant> _properties = new();
        private readonly Dictionary<(Control Node, string Name), (bool Had, int Size)> _fonts = new();
        private readonly Dictionary<NCombatCardPile, bool> _pileHidden = new();
        private readonly Dictionary<NCombatCardPile, Panel> _pileSurfaces = new();
        private readonly Panel _footer;
        private readonly Panel _energySurface;
        private readonly Panel _endTurnSurface;
        private readonly Control _endVisuals;
        private readonly Control _endImage;
        private PortraitControlSnapshot _energyOriginal;
        private NStarCounter? _starCounter;
        private Vector2 _viewportSize;
        private bool _active;
        private bool _queued;

        public LayoutState(NCombatUi ui)
        {
            _ui = ui;
            _piles = new NCombatCardPile[] { ui.DrawPile, ui.DiscardPile, ui.ExhaustPile };
            _energyOriginal = new PortraitControlSnapshot(ui.EnergyCounterContainer);
            _footer = Surface(ui, "PortraitCombatFooter");
            ui.MoveChild(_footer, 0);
            _energySurface = Surface(ui.EnergyCounterContainer, "PortraitEnergySurface");
            _endVisuals = ui.EndTurnButton.GetChildren().OfType<Control>().Single(node => node.Name == "Visuals");
            _endImage = _endVisuals.GetChildren().OfType<Control>().Single(node => node.Name == "Image");
            _endTurnSurface = Surface(_endVisuals, "PortraitEndTurnSurface");
            _endVisuals.MoveChild(_endTurnSurface, 0);
            foreach (NCombatCardPile pile in _piles)
            {
                Panel panel = Surface(pile, "PortraitPileSurface");
                pile.MoveChild(panel, 0);
                _pileSurfaces.Add(pile, panel);
                _pileHidden.Add(pile, false);
                Remember(pile);
            }
            Remember(ui.EndTurnButton);
            ui.Resized += Queue;
            Window window = ui.GetWindow();
            window.SizeChanged += Queue;
            ui.TreeExiting += () => window.SizeChanged -= Queue;
        }

        public void BeforeActivate()
        {
            // Regent writes this container during Activate. Let that native
            // write start from its own geometry before recording its new result.
            if (_active)
                _energyOriginal.Restore();
        }

        public void AfterActivate()
        {
            _energyOriginal = new PortraitControlSnapshot(_ui.EnergyCounterContainer);
            Apply();
        }

        public void Queue()
        {
            if (_queued)
                return;
            _queued = true;
            Callable.From(() =>
            {
                _queued = false;
                if (GodotObject.IsInstanceValid(_ui) && _ui.IsInsideTree())
                    Apply();
            }).CallDeferred();
        }

        public void PreparePileAnimation(NCombatCardPile pile, bool hidden)
        {
            _pileHidden[pile] = hidden;
            SyncPileTargets(pile);
        }

        public void SyncPileTargets(NCombatCardPile pile)
        {
            if (!PortraitViewportPatch.IsPortrait)
                return;
            int index = Array.IndexOf(_piles, pile);
            float height = _ui.GetViewportRect().Size.Y;
            pile.Set("_showPosition", new Vector2(216 + index * 168, height - 144));
            pile.Set("_hidePosition", new Vector2(216 + index * 168, height + 48));
        }

        public void UpdateEndTurnSkin()
        {
            if (_active)
                _endTurnSurface.Hide();
        }

        private void Apply()
        {
            bool portrait = PortraitViewportPatch.IsPortrait;
            Vector2 viewport = _ui.GetViewportRect().Size;
            bool remap = portrait != _active || viewport != _viewportSize;
            if (remap)
            {
                // Complete an old-coordinate tween normally before moving its
                // endpoint. FastForward retains the native Finished signal.
                foreach (Control node in _piles.Cast<Control>().Append(_ui.EndTurnButton))
                    if (node.Get("_positionTween").AsGodotObject() is Tween tween && tween.IsValid() && tween.IsRunning())
                        tween.FastForwardToCompletion();
            }
            _viewportSize = viewport;
            if (!portrait)
            {
                if (_active)
                    Restore();
                if (remap)
                {
                    // Window and canvas sizes settle in separate notifications.
                    // Recompute native endpoints for every settled size, not
                    // only for the first frame that leaves portrait mode.
                    foreach (NCombatCardPile pile in new NCombatCardPile[] { _ui.DrawPile, _ui.DiscardPile })
                    {
                        _geometry[pile].Restore();
                        pile.Call("SetAnimInOutPositions");
                        pile.Position = pile.Get(_pileHidden[pile] ? "_hidePosition" : "_showPosition").AsVector2();
                    }
                    NEndTurnButton nativeEnd = _ui.EndTurnButton;
                    nativeEnd.Position = nativeEnd.Get("_state").AsInt32() == 2
                        ? EndTurnHidden(nativeEnd) : EndTurnShown(nativeEnd);
                }
                return;
            }
            _active = true;
            float y = viewport.Y - 144;
            _footer.Position = new Vector2(0, y - 24);
            _footer.Size = new Vector2(viewport.X, 168);
            // Native counters and actions float on the scene; hide only the added passive leaf skins.
            _footer.Hide();
            Place(_ui.EnergyCounterContainer, new Vector2(48, y), new Vector2(144, 120), false);
            _energySurface.Size = new Vector2(144, 120);
            _energySurface.Hide();
            ApplyEnergy();

            foreach (NCombatCardPile pile in _piles)
            {
                SyncPileTargets(pile);
                Vector2 position = remap ? pile.Get(_pileHidden[pile] ? "_hidePosition" : "_showPosition").AsVector2() : pile.Position;
                Place(pile, position, new Vector2(144, 120));
                pile.PivotOffset = new Vector2(72, 60);
                _pileSurfaces[pile].Size = pile.Size;
                // Keep the native pile owner, icon and count; this panel owns no native children.
                _pileSurfaces[pile].Hide();
                Control icon = pile.GetChildren().OfType<Control>().Single(node => node.Name == "Icon");
                Place(icon, new Vector2(36, 0), new Vector2(72, 72));
                icon.PivotOffset = new Vector2(36, 36);
                Control count = pile.GetChildren().OfType<Control>().Single(node => node.Name == "CountContainer");
                Place(count, new Vector2(12, 66), new Vector2(120, 54));
                Label label = count.GetChildren().OfType<Label>().Single();
                Place(label, Vector2.Zero, count.Size);
                Font(label, "font_size", 36);
                foreach (Control background in count.GetChildren().OfType<Control>().Where(node => node.Name == "Background"))
                    Saved(background, "visible", false);
                foreach (Control hotkey in pile.GetChildren().OfType<Control>().Where(node => node.Name == "HotkeyIcon"))
                    Saved(hotkey, "visible", false);
            }

            NEndTurnButton end = _ui.EndTurnButton;
            bool endHidden = end.Get("_state").AsInt32() == 2;
            Place(end, remap ? (endHidden ? EndTurnHidden(end) : EndTurnShown(end)) : end.Position, new Vector2(312, 120));
            end.PivotOffset = end.Size * 0.5f;
            _endTurnSurface.Hide();
            // Reuse the native 512x256 art ratio and its half-scale. Glow scale/alpha
            // remain owned by the original pulse tweens; the input rectangle stays 312x120.
            Vector2 endArtSize = new(720, 360);
            foreach (TextureRect artwork in _endVisuals.GetChildren().OfType<TextureRect>()
                .Where(node => node == _endImage || node.Name == "Glow" || node.Name == "GlowVfx"))
            {
                Place(artwork, (end.Size - endArtSize) * .5f, endArtSize);
                artwork.PivotOffset = endArtSize * .5f;
            }
            foreach (Control hotkey in _endVisuals.GetChildren().OfType<Control>().Where(node => node.Name == "HotkeyIcon"))
                Saved(hotkey, "visible", false);
            Label endLabel = _endVisuals.GetChildren().OfType<Label>().Single();
            Place(endLabel, new Vector2(18, 12), new Vector2(276, 96));
            Font(endLabel, "font_size", 48);
            UpdateEndTurnSkin();
            // Native long-press and multiplayer children remain attached and
            // retain their state. Only move the bar within the larger hitbox.
            Control bar = end.GetChildren().OfType<Control>().Single(node => node.Name == "Bar");
            Place(bar, new Vector2(12, 108), new Vector2(288, 6));
        }

        private void ApplyEnergy()
        {
            NEnergyCounter? energy = _ui.EnergyCounterContainer.GetChildren().OfType<NEnergyCounter>().SingleOrDefault();
            if (energy == null)
                return; // The counter is created later by native Activate.
            // Preserve energy.Position: its own intro/outro tween ends at zero.
            Saved(energy, "pivot_offset", Vector2.Zero);
            // Fit the native 128-square counter inside the 120-high footer.
            Saved(energy, "scale", Vector2.One * .9375f);
            NStarCounter star = energy.GetChildren().OfType<NStarCounter>().Single();
            if (_starCounter != star)
            {
                _starCounter = star;
                star.VisibilityChanged += Queue;
            }
            Label label = energy.GetChildren().OfType<Label>().Single(node => node.Name == "Label");
            Place(label, new Vector2(0, star.Visible ? 8 : 36), new Vector2(128, 56));
            Font(label, "font_size", 48);
            // Stars stay in their native counter and update from native events.
            // A lower-corner badge prevents the old -212 Y offset hitting cards.
            Place(star, new Vector2(64, 64), new Vector2(128, 128));
            star.Scale = Vector2.One * 0.5f;
            star.PivotOffset = Vector2.Zero;
            RichTextLabel starLabel = star.Get("_label").As<RichTextLabel>();
            Font(starLabel, "normal_font_size", 84);
        }

        private void Restore()
        {
            _active = false;
            foreach (var item in _properties)
                if (GodotObject.IsInstanceValid(item.Key.Node))
                    item.Key.Node.Set(item.Key.Name, item.Value);
            foreach (var item in _fonts)
                if (GodotObject.IsInstanceValid(item.Key.Node))
                {
                    if (item.Value.Had)
                        item.Key.Node.AddThemeFontSizeOverride(item.Key.Name, item.Value.Size);
                    else
                        item.Key.Node.RemoveThemeFontSizeOverride(item.Key.Name);
                }
            foreach (var item in _geometry)
                if (GodotObject.IsInstanceValid(item.Key))
                    item.Value.Restore();
            _energyOriginal.Restore();
            foreach (NCombatCardPile pile in _piles)
            {
                // Restore anchors first, then let each native override derive
                // its current landscape destinations from that native geometry.
                pile.Call("SetAnimInOutPositions");
                if (_pileHidden[pile])
                    pile.Position = pile.Get("_hidePosition").AsVector2();
                _pileSurfaces[pile].Hide();
            }
            NEndTurnButton end = _ui.EndTurnButton;
            end.Position = end.Get("_state").AsInt32() == 2 ? EndTurnHidden(end) : EndTurnShown(end);
            _footer.Hide();
            _energySurface.Hide();
            _endTurnSurface.Hide();
        }

        private void Remember(Control node)
        {
            if (!_geometry.ContainsKey(node))
                _geometry.Add(node, new PortraitControlSnapshot(node));
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
            if (!_properties.ContainsKey((node, name)))
                _properties.Add((node, name), node.Get(name));
            node.Set(name, value);
        }

        private void Font(Control node, string name, int size)
        {
            if (!_fonts.ContainsKey((node, name)))
                _fonts.Add((node, name), (node.HasThemeFontSizeOverride(name), node.GetThemeFontSize(name)));
            if (node is MegaLabel || node is MegaRichTextLabel)
                Saved(node, "AutoSizeEnabled", false);
            node.AddThemeFontSizeOverride(name, size);
        }

        private static Panel Surface(Control parent, string name)
        {
            // Decorative skins inherit native movement and never intercept input.
            Panel panel = new() { Name = name, Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
            // Native hover-tip paper supplies the footer/slot skin without adding content minima.
            panel.AddThemeStyleboxOverride("panel", new StyleBoxTexture
            {
                Texture = ResourceLoader.Load<Texture2D>("res://images/ui/hover_tip.png")
                    ?? throw new InvalidOperationException($"Native combat footer texture failed to load: {name}"),
                TextureMarginLeft = 55, TextureMarginTop = 43,
                TextureMarginRight = 91, TextureMarginBottom = 32,
                AxisStretchHorizontal = StyleBoxTexture.AxisStretchMode.Tile,
                AxisStretchVertical = StyleBoxTexture.AxisStretchMode.Tile,
                ContentMarginLeft = 0, ContentMarginTop = 0,
                ContentMarginRight = 0, ContentMarginBottom = 0,
            });
            parent.AddChild(panel);
            return panel;
        }
    }
}
