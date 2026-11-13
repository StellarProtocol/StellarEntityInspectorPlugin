using System;
using System.Collections.Generic;
using System.Globalization;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.EntityInspector;

// PORTRAIT panel (approved prototype): caption + the Weapon chip, the live 3D model (the game's own UI-model pipeline via
// IEntityPortrait — works for self and other players), a muted gesture hint, the HP bar with its shield segment and the
// two equipped Battle Imagines as 26 px tiles. The model fills the panel's leftover height (the host's flexibleHeight)
// and is aspect-preserved; everything under it has a fixed height, so nothing moves when HP/shield/imagines change.
public sealed partial class Plugin
{
    private const int PortraitMinW = 248, PortraitMinH = 260;   // RT host minimum (panel 268 − 2×10 padding)
    private const float HpBarH = 20f, HpFrameRadius = 6f, HpFramePad = 2f;
    private const float TileRowH = 26f;
    private static readonly ColorRgba ShieldBand = new(1f, 1f, 1f, 0.55f);   // light overlay after the HP fill

    private HudElement BuildPortraitPanel() => CaptionedPanel(
        () => _loc.T("ei.portrait.caption"), NoHint, WeaponChip(), new HudElement[]
        {
            new RenderTextureHostElement(() => _services.EntityPortrait.Texture, PortraitMinW, PortraitMinH,
                OnDrag:           (dx, dy) => _services.EntityPortrait.Orbit(dx, dy),
                OnScroll:         d        => _services.EntityPortrait.Zoom(d),
                OnPan:            (dx, dy) => _services.EntityPortrait.Pan(dx, dy),     // shift+drag still pans the camera
                OnViewportResize: (w, h)   => _services.EntityPortrait.SetViewport(w, h),   // RT tracks the pane size
                TransparentBackground: true),   // only the character draws — no dark box behind (user request 2026-06-13)
            new TextElement(() => _loc.T("ei.portrait.gestures"), Color: () => MutedColor(), NoWrap: true, FontSize: 12)
                { Ellipsize = true, Height = 16f },
            BuildHpBar(),
            BuildImagineTile(0),
            BuildImagineTile(1),
        });

    // ── Weapon chip (house "clickable on/off chip", as Wardrobe's PREVIEW chip) ───────────────────────────────────
    // ONE fixed-width cell → a Conditional swapping two COMPLETE chips (never a Conditional inside a bubble's measured
    // child — the re-hug gotcha), both sharing one hover flag, so the chip is the same size in both states. On = bold
    // Success on its 22% wash; Off = bold muted on a faint muted wash (still pressable, rule 22). Width: the prototype's
    // 84 px cut "Weapon: Off" to "Weapon:…" in the game font (Wardrobe 8db21eb measured it) — 120 fits every locale.
    private const float WeaponChipW = 120f, WeaponChipH = 24f, WeaponChipPadX = 10f;
    private const float WeaponChipOffWash = 0.15f, WeaponChipHoverAdd = 0.16f;

    private HudElement WeaponChip()
    {
        var hover = new HoverFlag();
        float Wash(float a) => a + (hover.On ? WeaponChipHoverAdd : 0f);
        return new CellElement(new ConditionalElement(() => _showWeapon,
            Then: WeaponChipShell(() => _loc.T("ei.portrait.weaponOn"), SuccessColor,
                () => Over(SuccessColor() with { A = Wash(ChipWashAlpha) }, PanelColor()), hover),
            Else: WeaponChipShell(() => _loc.T("ei.portrait.weaponOff"), MutedColor,
                () => Over(MutedColor() with { A = Wash(WeaponChipOffWash) }, PanelColor()), hover)),
            Width: WeaponChipW);
    }

