// Copyright (c) 2025 Yize Wu
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Gsplat
{
    public class GsplatSettings : ScriptableObject
    {
        const string k_gsplatSettingsResourcesPath = "GsplatSettings";

        const float k_defaultOffscreenScale = 0.5f;

        const string k_gsplatSettingsPath =
            "Assets/Gsplat/Settings/Resources/" + k_gsplatSettingsResourcesPath + ".asset";

        static GsplatSettings s_instance;

        public static GsplatSettings Instance
        {
            get
            {
                if (s_instance)
                    return s_instance;

                var settings = Resources.Load<GsplatSettings>(k_gsplatSettingsResourcesPath);
#if UNITY_EDITOR
                if (!settings)
                {
                    var assetPath = Path.GetDirectoryName(k_gsplatSettingsPath);
                    if (!Directory.Exists(assetPath))
                        Directory.CreateDirectory(assetPath);

                    settings = CreateInstance<GsplatSettings>();
                    settings.Reset();
                    AssetDatabase.CreateAsset(settings, k_gsplatSettingsPath);
                    AssetDatabase.SaveAssets();
                }
                else
                {
                    // Checked outside the version ladder below: those branches are chained with
                    // else-if, so an asset upgrading from an early version takes one branch, stamps
                    // the current version, and never picks up fields added by the later ones.
                    if (!settings.CompositeShader || settings.OffscreenScale <= 0f)
                    {
                        if (!settings.CompositeShader)
                            settings.CompositeShader = DefaultCompositeShader;
                        // An asset serialised before this field existed reads back as 0, which is
                        // not a resolution. Treat it as "never set" rather than clamping it to the
                        // range minimum, which would silently pick a scale nobody chose.
                        if (settings.OffscreenScale <= 0f)
                            settings.OffscreenScale = k_defaultOffscreenScale;
                        EditorUtility.SetDirty(settings);
                        AssetDatabase.SaveAssets();
                    }

                    if (settings.Version < new Version("1.2.0"))
                    {
                        Debug.Log($"Updated GsplatSettings from version {settings.Version}.");
                        settings.Materials = DefaultMaterials;
                        settings.GlobalMaterial = DefaultGlobalMaterial;
                        settings.m_prevComputeShader = null;
                        settings.Version = GsplatUtils.k_Version;
                        settings.OnValidate();
                        EditorUtility.SetDirty(settings);
                        AssetDatabase.SaveAssets();
                    }
                    else if (settings.Version < new Version("1.4.0"))
                    {
                        Debug.Log($"Updated GsplatSettings from version {settings.Version}.");
                        settings.GlobalMaterial = DefaultGlobalMaterial;
                        settings.Version = GsplatUtils.k_Version;
                        settings.OnValidate();
                        EditorUtility.SetDirty(settings);
                        AssetDatabase.SaveAssets();
                    }
                }
#endif

                s_instance = settings;
                return s_instance;
            }
        }

        public ComputeShader ComputeShader;
        public GsplatGlobalMaterial GlobalMaterial;

        [Tooltip("Resolves the offscreen gsplat target into the camera target. Required under URP.")]
        public Shader CompositeShader;

        [Tooltip(
            "Size of the offscreen gsplat target as a fraction of camera resolution. Splats are soft "
            + "by construction, so they tolerate this far better than geometry or UI, which stay at "
            + "full resolution. 1 renders at camera resolution.")]
        [Range(0.25f, 1f)]
        public float OffscreenScale;

        [Tooltip(
            "When enabled, 2+ active Gaussian splat renderers are merged into a single globally depth-sorted draw call.")]
        public bool EnableGlobalSort;

        public uint SplatInstanceSize;
        public uint UploadBatchSize;
        [Range(1, 20)] public uint MaxRenderOrder;
        public bool DisplayBoundingBoxes;

        [Tooltip(
            "If a camera moves more that this threshold, each GsplatRenderer compute sorting and cutouts regardless of refresh rate")]
        [Range(0.05f, 1f)]
        public float CameraTranslationRefreshTreshold;

        [Tooltip(
            "If a camera rotates more that this threshold, each GsplatRenderer compute sorting and cutouts refresh regardless of refresh rate")]
        [Range(0.2f, 30f)]
        public float CameraRotationRefreshTreshold;

        public bool ShowImportErrors;
        public GsplatMaterial[] Materials;
        public Mesh Mesh { get; private set; }

        public bool Valid => Materials?.Length != 0 && Mesh && SplatInstanceSize > 0;

        public Version Version
        {
            get => Version.Parse(m_version);
            set => m_version = value.ToString();
        }

        ComputeShader m_prevComputeShader;
        uint m_prevSplatInstanceSize;

        [HideInInspector] [SerializeField] string m_version = "1.0.0";

#if UNITY_EDITOR
        static ComputeShader DefaultComputeShader => AssetDatabase.LoadAssetAtPath<ComputeShader>(
            GsplatUtils.k_PackagePath + "Runtime/Shaders/Gsplat.compute");

        static GsplatGlobalMaterial DefaultGlobalMaterial => AssetDatabase.LoadAssetAtPath<GsplatGlobalMaterial>(
            GsplatUtils.k_PackagePath + "Runtime/Materials/GsplatGlobal.asset");

        static Shader DefaultCompositeShader => AssetDatabase.LoadAssetAtPath<Shader>(
            GsplatUtils.k_PackagePath + "Runtime/Shaders/GsplatComposite.shader");

        static GsplatMaterial[] DefaultMaterials
        {
            get
            {
                var materials = new GsplatMaterial[Enum.GetValues(typeof(CompressionMode)).Length];
                materials[(int)CompressionMode.Uncompressed] =
                    AssetDatabase.LoadAssetAtPath<GsplatMaterial>(GsplatUtils.k_PackagePath +
                                                                  "Runtime/Materials/GsplatUncompressed.asset");
                materials[(int)CompressionMode.Spark] =
                    AssetDatabase.LoadAssetAtPath<GsplatMaterial>(GsplatUtils.k_PackagePath +
                                                                  "Runtime/Materials/GsplatSpark.asset");
                return materials;
            }
        }

        public void Reset()
        {
            Version = GsplatUtils.k_Version;
            ComputeShader = DefaultComputeShader;
            GlobalMaterial = DefaultGlobalMaterial;
            CompositeShader = DefaultCompositeShader;
            OffscreenScale = k_defaultOffscreenScale;
            Materials = DefaultMaterials;
            SplatInstanceSize = 128;
            UploadBatchSize = 100000;
            MaxRenderOrder = 1;
            DisplayBoundingBoxes = false;
            CameraTranslationRefreshTreshold = 0.2f;
            CameraRotationRefreshTreshold = 10;
            ShowImportErrors = true;

            m_prevComputeShader = null;
            m_prevSplatInstanceSize = 0;
            OnValidate();
        }
#endif

        void CreateMeshInstance()
        {
            var meshPositions = new Vector3[4 * SplatInstanceSize];
            var meshIndices = new int[6 * SplatInstanceSize];
            for (uint i = 0; i < SplatInstanceSize; ++i)
            {
                unsafe
                {
                    meshPositions[i * 4] = new Vector3(-1, -1, *(float*)&i);
                    meshPositions[i * 4 + 1] = new Vector3(1, -1, *(float*)&i);
                    meshPositions[i * 4 + 2] = new Vector3(-1, 1, *(float*)&i);
                    meshPositions[i * 4 + 3] = new Vector3(1, 1, *(float*)&i);
                }

                int b = (int)i * 4;
                Array.Copy(new[] { 0 + b, 1 + b, 2 + b, 1 + b, 3 + b, 2 + b }, 0, meshIndices, i * 6, 6);
            }

            Mesh = new Mesh
            {
                name = "GsplatMeshInstance",
                vertices = meshPositions,
                triangles = meshIndices,
                hideFlags = HideFlags.HideAndDontSave
            };
        }

        void OnValidate()
        {
            if (ComputeShader != m_prevComputeShader)
            {
                GsplatSorter.Instance.InitSorter(ComputeShader);
                m_prevComputeShader = ComputeShader;
            }

            GsplatSorter.Instance.InitGlobal(GlobalMaterial);

            if (SplatInstanceSize != m_prevSplatInstanceSize)
            {
                DestroyImmediate(Mesh);
                CreateMeshInstance();
                m_prevSplatInstanceSize = SplatInstanceSize;
            }
#if UNITY_EDITOR
            foreach (var mat in Materials)
            {
                mat.Reset();
            }

            if (GlobalMaterial)
                GlobalMaterial.Reset();
#endif
        }

        void OnEnable()
        {
            GsplatSorter.Instance.InitSorter(ComputeShader);
            GsplatSorter.Instance.InitGlobal(GlobalMaterial);
            m_prevComputeShader = ComputeShader;

            CreateMeshInstance();
            m_prevSplatInstanceSize = SplatInstanceSize;
        }
    }
}