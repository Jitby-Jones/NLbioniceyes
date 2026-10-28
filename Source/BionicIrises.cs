// Bionic Irises: per-eye iris sprites for bionic eyes on [NL] Facial Animation faces.
// Written in C# 5 so it builds with the compiler that ships with .NET Framework (see build.ps1).
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using FacialAnimation;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
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

        // Folder holding Female/ and Male/ sets drawn on Facial Animation's Normal eyes (see make_irises.py):
        // normal_{L,R}_{south,east}.png is the eye with its lights off, normal_{L,R}_glow_{south,east}.png its lights in
        // white, tinted with the pawn's eye colour.
        public string texPath;
        public string shader = "Map/Cutout";

        // Drawn after the map's darkness, so the lights stay bright at night.
        public string glowShader = "Map/TransparentPostLight";

        // Drawn instead when glowing is turned off in the mod settings: the lights then darken at night like the rest.
        public string litShader = "Map/Transparent";

        // Decides between two mapped hediffs on the same eye; the higher one is drawn.
        public int priority;
    }

    /// <summary>The mod's settings, under Options, Mod settings.</summary>
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

        // Runs when the settings window closes: faces already made are rebuilt with the new settings.
        public override void WriteSettings()
        {
            base.WriteSettings();
            BionicIrisUtility.ReloadAll();
        }
    }

    /// <summary>
    /// Where the iris textures of one gender sit on Facial Animation's 512 px face, written by make_irises.py: top-left
    /// corners and sizes in pixels, facing south for the eye on the viewer's left and right, and facing east for the eye
    /// seen side-on, which the game mirrors facing west.
    /// </summary>
    public class BionicIrisLayoutDef : Def
    {
        public Gender gender;
        public Vector2 left;
        public Vector2 right;
        public Vector2 frontSize;
        public Vector2 side;
        public Vector2 sideSize;

        // The rectangle in texture coordinates, which run from the bottom left.
        internal static Rect Uv(Vector2 corner, Vector2 size)
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

        static readonly HashSet<HediffDef> MappedHediffs = new HashSet<HediffDef>();

        // The irises on each pawn's eyes, per comp, without keeping dead pawns alive.
        static readonly ConditionalWeakTable<EyeballControllerComp, Irises> Fitted = new ConditionalWeakTable<EyeballControllerComp, Irises>();

        // Pieces of Facial Animation's head meshes that iris textures are drawn on, by head mesh and texture rectangle.
        // They are made on the main thread and only read while the game gathers draw requests on its worker threads.
        static readonly Dictionary<Mesh, Dictionary<Rect, Mesh>> Pieces = new Dictionary<Mesh, Dictionary<Rect, Mesh>>();

        // Iris textures cover only the eye, so they are only drawn when they can be put on their piece of the head.
        static bool DrawsPieces;
        static bool WarnedMissingPiece;
        internal static bool Disabled;

        sealed class Irises
        {
            public bool left, right;
            public Rect leftUv, rightUv, sideUv;
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
            }
            if (PawnField == null || FaceTypeField == null || GraphicListField == null || SecondColorField == null || CvtMainColor == null)
            {
                Log.Error("[Bionic Irises] This Facial Animation version is not supported; iris sprites are off.");
                return;
            }
            // Each patch goes on separately so that one target changing in a game or Facial Animation update
            // (1.6 and 1.7 share this assembly) cannot stop the others.
            var harmony = new Harmony("neztech.fabioniciris");
            DrawsPieces = Patch(harmony, AccessTools.Method(typeof(NLFacialAnimationHeadNodeWorker), "AppendDrawRequests"), "AppendDrawRequestsPostfix");
            Type faceGraphics = AccessTools.TypeByName("FacialAnimation.DrawFaceGraphicsComp");
            Patch(harmony, faceGraphics == null ? null : AccessTools.Method(faceGraphics, "CompRenderNodes"), "CompRenderNodesPostfix");
            Patch(harmony, AccessTools.Method(typeof(EyeballControllerComp), "LoadTextures"), "LoadTexturesPostfix");
            Patch(harmony, AccessTools.Method(typeof(AnimationFrameMapper), "ApplyFrame"), "ApplyFramePostfix");
            foreach (string name in new[] { "AddHediff", "RemoveHediff" })
            {
                List<MethodInfo> overloads = HediffMethods(name);
                if (overloads.Count == 0)
                    Patch(harmony, null, name);
                foreach (MethodInfo method in overloads)
                    Patch(harmony, method, "HediffChangedPostfix");
            }
        }

        static bool Patch(Harmony harmony, MethodInfo target, string postfix)
        {
            if (target == null)
            {
                Log.Warning("[Bionic Irises] Could not find a method to patch for " + postfix + "; that part is off.");
                return false;
            }
            try
            {
                harmony.Patch(target, null, new HarmonyMethod(AccessTools.Method(typeof(BionicIrisUtility), postfix)));
                return true;
            }
            catch (Exception e)
            {
                Log.Error("[Bionic Irises] Could not patch " + target.DeclaringType.Name + "." + target.Name + ": " + e);
                return false;
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

        // Facial Animation has just made a pawn's face layers, on the main thread, with the head mesh it will draw them
        // on: the pieces of that mesh are made now, as they cannot be made while draw requests are gathered.
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

        // Facial Animation draws every face layer on the whole head. An iris layer is drawn on just the piece of the head
        // its texture covers instead, and not at all when its eye is out of sight (L facing west, R facing east).
        static void AppendDrawRequestsPostfix(PawnRenderNode node, List<PawnGraphicDrawRequest> requests)
        {
            NLFacialAnimationPartNode part = node as NLFacialAnimationPartNode;
            if (part == null || requests == null || requests.Count == 0)
                return;
            bool left;
            switch (part.layerType)
            {
                case NLFacialAnimationLayerType.L_Basic:
                case NLFacialAnimationLayerType.L_Option:
                    left = true;
                    break;
                case NLFacialAnimationLayerType.R_Basic:
                case NLFacialAnimationLayerType.R_Option:
                    left = false;
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
            Mesh piece = null;
            if (facing == Rot4.South)
                piece = PieceOf(request.mesh, left ? irises.leftUv : irises.rightUv);
            else if (facing == (left ? Rot4.East : Rot4.West))
                piece = PieceOf(request.mesh, irises.sideUv);
            else
            {
                requests.RemoveAt(last);
                return;
            }
            if (piece == null)
            {
                // Never stretch the small texture over the whole head; skip the eye for this frame instead.
                requests.RemoveAt(last);
                if (!WarnedMissingPiece)
                {
                    WarnedMissingPiece = true;
                    Log.Warning("[Bionic Irises] A face was drawn on a head mesh the irises were not prepared for; that iris is hidden.");
                }
                return;
            }
            var replaced = new PawnGraphicDrawRequest(node, piece, request.material);
            replaced.preDrawnComputedMatrix = request.preDrawnComputedMatrix;
            requests[last] = replaced;
        }

        // Facial Animation shows eye highlights only for content, conscious pawns. An iris's lights sit in that layer
        // but should only go out with the eye itself (blinks and closed eyes), so they are shown again whenever it is.
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
            Fitted.Remove(comp);
            if (!DrawsPieces)
                return;
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
            string gender = pawn.gender == Gender.Female ? "Female" : "Male";
            BionicIrisLayoutDef layout = Layout(pawn.gender == Gender.Female ? Gender.Female : Gender.Male);
            if (layout == null)
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

            // Facial Animation draws the left eye in its L layer and the right eye in its R layer, unless its
            // "switch left and right eyes" option swaps them. L is the eye on the viewer's left facing south and the one
            // shown facing east; R is the other one, shown facing west.
            if (FacialAnimationMod.Settings != null && FacialAnimationMod.Settings.SwitchLeftAndRightEyes)
            {
                BionicIrisDef iris = rightIris;
                rightIris = leftIris;
                leftIris = iris;
                Color color = rightColor;
                rightColor = leftColor;
                leftColor = color;
            }
            var irises = new Irises
            {
                leftUv = BionicIrisLayoutDef.Uv(layout.left, layout.frontSize),
                rightUv = BionicIrisLayoutDef.Uv(layout.right, layout.frontSize),
                sideUv = BionicIrisLayoutDef.Uv(layout.side, layout.sideSize),
            };
            if (leftIris != null)
                irises.left = Replace(graphics, NLFacialAnimationLayerType.L_Basic, NLFacialAnimationLayerType.L_Option, leftIris, gender, "L", leftColor);
            if (rightIris != null)
                irises.right = Replace(graphics, NLFacialAnimationLayerType.R_Basic, NLFacialAnimationLayerType.R_Option, rightIris, gender, "R", rightColor);
            if (!irises.left && !irises.right)
                return;
            PreparePieces(pawn, irises);
            Fitted.Add(comp, irises);
        }

        /// <summary>Asks Facial Animation to rebuild a pawn's eyes when a mapped eye is installed or removed.</summary>
        internal static void Refresh(Pawn pawn, Hediff hediff)
        {
            if (pawn == null || hediff == null || !MappedHediffs.Contains(hediff.def))
                return;
            EyeballControllerComp comp = pawn.GetComp<EyeballControllerComp>();
            if (comp != null)
                Reload(pawn, comp);
        }

        /// <summary>Rebuilds the eyes of every pawn with irises, on the maps and in caravans, for new settings.</summary>
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
            // Facial Animation only reloads eyes when the pawn's render tree is rebuilt, so ask for that as its own
            // editor does; graphics can only be touched from the main thread.
            if (UnityData.IsInMainThread)
            {
                pawn.Drawer.renderer.SetAllGraphicsDirty();
                PortraitsCache.SetDirty(pawn);
            }
        }

        // The sprites take the outline and position of Facial Animation's Normal eye, whatever the pawn's eye type, so a
        // bionic eye looks where a natural one does. Each layer's textures cover its eye facing south and the eye seen
        // side-on.
        static bool Replace(Dictionary<NLFacialAnimationLayerType, NLGraphic_Collection<EyeballShapeDef>> graphics,
            NLFacialAnimationLayerType basic, NLFacialAnimationLayerType highlight, BionicIrisDef iris, string gender,
            string side, Color accent)
        {
            string tex = iris.texPath + "/" + gender + "/{0}_" + side;
            if (!GraphicHelper.CheckTexPathExist(string.Format(tex, "normal")))
                return false;
            graphics[basic] = Collection(new NLGraphicParam(iris.shader, tex, null, Color.white, Color.white));
            // The highlight layer sits just above the eyeball, moves with it and closes with it, so it carries the
            // lights; its own glints are not wanted, as the sprite has them.
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

        // Makes the pieces of the head meshes Facial Animation draws this pawn's face on, in every facing, for every iris
        // rectangle. Main thread only.
        static void PreparePieces(Pawn pawn, Irises irises)
        {
            GraphicMeshSet set = MeshPool.GetMeshSetForSize(GraphicHelper.GetHeadMeshSet(pawn));
            foreach (Rot4 rot in new[] { Rot4.North, Rot4.East, Rot4.South, Rot4.West })
            {
                Mesh head = set.MeshAt(rot);
                if (head == null)
                    continue;
                if (irises.left)
                    MakePiece(head, irises.leftUv);
                if (irises.right)
                    MakePiece(head, irises.rightUv);
                MakePiece(head, irises.sideUv);
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

        // The part of a head mesh that shows the texture rectangle uv: its corners are the head's own, interpolated, so
        // it keeps the head's depth, its mirroring facing west and its winding, and it maps a whole texture onto the
        // rectangle.
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
                Vector3[] vertices = head.vertices;
                Vector2[] uvs = head.uv;
                if (vertices.Length != 4 || uvs.Length != 4)
                {
                    pieces[uv] = null;
                    return null;
                }
                var corners = new Vector3[4];
                int found = 0;
                for (int i = 0; i < 4; i++)
                {
                    int corner = Mathf.RoundToInt(uvs[i].x) + 2 * Mathf.RoundToInt(uvs[i].y);
                    if (corner < 0 || corner > 3 || Mathf.Abs(uvs[i].x - Mathf.Round(uvs[i].x)) > 0.001f || Mathf.Abs(uvs[i].y - Mathf.Round(uvs[i].y)) > 0.001f)
                        break;
                    corners[corner] = vertices[i];
                    found |= 1 << corner;
                }
                if (found != 15)
                {
                    pieces[uv] = null;
                    return null;
                }
                var pieceVertices = new Vector3[4];
                for (int i = 0; i < 4; i++)
                {
                    float u = uv.xMin + uvs[i].x * uv.width;
                    float v = uv.yMin + uvs[i].y * uv.height;
                    pieceVertices[i] = Vector3.Lerp(Vector3.Lerp(corners[0], corners[1], u), Vector3.Lerp(corners[2], corners[3], u), v);
                }
                piece = new Mesh();
                piece.name = "BionicIrisPiece";
                piece.vertices = pieceVertices;
                piece.uv = uvs;
                piece.triangles = head.triangles;
                piece.RecalculateNormals();
                piece.RecalculateBounds();
                pieces[uv] = piece;
                return piece;
            }
        }

        // The eye colour at full brightness, so that dark eyes still give off light.
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
