// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Generic;
using D2RMultiExport.Lib.Models;

namespace D2RMultiExport.Lib.Translation;

/// <summary>
/// Maps the raw cube-recipe ingredient/output qualifier tokens (as they appear in
/// CubeMain.txt — e.g. <c>"low"</c>, <c>"mag"</c>, <c>"sock=3"</c>, <c>"pre=12"</c>)
/// to <see cref="KeyedLine"/> rows so the keyed wire-format bundle can render
/// them in the active language without relying on the hardcoded English friendly
/// strings emitted by <see cref="Import.CubeRecipeImporter"/>'s
/// <c>InputDisplay</c>/<c>OutputDisplay</c> tables.
/// <para/>
/// Adding a new token? Add the constant to <see cref="SyntheticStringRegistry.Keys"/>,
/// the seed entry to <c>Config/synthetic-strings.json</c>, and a row to the
/// matching dictionary below.
/// </summary>
public static class CubeQualifierKeyMap
{
    private static readonly IReadOnlyDictionary<string, string> InputFixedTokens =
        new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
        {
            ["low"] = SyntheticStringRegistry.Keys.CubeQualifierLowQuality,
            ["nor"] = SyntheticStringRegistry.Keys.CubeQualifierNormalQuality,
            ["hiq"] = SyntheticStringRegistry.Keys.CubeQualifierHighQualitySuperior,
            ["mag"] = SyntheticStringRegistry.Keys.CubeQualifierMagicItem,
            ["set"] = SyntheticStringRegistry.Keys.CubeQualifierSetItem,
            ["rar"] = SyntheticStringRegistry.Keys.CubeQualifierRareItem,
            ["uni"] = SyntheticStringRegistry.Keys.CubeQualifierUniqueItem,
            ["crf"] = SyntheticStringRegistry.Keys.CubeQualifierCraftedItem,
            ["tmp"] = SyntheticStringRegistry.Keys.CubeQualifierTemperedItem,
            ["nos"] = SyntheticStringRegistry.Keys.CubeQualifierNoSockets,
            ["sock"] = SyntheticStringRegistry.Keys.CubeQualifierItemWithSockets,
            ["noe"] = SyntheticStringRegistry.Keys.CubeQualifierNotEthereal,
            ["eth"] = SyntheticStringRegistry.Keys.CubeQualifierEthereal,
            ["upg"] = SyntheticStringRegistry.Keys.CubeQualifierUpgradeable,
            ["bas"] = SyntheticStringRegistry.Keys.CubeQualifierBasicItem,
            ["exc"] = SyntheticStringRegistry.Keys.CubeQualifierExceptionalItem,
            ["eli"] = SyntheticStringRegistry.Keys.CubeQualifierEliteItem,
            ["nru"] = SyntheticStringRegistry.Keys.CubeQualifierNotARuneword
        };

    private static readonly IReadOnlyDictionary<string, string> OutputFixedTokens =
        new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
        {
            ["low"] = SyntheticStringRegistry.Keys.CubeQualifierLowQualityItem,
            ["nor"] = SyntheticStringRegistry.Keys.CubeQualifierNormalItem,
            ["hiq"] = SyntheticStringRegistry.Keys.CubeQualifierHighQualityItemSuperior,
            ["mag"] = SyntheticStringRegistry.Keys.CubeQualifierMagicItem,
            ["set"] = SyntheticStringRegistry.Keys.CubeQualifierSetItem,
            ["rar"] = SyntheticStringRegistry.Keys.CubeQualifierRareItem,
            ["uni"] = SyntheticStringRegistry.Keys.CubeQualifierUniqueItem,
            ["crf"] = SyntheticStringRegistry.Keys.CubeQualifierCraftedItem,
            ["tmp"] = SyntheticStringRegistry.Keys.CubeQualifierTemperedItem,
            ["eth"] = SyntheticStringRegistry.Keys.CubeQualifierEtherealItem,
            ["sock"] = SyntheticStringRegistry.Keys.CubeQualifierItemWithSockets,
            ["mod"] = SyntheticStringRegistry.Keys.CubeQualifierKeepModifiers,
            ["uns"] = SyntheticStringRegistry.Keys.CubeQualifierUnsocketDestroy,
            ["rem"] = SyntheticStringRegistry.Keys.CubeQualifierRemoveSocketedReturn,
            ["reg"] = SyntheticStringRegistry.Keys.CubeQualifierRegenerateUnique,
            ["exc"] = SyntheticStringRegistry.Keys.CubeQualifierExceptionalItem,
            ["eli"] = SyntheticStringRegistry.Keys.CubeQualifierEliteItem,
            ["rep"] = SyntheticStringRegistry.Keys.CubeQualifierRepairItem,
            ["rch"] = SyntheticStringRegistry.Keys.CubeQualifierRechargeCharges,
            // Output-only English literals embedded in CubeMain.txt for portals.
            ["Cow Portal"] = SyntheticStringRegistry.Keys.CubeNoteCowPortal,
            ["Pandemonium Portal"] = SyntheticStringRegistry.Keys.CubeNotePandemoniumPortal,
            ["Pandemonium Finale Portal"] = SyntheticStringRegistry.Keys.CubeNotePandemoniumFinalePortal,
            ["Red Portal"] = SyntheticStringRegistry.Keys.CubeNoteRedPortal
        };

