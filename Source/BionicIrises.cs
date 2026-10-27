// Bionic Irises: per-eye iris sprites for bionic eyes on [NL] Facial Animation faces.
// Written in C# 5 so it builds with the compiler that ships with .NET Framework (see build.ps1).
using System;
using System.Collections.Generic;
using System.Reflection;
using FacialAnimation;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace BionicIrises
{
    /// <summary>
    /// An iris sprite set drawn in place of Facial Animation's eyeball on any eye that carries one of these hediffs.
    /// Only eye replacements belong here: implants that sit on top of an eye (such as EPOE's tactical cornea implant)
    /// are left unmapped, so the replacement underneath keeps its iris.
    /// </summary>
    public class BionicIrisDef : Def
    {
        public List<HediffDef> hediffs = new List<HediffDef>();

        // Folder holding Female/ and Male/ sets named normal_{L,R}_{north,east,south,west}.png, with accent masks
        // normal_{L,R}_m_{dir}.png: red marks the parts tinted with the pawn's own eye colour, black keeps the sprite's colours.
        public string texPath;
        public string shader = "Map/CutoutComplex";

        // Decides between two mapped hediffs on the same eye; the higher one is drawn.
        public int priority;
    }

    [StaticConstructorOnStartup]
    public static class BionicIrisUtility
    {
        // A replaced eye's highlight layer is blanked: its sprite carries its own glints, which should not fade with mood.
        const string EmptyTexPath = "Things/Pawn/BionicIrises/Empty/{0}";

        static readonly FieldInfo PawnField = AccessTools.Field(typeof(EyeballControllerComp), "pawn");
        static readonly FieldInfo FaceTypeField = AccessTools.Field(typeof(EyeballControllerComp), "faceType");
        static readonly FieldInfo GraphicListField = AccessTools.Field(typeof(EyeballControllerComp), "graphicList");
        static readonly FieldInfo SecondColorField = AccessTools.Field(typeof(EyeballControllerComp), "secondColor");
        static readonly MethodInfo CvtMainColor = AccessTools.Method(typeof(EyeballControllerComp), "CvtMainColor");

        static readonly HashSet<HediffDef> MappedHediffs = new HashSet<HediffDef>();
        internal static bool Disabled;

        static BionicIrisUtility()
        {
            foreach (BionicIrisDef def in DefDatabase<BionicIrisDef>.AllDefsListForReading)
            {
                foreach (HediffDef hediff in def.hediffs)
                {
                    if (hediff != null)
                        MappedHediffs.Add(hediff);
                }
            }
            if (PawnField == null || FaceTypeField == null || GraphicListField == null || SecondColorField == null || CvtMainColor == null)
            {
                Log.Error("[Bionic Irises] This Facial Animation version is not supported; iris sprites are off.");
                return;
            }
            // Each patch goes on separately so that one target changing in a game or Facial Animation update
            // (1.6 and 1.7 share this assembly) cannot stop the others.
            var harmony = new Harmony("kelby.BionicIrises");
            Patch(harmony, AccessTools.Method(typeof(EyeballControllerComp), "LoadTextures"), "LoadTexturesPostfix");
            foreach (string name in new[] { "AddHediff", "RemoveHediff" })
            {
                List<MethodInfo> overloads = HediffMethods(name);
                if (overloads.Count == 0)
                    Patch(harmony, null, name);
                foreach (MethodInfo method in overloads)
                    Patch(harmony, method, "HediffChangedPostfix");
            }
        }

        static void Patch(Harmony harmony, MethodInfo target, string postfix)
        {
            if (target == null)
            {
                Log.Warning("[Bionic Irises] Could not find a method to patch for " + postfix + "; that part is off.");
                return;
            }
            try
            {
                harmony.Patch(target, null, new HarmonyMethod(AccessTools.Method(typeof(BionicIrisUtility), postfix)));
            }
            catch (Exception e)
            {
                Log.Error("[Bionic Irises] Could not patch " + target.DeclaringType.Name + "." + target.Name + ": " + e);
            }
        }

        // The Pawn_HealthTracker overloads that take the Hediff itself, found by their first parameter so that
        // signature changes between game versions do not matter.
        static List<MethodInfo> HediffMethods(string name)
        {
            var found = new List<MethodInfo>();
            foreach (MethodInfo method in typeof(Pawn_HealthTracker).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                ParameterInfo[] parameters = method.GetParameters();
                if (method.Name == name && parameters.Length > 0 && parameters[0].ParameterType == typeof(Hediff))
                    found.Add(method);
            }
            return found;
        }

        static void LoadTexturesPostfix(EyeballControllerComp __instance)
        {
            if (Disabled)
                return;
            try
            {
                Apply(__instance);
            }
            catch (Exception e)
            {
                Disabled = true;
                Log.Error("[Bionic Irises] Turned off after an error (Facial Animation may have changed): " + e);
            }
        }

        // Runs inside every hediff change in the game, so it must never throw.
        static void HediffChangedPostfix(Hediff __0)
        {
            if (Disabled || __0 == null)
                return;
            try
            {
                Refresh(__0.pawn, __0);
            }
            catch (Exception e)
            {
                Disabled = true;
                Log.Error("[Bionic Irises] Turned off after an error (Facial Animation may have changed): " + e);
            }
        }

        /// <summary>Swaps in the iris sprites after Facial Animation has built a pawn's eyeball layers.</summary>
        internal static void Apply(EyeballControllerComp comp)
        {
            Pawn pawn = (Pawn)PawnField.GetValue(comp);
            EyeballTypeDef faceType = (EyeballTypeDef)FaceTypeField.GetValue(comp);
            // The sprites are drawn for Facial Animation's human faces, and per-eye layers only exist with the
            // Experimentals eye masks.
            if (pawn == null || pawn.def != ThingDefOf.Human || pawn.health == null || faceType == null || faceType.altMaskPath.NullOrEmpty())
                return;
            var graphics = (Dictionary<NLFacialAnimationLayerType, NLGraphic_Collection<EyeballShapeDef>>)GraphicListField.GetValue(comp);
            if (graphics == null)
                return;

            BodyPartRecord rightEye = EyePart(pawn, GraphicHelper.GetRightEyeLabel(pawn.def.defName));
            BodyPartRecord leftEye = EyePart(pawn, GraphicHelper.GetLeftEyeLabel(pawn.def.defName));
            BionicIrisDef rightIris = IrisOn(pawn, rightEye);
            BionicIrisDef leftIris = IrisOn(pawn, leftEye);
            if (rightIris == null && leftIris == null)
                return;

            // Accents take the pawn's own eye colours, not the tint Facial Animation gives bionic eyes.
            Color rightColor = comp.GetCurrentColor();
            Color leftColor = (Color)SecondColorField.GetValue(comp);
            Color geneColor;
            if (GeneEyeColor(pawn, out geneColor))
            {
                rightColor = geneColor;
                leftColor = geneColor;
            }
            rightColor = Efficiency(pawn, rightEye, rightColor);
            leftColor = Efficiency(pawn, leftEye, leftColor);

            // Facial Animation's "switch left and right eyes" option swaps which mask each eye is drawn in.
            if (FacialAnimationMod.Settings != null && FacialAnimationMod.Settings.SwitchLeftAndRightEyes)
            {
                BionicIrisDef iris = rightIris;
                rightIris = leftIris;
                leftIris = iris;
                Color color = rightColor;
                rightColor = leftColor;
                leftColor = color;
            }
            string gender = pawn.gender == Gender.Female ? "Female" : "Male";
            if (leftIris != null)
                Replace(graphics, NLFacialAnimationLayerType.L_Basic, NLFacialAnimationLayerType.L_Option, leftIris, "L", gender, leftColor);
            if (rightIris != null)
                Replace(graphics, NLFacialAnimationLayerType.R_Basic, NLFacialAnimationLayerType.R_Option, rightIris, "R", gender, rightColor);
        }

        /// <summary>Asks Facial Animation to rebuild a pawn's eyes when a mapped eye is installed or removed.</summary>
        internal static void Refresh(Pawn pawn, Hediff hediff)
        {
            if (pawn == null || hediff == null || !MappedHediffs.Contains(hediff.def))
                return;
            EyeballControllerComp comp = pawn.GetComp<EyeballControllerComp>();
            if (comp != null)
                comp.SetDirty();
        }

        static void Replace(Dictionary<NLFacialAnimationLayerType, NLGraphic_Collection<EyeballShapeDef>> graphics,
            NLFacialAnimationLayerType basic, NLFacialAnimationLayerType highlight, BionicIrisDef iris, string side, string gender, Color accent)
        {
            string tex = iris.texPath + "/" + gender + "/{0}_" + side;
            graphics[basic] = Collection(new NLGraphicParam(iris.shader, tex, tex + "_m", accent, Color.white));
            graphics[highlight] = Collection(new NLGraphicParam("Map/Cutout", EmptyTexPath, null, Color.white, Color.white));
        }

        static NLGraphic_Collection<EyeballShapeDef> Collection(NLGraphicParam param)
        {
            return (NLGraphic_Collection<EyeballShapeDef>)GraphicDatabase.Get<NLGraphic_Collection<EyeballShapeDef>>(param.ToXML());
        }

        static BodyPartRecord EyePart(Pawn pawn, string label)
        {
            foreach (BodyPartRecord part in pawn.RaceProps.body.AllParts)
            {
                if (part.untranslatedCustomLabel == label)
                    return part;
            }
            return null;
        }

        static BionicIrisDef IrisOn(Pawn pawn, BodyPartRecord eye)
        {
            if (eye == null)
                return null;
            BionicIrisDef best = null;
            foreach (Hediff hediff in pawn.health.hediffSet.hediffs)
            {
                if (hediff.Part != eye || !MappedHediffs.Contains(hediff.def))
                    continue;
                foreach (BionicIrisDef def in DefDatabase<BionicIrisDef>.AllDefsListForReading)
                {
                    if (def.hediffs.Contains(hediff.def) && (best == null || def.priority > best.priority))
                        best = def;
                }
            }
            return best;
        }

        // Matches Facial Animation: an active gene with an eye colour def recolours both eyes.
        static bool GeneEyeColor(Pawn pawn, out Color color)
        {
            color = Color.white;
            if (!ModsConfig.BiotechActive || pawn.genes == null)
                return false;
            bool found = false;
            foreach (Gene gene in pawn.genes.GenesListForReading)
            {
                if (!gene.Active)
                    continue;
                foreach (EyeballColorDef def in DefDatabase<EyeballColorDef>.AllDefsListForReading)
                {
                    if (def.geneDef == gene.def.defName)
                    {
                        color = def.eyeballColor;
                        found = true;
                    }
                }
            }
            return found;
        }

        // Matches Facial Animation: a damaged eye's colour fades with its efficiency.
        static Color Efficiency(Pawn pawn, BodyPartRecord eye, Color color)
        {
            if (eye == null)
                return color;
            float efficiency = Math.Min(1f, PawnCapacityUtility.CalculatePartEfficiency(pawn.health.hediffSet, eye, false, null));
            return (Color)CvtMainColor.Invoke(null, new object[] { color, efficiency });
        }
    }
}