    private HudElement WeaponChipShell(Func<string> label, Func<ColorRgba> text, Func<ColorRgba> fill, HoverFlag hover)
        => new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
            {
                new SpacerElement(WeaponChipPadX, WeaponChipH),   // padding + the 24 px height strut
                DropBold(new TextElement(label, Color: () => text(), Width: WeaponChipW - 2f * WeaponChipPadX,
                    Align: TextAlign.Center, NoWrap: true, FontSize: 12) { Bold = true, Ellipsize = true }),
                new SpacerElement(WeaponChipPadX, WeaponChipH),
            }, Gap: 0.01f, Justify: RowJustify.Center),
            OnClick: () => SetShowWeapon(!_showWeapon))
            { Bare = true, OnHover = over => hover.On = over })
        {
            // MaxWidth pins the width: a MaxWidth-0 bubble under a Conditional caps itself at the 100 px default.
            FillColorFn = () => Fade(fill()), CornerRadius = RoundRadius(WeaponChipH), PaddingX = 0f, PaddingY = 0f,
            MaxWidth = WeaponChipW,
        };

    // ── HP bar with shield segment ────────────────────────────────────────────────────────────────────────────────
    // The Modern bar is the toolkit's only bar with a second fill (Overlay01): drawn BEHIND the HP fill as
    // (hp + shield) / max, so only the shield's excess shows as a light segment after the HP fill. The Modern track is
    // flat (square), so it sits inside a recessed r6 frame (2 px padding) to keep the panel's rounded look. Label =
    // "HP a / b · Shield c" (shield part only when > 0), left-aligned (the Modern bar has no centred label).
    private HudElement BuildHpBar() => new BubbleElement(new BarElement(HpFraction, new ColorRgba(0.37f, 0.83f, 0.55f, 1f),
        Label: () => _hpLabel)
    {
        Style = BarStyle.Modern, FillWidth = true, Height = HpBarH - 2f * HpFramePad, LabelFontSize = 12,
        FillColorFn = () => Fade(SuccessColor()), LabelColorFn = () => White,
        Overlay01 = ShieldFraction, OverlayColor = ShieldBand, OverlayInFront = false,
    })
    {
        FillColorFn = () => DisabledShade, CornerRadius = HpFrameRadius, PaddingX = HpFramePad, PaddingY = HpFramePad,
    };

    private float _hpFrac, _shieldFrac;
    private string _hpLabel = "";
    private long _hpLastHp = -1, _hpLastMax = -1, _hpLastShield = -1;

    private float HpFraction() => _hpFrac;
    private float ShieldFraction() => _shieldFrac;

    // 10 Hz: vitals → fractions (quantised to 0.5 % so a tick of regen doesn't re-mesh the bar) + the cached label.
    private void RebuildVitals()
    {
        long hp = 0, max = 0, shield = 0;
        var v = TargetVitals();
        if (v.IsKnown && v.MaxHp > 0) { hp = v.Hp; max = v.MaxHp; shield = v.Shield; }
        else if (IsSelf && _services.PlayerState.MaxHealth > 0)
        { hp = _services.PlayerState.Health; max = _services.PlayerState.MaxHealth; }
        if (hp == _hpLastHp && max == _hpLastMax && shield == _hpLastShield) return;
        _hpLastHp = hp; _hpLastMax = max; _hpLastShield = shield;
        if (max <= 0) { _hpFrac = _shieldFrac = 0f; _hpLabel = "—"; return; }
        _hpFrac = Quantise((float)hp / max);
        _shieldFrac = shield > 0 ? Quantise(Math.Min(1f, (float)(hp + shield) / max)) : 0f;
        var ci = CultureInfo.InvariantCulture;
        _hpLabel = shield > 0
            ? _loc.TFormat("ei.portrait.hpShield", hp.ToString("N0", ci), max.ToString("N0", ci), shield.ToString("N0", ci))
            : _loc.TFormat("ei.portrait.hp", hp.ToString("N0", ci), max.ToString("N0", ci));
    }

    private static float Quantise(float f) => MathF.Round(Math.Clamp(f, 0f, 1f) * 200f) / 200f;

    // ── imagine tiles ─────────────────────────────────────────────────────────────────────────────────────────────
    // (name, precomputed star string, icon skill id). Identity = the first 2 Battle Imagines in the loadout (same filter
    // the CombatMeter uses). Star source = SkillLevel.Tier (remodel_level) clamped 0-5 — .Level read 1★ for everything
    // in-world 2026-06-13. Strings are built at 10 Hz, so the tile Funcs are pure cache reads. Empty slot = an empty tile.
    private readonly List<(string Name, string Stars, int SkillId)> _imagines = new(2);
    private static readonly string[] StarStrings =
        { "☆☆☆☆☆", "★☆☆☆☆", "★★☆☆☆", "★★★☆☆", "★★★★☆", "★★★★★" };
    private readonly UvRect[] _imgUv = { new(0f, 0f, 1f, 1f), new(0f, 0f, 1f, 1f) };

    private void RebuildImagines()
    {
        _imagines.Clear();
        foreach (var sl in TargetSkills())
        {
            if (_services.ResonanceData.GetImagineForSkill(sl.SkillId) is not { } info) continue;
            var stars = StarStrings[sl.Tier < 0 ? 0 : sl.Tier > 5 ? 5 : sl.Tier];
            _imagines.Add((info.Name, stars, info.SkillId));
            if (_imagines.Count == 2) break;
        }
    }

    private HudElement BuildImagineTile(int slot) => new BubbleElement(new RowElement(new HudElement[]
    {
        new SpacerElement(6f, TileRowH),
        new GameTextureElement(() => ImagineIcon(slot), 18, 18, () => _imgUv[slot]),
        new SpacerElement(6f, 0f),
        new CellElement(new TextElement(() => slot < _imagines.Count ? _imagines[slot].Name : "", Color: () => TextColor(),
            NoWrap: true) { Ellipsize = true }, Weight: 1f),
        new TextElement(() => slot < _imagines.Count ? _imagines[slot].Stars : "", Color: () => CautionColor(), NoWrap: true),
        new SpacerElement(6f, 0f),
    }, Gap: 0.01f))
    {
        FillColorFn = () => Fade(TileColor()), CornerRadius = TileRadius, PaddingX = 0f, PaddingY = 0f,
    };

    private object? ImagineIcon(int slot)
    {
        if (slot >= _imagines.Count) return null;
        return _services.GameAssets.LoadImagineIcon(_imagines[slot].SkillId, out _imgUv[slot]);
    }

    // Portrait "Weapon" chip: flip the (global) portrait service — it re-asserts the weapon exclusion on the live model
    // immediately — and persist the choice so it survives a restart (config section "portrait", key "showWeapon").
    private void SetShowWeapon(bool v)
    {
        _showWeapon = v;
        _services.EntityPortrait.ShowWeapon = v;
        var s = _services.Config.GetSection("portrait");
        s.Set("showWeapon", v);
        s.Save();
    }
}