    /// <summary>
    /// Resolves a single ingredient (input) qualifier token to a <see cref="KeyedLine"/>.
    /// Returns <c>null</c> when the token is not a recognised qualifier (caller should
    /// then treat the token as the <c>mainCode</c>).
    /// </summary>
    public static KeyedLine? TryGetInputQualifier(string rawPart, int currentQuantity)
    {
        if (string.IsNullOrEmpty(rawPart)) return null;
        if (TryParametrised(rawPart, currentQuantity, isInput: true, out var keyed)) return keyed;
        return InputFixedTokens.TryGetValue(rawPart, out var key) ? KeyedLine.Of(key) : null;
    }

    /// <summary>
    /// Resolves a single output qualifier token to a <see cref="KeyedLine"/>.
    /// Returns <c>null</c> when the token is not a recognised qualifier (caller should
    /// then treat the token as the <c>mainCode</c>).
    /// </summary>
    public static KeyedLine? TryGetOutputQualifier(string rawPart, int currentQuantity)
    {
        if (string.IsNullOrEmpty(rawPart)) return null;
        if (TryParametrised(rawPart, currentQuantity, isInput: false, out var keyed)) return keyed;
        return OutputFixedTokens.TryGetValue(rawPart, out var key) ? KeyedLine.Of(key) : null;
    }

    private static bool TryParametrised(string rawPart, int currentQuantity, bool isInput, out KeyedLine keyed)
    {
        keyed = default!;

        // qty=N : both sides emit "Quantity (N)"; if no parsable number, fall back to bare "Quantity".
        if (rawPart.StartsWith("qty=", System.StringComparison.OrdinalIgnoreCase))
        {
            var n = ParseInt(rawPart.AsSpan(4), currentQuantity);
            keyed = n.HasValue
                ? KeyedLine.Of(SyntheticStringRegistry.Keys.CubeQualifierQuantityN, n.Value)
                : KeyedLine.Of(SyntheticStringRegistry.Keys.CubeQualifierQuantity);
            return true;
        }

        if (rawPart.StartsWith("sock=", System.StringComparison.OrdinalIgnoreCase))
        {
            var n = ParseInt(rawPart.AsSpan(5), null);
            keyed = n.HasValue
                ? KeyedLine.Of(SyntheticStringRegistry.Keys.CubeQualifierItemWithSocketsN, n.Value)
                : KeyedLine.Of(SyntheticStringRegistry.Keys.CubeQualifierItemWithSockets);
            return true;
        }

        // pre/suf/lvl are output-only in CubeMain.txt, but accept on either side defensively.
        if (rawPart.StartsWith("pre=", System.StringComparison.OrdinalIgnoreCase))
        {
            var n = ParseInt(rawPart.AsSpan(4), null);
            if (!n.HasValue) return false;
            keyed = KeyedLine.Of(SyntheticStringRegistry.Keys.CubeQualifierForcePrefixN, n.Value);
            return true;
        }

        if (rawPart.StartsWith("suf=", System.StringComparison.OrdinalIgnoreCase))
        {
            var n = ParseInt(rawPart.AsSpan(4), null);
            if (!n.HasValue) return false;
            keyed = KeyedLine.Of(SyntheticStringRegistry.Keys.CubeQualifierForceSuffixN, n.Value);
            return true;
        }

        if (rawPart.StartsWith("lvl=", System.StringComparison.OrdinalIgnoreCase))
        {
            var n = ParseInt(rawPart.AsSpan(4), null);
            if (!n.HasValue) return false;
            keyed = KeyedLine.Of(SyntheticStringRegistry.Keys.CubeQualifierSetLevelN, n.Value);
            return true;
        }

        return false;
    }

    private static int? ParseInt(System.ReadOnlySpan<char> span, int? fallback)
    {
        if (int.TryParse(span, out var v)) return v;
        return fallback;
    }
}
