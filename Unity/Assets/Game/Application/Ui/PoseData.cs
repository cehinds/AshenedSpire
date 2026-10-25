using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.Generated;
using Newtonsoft.Json.Linq;

namespace Ashen.App.Ui
{
    /// <summary>One frame of the player figure on the shared pose canvas: where its crop sits, its size, the pelvis and the floor line (the web's src/content/poseSprites.js row).</summary>
    public sealed class PoseFrame
    {
        public double X;
        public double Y;
        public double W;
        public double H;
        public double Rx;
        public double Ry;
        public double G;
    }

    /// <summary>
    /// ui/poses.json (P-1, D-1320..): the player figure as the web draws it, a pose stage. Every frame of a class and tint is cropped tight, so each carries where its crop sat on a shared
    /// canvas and that canvas's floor line; the stage seats the idle frame's floor on the bottom of the figure box and its pelvis on the box centre and places each frame at its own offset,
    /// so the feet stay planted and a lunge reaches out past the box (PoseAnimator.js). Engine-free; the placement is arithmetic over fractions of the box, tested without the editor.
    /// </summary>
    public sealed class PoseData
    {
        private readonly Dictionary<string, PoseFrame> _frames = new Dictionary<string, PoseFrame>(StringComparer.Ordinal);

        public readonly double CanvasWidth;
        public readonly double CanvasHeight;
        public readonly IReadOnlyList<string> Strip;
        public readonly IReadOnlyList<string> AttackSequence;
        public readonly double DefaultPlayMs;
        public readonly double MinimumPlayMs;

        public PoseData(JObject json)
        {
            var canvas = json[PoseKeys.Canvas] as JObject ?? throw new ArgumentException(PoseKeys.Canvas);
            CanvasWidth = canvas.Value<double>(PoseKeys.Width);
            CanvasHeight = canvas.Value<double>(PoseKeys.Height);
            Strip = ((JArray)json[PoseKeys.Strip]).Select(t => t.Value<string>()).ToList();
            AttackSequence = ((JArray)json[PoseKeys.AttackSequence]).Select(t => t.Value<string>()).ToList();
            var motion = (JObject)json[PoseKeys.Motion];
            DefaultPlayMs = motion.Value<double>(PoseKeys.DefaultPlayMs);
            MinimumPlayMs = motion.Value<double>(PoseKeys.MinimumPlayMs);
            foreach (var row in (JObject)json[PoseKeys.Frames])
            {
                var v = ((JArray)row.Value).Select(t => t.Value<double>()).ToArray();
                _frames[row.Key] = new PoseFrame { X = v[0], Y = v[1], W = v[P1Count.FrameW], H = v[P1Count.FrameH], Rx = v[P1Count.FrameRx], Ry = v[P1Count.FrameRy], G = v[P1Count.FrameG] };
            }
        }

        public static string Key(string classId, string pose, string tint) => classId + PoseKeys.Separator + pose + PoseKeys.Separator + tint;

        /// <summary>The frame row of one pose, or null when this build does not ship it (poseFrame).</summary>
        public PoseFrame Frame(string classId, string pose, string tint) =>
            classId != null && pose != null && tint != null && _frames.TryGetValue(Key(classId, pose, tint), out var frame) ? frame : null;

        /// <summary>A class and tint with the idle frame every stage is anchored on (hasPoses).</summary>
        public bool Has(string classId, string tint) => Frame(classId, PoseKeys.Idle, tint) != null;

        /// <summary>The swings the class and tint carry, in the order the rotation throws them (PoseAnimator.js ATTACK_SEQUENCE filtered by what ships).</summary>
        public IReadOnlyList<string> Throwable(string classId, string tint) => AttackSequence.Where(p => Frame(classId, p, tint) != null).ToList();

        /// <summary>
        /// The placement of a frame in the figure box as fractions of the box (createPoseStage): the canvas layer is as tall as the box scaled so the idle figure (its top down to the
        /// floor line) fills it, its floor line on the box bottom and the idle pelvis on the box centre; the frame sits at its own crop on that layer. Left and top are the frame's
        /// corner from the box's left and top, width and height its size, all as multiples of the box's width and height. <paramref name="aspect"/> is box width over box height.
        /// </summary>
        public (double Left, double Top, double Width, double Height) Place(PoseFrame idle, PoseFrame frame, double aspect)
        {
            var figureHeight = Math.Max(1, idle.G - idle.Y);
            var layerHeight = CanvasHeight / figureHeight;
            var layerTop = 1 - idle.G / figureHeight;
            var layerWidth = layerHeight * CanvasWidth / CanvasHeight / Math.Max(P1Math.AspectFloor, aspect);
            var layerLeft = P1Math.Half - idle.Rx / CanvasWidth * layerWidth;
            return (layerLeft + frame.X / CanvasWidth * layerWidth, layerTop + frame.Y / CanvasHeight * layerHeight, frame.W / CanvasWidth * layerWidth, frame.H / CanvasHeight * layerHeight);
        }
    }
}
