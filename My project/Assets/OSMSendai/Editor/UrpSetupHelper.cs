using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace OsmSendai.EditorTools
{
    internal static class UrpSetupHelper
    {
        private const string MenuRoot = "OSM Sendai/Setup/";
        private const string SettingsFolder = "Assets/OSMSendai/Settings";
        private const string UrpAssetPath = "Assets/OSMSendai/Settings/OSMSendai_URP.asset";

        [MenuItem(MenuRoot + "Open Render Pipeline Converter")]
        private static void OpenRenderPipelineConverter()
        {
            var opened = EditorApplication.ExecuteMenuItem("Window/Rendering/Render Pipeline Converter");
            if (!opened)
            {
                EditorUtility.DisplayDialog(
                    "URP Setup",
                    "Couldn't open the Render Pipeline Converter window automatically.\n\n" +
                    "In Unity, open:\n" +
                    "Window > Rendering > Render Pipeline Converter\n\n" +
                    "Then run the Built-in to URP conversion (Fix All).",
                    "OK");
            }
        }

        [MenuItem(MenuRoot + "Create & Assign URP Asset")]
        private static void CreateAndAssignUrpAsset()
        {
            EnsureFolder("Assets/OSMSendai");
            EnsureFolder(SettingsFolder);

            var existing = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(UrpAssetPath);
            var urpAsset = existing != null ? existing : CreateUrpAsset(UrpAssetPath);
            if (urpAsset == null)
            {
                EditorUtility.DisplayDialog("URP Setup", "Failed to create a URP Render Pipeline Asset.", "OK");
                return;
            }

            GraphicsSettings.renderPipelineAsset = urpAsset;
            AssignPipelineToQualityLevels(urpAsset);

            EditorUtility.SetDirty(urpAsset);
            AssetDatabase.SaveAssets();

            EditorUtility.DisplayDialog(
                "URP Setup",
                "URP Render Pipeline Asset is now assigned.\n\n" +
                "If you still see pink materials, run:\n" +
                "OSM Sendai > Setup > Open Render Pipeline Converter\n" +
                "and convert materials (Built-in to URP).",
                "OK");
        }

        [MenuItem(MenuRoot + "Check URP Enabled")]
        private static void CheckUrpEnabled()
        {
            var rp = GraphicsSettings.currentRenderPipeline;
            if (rp == null)
            {
                EditorUtility.DisplayDialog(
                    "URP Setup",
                    "URP is not enabled yet (GraphicsSettings has no Render Pipeline Asset).\n\n" +
                    "If you created this project from the Core 3D template, convert it:\n" +
                    "OSM Sendai > Setup > Open Render Pipeline Converter\n\n" +
                    "Or create a new project using the Universal 3D (URP) template.",
                    "OK");
                return;
            }

            EditorUtility.DisplayDialog("URP Setup", $"Current Render Pipeline Asset:\n{rp.name}", "OK");
        }

        private static UniversalRenderPipelineAsset CreateUrpAsset(string assetPath)
        {
            // Unity 2022.3 + URP 14: CreateRendererAsset is not public. Use Create() and embed the renderer data as a sub-asset.
            var asset = UniversalRenderPipelineAsset.Create();
            AssetDatabase.CreateAsset(asset, assetPath);

            // Ensure the default renderer data is saved with the asset, otherwise it may be lost on reimport.
            try
            {
                var so = new SerializedObject(asset);
                var list = so.FindProperty("m_RendererDataList");
                if (list != null && list.isArray && list.arraySize > 0)
                {
                    var elem = list.GetArrayElementAtIndex(0);
                    var rendererData = elem.objectReferenceValue;
                    if (rendererData != null && string.IsNullOrEmpty(AssetDatabase.GetAssetPath(rendererData)))
                    {
                        AssetDatabase.AddObjectToAsset(rendererData, asset);
                    }
                }
            }
            catch
            {
                // Best-effort only. If embedding fails, the user can create URP assets via Unity's UI.
            }

            AssetDatabase.SaveAssets();
            return asset;
        }

        private static void AssignPipelineToQualityLevels(RenderPipelineAsset asset)
        {
            // Newer Unity versions have SetRenderPipelineAssetAt; Unity 2022.3 may not.
            var method = typeof(QualitySettings).GetMethod(
                "SetRenderPipelineAssetAt",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(int), typeof(RenderPipelineAsset) },
                null);

            if (method != null)
            {
                for (var i = 0; i < QualitySettings.names.Length; i++)
                {
                    method.Invoke(null, new object[] { i, asset });
                }
                return;
            }

            // Fallback: set the current quality level pipeline if available.
            var prop = typeof(QualitySettings).GetProperty("renderPipeline", BindingFlags.Public | BindingFlags.Static);
            if (prop != null && prop.CanWrite && prop.PropertyType == typeof(RenderPipelineAsset))
            {
                prop.SetValue(null, asset);
            }
        }

        private static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;

            var parent = Path.GetDirectoryName(folder)?.Replace('\\', '/');
            var name = Path.GetFileName(folder);

            if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent))
            {
                EnsureFolder(parent);
            }

            AssetDatabase.CreateFolder(parent ?? "Assets", name);
        }
    }
}
