using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimWorldAccess
{
    /// <summary>
    /// Reflection facade for the optional third-party mod <b>Drafted Auto-Combat</b>
    /// (packageId <c>RedMattis.AutoCombat</c>, assembly <c>AutoCombat</c>, namespace
    /// <c>BigAndSmall</c>). As with every optional mod, RimWorld Access takes no assembly
    /// reference: every type and member is resolved by name once and cached, and
    /// <see cref="Available"/> stays false when the mod is absent, leaving the whole feature
    /// inert.
    ///
    /// The mod keeps one <c>DraftedActionData</c> per pawn holding four independent flags, and
    /// exposes each as a drafted gizmo. Those gizmos derive from vanilla <c>Command_Toggle</c>,
    /// so RimWorld Access already reads them one pawn at a time — what it could not do is set
    /// them across a whole selection, which is what <see cref="AutoCombatState"/> adds.
    ///
    /// Writes always go through the mod's own <c>Toggle*</c> methods rather than the backing
    /// fields, because those methods also call the mod's <c>RefreshDraft()</c> — setting the
    /// field alone would leave the pawn's current job stale until something else refreshed it.
    /// </summary>
    public static class AutoCombatReflection
    {
        /// <summary>The four per-pawn switches the mod exposes, in the order its gizmos appear.</summary>
        public enum Flag
        {
            /// <summary>Master switch. The other three only surface as gizmos while this is on.</summary>
            Hunt,
            TakeCover,
            MeleeCharge,
            FullAIControl,
            /// <summary>Auto-cast every compatible ability (a list, exposed here as a flag).</summary>
            AutoUseAll
        }

        private static bool resolved;
        private static bool available;

        private static MethodInfo getDataMethod;      // static DraftedActionData GetData(Pawn)
        private static FieldInfo huntField;
        private static FieldInfo takeCoverField;
        private static FieldInfo meleeChargeField;
        private static FieldInfo fullAIControlField;
        private static FieldInfo autocastAbilitiesField;

        private static MethodInfo toggleHunt;         // bool ToggleHuntMode()
        private static MethodInfo toggleCover;        // bool ToggleCoverMode(bool? force = null)
        private static MethodInfo toggleCharge;       // bool ToggleMeleeCharge(bool? force = null)
        private static MethodInfo toggleAI;           // bool ToggleFullAIControl(bool? force = null)
        private static MethodInfo toggleAutoForAll;   // void ToggleAutoForAll(bool? force = null)

        private static Type autoCombatExtensionType;  // BigAndSmall.AutoCombatExtension
        private static FieldInfo canMassToggleField;

        public static bool Available
        {
            get
            {
                EnsureResolved();
                return available;
            }
        }

        private static void EnsureResolved()
        {
            if (resolved)
            {
                return;
            }
            resolved = true;

            try
            {
                var holderType = AccessTools.TypeByName("BigAndSmall.DraftedActionHolder");
                var dataType = AccessTools.TypeByName("BigAndSmall.DraftedActionData");
                if (holderType == null || dataType == null)
                {
                    return;
                }

                getDataMethod = AccessTools.Method(holderType, "GetData", new[] { typeof(Pawn) });

                huntField = AccessTools.Field(dataType, "hunt");
                takeCoverField = AccessTools.Field(dataType, "takeCover");
                meleeChargeField = AccessTools.Field(dataType, "meleeCharge");
                fullAIControlField = AccessTools.Field(dataType, "fullAIControl");
                autocastAbilitiesField = AccessTools.Field(dataType, "autocastAbilities");

                toggleHunt = AccessTools.Method(dataType, "ToggleHuntMode");
                toggleCover = AccessTools.Method(dataType, "ToggleCoverMode");
                toggleCharge = AccessTools.Method(dataType, "ToggleMeleeCharge");
                toggleAI = AccessTools.Method(dataType, "ToggleFullAIControl");
                toggleAutoForAll = AccessTools.Method(dataType, "ToggleAutoForAll");

                autoCombatExtensionType = AccessTools.TypeByName("BigAndSmall.AutoCombatExtension");
                if (autoCombatExtensionType != null)
                {
                    canMassToggleField = AccessTools.Field(autoCombatExtensionType, "canMassToggle");
                }

                available = getDataMethod != null
                    && huntField != null && takeCoverField != null
                    && meleeChargeField != null && fullAIControlField != null
                    && autocastAbilitiesField != null
                    && toggleHunt != null && toggleCover != null
                    && toggleCharge != null && toggleAI != null && toggleAutoForAll != null;
            }
            catch (Exception e)
            {
                Log.Warning($"[RimWorld Access] Auto-Combat compatibility unavailable: {e.Message}");
                available = false;
            }
        }

        private static object GetData(Pawn pawn)
        {
            if (!Available || pawn == null)
            {
                return null;
            }
            try
            {
                return getDataMethod.Invoke(null, new object[] { pawn });
            }
            catch (Exception e)
            {
                Log.Warning($"[RimWorld Access] Auto-Combat GetData failed: {e.Message}");
                return null;
            }
        }

        /// <summary>True if the mod tracks this pawn at all (player-faction humanlike, drafted or not).</summary>
        public static bool TracksPawn(Pawn pawn) => GetData(pawn) != null;

        public static bool GetFlag(Pawn pawn, Flag flag)
        {
            var data = GetData(pawn);
            if (data == null)
            {
                return false;
            }
            try
            {
                switch (flag)
                {
                    case Flag.Hunt: return (bool)huntField.GetValue(data);
                    case Flag.TakeCover: return (bool)takeCoverField.GetValue(data);
                    case Flag.MeleeCharge: return (bool)meleeChargeField.GetValue(data);
                    case Flag.FullAIControl: return (bool)fullAIControlField.GetValue(data);
                    // The mod has no "auto-use all" bool — the gizmo reports itself active when
                    // the auto-cast list is non-empty, so mirror that exactly.
                    default: return CountAutocast(data) > 0;
                }
            }
            catch (Exception e)
            {
                Log.Warning($"[RimWorld Access] Auto-Combat read '{flag}' failed: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Set a flag through the mod's own toggle method. Returns true if the value afterwards
        /// matches what was asked for — <see cref="Flag.AutoUseAll"/> can silently refuse when
        /// the pawn owns no ability the mod is willing to auto-cast (see
        /// <see cref="CompatibleAbilityCount"/>), and the caller reports that.
        /// </summary>
        public static bool SetFlag(Pawn pawn, Flag flag, bool value)
        {
            var data = GetData(pawn);
            if (data == null)
            {
                return false;
            }
            try
            {
                switch (flag)
                {
                    case Flag.Hunt:
                        // ToggleHuntMode takes no argument, so only call it when it would flip.
                        if ((bool)huntField.GetValue(data) != value)
                        {
                            toggleHunt.Invoke(data, null);
                        }
                        break;
                    case Flag.TakeCover:
                        toggleCover.Invoke(data, ForceArg(toggleCover, value));
                        break;
                    case Flag.MeleeCharge:
                        toggleCharge.Invoke(data, ForceArg(toggleCharge, value));
                        break;
                    case Flag.FullAIControl:
                        toggleAI.Invoke(data, ForceArg(toggleAI, value));
                        break;
                    default:
                        toggleAutoForAll.Invoke(data, ForceArg(toggleAutoForAll, value));
                        break;
                }
                return GetFlag(pawn, flag) == value;
            }
            catch (Exception e)
            {
                Log.Warning($"[RimWorld Access] Auto-Combat write '{flag}' failed: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// The mod's setters take an optional <c>bool?</c> force argument; pass it when present
        /// so we drive the value directly instead of flipping whatever is there.
        /// </summary>
        private static object[] ForceArg(MethodInfo method, bool value)
        {
            return method.GetParameters().Length == 0
                ? null
                : new object[] { value };
        }

        private static int CountAutocast(object data)
        {
            return (autocastAbilitiesField.GetValue(data) as ICollection)?.Count ?? 0;
        }

        /// <summary>
        /// How many of the pawn's abilities the mod would actually auto-cast, mirroring its own
        /// test: a vanilla ability whose def either sets <c>aiCanUse</c> or carries the mod's
        /// <c>AutoCombatExtension</c> with mass-toggle allowed.
        ///
        /// This matters because the mod reads <c>Pawn.abilities</c> only. Abilities that live in
        /// another tracker — notably Vanilla Psycasts Expanded, which keeps psycasts on a VEF
        /// comp — are invisible to it, so "auto-use all" is a no-op for a pure psycaster no
        /// matter how many psycasts they know. Counting here lets the menu say so instead of
        /// appearing to do nothing.
        /// </summary>
        public static int CompatibleAbilityCount(Pawn pawn)
        {
            var abilities = pawn?.abilities?.AllAbilitiesForReading;
            if (abilities == null)
            {
                return 0;
            }

            int count = 0;
            foreach (var ability in abilities)
            {
                var def = ability?.def;
                if (def == null)
                {
                    continue;
                }
                if (def.aiCanUse || HasMassToggleExtension(def))
                {
                    count++;
                }
            }
            return count;
        }

        private static bool HasMassToggleExtension(Def def)
        {
            if (autoCombatExtensionType == null || def.modExtensions == null)
            {
                return false;
            }
            foreach (var extension in def.modExtensions)
            {
                if (extension == null || !autoCombatExtensionType.IsInstanceOfType(extension))
                {
                    continue;
                }
                if (canMassToggleField == null || (canMassToggleField.GetValue(extension) is bool b && b))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// The colonists this menu acts on: the current selection, or the single selected pawn.
        /// Mirrors the mod's own batch menu, which walks <c>Find.Selector.SelectedPawns</c>.
        /// </summary>
        public static List<Pawn> GetTargetPawns()
        {
            var result = new List<Pawn>();
            if (Find.Selector == null)
            {
                return result;
            }
            foreach (var pawn in Find.Selector.SelectedPawns)
            {
                if (pawn != null && TracksPawn(pawn))
                {
                    result.Add(pawn);
                }
            }
            return result;
        }
    }
}
