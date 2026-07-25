using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimWorldAccess
{
    /// <summary>
    /// Stops the whole game from hard-freezing inside the vanilla colonist-bar layout search.
    ///
    /// <c>ColonistBarDrawLocsFinder.FindBestScale</c> shrinks a scale factor in a
    /// <c>while (true)</c> loop, multiplying it by 0.95 each pass, and the ONLY way out is the
    /// layout fitting every entry in at most three rows. There is no lower bound on the scale and
    /// no iteration cap. When the entry set can't be made to fit — enough colonists plus a
    /// mechanitor's controlled mechs spread across several groups, on some screen widths — the scale
    /// underflows toward zero and the loop spins the main thread forever: one core pinned at 100%,
    /// no exception, no log line, the game "not responding". A full native dump of a live freeze
    /// caught the main thread deep in this method, churning List&lt;Pawn&gt; enumerators through the GC.
    ///
    /// This is the same vanilla trap documented in <see cref="ColonistBarOrderHelper"/>: that fix
    /// removed RimWorld Access's own calls that forced the recache (comma/period cycling, bar
    /// navigation, the skills table), but vanilla's normal per-frame <c>ColonistBarOnGUI</c> draw
    /// still runs the search, so the freeze can still be reached through the game's own rendering —
    /// which is what happened while a targeted ability was being aimed and the map cursor moved.
    ///
    /// The transpiler adds a floor to the scale: once it falls below <see cref="MinScale"/> the loop
    /// returns the current value instead of looping again. The out parameters (onlyOneRow,
    /// maxPerGlobalRow) are assigned every pass before this point, so the bailout returns a
    /// coherent (if tiny) layout. In practice a solvable bar converges far above the floor, so this
    /// only ever triggers on the degenerate case that would otherwise hang.
    /// </summary>
    [HarmonyPatch(typeof(ColonistBarDrawLocsFinder), "FindBestScale")]
    public static class ColonistBarLayoutFreezeFixPatch
    {
        // Vanilla's smallest "allowed rows" tier kicks in at scale <= 0.42, and any solvable bar
        // fits well before the scale gets this small (maxPerGlobalRow grows without bound as the
        // scale shrinks). A floor an order of magnitude below that tier keeps every real layout
        // identical to vanilla and only catches the never-converges case.
        private const float MinScale = 0.02f;

        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator il)
        {
            var codes = new List<CodeInstruction>(instructions);

            // Locate `num *= 0.95f`: the ldc.r4 0.95 constant, then the stloc that writes num back.
            int mulConstIdx = -1;
            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i].opcode == OpCodes.Ldc_R4 &&
                    codes[i].operand is float f && Mathf.Abs(f - 0.95f) < 0.0001f)
                {
                    mulConstIdx = i;
                    break;
                }
            }

            if (mulConstIdx < 0)
            {
                Log.Warning("[RimWorld Access] ColonistBar freeze fix: could not find the 0.95 scale-shrink constant; " +
                            "leaving FindBestScale unpatched (colonist-bar layout may still hang).");
                return codes;
            }

            // The stloc immediately after the multiply stores the shrunk scale back into `num`.
            int storeIdx = -1;
            for (int i = mulConstIdx + 1; i < codes.Count; i++)
            {
                if (codes[i].opcode == OpCodes.Stloc_0 || codes[i].opcode == OpCodes.Stloc_1 ||
                    codes[i].opcode == OpCodes.Stloc_2 || codes[i].opcode == OpCodes.Stloc_3 ||
                    codes[i].opcode == OpCodes.Stloc || codes[i].opcode == OpCodes.Stloc_S)
                {
                    storeIdx = i;
                    break;
                }
            }

            if (storeIdx < 0 || storeIdx + 1 >= codes.Count)
            {
                Log.Warning("[RimWorld Access] ColonistBar freeze fix: could not find the scale store; " +
                            "leaving FindBestScale unpatched.");
                return codes;
            }

            CodeInstruction storeNum = codes[storeIdx];
            CodeInstruction loadNum = LoadForStore(storeNum);
            if (loadNum == null)
            {
                Log.Warning("[RimWorld Access] ColonistBar freeze fix: unrecognized scale local; " +
                            "leaving FindBestScale unpatched.");
                return codes;
            }

            // The instruction right after the store is the unconditional branch back to the loop top.
            // Tag it as the "continue looping" target; our guard falls through to our own return when
            // the scale has underflowed past the floor.
            CodeInstruction continueTarget = codes[storeIdx + 1];
            var continueLabel = il.DefineLabel();
            // Build: if (num >= MinScale) goto continueTarget; else return num;
            var guard = new List<CodeInstruction>
            {
                (CodeInstruction)loadNum.Clone(),                    // ldloc num
                new CodeInstruction(OpCodes.Ldc_R4, MinScale),       // MinScale
                new CodeInstruction(OpCodes.Bge_Un, continueLabel),  // num >= MinScale -> keep looping
                (CodeInstruction)loadNum.Clone(),                    // else: ldloc num
                new CodeInstruction(OpCodes.Ret)                     // return num
            };

            continueTarget.labels.Add(continueLabel);
            codes.InsertRange(storeIdx + 1, guard);
            return codes;
        }

        /// <summary>Maps a stloc that writes the scale local to the matching ldloc.</summary>
        private static CodeInstruction LoadForStore(CodeInstruction store)
        {
            if (store.opcode == OpCodes.Stloc_0) return new CodeInstruction(OpCodes.Ldloc_0);
            if (store.opcode == OpCodes.Stloc_1) return new CodeInstruction(OpCodes.Ldloc_1);
            if (store.opcode == OpCodes.Stloc_2) return new CodeInstruction(OpCodes.Ldloc_2);
            if (store.opcode == OpCodes.Stloc_3) return new CodeInstruction(OpCodes.Ldloc_3);
            if (store.opcode == OpCodes.Stloc || store.opcode == OpCodes.Stloc_S)
                return new CodeInstruction(OpCodes.Ldloc, store.operand);
            return null;
        }
    }
}
