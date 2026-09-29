// Copyright (c) 2026 wwj
// SPDX-License-Identifier: MIT

using System.IO;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace Gsplat.Editor
{
    /// <summary>
    /// Imports <c>.gsd</c> LoD trees written by <c>Tools~/gsd-build</c>. Parsing only: the tree was
    /// built offline, so importing costs a read and a copy.
    /// </summary>
    [ScriptedImporter(1, "gsd")]
    public class GsdImporter : ScriptedImporter
    {
        public override void OnImportAsset(AssetImportContext ctx)
        {
            var asset = ScriptableObject.CreateInstance<GsplatLodAsset>();
            try
            {
                asset.LoadFromGsd(File.ReadAllBytes(ctx.assetPath));
            }
            catch (GsdFormatException e)
            {
                Object.DestroyImmediate(asset);
                ctx.LogImportError($"{ctx.assetPath}: {e.Message}");
                return;
            }

            asset.name = Path.GetFileNameWithoutExtension(ctx.assetPath);
            ctx.AddObjectToAsset("gsplatLodAsset", asset);
            ctx.SetMainObject(asset);
        }
    }
}
