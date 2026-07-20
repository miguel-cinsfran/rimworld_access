using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimWorldAccess
{
    /// <summary>
    /// Reflection facade for the optional third-party mod <b>Vanilla Races Expanded - Android</b>
    /// (packageId <c>vanillaracesexpanded.android</c>, C# namespace <c>VREAndroids</c>). RimWorld
    /// Access must not take a hard assembly reference on an optional mod, so every mod-specific
    /// type and member is resolved by name through <see cref="AccessTools"/> the first time it is
    /// needed and cached. When the mod is not loaded (or its API shifted) resolution fails softly:
    /// <see cref="Available"/> stays false and every accessor is inert.
    ///
    /// The android creation windows (<c>Window_CreateAndroidBase</c> and its three subclasses)
    /// extend the <b>vanilla</b> <see cref="GeneCreationDialogBase"/> unchanged, so all of that
    /// base class's fields/methods are reached directly via <c>typeof(GeneCreationDialogBase)</c>
    /// reflection (no type-name lookup needed - see AndroidCreationState). Only the members the
    /// mod itself declares (selectedGenes, requiredItems, disableAndroidHardwareLimitation,
    /// GeneValidator, and the Utils helpers) need resolution here.
    /// </summary>
    public static class VREAndroidReflection
    {
        public const string PackageId = "vanillaracesexpanded.android";

        private static bool resolved;
        private static bool available;

        // ===== Types =====
        public static Type WindowCreateAndroidBaseType { get; private set; }
        public static Type DialogAndroidProjectListType { get; private set; }
        public static Type DialogAndroidAwakenedChoicesType { get; private set; }
        public static Type ChoiceLetterAndroidAwakenedType { get; private set; }
        public static Type HediffAndroidPartType { get; private set; }
        public static Type WindowCreateAndroidXenotypeType { get; private set; }
        private static Type dialogAndroidProjectListLoadType;
        private static Type dialogAndroidProjectListSaveType;
        private static Type utilsType;

        // ===== Window_CreateAndroidBase members =====
        private static FieldInfo fi_selectedGenes;
        private static FieldInfo fi_requiredItems;
        private static FieldInfo fi_disableAndroidHardwareLimitation;
        private static MethodInfo mi_geneValidator;
        private static MethodInfo mi_getAndroidTypeName;

        // ===== Utils static helpers =====
        private static PropertyInfo pi_androidGenesInOrder;
        private static MethodInfo mi_canBeRemovedFromAndroid;
        private static MethodInfo mi_canBeRemovedFromAndroidAwakened;
        private static MethodInfo mi_isAndroidTypeCustomXenotype;
        private static MethodInfo mi_isAndroidTypeXenotypeDef;

        // ===== Dialog_AndroidProjectList_Load / _Save constructors =====
        private static ConstructorInfo ctor_projectListLoad;
        private static ConstructorInfo ctor_projectListSave;

        // ===== Window_CreateAndroidXenotype constructor (chargen entry point) =====
        private static ConstructorInfo ctor_createAndroidXenotype;

        // ===== Dialog_AndroidAwakenedChoices (private field) =====
        private static FieldInfo fi_awakenedDialogLetter;

        // ===== ChoiceLetter_AndroidAwakened members (all public) =====
        private static FieldInfo fi_letterPawn;
        private static FieldInfo fi_letterPassionChoices;
        private static FieldInfo fi_letterTraitChoices;
        private static FieldInfo fi_letterPassionGainsCount;
        private static PropertyInfo pi_letterArchiveView;
        private static MethodInfo mi_letterMakeChoices;

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
                return;
            resolved = true;

            try
            {
                WindowCreateAndroidBaseType = AccessTools.TypeByName("VREAndroids.Window_CreateAndroidBase");
                if (WindowCreateAndroidBaseType == null)
                {
                    // Mod not present (or renamed). Stay unavailable, quietly.
                    return;
                }

                DialogAndroidProjectListType = AccessTools.TypeByName("VREAndroids.Dialog_AndroidProjectList");
                DialogAndroidAwakenedChoicesType = AccessTools.TypeByName("VREAndroids.Dialog_AndroidAwakenedChoices");
                ChoiceLetterAndroidAwakenedType = AccessTools.TypeByName("VREAndroids.ChoiceLetter_AndroidAwakened");
                HediffAndroidPartType = AccessTools.TypeByName("VREAndroids.Hediff_AndroidPart");
                utilsType = AccessTools.TypeByName("VREAndroids.Utils");

                fi_selectedGenes = AccessTools.Field(WindowCreateAndroidBaseType, "selectedGenes");
                fi_requiredItems = AccessTools.Field(WindowCreateAndroidBaseType, "requiredItems");
                fi_disableAndroidHardwareLimitation = AccessTools.Field(WindowCreateAndroidBaseType, "disableAndroidHardwareLimitation");
                mi_geneValidator = AccessTools.Method(WindowCreateAndroidBaseType, "GeneValidator", new[] { typeof(GeneDef) });
                mi_getAndroidTypeName = AccessTools.Method(WindowCreateAndroidBaseType, "GetAndroidTypeName");
                WindowCreateAndroidXenotypeType = AccessTools.TypeByName("VREAndroids.Window_CreateAndroidXenotype");
                if (WindowCreateAndroidXenotypeType != null)
                    ctor_createAndroidXenotype = AccessTools.Constructor(WindowCreateAndroidXenotypeType, new[] { typeof(int), typeof(Action) });

                dialogAndroidProjectListLoadType = AccessTools.TypeByName("VREAndroids.Dialog_AndroidProjectList_Load");
                dialogAndroidProjectListSaveType = AccessTools.TypeByName("VREAndroids.Dialog_AndroidProjectList_Save");
                if (dialogAndroidProjectListLoadType != null)
                    ctor_projectListLoad = AccessTools.Constructor(dialogAndroidProjectListLoadType, new[] { typeof(Action<CustomXenotype>) });
                if (dialogAndroidProjectListSaveType != null)
                    ctor_projectListSave = AccessTools.Constructor(dialogAndroidProjectListSaveType, new[] { typeof(CustomXenotype) });

                if (utilsType != null)
                {
                    pi_androidGenesInOrder = AccessTools.Property(utilsType, "AndroidGenesGenesInOrder");
                    mi_canBeRemovedFromAndroid = AccessTools.Method(utilsType, "CanBeRemovedFromAndroid", new[] { typeof(GeneDef) });
                    mi_canBeRemovedFromAndroidAwakened = AccessTools.Method(utilsType, "CanBeRemovedFromAndroidAwakened", new[] { typeof(GeneDef) });
                    mi_isAndroidTypeCustomXenotype = AccessTools.Method(utilsType, "IsAndroidType", new[] { typeof(CustomXenotype) });
                    mi_isAndroidTypeXenotypeDef = AccessTools.Method(utilsType, "IsAndroidType", new[] { typeof(XenotypeDef) });
                }

                if (DialogAndroidAwakenedChoicesType != null)
                {
                    fi_awakenedDialogLetter = AccessTools.Field(DialogAndroidAwakenedChoicesType, "letter");
                }

                if (ChoiceLetterAndroidAwakenedType != null)
                {
                    fi_letterPawn = AccessTools.Field(ChoiceLetterAndroidAwakenedType, "pawn");
                    fi_letterPassionChoices = AccessTools.Field(ChoiceLetterAndroidAwakenedType, "passionChoices");
                    fi_letterTraitChoices = AccessTools.Field(ChoiceLetterAndroidAwakenedType, "traitChoices");
                    fi_letterPassionGainsCount = AccessTools.Field(ChoiceLetterAndroidAwakenedType, "passionGainsCount");
                    pi_letterArchiveView = AccessTools.Property(ChoiceLetterAndroidAwakenedType, "ArchiveView");
                    mi_letterMakeChoices = AccessTools.Method(ChoiceLetterAndroidAwakenedType, "MakeChoices",
                        new[] { typeof(List<SkillDef>), typeof(Trait) });
                }

                available = fi_selectedGenes != null && fi_requiredItems != null
                    && fi_disableAndroidHardwareLimitation != null && mi_geneValidator != null
                    && pi_androidGenesInOrder != null;
            }
            catch (Exception ex)
            {
                Log.Warning($"[RimWorld Access] VREAndroidReflection resolution failed: {ex.Message}");
                available = false;
            }
        }

        // ===== Window_CreateAndroidBase accessors =====

        public static bool IsAndroidCreationWindow(Window window)
        {
            return Available && window != null && WindowCreateAndroidBaseType.IsInstanceOfType(window);
        }

        public static List<GeneDef> GetSelectedGenes(Window dialog) =>
            fi_selectedGenes?.GetValue(dialog) as List<GeneDef>;

        public static List<ThingDefCount> GetRequiredItems(Window dialog) =>
            fi_requiredItems?.GetValue(dialog) as List<ThingDefCount>;

        public static bool GetDisableAndroidHardwareLimitation(Window dialog) =>
            dialog != null && fi_disableAndroidHardwareLimitation != null
                && (bool)fi_disableAndroidHardwareLimitation.GetValue(dialog);

        public static bool GeneValidator(Window dialog, GeneDef gene)
        {
            if (dialog == null || mi_geneValidator == null)
                return true;
            return (bool)mi_geneValidator.Invoke(dialog, new object[] { gene });
        }

        /// <summary>Invokes the mod's own private android-name generator (grammar-resolver based).</summary>
        public static string GetAndroidTypeName(Window dialog)
        {
            if (dialog == null || mi_getAndroidTypeName == null)
                return null;
            return mi_getAndroidTypeName.Invoke(dialog, null) as string;
        }

        /// <summary>
        /// Constructs the android chargen xenotype-editor window (the mod's "Android Editor"
        /// entry point, normally reached from a bottom button / xenotype context-menu option on
        /// Page_ConfigureStartingPawns that RWA's own windowless replacements don't read - see
        /// MainMenu/StartingPawnHelper.BuildXenotypeOptions).
        /// </summary>
        public static Window CreateAndroidXenotypeWindow(int generationRequestIndex, Action callback)
        {
            if (ctor_createAndroidXenotype == null)
                return null;
            return ctor_createAndroidXenotype.Invoke(new object[] { generationRequestIndex, callback }) as Window;
        }

        public static bool IsAndroidCreationXenotypeWindow(Window window)
        {
            return WindowCreateAndroidXenotypeType != null && window != null
                && WindowCreateAndroidXenotypeType.IsInstanceOfType(window);
        }

        public static Window CreateProjectListLoadDialog(Action<CustomXenotype> callback)
        {
            if (ctor_projectListLoad == null)
                return null;
            return ctor_projectListLoad.Invoke(new object[] { callback }) as Window;
        }

        public static Window CreateProjectListSaveDialog(CustomXenotype project)
        {
            if (ctor_projectListSave == null)
                return null;
            return ctor_projectListSave.Invoke(new object[] { project }) as Window;
        }

        public static bool IsAndroidType(XenotypeDef def)
        {
            if (mi_isAndroidTypeXenotypeDef == null)
                return false;
            return (bool)mi_isAndroidTypeXenotypeDef.Invoke(null, new object[] { def });
        }

        // ===== Utils accessors =====

        public static List<GeneDef> AndroidGenesInOrder() =>
            pi_androidGenesInOrder?.GetValue(null) as List<GeneDef>;

        public static bool CanBeRemovedFromAndroid(GeneDef gene)
        {
            if (mi_canBeRemovedFromAndroid == null)
                return true;
            return (bool)mi_canBeRemovedFromAndroid.Invoke(null, new object[] { gene });
        }

        public static bool CanBeRemovedFromAndroidAwakened(GeneDef gene)
        {
            if (mi_canBeRemovedFromAndroidAwakened == null)
                return true;
            return (bool)mi_canBeRemovedFromAndroidAwakened.Invoke(null, new object[] { gene });
        }

        /// <summary>
        /// True when a gene in the Selected list can be toggled off given the dialog's current
        /// hardware-limitation state (mirrors Window_CreateAndroidBase.DrawGene's condition).
        /// </summary>
        public static bool CanToggleOffSelected(Window dialog, GeneDef gene)
        {
            if (CanBeRemovedFromAndroid(gene))
                return true;
            return GetDisableAndroidHardwareLimitation(dialog) && CanBeRemovedFromAndroidAwakened(gene);
        }

        // ===== Dialog_AndroidProjectList =====

        public static bool IsAndroidProjectListWindow(Window window)
        {
            return DialogAndroidProjectListType != null && window != null
                && DialogAndroidProjectListType.IsInstanceOfType(window);
        }

        // ===== Dialog_AndroidAwakenedChoices / ChoiceLetter_AndroidAwakened =====

        public static bool IsAndroidAwakenedDialog(Window window)
        {
            return DialogAndroidAwakenedChoicesType != null && window != null
                && DialogAndroidAwakenedChoicesType.IsInstanceOfType(window);
        }

        /// <summary>
        /// True for the ~60 body-part-replacement hediffs (Hediff_AndroidPart) android pawns
        /// carry - every toe/finger/organ etc. gets its own distinctly-labeled hediff, which
        /// floods the health tree with near-identical entries. Used to collapse them into one
        /// summary node (see Inspection/InspectionTreeBuilder.BuildHealthChildren).
        /// </summary>
        public static bool IsAndroidPartHediff(Hediff hediff)
        {
            return HediffAndroidPartType != null && hediff != null && HediffAndroidPartType.IsInstanceOfType(hediff);
        }

        public static bool IsAndroidAwakenedLetter(Letter letter)
        {
            return ChoiceLetterAndroidAwakenedType != null && letter != null
                && ChoiceLetterAndroidAwakenedType.IsInstanceOfType(letter);
        }

        public static ChoiceLetter GetAwakenedDialogLetter(Window dialog) =>
            fi_awakenedDialogLetter?.GetValue(dialog) as ChoiceLetter;

        public static Pawn GetLetterPawn(Letter letter) =>
            fi_letterPawn?.GetValue(letter) as Pawn;

        public static List<SkillDef> GetLetterPassionChoices(Letter letter) =>
            fi_letterPassionChoices?.GetValue(letter) as List<SkillDef>;

        public static List<Trait> GetLetterTraitChoices(Letter letter) =>
            fi_letterTraitChoices?.GetValue(letter) as List<Trait>;

        public static int GetLetterPassionGainsCount(Letter letter)
        {
            if (letter == null || fi_letterPassionGainsCount == null)
                return 0;
            return (int)fi_letterPassionGainsCount.GetValue(letter);
        }

        public static bool GetLetterArchiveView(Letter letter)
        {
            if (letter == null || pi_letterArchiveView == null)
                return true;
            return (bool)pi_letterArchiveView.GetValue(letter);
        }

        public static void MakeChoices(Letter letter, List<SkillDef> passions, Trait trait)
        {
            mi_letterMakeChoices?.Invoke(letter, new object[] { passions, trait });
        }
    }
}
