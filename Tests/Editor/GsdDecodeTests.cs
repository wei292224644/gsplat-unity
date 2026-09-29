// Copyright (c) 2026 wwj
// SPDX-License-Identifier: MIT

using System;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;

namespace Gsplat.Tests
{
    public class GsdDecodeTests
    {
        const int k_stride = 63;
        const string k_probePath = "Packages/wu.yize.gsplat/Tests/Editor/GsdDecodeProbe.compute";

        /// <summary>
        /// Rust encodes, Rust decodes into the JSON, HLSL decodes the same bytes: the two decoders
        /// must agree (spec §8.1). Centres, alpha, colour and SH are exact arithmetic on both sides;
        /// scale and rotation go through exp/sin/cos, which GPUs are allowed to approximate.
        /// </summary>
        [Test]
        public void GpuDecodeMatchesTheRustDecoder()
        {
            var data = GsdReader.Read(GsdFixture.Bytes());
            var expected = GsdFixture.LoadExpected();
            var cs = AssetDatabase.LoadAssetAtPath<ComputeShader>(k_probePath);
            Assert.IsNotNull(cs, "probe compute shader missing");
            var n = (int)data.NodeCount;

            using var nodes = new GraphicsBuffer(GraphicsBuffer.Target.Structured, n * 2, 16);
            using var sh1 = new GraphicsBuffer(GraphicsBuffer.Target.Structured, n, 8);
            using var sh2 = new GraphicsBuffer(GraphicsBuffer.Target.Structured, n, 16);
            using var sh3 = new GraphicsBuffer(GraphicsBuffer.Target.Structured, n, 16);
            using var output = new GraphicsBuffer(GraphicsBuffer.Target.Structured, n * k_stride, 4);
            nodes.SetData(data.Nodes);
            sh1.SetData(data.SH1);
            sh2.SetData(data.SH2);
            sh3.SetData(data.SH3);

            var kernel = cs.FindKernel("Decode");
            cs.SetBuffer(kernel, "_LodNodesBuffer", nodes);
            cs.SetBuffer(kernel, "_PackedSH1Buffer", sh1);
            cs.SetBuffer(kernel, "_PackedSH2Buffer", sh2);
            cs.SetBuffer(kernel, "_PackedSH3Buffer", sh3);
            cs.SetBuffer(kernel, "_Out", output);
            cs.SetInt("_Count", n);
            cs.Dispatch(kernel, (n + 63) / 64, 1, 1);
            var got = new float[n * k_stride];
            output.GetData(got);

            for (var i = 0; i < n; ++i)
            {
                var e = expected.nodes[i];
                var o = i * k_stride;
                Close(e.center, got, o, 0f, $"node {i} center");
                Close(new[] { e.alpha }, got, o + 3, 1e-6f, $"node {i} alpha");
                Close(e.rgb, got, o + 4, 1e-6f, $"node {i} rgb");
                Close(e.scale, got, o + 7, 1e-4f * e.scale.Max(), $"node {i} scale");
                Close(e.quat, got, o + 10, 1e-4f, $"node {i} quat (x, y, z, w)");
                Close(new[] { e.quat[3], e.quat[0], e.quat[1], e.quat[2] }, got, o + 14, 1e-4f,
                    $"node {i} quat in shader order (w, x, y, z)");
                Close(e.sh, got, o + 18, 1e-6f, $"node {i} sh");
            }
        }

        /// <summary>
        /// Every variant the headset build ships must compile, not just the Editor's: both
        /// encodings (SPARK guards the SH split) × SH bands × mono/stereo. The Quest/PICO build
        /// (Vulkan, OpenXR single-pass) adds the built-in STEREO_MULTIVIEW_ON to every variant
        /// ("Compiling … with STEREO_MULTIVIEW_ON" in the Android build log); on Metal that keyword
        /// defines nothing, so the stereo cases compile Vulkan only.
        /// </summary>
        [Test]
        public void VariantCompilesForQuestAndEditor(
            [Values("SPARK", "LOD")] string encoding,
            [Values("SH_BANDS_0", "SH_BANDS_1", "SH_BANDS_2", "SH_BANDS_3")] string bands,
            [Values(false, true)] bool stereo)
        {
            var shader = Shader.Find("Gsplat/Standard");
            var pass = ShaderUtil.GetShaderData(shader).GetSubshader(0).GetPass(0);
            var targets = stereo
                ? new[] { (ShaderCompilerPlatform.Vulkan, BuildTarget.Android) }
                : new[]
                {
                    (ShaderCompilerPlatform.Vulkan, BuildTarget.Android),
                    (ShaderCompilerPlatform.Metal, BuildTarget.StandaloneOSX),
                };
            var keywords = stereo ? new[] { encoding, bands, "STEREO_MULTIVIEW_ON" } : new[] { encoding, bands };
            foreach (var (platform, target) in targets)
            foreach (var stage in new[] { ShaderType.Vertex, ShaderType.Fragment })
            {
                var info = pass.CompileVariant(stage, keywords, platform, target);
                Assert.IsTrue(info.Success, $"{string.Join(" ", keywords)} {stage} {platform}:\n" +
                                            string.Join("\n", info.Messages.Select(m => $"{m.severity}: {m.message} (line {m.line})")));
            }
        }

