// Copyright (c) 2026 wwj
// SPDX-License-Identifier: MIT

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
            var cs = AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/wu.yize.gsplat/Tests/Editor/GsdDecodeProbe.compute");
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

        /// <summary>The LOD variant must compile for the headset target as well as the Editor; SPARK guards the SH split.</summary>
        [TestCase("SPARK")]
        [TestCase("LOD")]
        public void VariantCompilesForQuestAndEditor(string encoding)
        {
            var shader = Shader.Find("Gsplat/Standard");
            var pass = ShaderUtil.GetShaderData(shader).GetSubshader(0).GetPass(0);
            var targets = new[]
            {
                (ShaderCompilerPlatform.Vulkan, BuildTarget.Android),
                (ShaderCompilerPlatform.Metal, BuildTarget.StandaloneOSX),
            };
            foreach (var (platform, target) in targets)
            foreach (var stage in new[] { ShaderType.Vertex, ShaderType.Fragment })
            {
                var info = pass.CompileVariant(stage, new[] { encoding, "SH_BANDS_3" }, platform, target);
                Assert.IsTrue(info.Success, $"{encoding} {stage} {platform}:\n" +
                                            string.Join("\n", info.Messages.Select(m => $"{m.severity}: {m.message} (line {m.line})")));
            }
        }

        static void Close(float[] expected, float[] got, int offset, float tolerance, string what)
        {
            for (var k = 0; k < expected.Length; ++k)
                Assert.AreEqual(expected[k], got[offset + k], tolerance, $"{what}[{k}]");
        }
    }
}
