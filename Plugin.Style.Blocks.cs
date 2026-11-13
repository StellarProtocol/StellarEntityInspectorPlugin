using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.EntityInspector;

// More house-kit building blocks (Stellar Design Language §4; reference StellarWardrobeLoadout Plugin.Style.Blocks.cs):
// the glyph icons, the section panel, the data-availability tags, the accent icon tile and the round ✕.
public sealed partial class Plugin
{
    // ── glyph icons ───────────────────────────────────────────────────────────────────────────────────────────────
    // White flat glyphs (tools/gen_icons.py → Icons/<name>-<px>.png), tinted at draw time with ImageElement.Tint so they
    // follow the theme. House icon rule: ONE pre-drawn, pixel-hinted bitmap per on-screen size, never a master scaled
    // down — each glyph ships at its canvas size × the usual UI scales (0.75 at 1080p … 2.0), and the window shows the one
    // nearest to `units × the live canvas→screen scale`. Bytes are read once per size and the SAME array handed back
    // (ImageElement compares by reference), so a poll never allocates.
    private sealed class IconSet
    {
        private readonly string _name;
        public readonly int Units;          // glyph height in canvas units (what the window lays out)
        private readonly int[] _sizes;      // pre-drawn pixel heights (ascending)
        private readonly byte[]?[] _pngs;
        private readonly bool[] _loaded;
        private float _scale = -1f;
        private byte[]? _picked;

        public IconSet(string name, int units, params int[] sizes)
        {
            _name = name; Units = units; _sizes = sizes;
            _pngs = new byte[]?[sizes.Length]; _loaded = new bool[sizes.Length];
        }

        // The bitmap for the current canvas→screen `scale`; cached until the scale changes (no work per poll).
        public byte[]? Pick(float scale)
        {
            if (scale == _scale) return _picked;
            _scale = scale;
            float want = Units * scale;
            int best = 0;
            for (int i = 1; i < _sizes.Length; i++)
                if (MathF.Abs(_sizes[i] - want) < MathF.Abs(_sizes[best] - want)) best = i;
            if (!_loaded[best])
            {
                _loaded[best] = true;   // latch even on a miss: a missing resource draws nothing, never re-reads
                _pngs[best] = LoadEmbedded($"Stellar.EntityInspector.Icons.{_name}-{_sizes[best]}.png");
            }
            return _picked = _pngs[best] ?? _picked;
        }
    }

    // Sizes = round(units × {0.75, 1, 1.25, 1.5, 2}) ∪ {16, 32} — keep in sync with tools/gen_icons.py.
    private static readonly IconSet RadarIcon = new("radar", 12, 9, 12, 15, 16, 18, 24, 32);   // "Nearby" tag
    private static readonly IconSet LockIcon  = new("lock", 12, 9, 12, 15, 16, 18, 24, 32);    // "Not shared" tag
    private static readonly IconSet CardIcon  = new("card", 12, 9, 12, 15, 16, 18, 24, 32);    // "Profile" tag
    private static readonly IconSet HatchIcon = new("hatch", 12, 9, 12, 15, 16, 18, 24, 32);   // undyed "Default" swatch
    private static readonly IconSet CloseIcon = new("close", 14, 10, 14, 16, 18, 21, 28, 32);
    private static readonly IconSet PinIcon   = new("pin", 16, 12, 16, 20, 24, 32);                 // Location empty state

    // Canvas units → screen pixels (1.0 at 1440p, 0.75 at 1080p). Refreshed by the 10 Hz tick while the window shows.
    private float _uiScale = 1f;

    private void RefreshUiScale()
    {
        var fw = _services.Framework;
        _uiScale = fw.CanvasWidth > 1 && fw.ScreenWidth > 0 ? (float)fw.ScreenWidth / fw.CanvasWidth : 1f;
    }

    private ImageElement Glyph(IconSet icon, Func<ColorRgba?>? tint = null)
        => new(() => icon.Pick(_uiScale), icon.Units, icon.Units) { Live = true, Tint = tint };

    // A glyph whose PICTURE is live too (the data tag's icon follows its kind): the Func hands back cached arrays only.
    private ImageElement Glyph(Func<byte[]?> png, int units, Func<ColorRgba?>? tint = null)
        => new(png, units, units) { Live = true, Tint = tint };