        /// <summary>
        /// Pins the merged-node profile (spec D15) as the shader evaluates it: the closed form at a
        /// grid of (D, A = |uv|²), centre 1, D → 1⁺ meeting the plain Gaussian e^{−4A}, and an edge
        /// value (A = 1, the rim the quad truncates at) that falls with D. The edge sits below
        /// 1/255 only from D ≈ 2.44 on — e.g. at the format's maximum D = 5 — and is 4.7e-3 at the
        /// fixture's D = 2.25; below that it still stays under the plain Gaussian's own rim value
        /// e^{−4} ≈ 1.8e-2, so a merged node is never cut harder than a plain splat. In σ units
        /// Spark's own profile and 0.7·(D − 1) widening reach the same rim values.
        /// </summary>
        [Test]
        public void MergedNodeProfileMeetsThePlainGaussianAndFadesTowardItsEdge()
        {
            var fixtureMaxD = GsdFixture.LoadExpected().nodes.Max(n => n.alpha);
            float[] ds = { 1.0001f, 1.25f, 1.5f, 2f, fixtureMaxD, 3f, 4f, 5f };
            float[] areas = { 0f, 0.1f, 0.25f, 0.5f, 0.75f, 1f };
            var samples = ds.SelectMany(d => areas.Select(a => new Vector2(d, a))).ToArray();
            var got = MergedAlphaOnGpu(samples);

            var previousRim = float.MaxValue;
            for (var i = 0; i < samples.Length; ++i)
            {
                var (d, a) = (samples[i].x, samples[i].y);
                var where = $"D {d}, A {a}";
                // GPU exp/pow are approximations; 5e-4 is far below the 1/255 that decides visibility.
                Assert.AreEqual(ReferenceMergedAlpha(d, a), got[i], 5e-4, where);
                if (a == 0f)
                    Assert.AreEqual(1f, got[i], 1e-6f, $"{where}: a merged node is opaque at its centre");
                if (d == ds[0])
                    Assert.AreEqual(Mathf.Exp(-4f * a), got[i], 1e-3f, $"{where}: D → 1⁺ must meet the plain Gaussian");
                if (a == 1f)
                {
                    Assert.Less(got[i], Mathf.Exp(-4f) + 1e-4f, $"{where}: rim above a plain splat's");
                    Assert.Less(got[i], previousRim, $"{where}: rim must fall as D grows");
                    previousRim = got[i];
                }
            }

            Assert.Less(got[Array.IndexOf(samples, new Vector2(5f, 1f))], 1f / 255f,
                "at the format's maximum D the truncated rim must be invisible");
        }

        // 1 − (1 − e^{−z²/2})^{exp((D²−1)/e)}, z² = 8k²A, k = (√8 + 0.7(D − 1)) / √8 — in double.
        static double ReferenceMergedAlpha(double d, double a)
        {
            var k = (Math.Sqrt(8) + 0.7 * (d - 1)) / Math.Sqrt(8);
            var z2 = 8 * k * k * a;
            return 1 - Math.Pow(Math.Max(1 - Math.Exp(-0.5 * z2), 0), Math.Exp((d * d - 1) / Math.E));
        }

        static float[] MergedAlphaOnGpu(Vector2[] samples)
        {
            var cs = AssetDatabase.LoadAssetAtPath<ComputeShader>(k_probePath);
            Assert.IsNotNull(cs, "probe compute shader missing");
            using var input = new GraphicsBuffer(GraphicsBuffer.Target.Structured, samples.Length, 8);
            using var output = new GraphicsBuffer(GraphicsBuffer.Target.Structured, samples.Length, 4);
            input.SetData(samples);
            var kernel = cs.FindKernel("MergedAlpha");
            cs.SetBuffer(kernel, "_AlphaIn", input);
            cs.SetBuffer(kernel, "_Out", output);
            cs.SetInt("_Count", samples.Length);
            cs.Dispatch(kernel, (samples.Length + 63) / 64, 1, 1);
            var got = new float[samples.Length];
            output.GetData(got);
            return got;
        }

        static void Close(float[] expected, float[] got, int offset, float tolerance, string what)
        {
            for (var k = 0; k < expected.Length; ++k)
                Assert.AreEqual(expected[k], got[offset + k], tolerance, $"{what}[{k}]");
        }
    }
}
