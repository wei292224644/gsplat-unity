// Copyright (c) 2026 wwj
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Gsplat.Tests
{
    // These tests drive Camera.main and create/destroy GameObjects in the active scene, so they
    // must not run against the user's open scene: they'd see (and could dirty) its contents, and
    // Camera.main would resolve to the scene's own camera instead of the test one. Isolate the
    // fixture into a fresh empty scene for the duration and restore the user's setup afterward.
    public class GsplatLodRendererTests
    {
        GsplatLodAsset m_asset;
        uint m_savedBudget;
        readonly List<GameObject> m_objects = new();
        SceneSetup[] m_sceneSetup;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            for (var i = 0; i < SceneManager.sceneCount; ++i)
            {
                if (!SceneManager.GetSceneAt(i).isDirty)
                    continue;
                Assert.Ignore("An open scene has unsaved changes; save or discard them before running GsplatLodRendererTests.");
                return;
            }

            m_sceneSetup = EditorSceneManager.GetSceneManagerSetup();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            if (m_sceneSetup != null && m_sceneSetup.Length > 0)
                EditorSceneManager.RestoreSceneManagerSetup(m_sceneSetup);
        }

        [SetUp]
        public void SetUp()
        {
            m_asset = AssetDatabase.LoadAssetAtPath<GsplatLodAsset>(GsdFixture.GsdPath);
            m_savedBudget = GsplatSettings.Instance.LodSplatBudget;
            GsplatSettings.Instance.LodSplatBudget = 4;
            var camera = Create("TestCamera");
            camera.AddComponent<Camera>();
            camera.tag = "MainCamera";
            camera.transform.position = new Vector3(0f, 0f, -20f);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var go in m_objects)
                if (go)
                    Object.DestroyImmediate(go);
            m_objects.Clear();
            GsplatSettings.Instance.LodSplatBudget = m_savedBudget;
        }

        GameObject Create(string name)
        {
            var go = new GameObject(name);
            m_objects.Add(go);
            return go;
        }

        GsplatRenderer CreateRenderer(string name)
        {
            var renderer = Create(name).AddComponent<GsplatRenderer>();
            renderer.GsplatAsset = m_asset;
            return renderer;
        }

        // Minimal in-memory non-LoD asset, built the same way RuntimePlyBytesLoaderTest builds one
        // from PLY bytes (Allocate then fill the arrays) — there is no existing small non-LoD asset
        // fixture in this test assembly, and importing a real .ply/.spz just for this would be far
        // more setup than the scenario needs.
        static GsplatAssetUncompressed CreateNonLodAsset(uint splatCount)
        {
            var asset = ScriptableObject.CreateInstance<GsplatAssetUncompressed>();
            asset.SplatCount = splatCount;
            asset.SHBands = 0;
            asset.Allocate();
            for (uint i = 0; i < splatCount; i++)
            {
                asset.Positions[i] = Vector3.zero;
                asset.Colors[i] = new Vector4(1f, 1f, 1f, 1f);
                asset.Scales[i] = Vector3.one * 0.01f;
                asset.Rotations[i] = new Vector4(0f, 0f, 0f, 1f);
            }

            asset.Bounds = new Bounds(Vector3.zero, Vector3.one);
            return asset;
        }

        [Test]
        public void PublishesACutWithinTheBudgetAndFlagsASort()
        {
            var renderer = CreateRenderer("lod");
            renderer.Update();
            Assert.IsTrue(renderer.IsLod);
            Assert.That(renderer.RemainingCount, Is.InRange(1u, 4u));
            Assert.IsTrue(renderer.ComputeSortRequired, "a fresh cut must be sorted the frame it lands (D11)");
            Assert.IsTrue(renderer.SorterResource.Initialized, "the cut is the payload; the sort must not identity-fill it (D14)");
        }

        [Test]
        public void SwitchingToLodWhileAnotherRendererIsLiveResetsStaleState()
        {
            var first = CreateRenderer("first");
            first.Update();
            Assert.AreEqual(1, GsplatLodDriver.LiveCount);

            // budget is 4 (SetUp); more splats than that so a leftover RemainingCount would be
            // visibly wrong against the LoD asset's budget-sized order buffer.
            var nonLodAsset = CreateNonLodAsset(6);
            try
            {
                var second = Create("second").AddComponent<GsplatRenderer>();
                second.GsplatAsset = nonLodAsset;
                second.Update();
                Assert.Greater(second.RemainingCount, 0u,
                    "the non-LoD asset must render normally before the switch");

                second.GsplatAsset = m_asset;
                LogAssert.Expect(LogType.Error, new Regex("at most one active LoD renderer"));
                second.Update();

                Assert.AreEqual(0u, second.RemainingCount,
                    "a D12-refused rebind must not keep drawing the previous binding's splat count " +
                    "over a budget-sized buffer");
            }
            finally
            {
                Object.DestroyImmediate(nonLodAsset);
            }
        }

        [Test]
        public void AFreshCutForcesASortEvenWhenTheScheduleWouldSkipIt()
        {
            var renderer = CreateRenderer("lod");
            renderer.SortMode = GsplatRenderer.GsplatSortMode.SortEveryNFrames;
            renderer.SortRefreshRate = 1000;
            renderer.Update(); // primes the driver; the camera is newly seen so this frame always sorts

            renderer.ReloadAsset(); // forces a rebind, which primes a brand-new cut next frame
            renderer.Update();

            Assert.IsTrue(renderer.ComputeSortRequired,
                "a fresh cut lands unsorted; it must be sorted the frame it publishes regardless of " +
                "the sort-refresh schedule (D11)");
        }

        [Test]
        public void ClearingTheAssetReleasesTheDriverSoAnotherCanBind()
        {
            var a = CreateRenderer("a");
            a.Update();
            Assert.AreEqual(1, GsplatLodDriver.LiveCount);

            a.GsplatAsset = null;
            a.Update();
            Assert.AreEqual(0, GsplatLodDriver.LiveCount,
                "clearing the asset must release the driver, or D12 starves every later LoD renderer");

            var b = CreateRenderer("b");
            b.Update(); // must not log the D12 error — LogAssert fails the test on any unexpected error
            Assert.IsTrue(b.IsLod);
            Assert.Greater(b.RemainingCount, 0u);
        }

        [Test]
        public void ChangingTheBudgetRebindsWithTheNewCapacity()
        {
            var renderer = CreateRenderer("lod");
            renderer.Update();
            GsplatSettings.Instance.LodSplatBudget = 2;
            renderer.Update();
            Assert.That(renderer.RemainingCount, Is.InRange(1u, 2u));
            Assert.AreEqual(2, renderer.SorterResource.OrderBuffer.count);
            Assert.AreEqual(1, GsplatLodDriver.LiveCount);
        }

        [Test]
        public void ASecondLodRendererRefusesToRender()
        {
            var first = CreateRenderer("first");
            first.Update();
            LogAssert.Expect(LogType.Error, new Regex("at most one active LoD renderer"));
            var second = CreateRenderer("second");
            second.Update();
            Assert.Greater(first.RemainingCount, 0u);
            Assert.AreEqual(0u, second.RemainingCount);
        }

        [Test]
        public void DestroyingTheRendererReleasesItsDriver()
        {
            var renderer = CreateRenderer("lod");
            renderer.Update();
            Assert.AreEqual(1, GsplatLodDriver.LiveCount);
            Object.DestroyImmediate(renderer.gameObject);
            Assert.AreEqual(0, GsplatLodDriver.LiveCount);
        }

        [Test]
        public void CutoutsAreReportedNotSilentlyApplied()
        {
            var renderer = CreateRenderer("lod");
            var cutout = Create("cutout").AddComponent<GsplatCutout>();
            cutout.transform.SetParent(renderer.transform);
            // GsplatCutout registers itself in OnEnable, which edit mode does not call for it.
            GsplatCutout.m_RegisteredCutouts.Add(cutout);
            try
            {
                LogAssert.Expect(LogType.Error, new Regex("do not support cutouts"));
                renderer.Update();
            }
            finally
            {
                GsplatCutout.m_RegisteredCutouts.Remove(cutout);
            }
        }

        [Test]
        public void UniformScaleIsInvisibleToTheSelection()
        {
            var camera = Create("ViewCamera").AddComponent<Camera>();
            var model = Create("Model").transform;

            camera.transform.position = new Vector3(0f, 0f, -10f);
            var plain = GsplatLodDriver.BuildView(camera, model, 64);

            model.localScale = Vector3.one * 2f;
            camera.transform.position = new Vector3(0f, 0f, -20f);
            var scaled = GsplatLodDriver.BuildView(camera, model, 64);

            Assert.AreEqual(plain.Origin.z, scaled.Origin.z, 1e-5f);
            Assert.AreEqual(plain.PixelScaleLimit, scaled.PixelScaleLimit);
        }

        [Test]
        public void AMissingMainCameraIsReported()
        {
            Object.DestroyImmediate(m_objects[0]);
            Assume.That(Camera.main == null, "another MainCamera exists in the open scene");
            var renderer = CreateRenderer("lod");
            LogAssert.Expect(LogType.Error, new Regex("needs a camera tagged MainCamera"));
            renderer.Update();
            Assert.AreEqual(0u, renderer.RemainingCount);
        }
    }
}