    private static byte[]? LoadEmbedded(string name)
    {
        try
        {
            using var s = typeof(Plugin).Assembly.GetManifestResourceStream(name);
            if (s is null) return null;
            using var ms = new System.IO.MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
        catch { return null; }
    }

    // ── section panel ─────────────────────────────────────────────────────────────────────────────────────────────
    // A rounded panel (background darkened, live): small bold UPPERCASE caption + a right-aligned hint slot (muted,
    // ellipsized in a weighted cell so a long translation never widens the panel), then the children. The caption row is
    // pinned to CaptionRowHeight in EVERY panel so a panel with a 24 px chip/tag and a text-only one start their content
    // on the same line. `trailing` (CaptionedPanel only — ⚠️ a second `params` overload would silently swallow the first
    // child, house rule) = a control at the right end of the caption row (the Weapon chip, a data tag).
    private const float CaptionRowHeight = 24f;
    // A state-dependent message line (far note, footer, empty message) ALWAYS keeps this height — its text is just empty
    // in the states that have nothing to say — so nothing below it moves when the target goes far ↔ nearby (in-game
    // review 2026-11-13). One line, ellipsized.
    private const float StateLineH = 20f;

    private static HudElement StateLine(Func<string> text, Func<ColorRgba?> color, bool bold = false)
        => FixedLine(StateLineH, bold
            ? new TextElement(text, Color: color, NoWrap: true, FontSize: 13) { Bold = true, Ellipsize = true }
            : new TextElement(text, Color: color, NoWrap: true) { Ellipsize = true });
    private const float PanelGap = 8f;

    private HudElement SectionPanel(Func<string> caption, Func<string> hint, params HudElement[] children)
        => CaptionedPanel(caption, hint, null, children);

    private HudElement CaptionedPanel(Func<string> caption, Func<string> hint, HudElement? trailing, HudElement[] children)
    {
        var captionRow = new HudElement[trailing is null ? 2 : 3];
        captionRow[0] = new TextElement(Upper(caption), Color: () => MutedColor(), NoWrap: true, FontSize: 12) { Bold = true };
        captionRow[1] = new CellElement(new TextElement(hint, Color: () => MutedColor(), Align: TextAlign.Right, NoWrap: true)
            { Ellipsize = true }, Weight: 1f);
        if (trailing is not null) captionRow[2] = trailing;
        var items = new HudElement[children.Length + 1];
        items[0] = FixedLine(CaptionRowHeight, new RowElement(captionRow, Gap: 6f));
        Array.Copy(children, 0, items, 1, children.Length);
        return new BubbleElement(new ColumnElement(items, Gap: 6f))
        {
            FillColorFn = () => Fade(PanelColor()), CornerRadius = TileRadius, PaddingX = 10f, PaddingY = 8f,
        };
    }

    // Upper-cases a localized caption once per language (the source string is a cached catalog entry, so a reference
    // compare spots a language switch) instead of allocating a new string on every poll.
    private static Func<string> Upper(Func<string> src)
    {
        string? last = null, upper = "";
        return () =>
        {
            var s = src();
            if (!ReferenceEquals(s, last)) { last = s; upper = s.ToUpperInvariant(); }
            return upper;
        };
    }

    private static string NoHint() => "";

    // ONE always-present line at a fixed height: a 0-width strut pins the row so bold / empty / feedback text never
    // changes the window height; the content cell ellipsizes.
    private static HudElement FixedLine(float height, HudElement content) => new RowElement(new HudElement[]
    {
        new SpacerElement(0f, height),
        new CellElement(content, Weight: 1f),
    }, Gap: 0.01f);

    // ── data-availability tags (approved prototype "Data-availability tags") ────────────────────────────────────────
    // A round chip: 12 px baked white glyph tinted with the tag colour + bold 11 label. 20 px in a caption slot, 18 px
    // inline in a value cell. Kind is LIVE (label, colours and glyph all follow it), so one element serves every state;
    // None hides it. MaxWidth pins the cap: a MaxWidth-0 bubble under a Conditional caps itself at the 100 px default
    // (KB WindowBuilder-Patterns, Dungeon Confirm 6b19310) and "ไม่ได้แชร์" / "Hindi ibinabahagi" are wider than that.
    private enum TagKind : byte { None, Nearby, NotShared, Profile }

    private const float TagCaptionH = 20f, TagInlineH = 18f, TagMaxW = 170f;
    private const float NearbyWashAlpha = 0.18f, NotSharedWashAlpha = 0.08f, ProfileWashAlpha = 0.15f;

    private HudElement DataTag(Func<TagKind> kind, float height)
    {
        var text = new ContrastCache();
        ColorRgba Fill(TagKind k) => k switch
        {
            TagKind.Nearby    => Over(AccentColor() with { A = NearbyWashAlpha }, PanelColor()),
            TagKind.Profile   => Over(SuccessColor() with { A = ProfileWashAlpha }, PanelColor()),
            _                 => Over(TextColor() with { A = NotSharedWashAlpha }, PanelColor()),
        };
        // "muted-light" for Not shared = halfway between muted and text; the coloured kinds keep 4.5:1 on their wash.
        ColorRgba Ink(TagKind k) => k switch
        {
            TagKind.Nearby  => text.Get(AccentColor(), Fill(k), PillMinContrast),
            TagKind.Profile => text.Get(SuccessColor(), Fill(k), PillMinContrast),
            _               => Mix(MutedColor(), TextColor(), 0.45f),
        };
        byte[]? Icon(TagKind k) => (k switch
        {
            TagKind.Nearby  => RadarIcon,
            TagKind.Profile => CardIcon,
            _               => LockIcon,
        }).Pick(_uiScale);
        string Label(TagKind k) => _loc.T(k switch
        {
            TagKind.Nearby  => "ei.tag.nearby",
            TagKind.Profile => "ei.tag.profile",
            _               => "ei.tag.notShared",
        });
        return new ConditionalElement(() => kind() != TagKind.None, new BubbleElement(new RowElement(new HudElement[]
        {
            new SpacerElement(6f, height),
            Glyph(() => Icon(kind()), RadarIcon.Units, () => Ink(kind())),
            new SpacerElement(4f, height),
            DropBold(new TextElement(() => Label(kind()), Color: () => Ink(kind()), NoWrap: true, FontSize: 11) { Bold = true }),
            new SpacerElement(7f, height),
        }, Gap: 0.01f))
        {
            FillColorFn = () => Fade(Fill(kind())), CornerRadius = RoundRadius(height), PaddingX = 0f, PaddingY = 0f,
            MaxWidth = TagMaxW,
        });
    }

    // ── accent icon tile (ToolHeader) ─────────────────────────────────────────────────────────────────────────────
    // A 40 px tile in the LIVE theme accent with the inspected player's class icon (24 px) centred: the tile follows the
    // theme (house "Icon tile"). Radius 9 at 40 px. The padding IS the margin → exactly 40 square.
    private const float ToolIconSize = 40f;
    private const int ToolGlyphSize = 24;

    private HudElement HeaderIconTile()
    {
        float pad = (ToolIconSize - ToolGlyphSize) / 2f;
        return new CellElement(new BubbleElement(new RowElement(new HudElement[]
            {
                new GameTextureElement(ProfIcon, ToolGlyphSize, ToolGlyphSize, () => _profUv),
            }, Gap: 0.01f, Justify: RowJustify.Center))
        {
            FillColorFn = () => Fade(AccentColor()), CornerRadius = 9f, PaddingX = pad, PaddingY = pad,
        }, Width: ToolIconSize);
    }

    // ── round ✕ (popover headers) ─────────────────────────────────────────────────────────────────────────────────
    // Icon-only ✕: a round 24 px hover wash around a Bare selectable; muted glyph at rest, text colour on hover. The ✕ is
    // the generated glyph (bold "✕" text can draw blank, KB §6).
    private HudElement RoundCloseButton(Action onClose)
    {
        var hover = new HoverFlag();
        return new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
            {
                new SpacerElement(5f, 24f),
                Glyph(CloseIcon, () => hover.On ? TextColor() : MutedColor()),
                new SpacerElement(5f, 24f),
            }, Gap: 0.01f, Justify: RowJustify.Center),
            OnClick: onClose) { Bare = true, OnHover = over => hover.On = over })
        {
            FillColorFn = () => hover.On ? Fade(Menu.MenuText with { A = HoverMix }) : Transparent,
            CornerRadius = RoundRadius(24f), PaddingX = 0f, PaddingY = 0f,
        };
    }

    // Screen px (OnClickWithRect, Input.mousePosition) → canvas units (SetRect). Scale first, or a popover drifts off its
    // anchor whenever the UI scale isn't 1 (WindowBuilder-Patterns.md, click-to-open tooltip).
    private float ScreenToCanvas()
    {
        var fw = _services.Framework;
        return fw.ScreenWidth > 0 && fw.CanvasWidth > 1 ? (float)fw.CanvasWidth / fw.ScreenWidth : 1f;
    }

    private float CanvasHeight()
    {
        var fw = _services.Framework;
        return fw.CanvasHeight > 1 ? fw.CanvasHeight : fw.ScreenHeight;
    }

    private float CanvasWidthUnits()
    {
        var fw = _services.Framework;
        return fw.CanvasWidth > 1 ? fw.CanvasWidth : fw.ScreenWidth;
    }
}
