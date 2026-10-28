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
        // white, tinted with the pawn's eye colour. The eye is drawn with Facial Animation's own eyeball shader, so that
        // the lids close over it as they do over a natural eye.
        public string texPath;

        // Drawn after the map's darkness, so the lights stay bright at night. As nothing drawn before can cover them,
        // they are cut to the part of the eye the lids leave open.
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
        static readonly FieldInfo LidTypeField = AccessTools.Field(typeof(LidControllerComp), "faceType");
        static readonly FieldInfo LidShapeField = AccessTools.Field(typeof(LidControllerComp), "Shape");

        // Plain white, so that Facial Animation's eyeball shader draws an iris texture as it is.
        const string WhiteMask = "Things/Pawn/BionicIrises/Mask";

        static readonly HashSet<HediffDef> MappedHediffs = new HashSet<HediffDef>();

        // The irises on each pawn's eyes, per comp and per pawn, without keeping dead pawns alive.
        static readonly ConditionalWeakTable<EyeballControllerComp, Irises> Fitted = new ConditionalWeakTable<EyeballControllerComp, Irises>();
        static readonly ConditionalWeakTable<Pawn, Irises> FittedPawns = new ConditionalWeakTable<Pawn, Irises>();

        // Pieces of Facial Animation's head meshes that iris textures are drawn on, by head mesh and texture rectangle.
        // They are made on the main thread and only read while the game gathers draw requests on its worker threads.
        static readonly Dictionary<Mesh, Dictionary<Rect, Mesh>> Pieces = new Dictionary<Mesh, Dictionary<Rect, Mesh>>();

        // Head meshes as quads (see HeadQuad), and the alpha of Facial Animation's lid covers around the eyes by lid type,
        // lid shape and view (see Cover). Main thread only.
        static readonly Dictionary<Mesh, HeadQuad> Heads = new Dictionary<Mesh, HeadQuad>();
        static readonly Dictionary<LidTypeDef, Dictionary<LidShapeDef, Cover[]>> Covers = new Dictionary<LidTypeDef, Dictionary<LidShapeDef, Cover[]>>();

        // Iris textures cover only the eye, so they are only drawn when they can be put on their piece of the head.
        static bool DrawsPieces;
        static bool WarnedMissingPiece;
        internal static bool Disabled;

        // Pieces are cut to the lids just before each pawn is drawn.
        static bool ClipsToLids;

        const int Steps = 4;  // a cut follows the lids in quarter pixels

        sealed class Irises
        {
            public bool left, right;
            public Rect leftUv, rightUv, sideUv;
            public EyeballControllerComp eyes;
            public LidControllerComp lids;
            public Gender gender;

            // The eyes (body parts) the irises are drawn on.
            public readonly List<BodyPartRecord> parts = new List<BodyPartRecord>();

            // The pieces each iris layer is drawn on, cut to the lids: [eye * 4 + layer * 2 + drawnAtOnce], eye 0 for L
            // and 1 for R, layer 0 for the eye and 1 for its lights, and apart for portraits and the like, which are drawn
            // at once, as the map is drawn at the end of the frame.
            public Clip[] clips;
        }

        // An iris layer's piece of the head, cut to the part of the eye Facial Animation's lids leave open. The head mesh
        // and rectangle it is wanted for are noted while draw requests are gathered on worker threads; the mesh is then
        // rebuilt on the main thread before the pawn is drawn, whenever those, the lids or the eye's offset from them
        // have changed.
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

        // A head mesh's corners by texture corner (0 bottom left, 1 bottom right, 2 top left, 3 top right) and its
        // triangles as those corners, for meshes that are a single quad.
        sealed class HeadQuad
        {
            public readonly Vector3[] corners = new Vector3[4];
            public int[] triangles;
        }

        // One lid cover texture around the eyes, worked out once: for each quarter-pixel row of a window of the 512 px
        // face, top down, where the cover leaves the eye open, as runs of quarter pixels (start and end pairs from the
        // window's left). Facial Animation cuts the cover where its filtered alpha reaches half: the lights are cut there,
        // the iris where the cover is all but opaque, a little wider. Past the window the eye counts as covered.
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
            Patch(harmony, AccessTools.Method(typeof(PawnRenderNodeWorker), "CanDrawNow"), "CanDrawNowPostfix");
            foreach (string name in new[] { "AddHediff", "RemoveHediff" })
            {
                List<MethodInfo> overloads = HediffMethods(name);
                if (overloads.Count == 0)
                    Patch(harmony, null, name);
                foreach (MethodInfo method in overloads)
                    Patch(harmony, method, "HediffChangedPostfix");
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
        // its texture covers instead, cut to the lids (see DrawPrefix), and not at all when its eye is out of sight (L
        // facing west, R facing east).
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
                uv = irises.sideUv;
            else
            {
                requests.RemoveAt(last);
                return;
            }
            Mesh piece = PieceOf(request.mesh, uv);
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

        // RimWorld draws its own small picture over an artificial eye, from the eye hediff's render nodes (vanilla's
        // bionic and archotech eyes, EPOE-Forked's), which would cover the iris. It is left out on the eyes the irises are
        // drawn on, and kept everywhere else, such as on faces Facial Animation does not draw.
        static void CanDrawNowPostfix(PawnRenderNode node, PawnDrawParms parms, ref bool __result)
        {
            Irises irises;
            if (__result && node.hediff != null && parms.pawn != null && MappedHediffs.Contains(node.hediff.def)
                && FittedPawns.TryGetValue(parms.pawn, out irises) && irises.parts.Contains(node.hediff.Part))
                __result = false;
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

        // Facial Animation hides the top of a natural eye under its lid layer. The iris itself is drawn with the same
        // shader as a natural eye so the lid covers it too, but its lights are drawn after the map's darkness, where
        // nothing can cover them, so they are cut to the part of the eye the lids leave open. The eye moves under the lids
        // as the face animates, so the cut follows it here, on the main thread, before the pawn is drawn. The iris layer
        // is cut as well, a little wider than the opening so that the lid still draws its edge, in case the game draws it
        // over the lid all the same.
        static void DrawPrefix(PawnRenderTree __instance, PawnDrawParms parms)
        {
            Irises irises;
            if (!ClipsToLids || Disabled || __instance.pawn == null || !FittedPawns.TryGetValue(__instance.pawn, out irises) || irises.clips == null)
                return;
            try
            {
                LidShapeDef shape = irises.lids == null ? null : LidShapeField.GetValue(irises.lids) as LidShapeDef;
                if (shape != null && shape.disableEyeball)
                    return;  // the eyes are closed and not drawn
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
                    Cut(clip, CoverFor(irises, shape, clip.side), eye - lids, lights);
                }
            }
            catch (Exception e)
            {
                ClipsToLids = false;
                Log.Error("[Bionic Irises] Stopped cutting irises to the lids after an error (Facial Animation may have changed): " + e);
            }
        }

        // The cover Facial Animation draws for the pawn's lids in their current shape, or null if it has none.
        static Cover CoverFor(Irises irises, LidShapeDef shape, bool side)
        {
            LidTypeDef type = irises.lids == null ? null : LidTypeField.GetValue(irises.lids) as LidTypeDef;
            Dictionary<LidShapeDef, Cover[]> shapes;
            Cover[] covers;
            if (type == null || shape == null || !Covers.TryGetValue(type, out shapes) || !shapes.TryGetValue(shape, out covers))
                return null;
            return covers[(irises.gender == Gender.Female ? 2 : 0) + (side ? 1 : 0)];
        }

        // Rebuilds a clip's mesh if the head, the rectangle, the lid cover or the eye's offset from the lids (delta, in
        // the head mesh's units) has changed.
        static void Cut(Clip clip, Cover cover, Vector3 delta, bool lights)
        {
            HeadQuad head = QuadOf(clip.head);
            if (head == null)
                return;
            Vector3 across = head.corners[1] - head.corners[0], up = head.corners[2] - head.corners[0];
            if (Mathf.Abs(across.x) < 1e-5f || Mathf.Abs(up.z) < 1e-5f)
                cover = null;
            // the offset in steps of the face texture's pixels, down the texture
            int x = cover == null ? 0 : Mathf.RoundToInt(delta.x / across.x * 512f * Steps);
            int y = cover == null ? 0 : -Mathf.RoundToInt(delta.z / up.z * 512f * Steps);
            if (clip.IsBuilt(clip.head, clip.uv, cover, x, y))
                return;
            Fill(clip.mesh, head, clip.uv, cover == null ? null : lights ? cover.lights : cover.iris, cover, x, y);
            clip.Built(clip.head, clip.uv, cover, x, y);
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
            Irises previous;
            Fitted.TryGetValue(comp, out previous);
            Fitted.Remove(comp);
            if (pawn != null)
                FittedPawns.Remove(pawn);
            if (!DrawsPieces)
                return;
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
            BodyPartRecord leftPart = leftEye, rightPart = rightEye;
            if (FacialAnimationMod.Settings != null && FacialAnimationMod.Settings.SwitchLeftAndRightEyes)
            {
                BionicIrisDef iris = rightIris;
                rightIris = leftIris;
                leftIris = iris;
                Color color = rightColor;
                rightColor = leftColor;
                leftColor = color;
                leftPart = rightEye;
                rightPart = leftEye;
            }
            var irises = new Irises
            {
                leftUv = BionicIrisLayoutDef.Uv(layout.left, layout.frontSize),
                rightUv = BionicIrisLayoutDef.Uv(layout.right, layout.frontSize),
                sideUv = BionicIrisLayoutDef.Uv(layout.side, layout.sideSize),
                eyes = comp,
                lids = pawn.GetComp<LidControllerComp>(),
                gender = pawn.gender == Gender.Female ? Gender.Female : Gender.Male,
            };
            // The same shader as Facial Animation's own eyeballs, so that its lids are drawn over the iris.
            string eyeShader = GraphicHelper.GetSkinShader(pawn, faceType);
            if (leftIris != null)
                irises.left = Replace(graphics, NLFacialAnimationLayerType.L_Basic, NLFacialAnimationLayerType.L_Option, leftIris, eyeShader, gender, "L", leftColor);
            if (rightIris != null)
                irises.right = Replace(graphics, NLFacialAnimationLayerType.R_Basic, NLFacialAnimationLayerType.R_Option, rightIris, eyeShader, gender, "R", rightColor);
            if (!irises.left && !irises.right)
                return;
            if (irises.left)
                irises.parts.Add(leftPart);
            if (irises.right)
                irises.parts.Add(rightPart);
            irises.clips = ClipsFor(previous);
            PreparePieces(pawn, irises);
            Fitted.Add(comp, irises);
            FittedPawns.Add(pawn, irises);
        }

        // The clips for a pawn's iris layers, reusing the meshes of the irises they replace, as meshes are not collected.
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
            NLFacialAnimationLayerType basic, NLFacialAnimationLayerType highlight, BionicIrisDef iris, string eyeShader,
            string gender, string side, Color accent)
        {
            string tex = iris.texPath + "/" + gender + "/{0}_" + side;
            if (!GraphicHelper.CheckTexPathExist(string.Format(tex, "normal")))
                return false;
            graphics[basic] = Collection(new NLGraphicParam(eyeShader, tex, WhiteMask, Color.white, Color.white));
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

        // The part of a head mesh that shows the texture rectangle uv, whole. Main thread only.
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
                    Fill(piece, quad, uv, null, null, 0, 0);
                }
                pieces[uv] = piece;
                return piece;
            }
        }

        // A head mesh as a quad, or null if it is not a single quad with whole texture corners. Main thread only.
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

        // Lists Fill reuses. Main thread only.
        static readonly List<Vector3> FillVertices = new List<Vector3>();
        static readonly List<Vector2> FillUvs = new List<Vector2>();
        static readonly List<int> FillTriangles = new List<int>(), FillRuns = new List<int>(), FillBand = new List<int>();

        // Fills mesh with the part of a head that shows the texture rectangle uv, mapping a whole texture onto it: all of
        // it without open runs, otherwise just where the cover's open runs (see Cover), moved by (dx, dy) quarter pixels,
        // fall. Its corners are the head's own, interpolated, so it keeps the head's depth, its mirroring facing west and
        // its winding.
        static void Fill(Mesh mesh, HeadQuad head, Rect uv, int[][] open, Cover cover, int dx, int dy)
        {
            int columns = Mathf.RoundToInt(uv.width * 512f) * Steps, rows = Mathf.RoundToInt(uv.height * 512f) * Steps;
            List<Vector3> vertices = FillVertices;
            List<Vector2> uvs = FillUvs;
            List<int> triangles = FillTriangles, runs = FillRuns, band = FillBand;
            vertices.Clear();
            uvs.Clear();
            triangles.Clear();
            if (open == null)
                AddQuad(head, uv, columns, rows, 0, 0, columns, rows, vertices, uvs, triangles);
            else
            {
                // where the rectangle's steps fall on the cover's
                int rowShift = (Mathf.RoundToInt((1f - uv.yMax) * 512f) - cover.y0) * Steps + dy;
                int columnShift = (Mathf.RoundToInt(uv.xMin * 512f) - cover.x0) * Steps + dx;
                // rows with the same runs make one band of quads
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
                        AddQuad(head, uv, columns, rows, band[i], bandTop, band[i + 1], row, vertices, uvs, triangles);
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

        // The runs of quarter pixels, row by row, where the cover's filtered alpha (the window's, rows from the top) is
        // below threshold. Quarter-pixel centres fall a fixed number of eighths past a texel centre, so the filtering is
        // done in whole numbers.
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

        // One quad of a piece, over columns left to right and rows top to bottom of the texture's steps.
        static void AddQuad(HeadQuad head, Rect uv, int columns, int rows, int left, int top, int right, int bottom,
            List<Vector3> vertices, List<Vector2> uvs, List<int> triangles)
        {
            int start = vertices.Count;
            for (int corner = 0; corner < 4; corner++)
            {
                float u = ((corner & 1) == 0 ? left : right) / (float)columns;
                float v = 1f - ((corner & 2) == 0 ? bottom : top) / (float)rows;
                float headU = uv.xMin + u * uv.width, headV = uv.yMin + v * uv.height;
                vertices.Add(Vector3.Lerp(Vector3.Lerp(head.corners[0], head.corners[1], headU), Vector3.Lerp(head.corners[2], head.corners[3], headU), headV));
                uvs.Add(new Vector2(u, v));
            }
            foreach (int corner in head.triangles)
                triangles.Add(start + corner);
        }

        // Reads the alpha of every lid cover Facial Animation can draw over open eyes, around the irises, by lid type,
        // shape, gender and view, with room for the eyes to move. The textures are not readable, so each is copied
        // through a render texture. Main thread only.
        static void ReadCovers()
        {
            const int margin = 12;
            var windows = new int[2][];  // south, east: left, top, right, bottom
            foreach (BionicIrisLayoutDef layout in DefDatabase<BionicIrisLayoutDef>.AllDefsListForReading)
            {
                Grow(ref windows[0], layout.left, layout.frontSize, margin);
                Grow(ref windows[0], layout.right, layout.frontSize, margin);
                Grow(ref windows[1], layout.side, layout.sideSize, margin);
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
                    var covers = new Cover[4];  // [gender * 2 + view]: gender 0 male, 1 female; view 0 south, 1 east
                    for (int gender = 0; gender < 2; gender++)
                    {
                        // as Facial Animation finds them: the shape's own cover, else the normal one
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
                    // texture rows run from the bottom
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
