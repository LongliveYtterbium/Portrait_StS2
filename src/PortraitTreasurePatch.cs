using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;
using MegaCrit.Sts2.addons.mega_text;
using STS2RitsuLib.Patching.Models;
using OpCodes = System.Reflection.Emit.OpCodes;

namespace STS2Portrait.Patches;

/// <summary>Adds readable treasure details while preserving native relic voting.</summary>
internal sealed class PortraitTreasurePatch : IPatchMethod
{
    public static string PatchId => "sts2portrait_treasure";
    public static string Description => "Readable native treasure rewards and touch footer";
    public static bool IsCritical => true;
    public static ModPatchTarget[] GetTargets() => new[]
    {
        PatchTarget.Method(typeof(NTreasureRoom), "_Ready"),
        PatchTarget.Method(typeof(NTreasureRoomRelicCollection), "InitializeRelics"),
        PatchTarget.Method(typeof(NProceedButton), "OnEnable"),
        PatchTarget.Method(typeof(NProceedButton), "OnDisable"),
    };

    private static readonly ConditionalWeakTable<NTreasureRoom, LayoutState> States = new();
    private static LayoutState? StateFor(Node node)
    {
        for (Node? parent = node; parent != null; parent = parent.GetParent())
            if (parent is NTreasureRoom room && States.TryGetValue(room, out LayoutState? state))
                return state;
        return null;
    }

