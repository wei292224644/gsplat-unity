// Copyright (c) 2026 wwj
// SPDX-License-Identifier: MIT

using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Gsplat.Tests
{
    public class GsdImporterTests
    {
        [Test]
        public void ImportsTheFixtureAsALodAsset()
        {
            var expected = GsdFixture.LoadExpected();
            var asset = AssetDatabase.LoadAssetAtPath<GsplatLodAsset>(GsdFixture.GsdPath);
            Assert.IsNotNull(asset, "the .gsd importer did not produce a GsplatLodAsset");
            Assert.AreEqual(expected.nodeCount, asset.SplatCount, "SplatCount counts every node");
            Assert.AreEqual(expected.leafCount, asset.LeafCount);
            Assert.AreEqual(expected.shDegree, asset.SHBands);
            Assert.AreEqual(CompressionMode.Lod, asset.Compression);
            Assert.AreEqual(asset.SplatCount * 2, asset.Nodes.Length);
        }

        [Test]
        public void SettingsProvideTheLodMaterial()
        {
            var asset = AssetDatabase.LoadAssetAtPath<GsplatLodAsset>(GsdFixture.GsdPath);
            Assert.IsNotNull(asset.GsplatMaterial, "GsplatSettings has no material for CompressionMode.Lod");
            Assert.IsNotNull(asset.GsplatMaterial.CalcDepthShader, "Lod material has no depth kernel");
            Assert.IsTrue(asset.Materials[0].IsKeywordEnabled("LOD"), "Lod material must enable the LOD keyword");
        }

        [Test]
        public void LoadFromGsdRejectsCorruptBytes()
        {
            var asset = ScriptableObject.CreateInstance<GsplatLodAsset>();
            try
            {
                var bytes = GsdFixture.Bytes();
                bytes[0] = (byte)'X';
                Assert.Throws<GsdFormatException>(() => asset.LoadFromGsd(bytes));
            }
            finally
            {
                Object.DestroyImmediate(asset);
            }
        }
    }
}
