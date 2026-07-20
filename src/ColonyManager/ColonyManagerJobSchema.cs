using System.Collections.Generic;

namespace RimWorldAccess
{
    /// <summary>
    /// Per-job-type descriptor of the editable settings we expose in a job's detail level,
    /// beyond the generic threshold target. Colony Manager draws these with its own custom
    /// widgets, so there is nothing to shadow — instead we name the job's own members and
    /// drive them by reflection (see <see cref="ColonyManagerReflection"/>).
    ///
    /// Everything here is <b>best-effort</b>: if a named member/method doesn't resolve at
    /// runtime (mod update renamed it, DLC-gated, etc.) the setting is silently dropped when
    /// the detail list is built, so a wrong name degrades to "one missing row", never a crash.
    ///
    /// Covered: the four threshold-based resource jobs (Hunting, Forestry, Foraging, Mining),
    /// which share the "how much / what / where / options" shape. Livestock, Power and
    /// Production are structurally different (per-animal tables, no threshold, recipe pickers)
    /// and are handled in a later pass.
    /// </summary>
    internal static class ColonyManagerJobSchema
    {
        public enum Kind
        {
            Toggle,
            Area,
            DefList
        }

        public sealed class Spec
        {
            public Kind Kind;
            public string LabelKey;

            // Toggle / Area: the job field or property name.
            public string Member;

            // DefList: the "all options" list member, the "allowed" set member, and the
            // set-allowed method (signature (Def, bool[, bool sync])).
            public string AllMember;
            public string AllowedMember;
            public string SetMethod;

            public static Spec Toggle(string labelKey, string member) =>
                new Spec { Kind = Kind.Toggle, LabelKey = labelKey, Member = member };

            public static Spec Area(string labelKey, string member) =>
                new Spec { Kind = Kind.Area, LabelKey = labelKey, Member = member };

            public static Spec DefList(string labelKey, string all, string allowed, string setMethod) =>
                new Spec
                {
                    Kind = Kind.DefList,
                    LabelKey = labelKey,
                    AllMember = all,
                    AllowedMember = allowed,
                    SetMethod = setMethod
                };
        }

        public static List<Spec> For(string jobClassName)
        {
            switch (jobClassName)
            {
                case "ManagerJob_Hunting":
                    return new List<Spec>
                    {
                        Spec.DefList("RimWorldAccess.ColonyManager.SettingWhatToHunt", "AllAnimals", "AllowedAnimals", "SetAnimalAllowed"),
                        Spec.Area("RimWorldAccess.ColonyManager.SettingArea", "HuntingGrounds"),
                        Spec.Toggle("RimWorldAccess.ColonyManager.SettingInvertArea", "InvertHuntingGrounds"),
                        Spec.Toggle("RimWorldAccess.ColonyManager.SettingLockedToMap", "AnimalsLockedToMap"),
                        Spec.Toggle("RimWorldAccess.ColonyManager.SettingUnforbidCorpses", "_unforbidAllCorpses"),
                    };

                case "ManagerJob_Forestry":
                    return new List<Spec>
                    {
                        Spec.DefList("RimWorldAccess.ColonyManager.SettingWhatToChop", "AllPlants", "AllowedTrees", "SetTreeAllowed"),
                        Spec.Area("RimWorldAccess.ColonyManager.SettingArea", "LoggingArea"),
                        Spec.Toggle("RimWorldAccess.ColonyManager.SettingAllowSaplings", "AllowSaplings"),
                        Spec.Toggle("RimWorldAccess.ColonyManager.SettingInvertArea", "InvertLoggingArea"),
                        Spec.Toggle("RimWorldAccess.ColonyManager.SettingLockedToMap", "PlantsLockedToMap"),
                    };

                case "ManagerJob_Foraging":
                    return new List<Spec>
                    {
                        Spec.DefList("RimWorldAccess.ColonyManager.SettingWhatToForage", "AllPlants", "AllowedPlants", "SetPlantAllowed"),
                        Spec.Area("RimWorldAccess.ColonyManager.SettingArea", "ForagingArea"),
                        Spec.Toggle("RimWorldAccess.ColonyManager.SettingFullyMatureOnly", "ForceFullyMature"),
                        Spec.Toggle("RimWorldAccess.ColonyManager.SettingInvertArea", "InvertForagingArea"),
                        Spec.Toggle("RimWorldAccess.ColonyManager.SettingLockedToMap", "PlantsLockedToMap"),
                    };

                case "ManagerJob_Mining":
                    return new List<Spec>
                    {
                        Spec.DefList("RimWorldAccess.ColonyManager.SettingWhatToMine", "AllMinerals", "AllowedMinerals", "SetAllowMineral"),
                        Spec.Area("RimWorldAccess.ColonyManager.SettingArea", "MiningArea"),
                        Spec.Toggle("RimWorldAccess.ColonyManager.SettingAllowMining", "AllowMining"),
                        Spec.Toggle("RimWorldAccess.ColonyManager.SettingInvertArea", "InvertMiningArea"),
                        Spec.Toggle("RimWorldAccess.ColonyManager.SettingHaulMinedChunks", "HaulMinedChunks"),
                        Spec.Toggle("RimWorldAccess.ColonyManager.SettingCheckRoofSupport", "CheckRoofSupport"),
                        Spec.Toggle("RimWorldAccess.ColonyManager.SettingCheckRoomDivision", "CheckRoomDivision"),
                        Spec.Toggle("RimWorldAccess.ColonyManager.SettingMineThickRoofs", "MineThickRoofs"),
                    };

                default:
                    return new List<Spec>();
            }
        }
    }
}