    public static void Postfix(Node __instance, MethodBase __originalMethod)
    {
        if (Entry.IsDisabled) return;
        if (__instance is NTreasureRoom room && __originalMethod.Name == "_Ready")
            States.GetValue(room, value => new LayoutState(value)).Apply();
        else if (__instance is NTreasureRoomRelicCollection && __originalMethod.Name == "InitializeRelics")
            StateFor(__instance)?.BuildDetails();
    }

    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        List<CodeInstruction> body = instructions.ToList();
        if (__originalMethod.DeclaringType != typeof(NProceedButton)) return body;
        // Compose with the B18 getter and B22 conversion hooks. Other owners keep
        // the incoming vector, including any destination already supplied by them.
        MethodInfo conversion = AccessTools.Method(typeof(Variant), "op_Implicit", new[] { typeof(Vector2) });
        int[] matches = body.Select((item, index) => (item, index))
            .Where(pair => pair.item.Calls(conversion)).Select(pair => pair.index).ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException($"Expected one treasure proceed vector in {__originalMethod.Name}, found {matches.Length}.");
        CodeInstruction owner = new(OpCodes.Ldarg_0);
        owner.labels.AddRange(body[matches[0]].labels);
        body[matches[0]].labels.Clear();
        body.InsertRange(matches[0], new[]
        {
            owner, new CodeInstruction(__originalMethod.Name == "OnDisable" ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0),
            new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(PortraitTreasurePatch), nameof(ProceedDestination))),
        });
        return body;
    }

    private static Vector2 ProceedDestination(Vector2 native, NProceedButton button, bool hidden) =>
        !Entry.IsDisabled && PortraitViewportPatch.IsPortrait && StateFor(button) is { Active: true } state
        && ReferenceEquals(button, state.Proceed) ? state.Destination(hidden) : native;

    private sealed class LayoutState
    {
        private readonly NTreasureRoom _room;
        private readonly NTreasureRoomRelicCollection _collection;
        internal readonly NProceedButton Proceed;
        private readonly MegaLabel _proceedLabel;
        private readonly Node _labelParent;
        private readonly int _labelIndex;
        private readonly MegaLabel _bannerLabel;
        private readonly MegaLabel _fightLabel;
        private readonly Panel _footer;
        private readonly ScrollContainer _reader;
        private readonly VBoxContainer _details;
        private readonly Dictionary<Control, PortraitControlSnapshot> _geometry = new();
        private readonly Dictionary<(GodotObject Node, string Name), Variant> _properties = new();
        private readonly Dictionary<(Control Node, string Name), (bool Had, int Size)> _fonts = new();
        private Vector2 _viewport;
        private bool _queued;
        public bool Active { get; private set; }

        public LayoutState(NTreasureRoom room)
        {
            _room = room;
            _collection = room.Get("_relicCollection").As<NTreasureRoomRelicCollection>();
            Proceed = room.Get("_proceedButton").As<NProceedButton>();
            _proceedLabel = Proceed.Get("_label").As<MegaLabel>();
            _labelParent = _proceedLabel.GetParent(); _labelIndex = _proceedLabel.GetIndex();
            _bannerLabel = room.Get("_banner").As<NCommonBanner>().label;
            _fightLabel = _collection.Get("_fightLabel").As<MegaLabel>();
            _footer = new Panel { Name = "PortraitTreasureFooter", Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
            _footer.AddThemeStyleboxOverride("panel", new StyleBoxFlat
            {
                BgColor = new Color(.43f, .30f, .10f, .98f), BorderColor = new Color(.56f, .48f, .30f),
                BorderWidthLeft = 2, BorderWidthTop = 2, BorderWidthRight = 2, BorderWidthBottom = 2,
                CornerRadiusTopLeft = 24, CornerRadiusTopRight = 24, CornerRadiusBottomLeft = 24, CornerRadiusBottomRight = 24,
            });
            Proceed.AddChild(_footer); Proceed.MoveChild(_footer, 0);
            _reader = new ScrollContainer { Name = "PortraitTreasureDetails", Visible = false,
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
                VerticalScrollMode = ScrollContainer.ScrollMode.Auto, ScrollDeadzone = 24 };
            // The collection owns this display, so its native fade and fight
            // backstop also govern the reader. No native holder leaves its parent.
            _collection.AddChild(_reader);
            _details = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                MouseFilter = Control.MouseFilterEnum.Pass };
            _details.AddThemeConstantOverride("separation", 24); _reader.AddChild(_details);
            room.GetViewport().SizeChanged += Queue;
            room.TreeExiting += () => room.GetViewport().SizeChanged -= Queue;
        }

        public void Queue()
        {
            if (_queued) return;
            _queued = true;
            Callable.From(() => { _queued = false; Apply(); }).CallDeferred();
        }

        public Vector2 Destination(bool hidden) => new(48, _viewport.Y + (hidden ? 48 : -192));

        public void Apply()
        {
            if (!GodotObject.IsInstanceValid(_room) || !_room.IsInsideTree()) return;
            if (!PortraitViewportPatch.IsPortrait) { Restore(); return; }
            Vector2 viewport = _room.GetViewportRect().Size;
            bool changed = !Active || _viewport != viewport;
            Active = true; _viewport = viewport;
            if (changed)
            {
                Finish(Proceed.Get("_animTween").AsGodotObject() as Tween);
                Finish(Proceed.Get("_hoverTween").AsGodotObject() as Tween);
                Remember(Proceed); Remember(_proceedLabel);
                Control buttonImage = Proceed.Get("_buttonImage").As<Control>();
                // Keep the native arrow, material and outline inside the wider touch target.
                Saved(buttonImage, "visible", true);
                Saved(buttonImage, "expand_mode", (int)TextureRect.ExpandModeEnum.IgnoreSize);
                foreach (Control decoration in Proceed.GetChildren().OfType<Control>()
                    .Where(node => node.Name == "HotkeyIcon"))
                    Saved(decoration, "visible", false);
                if (_proceedLabel.GetParent() != _labelParent) _proceedLabel.Reparent(_labelParent, false);
                Saved(_proceedLabel, "mouse_filter", (int)Control.MouseFilterEnum.Ignore);
                Font(_proceedLabel, 48);
                Place(Proceed, Destination(!Proceed.IsEnabled), new Vector2(viewport.X - 96, 144));
                Proceed.Scale = Vector2.One; Proceed.PivotOffset = Proceed.Size / 2;
                Place(buttonImage, new Vector2(300, -24), new Vector2(384, 192));
                Place(_proceedLabel, Vector2.Zero, buttonImage.Size);
                _footer.Position = Vector2.Zero; _footer.Size = Proceed.Size;
                // The banner already fits. Only enlarge its text, retaining its
                // original image, position caches and complete animation path.
                Font(_bannerLabel, 48);
                Place(_fightLabel, new Vector2(48, 336), new Vector2(viewport.X - 96, 144));
            }
            // The original arrow supplies the action surface; the added flat plate stays hidden.
            _footer.Hide();
            _reader.Position = new Vector2(48, viewport.Y / 2 + 260) - _collection.Position;
            _reader.Size = new Vector2(viewport.X - 96, viewport.Y / 2 - 476);
            _reader.Visible = _details.GetChildCount() != 0;
        }

        public void BuildDetails()
        {
            // InitializeRelics has now assigned models and visible holders. The
            // collection itself is still hidden until its native AnimIn starts.
            foreach (Node old in _details.GetChildren()) { _details.RemoveChild(old); old.QueueFree(); }
            foreach (NTreasureRoomRelicHolder holder in Descendants(_collection).OfType<NTreasureRoomRelicHolder>()
                .Where(holder => holder.Visible).OrderBy(holder => holder.Index))
            {
                HoverTip tip = holder.Relic.Model.HoverTip;
                Control row = PreloadManager.Cache.GetScene("res://scenes/ui/hover_tip.tscn").Instantiate<Control>();
                row.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                row.AddThemeConstantOverride("margin_right", 0); row.AddThemeConstantOverride("margin_bottom", 0);
                foreach (Control node in Descendants(row).OfType<Control>().Prepend(row))
                    node.MouseFilter = node is TextureRect or NinePatchRect ? Control.MouseFilterEnum.Ignore : Control.MouseFilterEnum.Pass;
                MegaLabel title = Descendants(row).OfType<MegaLabel>().Single(node => node.Name == "Title");
                title.AutoSizeEnabled = false; title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                title.AutowrapMode = TextServer.AutowrapMode.WordSmart; title.AddThemeFontSizeOverride("font_size", 54);
                title.Text = tip.Title ?? ""; title.Visible = tip.Title != null;
                TextureRect icon = Descendants(row).OfType<TextureRect>().Single(node => node.Name == "Icon");
                icon.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;
                icon.StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered;
                icon.Texture = holder.Relic.Icon.Texture; icon.CustomMinimumSize = new Vector2(64, 64);
                MegaRichTextLabel body = Descendants(row).OfType<MegaRichTextLabel>().Single();
                body.AutoSizeEnabled = false; body.CustomMinimumSize = Vector2.Zero;
                body.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                body.AutowrapMode = TextServer.AutowrapMode.WordSmart; body.FitContent = true; body.ScrollActive = false;
                foreach (string font in RichFonts) body.AddThemeFontSizeOverride(font, 48);
                body.Text = tip.Description;
                _details.AddChild(row);
            }
            _reader.ScrollVertical = 0;
            Apply();
        }

        private void Restore()
        {
            if (!Active) return;
            Active = false;
            Finish(Proceed.Get("_animTween").AsGodotObject() as Tween);
            Finish(Proceed.Get("_hoverTween").AsGodotObject() as Tween);
            _reader.Hide(); _footer.Hide();
            _proceedLabel.Reparent(_labelParent, false);
            _labelParent.MoveChild(_proceedLabel, _labelIndex);
            foreach (var entry in _fonts)
                if (entry.Value.Had) entry.Key.Node.AddThemeFontSizeOverride(entry.Key.Name, entry.Value.Size);
                else entry.Key.Node.RemoveThemeFontSizeOverride(entry.Key.Name);
            foreach (PortraitControlSnapshot snapshot in _geometry.Values) snapshot.Restore();
            // Restore auto-size only after native font sizes and bounds are back.
            foreach (var entry in _properties.Reverse()) entry.Key.Node.Set(entry.Key.Name, entry.Value);
            Proceed.Position = Proceed.Get(Proceed.IsEnabled ? "ShowPos" : "HidePos").AsVector2();
        }

        private void Remember(Control node)
        {
            if (!_geometry.ContainsKey(node)) _geometry.Add(node, new PortraitControlSnapshot(node));
        }
        private void Place(Control node, Vector2 position, Vector2 size)
        {
            Remember(node); node.CustomMinimumSize = Vector2.Zero;
            node.SetAnchorsPreset(Control.LayoutPreset.TopLeft); node.Position = position; node.Size = size;
        }
        private void Saved(GodotObject node, string name, Variant value)
        {
            if (!_properties.ContainsKey((node, name))) _properties.Add((node, name), node.Get(name));
            node.Set(name, value);
        }
        private void Font(MegaLabel label, int size)
        {
            const string name = "font_size";
            if (!_fonts.ContainsKey((label, name))) _fonts.Add((label, name), (label.HasThemeFontSizeOverride(name), label.GetThemeFontSize(name)));
            Saved(label, "AutoSizeEnabled", false);
            if (label.GetThemeFontSize(name) != size) label.AddThemeFontSizeOverride(name, size);
        }
    }

    private static readonly string[] RichFonts = { "normal_font_size", "bold_font_size", "italics_font_size", "bold_italics_font_size", "mono_font_size" };
    private static IEnumerable<Node> Descendants(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            yield return child;
            foreach (Node descendant in Descendants(child)) yield return descendant;
        }
    }
    private static void Finish(Tween? tween)
    {
        if (GodotObject.IsInstanceValid(tween) && tween!.IsValid() && tween.IsRunning()) tween.FastForwardToCompletion();
    }
}
