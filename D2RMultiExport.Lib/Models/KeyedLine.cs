// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json.Serialization;

namespace D2RMultiExport.Lib.Models;

/// <summary>
/// A single rendered line on the wire. Replaces the legacy English `DisplayString` shape.
///
/// The website resolves <see cref="Key"/> against the active-language `strings/{lang}.json`
/// bundle, then performs a positional substitution of <see cref="Args"/> into the
/// translated template (handling `%d`, `%s`, `%+d`, `%D`, `%S`, `%0`–`%9`, `%%`).
///
/// All numeric math (ED%, ethereal 1.5×, durability, smite, elemental, requirement
/// reductions, charm-weight arithmetic, etc.) MUST be finalized before a
/// <see cref="KeyedLine"/> is constructed — args are the post-math values.
/// </summary>
public sealed class KeyedLine
{
    /// <summary>
    /// Translation key. May be a key from the game's own translation tables
    /// (e.g. <c>ModStr3a</c>, <c>StrSklTabItem4</c>) or a synthetic key registered in
    /// <see cref="Translation.SyntheticStringRegistry"/> (e.g. <c>strSkillTabBonusClassOnly</c>).
    /// </summary>
    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    /// <summary>
    /// Positional arguments to splat into the translated template. Each element is
    /// either a finalized number (int/double) or another translation key (string)
    /// referencing a sub-string the website should look up before substitution
    /// (e.g. a skill name key, class name key, monster name key).
    /// </summary>
    [JsonPropertyName("args")]
    public object[] Args { get; set; } = [];

    /// <summary>
    /// Marker for descfunc=19 stats whose underlying stat name actually scales
    /// per character level (e.g. <c>*_perlevel</c>, <c>*_perlvl</c>).
    ///
    /// When <c>true</c>, the website should:
    ///   1. Render <see cref="Key"/> with <see cref="Args"/> normally (the inner
    ///      template + its single per-level growth value).
    ///   2. Append the localized <c>strPerCharacterLevelSuffix</c> string
    ///      (e.g. <c>" (Based on Character Level)"</c>) to the rendered text.
    ///
    /// Replaces the old <c>strPerCharacterLevel</c> wrapper synthetic, which
    /// passed the inner template literal as a positional arg and lost the
    /// growth value during substitution.
    ///
    /// Serialized only when <c>true</c> to keep the wire format minimal.
    /// </summary>
    [JsonPropertyName("perLevel")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool PerLevel { get; set; }

    /// <summary>
    /// Optional itype-scope qualifier for runeword rune-contributed lines that
    /// only apply when the runeword is socketed into a specific equipment class.
    /// One of <c>"weapon"</c>, <c>"shield"</c>, <c>"armor"</c>, or <c>null</c>
    /// (line applies regardless of host item).
    ///
    /// When set, the website should render the line, then append the localized
    /// scope label (synthetic strings <c>strRuneScopeWeapon</c> /
    /// <c>strRuneScopeShield</c> / <c>strRuneScopeArmor</c>, e.g.
    /// <c>" (Weapon)"</c>) so users can identify per-itype rune contributions
    /// on multi-itype runewords (e.g. Steel: Sword/Mace/Axe).
    ///
    /// Serialized only when set to keep the wire format minimal.
    /// </summary>
    [JsonPropertyName("qualifier")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Qualifier { get; set; }

    /// <summary>
    /// Optional class-only suffix translation key (e.g. <c>"AmaOnly"</c>,
    /// <c>"AssOnly"</c>, <c>"NecOnly"</c>) appended to the rendered line as a
    /// localized parenthetical like <c>" (Amazon Only)"</c>. Used by
    /// <c>+X to &lt;Class&gt; Skill Levels</c> and <c>+X to &lt;Skill Tab&gt;</c>
    /// rows so the inner CASC <c>ModStr</c> template can be emitted directly
    /// (with its <c>%+d</c> value substituted) without nesting it inside a
    /// wrapper synthetic that would otherwise duplicate the leading
    /// <c>"+%d to "</c> prefix.
    ///
    /// When set, the website should render the line, then append a single
    /// space followed by the resolved class-only text.
    ///
    /// Serialized only when set, to keep the wire format minimal.
    /// </summary>
    [JsonPropertyName("classOnly")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ClassOnly { get; set; }

    /// <summary>
    /// For set partial-bonus and per-item set-bonus lines: the number of set items
    /// the wearer must equip for this bonus to activate (2..5). Mirrors the
    /// "(N Items)" suffix the legacy English path appends to the rendered string.
    /// Consumers can render their own localized "(N Items)" label using this value
    /// instead of relying on the embedded English text. Mutually exclusive with
    /// <see cref="FullSet"/>.
    ///
    /// Serialized only when set, to keep the wire format minimal.
    /// </summary>
    [JsonPropertyName("itemsRequired")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ItemsRequired { get; set; }

    /// <summary>
    /// For set full-bonus lines: the bonus is granted only when the entire set is
    /// equipped. Mirrors the legacy "(full set)" suffix. Consumers can render
    /// their own localized "(full set)" label using this flag. Mutually exclusive
    /// with <see cref="ItemsRequired"/>.
    ///
    /// Serialized only when <c>true</c>, to keep the wire format minimal.
    /// </summary>
    [JsonPropertyName("fullSet")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool FullSet { get; set; }

    /// <summary>
    /// Sort priority (descPriority from ItemStatCost.txt) — used by the importer
    /// to order properties. Not serialized; the wire format already presents them
    /// in display order.
    /// </summary>
    [JsonIgnore]
    public int Priority { get; set; }

    public static KeyedLine Of(string key, params object[] args) => new() { Key = key, Args = args };
}
