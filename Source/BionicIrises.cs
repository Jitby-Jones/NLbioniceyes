using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using FacialAnimation;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace BionicIrises
{
    public class BionicIrisDef : Def
    {
        public List<HediffDef> hediffs = new List<HediffDef>();

        public List<string> matchNames = new List<string>();

        public string texPath;

        public string glowShader = "Map/TransparentPostLight";

        public string litShader = "Map/Transparent";

        public int priority;

        public bool missingEye;

        public bool blindEye;

        public float eyeColorTint;
    }

    public class BionicIrisSettings : ModSettings
    {
        public bool glow = true;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref glow, "glow", true);
        }
    }

    public class BionicIrisesMod : Mod
    {
        public static BionicIrisSettings Settings;

        public BionicIrisesMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<BionicIrisSettings>();
        }

        public override string SettingsCategory()
        {
            return Content.Name;
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            var list = new Listing_Standard();
            list.Begin(inRect);
            list.CheckboxLabeled("BionicIrises_Glow".Translate().Resolve(), ref Settings.glow, "BionicIrises_GlowTip".Translate().Resolve());
            list.End();
        }

        public override void WriteSettings()
        {
            base.WriteSettings();
            BionicIrisUtility.ReloadAll();
        }
    }

    public class BionicIrisLayoutDef : Def
    {
        public Gender gender;
        public Vector2 left;
        public Vector2 right;
        public Vector2 frontSize;
        public Vector2 side;
        public Vector2 sideSize;
        public Vector2 leftSocket;
        public Vector2 rightSocket;
        public Vector2 socketSize;
        public Vector2 sideSocket;
        public Vector2 sideSocketSize;

        internal Rect Front(bool isLeft, bool socket)
        {
            if (socket && socketSize.x > 0f && socketSize.y > 0f)
                return Uv(isLeft ? leftSocket : rightSocket, socketSize);
            return Uv(isLeft ? left : right, frontSize);
        }

        internal Rect Side(bool socket)
        {
            if (socket && sideSocketSize.x > 0f && sideSocketSize.y > 0f)
                return Uv(sideSocket, sideSocketSize);
            return Uv(side, sideSize);
        }

        static Rect Uv(Vector2 corner, Vector2 size)
        {
            return new Rect(corner.x / 512f, 1f - (corner.y + size.y) / 512f, size.x / 512f, size.y / 512f);
        }
    }

    [StaticConstructorOnStartup]
    public static class BionicIrisUtility
    {
        static readonly FieldInfo PawnField = AccessTools.Field(typeof(EyeballControllerComp), "pawn");
        static readonly FieldInfo FaceTypeField = AccessTools.Field(typeof(EyeballControllerComp), "faceType");
        static readonly FieldInfo GraphicListField = AccessTools.Field(typeof(EyeballControllerComp), "graphicList");
        static readonly FieldInfo SecondColorField = AccessTools.Field(typeof(EyeballControllerComp), "secondColor");
        static readonly MethodInfo CvtMainColor = AccessTools.Method(typeof(EyeballControllerComp), "CvtMainColor");
        static readonly FieldInfo LidTypeField = AccessTools.Field(typeof(LidControllerComp), "faceType");
        static readonly FieldInfo LidShapeField = AccessTools.Field(typeof(LidControllerComp), "Shape");

        const string WhiteMask = "Things/Pawn/BionicIrises/Mask";

        static readonly HashSet<HediffDef> MappedHediffs = new HashSet<HediffDef>();

        static BionicIrisDef MissingIris, BlindIris;

        static readonly ConditionalWeakTable<EyeballControllerComp, Irises> Fitted = new ConditionalWeakTable<EyeballControllerComp, Irises>();
        static readonly ConditionalWeakTable<Pawn, Irises> FittedPawns = new ConditionalWeakTable<Pawn, Irises>();

        static readonly Dictionary<Mesh, Dictionary<Rect, Mesh>> Pieces = new Dictionary<Mesh, Dictionary<Rect, Mesh>>();

        static readonly Dictionary<Mesh, HeadQuad> Heads = new Dictionary<Mesh, HeadQuad>();
        static readonly Dictionary<LidTypeDef, Dictionary<LidShapeDef, Cover[]>> Covers = new Dictionary<LidTypeDef, Dictionary<LidShapeDef, Cover[]>>();

        static bool DrawsPieces;
        static bool WarnedMissingPiece;
        internal static bool Disabled;

        static bool ClipsToLids;

        const int Steps = 4;

        sealed class Irises
        {
            public bool left, right;
            public bool leftSocket, rightSocket;
            public Rect leftUv, rightUv, leftSideUv, rightSideUv;
            public EyeballControllerComp eyes;
            public LidControllerComp lids;
            public Gender gender;

            public readonly List<BodyPartRecord> parts = new List<BodyPartRecord>();

            public Clip[] clips;
        }

        sealed class Clip
        {
            public Mesh mesh;
            public Mesh head;
            public Rect uv;
            public bool side;

            Mesh builtHead;
            Rect builtUv;
            Cover builtCover;
            int builtX, builtY;

            public bool IsBuilt(Mesh head, Rect uv, Cover cover, int x, int y)
            {
                return builtHead == head && builtUv == uv && builtCover == cover && builtX == x && builtY == y;
            }

            public void Built(Mesh head, Rect uv, Cover cover, int x, int y)
            {
                builtHead = head;
                builtUv = uv;
                builtCover = cover;
                builtX = x;
                builtY = y;
            }
        }

        sealed class HeadQuad
        {
            public readonly Vector3[] corners = new Vector3[4];
            public int[] triangles;
        }

        sealed class Cover
        {
            public int x0, y0;
            public int[][] lights, iris;
        }

        static BionicIrisUtility()
        {
            foreach (BionicIrisDef def in DefDatabase<BionicIrisDef>.AllDefsListForReading)
            {
                foreach (HediffDef hediff in def.hediffs)
                {
                    if (hediff != null)
                        MappedHediffs.Add(hediff);
                }
                if (def.missingEye && (MissingIris == null || def.priority > MissingIris.priority))
                    MissingIris = def;
                if (def.blindEye && (BlindIris == null || def.priority > BlindIris.priority))
                    BlindIris = def;
            }
            MatchByName();
            if (PawnField == null || FaceTypeField == null || GraphicListField == null || SecondColorField == null || CvtMainColor == null)
            {
                Log.Error("[Bionic Irises] This Facial Animation version is not supported; iris sprites are off.");
                return;
            }
            var harmony = new Harmony("neztech.fabioniciris");
            DrawsPieces = Patch(harmony, AccessTools.Method(typeof(NLFacialAnimationHeadNodeWorker), "AppendDrawRequests"), "AppendDrawRequestsPostfix");
            Type faceGraphics = AccessTools.TypeByName("FacialAnimation.DrawFaceGraphicsComp");
            Patch(harmony, faceGraphics == null ? null : AccessTools.Method(faceGraphics, "CompRenderNodes"), "CompRenderNodesPostfix");
            Patch(harmony, AccessTools.Method(typeof(EyeballControllerComp), "LoadTextures"), "LoadTexturesPostfix");
            Patch(harmony, AccessTools.Method(typeof(AnimationFrameMapper), "ApplyFrame"), "ApplyFramePostfix");
            Patch(harmony, AccessTools.Method(typeof(PawnRenderNodeWorker), "CanDrawNow"), "CanDrawNowPostfix");
            foreach (string name in new[] { "AddHediff", "RemoveHediff", "RestorePart" })
            {
                bool restore = name == "RestorePart";
                List<MethodInfo> overloads = HealthMethods(name, restore ? typeof(BodyPartRecord) : typeof(Hediff));
                if (overloads.Count == 0)
                    Patch(harmony, null, name);
                foreach (MethodInfo method in overloads)
                    Patch(harmony, method, restore ? "RestorePartPostfix" : "HediffChangedPostfix");
            }
            if (DrawsPieces && LidTypeField != null && LidShapeField != null
                && Patch(harmony, AccessTools.Method(typeof(PawnRenderTree), "Draw", new[] { typeof(PawnDrawParms) }), "DrawPrefix", true))
            {
                try
                {
                    ReadCovers();
                    ClipsToLids = true;
                }
                catch (Exception e)
                {
                    Log.Error("[Bionic Irises] Could not read Facial Animation's lids, so the irises' lights are not cut to them: " + e);
                }
            }
        }

        static void MatchByName()
        {
            var irises = new List<BionicIrisDef>();
            foreach (BionicIrisDef def in DefDatabase<BionicIrisDef>.AllDefsListForReading)
            {
                if (def.matchNames.Count > 0)
                    irises.Add(def);
            }
            if (irises.Count == 0)
                return;
            var eyes = new HashSet<HediffDef>();
            foreach (RecipeDef recipe in DefDatabase<RecipeDef>.AllDefsListForReading)
            {
                if (recipe.addsHediff == null || recipe.appliedOnFixedBodyParts == null)
                    continue;
                foreach (BodyPartDef part in recipe.appliedOnFixedBodyParts)
                {
                    if (part != null && ((part.tags != null && part.tags.Contains(BodyPartTagDefOf.SightSource)) || Plain(part.defName).Contains("eye")))
                        eyes.Add(recipe.addsHediff);
                }
            }
            var matched = new List<string>();
            foreach (HediffDef hediff in eyes)
            {
                if (MappedHediffs.Contains(hediff) || hediff.hediffClass == null || !typeof(Hediff_AddedPart).IsAssignableFrom(hediff.hediffClass))
                    continue;
                string names = Plain(hediff.defName) + " " + Plain(hediff.label);
                if (names.Contains("patch"))
                    continue;
                foreach (BionicIrisDef iris in irises)
                {
                    bool found = false;
                    foreach (string name in iris.matchNames)
                        found |= names.Contains(Plain(name));
                    if (!found)
                        continue;
                    iris.hediffs.Add(hediff);
                    MappedHediffs.Add(hediff);
                    matched.Add(hediff.defName + " (" + iris.defName + ")");
                    break;
                }
            }
            if (matched.Count > 0)
                Log.Message("[Bionic Irises] Matched by name: " + string.Join(", ", matched.ToArray()));
        }

        static string Plain(string text)
        {
            if (string.IsNullOrEmpty(text))
                return "";
            string lower = text.ToLowerInvariant();
            try
            {
                lower = lower.Normalize(NormalizationForm.FormD);
            }
            catch (Exception)
            {
                return lower;
            }
            var plain = new StringBuilder(lower.Length);
            foreach (char c in lower)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                    plain.Append(c);
            }
            return plain.ToString();
        }

        static bool Patch(Harmony harmony, MethodInfo target, string patch, bool prefix = false)
        {
            if (target == null)
            {
                Log.Warning("[Bionic Irises] Could not find a method to patch for " + patch + "; that part is off.");
                return false;
            }
            try
            {
                var method = new HarmonyMethod(AccessTools.Method(typeof(BionicIrisUtility), patch));
                harmony.Patch(target, prefix ? method : null, prefix ? null : method);
                return true;
            }
            catch (Exception e)
            {
                Log.Error("[Bionic Irises] Could not patch " + target.DeclaringType.Name + "." + target.Name + ": " + e);
                return false;
            }
        }

        static List<MethodInfo> HealthMethods(string name, Type first)
        {
            var found = new List<MethodInfo>();
            foreach (MethodInfo method in typeof(Pawn_HealthTracker).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                ParameterInfo[] parameters = method.GetParameters();
                if (method.Name == name && parameters.Length > 0 && parameters[0].ParameterType == first)
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

        static void CompRenderNodesPostfix(ThingComp __instance)
        {
            if (Disabled)
                return;
            try
            {
                Pawn pawn = __instance.parent as Pawn;
                EyeballControllerComp comp = pawn == null ? null : pawn.GetComp<EyeballControllerComp>();
                Irises irises;
                if (comp != null && Fitted.TryGetValue(comp, out irises))
                    PreparePieces(pawn, irises);
            }
            catch (Exception e)
            {
                Disabled = true;
                Log.Error("[Bionic Irises] Turned off after an error (Facial Animation may have changed): " + e);
            }
        }

        static void AppendDrawRequestsPostfix(PawnRenderNode node, PawnDrawParms parms, List<PawnGraphicDrawRequest> requests)
        {
            NLFacialAnimationPartNode part = node as NLFacialAnimationPartNode;
            if (part == null || requests == null || requests.Count == 0)
                return;
            bool left, lights;
            switch (part.layerType)
            {
                case NLFacialAnimationLayerType.L_Basic:
                    left = true;
                    lights = false;
                    break;
                case NLFacialAnimationLayerType.L_Option:
                    left = true;
                    lights = true;
                    break;
                case NLFacialAnimationLayerType.R_Basic:
                    left = false;
                    lights = false;
                    break;
                case NLFacialAnimationLayerType.R_Option:
                    left = false;
                    lights = true;
                    break;
                default:
                    return;
            }
            EyeballControllerComp comp = part.controller as EyeballControllerComp;
            Irises irises;
            if (comp == null || !Fitted.TryGetValue(comp, out irises) || !(left ? irises.left : irises.right))
                return;
            int last = requests.Count - 1;
            PawnGraphicDrawRequest request = requests[last];
            if (request.node != node)
                return;
            Rot4 facing = part.overrideHeadRot;
            Rect uv;
            if (facing == Rot4.South)
                uv = left ? irises.leftUv : irises.rightUv;
            else if (facing == (left ? Rot4.East : Rot4.West))
                uv = left ? irises.leftSideUv : irises.rightSideUv;
            else
            {
                requests.RemoveAt(last);
                return;
            }
            Mesh piece = PieceOf(request.mesh, uv);
            if (piece == null)
            {
                requests.RemoveAt(last);
                if (!WarnedMissingPiece)
                {
                    WarnedMissingPiece = true;
                    Log.Warning("[Bionic Irises] A face was drawn on a head mesh the irises were not prepared for; that iris is hidden.");
                }
                return;
            }
            Clip clip = ClipsToLids && irises.clips != null ? irises.clips[(left ? 0 : 4) + (lights ? 2 : 0) + (parms.DrawNow ? 1 : 0)] : null;
            if (clip != null)
            {
                clip.head = request.mesh;
                clip.uv = uv;
                clip.side = facing != Rot4.South;
                piece = clip.mesh;
            }
            var replaced = new PawnGraphicDrawRequest(node, piece, request.material);
            replaced.preDrawnComputedMatrix = request.preDrawnComputedMatrix;
            requests[last] = replaced;
        }

        static void CanDrawNowPostfix(PawnRenderNode node, PawnDrawParms parms, ref bool __result)
        {
            Irises irises;
            if (__result && node.hediff != null && parms.pawn != null && MappedHediffs.Contains(node.hediff.def)
                && FittedPawns.TryGetValue(parms.pawn, out irises) && irises.parts.Contains(node.hediff.Part))
                __result = false;
        }

        static void ApplyFramePostfix(FaceAnimationDef.AnimationFrame __0, EyeballControllerComp ___eyeballController)
        {
            Irises irises;
            if (Disabled || ___eyeballController == null || __0 == null || __0.lidShapeDef == null || !Fitted.TryGetValue(___eyeballController, out irises))
                return;
            bool open = !__0.lidShapeDef.disableEyeball;
            if (irises.left)
                ___eyeballController.SetVisible(NLFacialAnimationLayerType.L_Option, open);
            if (irises.right)
                ___eyeballController.SetVisible(NLFacialAnimationLayerType.R_Option, open);
        }

        static void DrawPrefix(PawnRenderTree __instance, PawnDrawParms parms)
        {
            Irises irises;
            if (!ClipsToLids || Disabled || __instance.pawn == null || !FittedPawns.TryGetValue(__instance.pawn, out irises) || irises.clips == null)
                return;
            try
            {
                LidShapeDef shape = irises.lids == null ? null : LidShapeField.GetValue(irises.lids) as LidShapeDef;
                if (shape != null && shape.disableEyeball)
                    return;
                Vector3 lids = irises.lids == null ? Vector3.zero
                    : ((IFacialAnimationController)irises.lids).GetOffset(NLFacialAnimationLayerType.Cover, parms.Portrait, parms.facing);
                for (int i = parms.DrawNow ? 1 : 0; i < irises.clips.Length; i += 2)
                {
                    Clip clip = irises.clips[i];
                    if (clip.head == null)
                        continue;
                    bool left = i < 4, lights = (i & 2) != 0;
                    NLFacialAnimationLayerType layer = left ? (lights ? NLFacialAnimationLayerType.L_Option : NLFacialAnimationLayerType.L_Basic)
                        : (lights ? NLFacialAnimationLayerType.R_Option : NLFacialAnimationLayerType.R_Basic);
                    Vector3 eye = ((IFacialAnimationController)irises.eyes).GetOffset(layer, parms.Portrait, parms.facing);
                    Cut(clip, CoverFor(irises, shape, clip.side), eye - lids, lights, left ? irises.leftSocket : irises.rightSocket);
                }
            }
            catch (Exception e)
            {
                ClipsToLids = false;
                Log.Error("[Bionic Irises] Stopped cutting irises to the lids after an error (Facial Animation may have changed): " + e);
            }
        }

        static Cover CoverFor(Irises irises, LidShapeDef shape, bool side)
        {
            LidTypeDef type = irises.lids == null ? null : LidTypeField.GetValue(irises.lids) as LidTypeDef;
            Dictionary<LidShapeDef, Cover[]> shapes;
            Cover[] covers;
            if (type == null || shape == null || !Covers.TryGetValue(type, out shapes) || !shapes.TryGetValue(shape, out covers))
                return null;
            return covers[(irises.gender == Gender.Female ? 2 : 0) + (side ? 1 : 0)];
        }

        static void Cut(Clip clip, Cover cover, Vector3 delta, bool lights, bool anchored)
        {
            HeadQuad head = QuadOf(clip.head);
            if (head == null)
                return;
            Vector3 across = head.corners[1] - head.corners[0], up = head.corners[2] - head.corners[0];
            bool flat = Mathf.Abs(across.x) < 1e-5f || Mathf.Abs(up.z) < 1e-5f;
            if (flat)
                cover = null;
            bool moves = !flat && (cover != null || anchored);
            int x = moves ? Mathf.RoundToInt(delta.x / across.x * 512f * Steps) : 0;
            int y = moves ? -Mathf.RoundToInt(delta.z / up.z * 512f * Steps) : 0;
            if (clip.IsBuilt(clip.head, clip.uv, cover, x, y))
                return;
            Fill(clip.mesh, head, clip.uv, cover == null ? null : lights ? cover.lights : cover.iris, cover, x, y, anchored);
            clip.Built(clip.head, clip.uv, cover, x, y);
        }

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

        internal static void Apply(EyeballControllerComp comp)
        {
            Pawn pawn = (Pawn)PawnField.GetValue(comp);
            Irises previous;
            Fitted.TryGetValue(comp, out previous);
            Fitted.Remove(comp);
            if (pawn != null)
                FittedPawns.Remove(pawn);
            if (!DrawsPieces)
                return;
            EyeballTypeDef faceType = (EyeballTypeDef)FaceTypeField.GetValue(comp);
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
            string gender = pawn.gender == Gender.Female ? "Female" : "Male";
            BionicIrisLayoutDef layout = Layout(pawn.gender == Gender.Female ? Gender.Female : Gender.Male);
            if (layout == null)
                return;

            Color rightColor = comp.GetCurrentColor();
            Color leftColor = (Color)SecondColorField.GetValue(comp);
            Color geneColor;
            if (GeneEyeColor(pawn, out geneColor))
            {
                rightColor = geneColor;
                leftColor = geneColor;
            }
            Color rightEyeColor = rightColor, leftEyeColor = leftColor;
            rightColor = Efficiency(pawn, rightEye, rightColor);
            leftColor = Efficiency(pawn, leftEye, leftColor);

            BodyPartRecord leftPart = leftEye, rightPart = rightEye;
            if (FacialAnimationMod.Settings != null && FacialAnimationMod.Settings.SwitchLeftAndRightEyes)
            {
                BionicIrisDef iris = rightIris;
                rightIris = leftIris;
                leftIris = iris;
                Color color = rightColor;
                rightColor = leftColor;
                leftColor = color;
                color = rightEyeColor;
                rightEyeColor = leftEyeColor;
                leftEyeColor = color;
                leftPart = rightEye;
                rightPart = leftEye;
            }
            var irises = new Irises
            {
                eyes = comp,
                lids = pawn.GetComp<LidControllerComp>(),
                gender = pawn.gender == Gender.Female ? Gender.Female : Gender.Male,
            };
            string eyeShader = GraphicHelper.GetSkinShader(pawn, faceType);
            Color skin = SkinOf(pawn);
            if (leftIris != null && Replace(graphics, NLFacialAnimationLayerType.L_Basic, NLFacialAnimationLayerType.L_Option, leftIris, eyeShader, gender, "L", leftColor,
                Tint(leftIris, skin, leftEyeColor)))
            {
                irises.left = true;
                irises.leftSocket = leftIris.missingEye;
                irises.leftUv = layout.Front(true, leftIris.missingEye);
                irises.leftSideUv = layout.Side(leftIris.missingEye);
                irises.parts.Add(leftPart);
            }
            if (rightIris != null && Replace(graphics, NLFacialAnimationLayerType.R_Basic, NLFacialAnimationLayerType.R_Option, rightIris, eyeShader, gender, "R", rightColor,
                Tint(rightIris, skin, rightEyeColor)))
            {
                irises.right = true;
                irises.rightSocket = rightIris.missingEye;
                irises.rightUv = layout.Front(false, rightIris.missingEye);
                irises.rightSideUv = layout.Side(rightIris.missingEye);
                irises.parts.Add(rightPart);
            }
            if (!irises.left && !irises.right)
                return;
            irises.clips = ClipsFor(previous);
            PreparePieces(pawn, irises);
            Fitted.Add(comp, irises);
            FittedPawns.Add(pawn, irises);
        }

        static Clip[] ClipsFor(Irises previous)
        {
            if (!ClipsToLids)
                return null;
            var clips = new Clip[8];
            for (int i = 0; i < clips.Length; i++)
            {
                Mesh mesh = previous != null && previous.clips != null ? previous.clips[i].mesh : null;
                if (mesh == null)
                {
                    mesh = new Mesh();
                    mesh.name = "BionicIrisClip";
                    mesh.MarkDynamic();
                }
                clips[i] = new Clip { mesh = mesh };
            }
            return clips;
        }

        internal static void Refresh(Pawn pawn, Hediff hediff)
        {
            if (pawn == null || hediff == null || !(MappedHediffs.Contains(hediff.def) || OnEye(hediff.Part)))
                return;
            EyeballControllerComp comp = pawn.GetComp<EyeballControllerComp>();
            if (comp != null)
                Reload(pawn, comp);
        }

        static bool OnEye(BodyPartRecord part)
        {
            return (MissingIris != null || BlindIris != null) && part != null && part.def != null && part.def.tags != null
                && part.def.tags.Contains(BodyPartTagDefOf.SightSource);
        }

        static void RestorePartPostfix(Pawn_HealthTracker __instance)
        {
            Irises irises;
            if (Disabled || __instance == null || __instance.hediffSet == null || __instance.hediffSet.pawn == null
                || !FittedPawns.TryGetValue(__instance.hediffSet.pawn, out irises))
                return;
            try
            {
                Reload(__instance.hediffSet.pawn, irises.eyes);
            }
            catch (Exception e)
            {
                Disabled = true;
                Log.Error("[Bionic Irises] Turned off after an error (Facial Animation may have changed): " + e);
            }
        }

        internal static void ReloadAll()
        {
            if (Current.Game == null || !UnityData.IsInMainThread)
                return;
            var pawns = new List<Pawn>();
            foreach (Map map in Find.Maps)
                pawns.AddRange(map.mapPawns.AllPawns);
            foreach (Caravan caravan in Find.WorldObjects.Caravans)
                pawns.AddRange(caravan.PawnsListForReading);
            foreach (Pawn pawn in pawns)
            {
                EyeballControllerComp comp = pawn.GetComp<EyeballControllerComp>();
                Irises irises;
                if (comp != null && Fitted.TryGetValue(comp, out irises))
                    Reload(pawn, comp);
            }
        }

        static void Reload(Pawn pawn, EyeballControllerComp comp)
        {
            comp.SetDirty();
            if (UnityData.IsInMainThread)
            {
                pawn.Drawer.renderer.SetAllGraphicsDirty();
                PortraitsCache.SetDirty(pawn);
            }
        }

        static bool Replace(Dictionary<NLFacialAnimationLayerType, NLGraphic_Collection<EyeballShapeDef>> graphics,
            NLFacialAnimationLayerType basic, NLFacialAnimationLayerType highlight, BionicIrisDef iris, string eyeShader,
            string gender, string side, Color accent, Color tint)
        {
            string tex = iris.texPath + "/" + gender + "/{0}_" + side;
            if (!GraphicHelper.CheckTexPathExist(string.Format(tex, "normal")))
                return false;
            graphics[basic] = Collection(new NLGraphicParam(eyeShader, tex, WhiteMask, tint, Color.white));
            string lights = BionicIrisesMod.Settings == null || BionicIrisesMod.Settings.glow ? iris.glowShader : iris.litShader;
            graphics[highlight] = Collection(new NLGraphicParam(lights, tex + "_glow", null, Glow(accent), Color.white));
            return true;
        }

        static NLGraphic_Collection<EyeballShapeDef> Collection(NLGraphicParam param)
        {
            return (NLGraphic_Collection<EyeballShapeDef>)GraphicDatabase.Get<NLGraphic_Collection<EyeballShapeDef>>(param.ToXML());
        }

        static BionicIrisLayoutDef Layout(Gender gender)
        {
            foreach (BionicIrisLayoutDef def in DefDatabase<BionicIrisLayoutDef>.AllDefsListForReading)
            {
                if (def.gender == gender)
                    return def;
            }
            return null;
        }

        static void PreparePieces(Pawn pawn, Irises irises)
        {
            GraphicMeshSet set = MeshPool.GetMeshSetForSize(GraphicHelper.GetHeadMeshSet(pawn));
            foreach (Rot4 rot in new[] { Rot4.North, Rot4.East, Rot4.South, Rot4.West })
            {
                Mesh head = set.MeshAt(rot);
                if (head == null)
                    continue;
                if (irises.left)
                {
                    MakePiece(head, irises.leftUv);
                    MakePiece(head, irises.leftSideUv);
                }
                if (irises.right)
                {
                    MakePiece(head, irises.rightUv);
                    MakePiece(head, irises.rightSideUv);
                }
            }
        }

        static Mesh PieceOf(Mesh head, Rect uv)
        {
            lock (Pieces)
            {
                Dictionary<Rect, Mesh> pieces;
                Mesh piece;
                if (Pieces.TryGetValue(head, out pieces) && pieces.TryGetValue(uv, out piece))
                    return piece;
            }
            return UnityData.IsInMainThread ? MakePiece(head, uv) : null;
        }

        static Mesh MakePiece(Mesh head, Rect uv)
        {
            lock (Pieces)
            {
                Dictionary<Rect, Mesh> pieces;
                if (!Pieces.TryGetValue(head, out pieces))
                {
                    pieces = new Dictionary<Rect, Mesh>();
                    Pieces[head] = pieces;
                }
                Mesh piece;
                if (pieces.TryGetValue(uv, out piece))
                    return piece;
                HeadQuad quad = QuadOf(head);
                if (quad != null)
                {
                    piece = new Mesh();
                    piece.name = "BionicIrisPiece";
                    Fill(piece, quad, uv, null, null, 0, 0, false);
                }
                pieces[uv] = piece;
                return piece;
            }
        }

        static HeadQuad QuadOf(Mesh head)
        {
            HeadQuad quad;
            if (Heads.TryGetValue(head, out quad))
                return quad;
            Vector3[] vertices = head.vertices;
            Vector2[] uvs = head.uv;
            int[] triangles = head.triangles;
            var cornerOf = new int[4];
            int found = 0;
            if (vertices.Length == 4 && uvs.Length == 4)
            {
                quad = new HeadQuad();
                for (int i = 0; i < 4; i++)
                {
                    int corner = Mathf.RoundToInt(uvs[i].x) + 2 * Mathf.RoundToInt(uvs[i].y);
                    if (corner < 0 || corner > 3 || Mathf.Abs(uvs[i].x - Mathf.Round(uvs[i].x)) > 0.001f || Mathf.Abs(uvs[i].y - Mathf.Round(uvs[i].y)) > 0.001f)
                        break;
                    quad.corners[corner] = vertices[i];
                    cornerOf[i] = corner;
                    found |= 1 << corner;
                }
            }
            if (found == 15)
            {
                quad.triangles = new int[triangles.Length];
                for (int i = 0; i < triangles.Length; i++)
                    quad.triangles[i] = cornerOf[triangles[i]];
            }
            else
                quad = null;
            Heads[head] = quad;
            return quad;
        }

        static readonly List<Vector3> FillVertices = new List<Vector3>();
        static readonly List<Vector2> FillUvs = new List<Vector2>();
        static readonly List<int> FillTriangles = new List<int>(), FillRuns = new List<int>(), FillBand = new List<int>();

        static void Fill(Mesh mesh, HeadQuad head, Rect uv, int[][] open, Cover cover, int dx, int dy, bool anchored)
        {
            int columns = Mathf.RoundToInt(uv.width * 512f) * Steps, rows = Mathf.RoundToInt(uv.height * 512f) * Steps;
            Vector2 shift = anchored ? new Vector2(-dx, dy) / (512f * Steps) : Vector2.zero;
            if (anchored)
            {
                dx = 0;
                dy = 0;
            }
            List<Vector3> vertices = FillVertices;
            List<Vector2> uvs = FillUvs;
            List<int> triangles = FillTriangles, runs = FillRuns, band = FillBand;
            vertices.Clear();
            uvs.Clear();
            triangles.Clear();
            if (open == null)
                AddQuad(head, uv, shift, columns, rows, 0, 0, columns, rows, vertices, uvs, triangles);
            else
            {
                int rowShift = (Mathf.RoundToInt((1f - uv.yMax) * 512f) - cover.y0) * Steps + dy;
                int columnShift = (Mathf.RoundToInt(uv.xMin * 512f) - cover.x0) * Steps + dx;
                band.Clear();
                int bandTop = 0;
                for (int row = 0; row <= rows; row++)
                {
                    runs.Clear();
                    int coverRow = row + rowShift;
                    if (row < rows && coverRow >= 0 && coverRow < open.Length)
                    {
                        int[] line = open[coverRow];
                        for (int i = 0; i < line.Length; i += 2)
                        {
                            int start = Math.Max(line[i] - columnShift, 0), end = Math.Min(line[i + 1] - columnShift, columns);
                            if (start < end)
                            {
                                runs.Add(start);
                                runs.Add(end);
                            }
                        }
                    }
                    if (row < rows && SameRuns(runs, band))
                        continue;
                    for (int i = 0; i < band.Count; i += 2)
                        AddQuad(head, uv, shift, columns, rows, band[i], bandTop, band[i + 1], row, vertices, uvs, triangles);
                    band.Clear();
                    band.AddRange(runs);
                    bandTop = row;
                }
            }
            mesh.Clear();
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
        }

        static int[][] OpenRuns(byte[] alpha, int width, int height, float threshold)
        {
            int[] back = { 1, 1, 0, 0 }, eighths = { 5, 7, 1, 3 };
            int limit = (int)Math.Ceiling(threshold * 255f * 64f);
            var rows = new int[height * Steps][];
            var line = new List<int>();
            for (int j = 0; j < rows.Length; j++)
            {
                int ty = j / Steps - back[j % Steps], fy = eighths[j % Steps];
                line.Clear();
                int start = -1;
                for (int i = 0; i <= width * Steps; i++)
                {
                    bool open = false;
                    if (i < width * Steps)
                    {
                        int tx = i / Steps - back[i % Steps], fx = eighths[i % Steps];
                        int upper = (8 - fx) * Texel(alpha, width, height, tx, ty) + fx * Texel(alpha, width, height, tx + 1, ty);
                        int lower = (8 - fx) * Texel(alpha, width, height, tx, ty + 1) + fx * Texel(alpha, width, height, tx + 1, ty + 1);
                        open = (8 - fy) * upper + fy * lower < limit;
                    }
                    if (open && start < 0)
                        start = i;
                    else if (!open && start >= 0)
                    {
                        line.Add(start);
                        line.Add(i);
                        start = -1;
                    }
                }
                rows[j] = line.ToArray();
            }
            return rows;
        }

        static int Texel(byte[] alpha, int width, int height, int x, int y)
        {
            return x < 0 || y < 0 || x >= width || y >= height ? 255 : alpha[y * width + x];
        }

        static bool SameRuns(List<int> a, List<int> b)
        {
            if (a.Count != b.Count)
                return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i] != b[i])
                    return false;
            }
            return true;
        }

        static void AddQuad(HeadQuad head, Rect uv, Vector2 shift, int columns, int rows, int left, int top, int right, int bottom,
            List<Vector3> vertices, List<Vector2> uvs, List<int> triangles)
        {
            int start = vertices.Count;
            for (int corner = 0; corner < 4; corner++)
            {
                float u = ((corner & 1) == 0 ? left : right) / (float)columns;
                float v = 1f - ((corner & 2) == 0 ? bottom : top) / (float)rows;
                float headU = uv.xMin + u * uv.width + shift.x, headV = uv.yMin + v * uv.height + shift.y;
                vertices.Add(Vector3.Lerp(Vector3.Lerp(head.corners[0], head.corners[1], headU), Vector3.Lerp(head.corners[2], head.corners[3], headU), headV));
                uvs.Add(new Vector2(u, v));
            }
            foreach (int corner in head.triangles)
                triangles.Add(start + corner);
        }

        static void ReadCovers()
        {
            const int margin = 12;
            var windows = new int[2][];
            foreach (BionicIrisLayoutDef layout in DefDatabase<BionicIrisLayoutDef>.AllDefsListForReading)
            {
                Grow(ref windows[0], layout.left, layout.frontSize, margin);
                Grow(ref windows[0], layout.right, layout.frontSize, margin);
                Grow(ref windows[1], layout.side, layout.sideSize, margin);
                Grow(ref windows[0], layout.leftSocket, layout.socketSize, margin);
                Grow(ref windows[0], layout.rightSocket, layout.socketSize, margin);
                Grow(ref windows[1], layout.sideSocket, layout.sideSocketSize, margin);
            }
            if (windows[0] == null || windows[1] == null)
                return;
            var read = new Dictionary<Texture2D, Cover>();
            foreach (LidTypeDef type in DefDatabase<LidTypeDef>.AllDefsListForReading)
            {
                var shapes = new Dictionary<LidShapeDef, Cover[]>();
                foreach (LidShapeDef shape in DefDatabase<LidShapeDef>.AllDefsListForReading)
                {
                    if (shape.disableEyeball)
                        continue;
                    var covers = new Cover[4];
                    for (int gender = 0; gender < 2; gender++)
                    {
                        string prefix = GraphicHelper.GetTexPathPrefix(type, gender == 0 ? Gender.Male : Gender.Female) + "_cover";
                        string path = string.Format(prefix, shape.defName);
                        if (!GraphicHelper.CheckTexPathExist(path))
                            path = string.Format(prefix, "normal");
                        for (int view = 0; view < 2; view++)
                        {
                            Texture2D texture = ContentFinder<Texture2D>.Get(path + (view == 0 ? "_south" : "_east"), false);
                            if (texture != null)
                                covers[gender * 2 + view] = Read(texture, windows[view], read);
                        }
                    }
                    shapes[shape] = covers;
                }
                Covers[type] = shapes;
            }
        }

        static void Grow(ref int[] window, Vector2 corner, Vector2 size, int margin)
        {
            if (size.x <= 0f || size.y <= 0f)
                return;
            int left = Mathf.FloorToInt(corner.x) - margin, top = Mathf.FloorToInt(corner.y) - margin;
            int right = Mathf.CeilToInt(corner.x + size.x) + margin, bottom = Mathf.CeilToInt(corner.y + size.y) + margin;
            if (window == null)
                window = new[] { left, top, right, bottom };
            else
            {
                window[0] = Math.Min(window[0], left);
                window[1] = Math.Min(window[1], top);
                window[2] = Math.Max(window[2], right);
                window[3] = Math.Max(window[3], bottom);
            }
        }

        static Cover Read(Texture2D texture, int[] window, Dictionary<Texture2D, Cover> read)
        {
            Cover cover;
            if (read.TryGetValue(texture, out cover))
                return cover;
            int width = texture.width, height = texture.height;
            RenderTexture target = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            RenderTexture active = RenderTexture.active;
            var copy = new Texture2D(width, height, TextureFormat.ARGB32, false, true);
            try
            {
                Graphics.Blit(texture, target);
                RenderTexture.active = target;
                copy.ReadPixels(new Rect(0f, 0f, width, height), 0, 0);
                Color32[] pixels = copy.GetPixels32();
                int windowWidth = window[2] - window[0], windowHeight = window[3] - window[1];
                var alpha = new byte[windowWidth * windowHeight];
                for (int j = 0; j < windowHeight; j++)
                {
                    int ty = height - 1 - (int)((window[1] + j + 0.5f) * height / 512f);
                    for (int i = 0; i < windowWidth; i++)
                    {
                        int tx = (int)((window[0] + i + 0.5f) * width / 512f);
                        alpha[j * windowWidth + i] = tx < 0 || ty < 0 || tx >= width || ty >= height ? (byte)255 : pixels[ty * width + tx].a;
                    }
                }
                cover = new Cover
                {
                    x0 = window[0],
                    y0 = window[1],
                    lights = OpenRuns(alpha, windowWidth, windowHeight, 0.5f),
                    iris = OpenRuns(alpha, windowWidth, windowHeight, 0.98f),
                };
            }
            finally
            {
                RenderTexture.active = active;
                RenderTexture.ReleaseTemporary(target);
                UnityEngine.Object.Destroy(copy);
            }
            read[texture] = cover;
            return cover;
        }

        static Color Tint(BionicIrisDef iris, Color skin, Color eye)
        {
            if (iris.missingEye)
                return skin;
            float share = Mathf.Clamp01(iris.eyeColorTint);
            return new Color(1f + (eye.r - 1f) * share, 1f + (eye.g - 1f) * share, 1f + (eye.b - 1f) * share);
        }

        static Color SkinOf(Pawn pawn)
        {
            HeadControllerComp head = pawn.GetComp<HeadControllerComp>();
            Color color = head != null ? head.GetCurrentColor() : Color.clear;
            if (color.a > 0f)
                return color;
            return pawn.story != null ? pawn.story.SkinColor : Color.white;
        }

        static Color Glow(Color color)
        {
            float max = Mathf.Max(color.r, Mathf.Max(color.g, color.b));
            return max > 0.01f ? new Color(color.r / max, color.g / max, color.b / max) : Color.white;
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
            bool natural = true;
            foreach (Hediff hediff in pawn.health.hediffSet.hediffs)
            {
                if (hediff.Part != eye)
                    continue;
                if (hediff is Hediff_AddedPart)
                    natural = false;
                if (!MappedHediffs.Contains(hediff.def))
                    continue;
                foreach (BionicIrisDef def in DefDatabase<BionicIrisDef>.AllDefsListForReading)
                {
                    if (def.hediffs.Contains(hediff.def) && (best == null || def.priority > best.priority))
                        best = def;
                }
            }
            if (best != null || !natural)
                return best;
            if (pawn.health.hediffSet.PartIsMissing(eye))
                return MissingIris;
            if (BlindIris != null && PawnCapacityUtility.CalculatePartEfficiency(pawn.health.hediffSet, eye, false, null) <= 0f)
                return BlindIris;
            return null;
        }

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

        static Color Efficiency(Pawn pawn, BodyPartRecord eye, Color color)
        {
            if (eye == null)
                return color;
            float efficiency = Math.Min(1f, PawnCapacityUtility.CalculatePartEfficiency(pawn.health.hediffSet, eye, false, null));
            return (Color)CvtMainColor.Invoke(null, new object[] { color, efficiency });
        }
    }
}
