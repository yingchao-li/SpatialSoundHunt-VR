using System.Collections.Generic;
using System.IO;
using TMPro;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Simulation;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Teleportation;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace SpatialSoundHunt.EditorTools
{
    /// <summary>
    /// One-click generator for the Spatial Sound Hunt demo.
    /// Menu:  Tools > Spatial Sound Hunt > Build Complete Demo
    ///
    /// It never touches the VR Template scenes: the template's BasicScene (XR Origin, XR Interaction Manager,
    /// EventSystem with XRUIInputModule) is COPIED to Assets/Scenes/SpatialSoundHunt.unity and extended.
    /// Batch mode:  Unity -batchmode -projectPath . -executeMethod SpatialSoundHunt.EditorTools.SpatialSoundHuntBuilder.BuildBatch -quit
    /// </summary>
    public static class SpatialSoundHuntBuilder
    {
        const string MaterialsFolder = "Assets/Materials/SpatialSoundHunt";
        const string TexturesFolder = "Assets/Textures/SpatialSoundHunt";
        const string ScenePath = "Assets/Scenes/SpatialSoundHunt.unity";
        const string TemplateScenePath = "Assets/Scenes/BasicScene.unity";
        const string PromptKey = "SpatialSoundHunt.BuildPromptShown";

        static readonly Vector3 StartPosition = new Vector3(0f, 0f, -7.2f);

        // Room dimensions (metres). X = east, Z = north, floor at y = 0.
        const float RoomW = 20f;
        const float RoomD = 18f;
        const float RoomH = 4f;
        const float WallT = 0.2f;

        sealed class Mats
        {
            public Material Floor, Wall, FeatureWall, Partition, Ceiling, Trim, LightPanel, Metal, Wood, Plant, Crate;
            public Material Pad, Pedestal, Orb, Ring, Beam, Arrow, Rack, Led, Holo;
        }

        struct TargetSpec
        {
            public string id;
            public int index;
            public Vector3 position;
            public float pedestalHeight;
            public float frequency;
            public float noise;
        }

        // =====================================================================================
        //  Menu
        // =====================================================================================
        [MenuItem("Tools/Spatial Sound Hunt/Build Complete Demo", false, 10)]
        public static void BuildCompleteDemo()
        {
            Build(true);
        }

        [MenuItem("Tools/Spatial Sound Hunt/Open Demo Scene", false, 20)]
        public static void OpenDemoScene()
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) == null)
            {
                EditorUtility.DisplayDialog("Spatial Sound Hunt", "The demo scene does not exist yet.\nRun Tools > Spatial Sound Hunt > Build Complete Demo first.", "OK");
                return;
            }

            if (EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        }

        [MenuItem("Tools/Spatial Sound Hunt/Validate Scene Setup", false, 30)]
        public static void ValidateSceneMenu()
        {
            string report;
            bool ok = Validate(out report);
            Debug.Log("[SpatialSoundHunt] Validation " + (ok ? "PASSED" : "FOUND PROBLEMS") + "\n" + report);
            EditorUtility.DisplayDialog("Spatial Sound Hunt - validation", (ok ? "All checks passed.\n\n" : "Problems found:\n\n") + report, "OK");
        }

        [MenuItem("Tools/Spatial Sound Hunt/Open Session Data Folder", false, 40)]
        public static void OpenDataFolder()
        {
            Directory.CreateDirectory(Application.persistentDataPath);
            EditorUtility.RevealInFinder(Application.persistentDataPath);
        }

        /// <summary>Entry point for batch mode (no dialogs).</summary>
        public static void BuildBatch()
        {
            Build(false);
        }

        /// <summary>
        /// Step 2 of the two-step flow: after the first successful script compilation, offer to build the scene once per editor session.
        /// </summary>
        [InitializeOnLoadMethod]
        static void OfferBuildAfterCompile()
        {
            if (Application.isBatchMode) return;
            if (SessionState.GetBool(PromptKey, false)) return;
            SessionState.SetBool(PromptKey, true);

            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) != null) return;

            EditorApplication.delayCall += () =>
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling) return;
                if (EditorUtility.DisplayDialog("Spatial Sound Hunt",
                        "The scripts compiled successfully.\n\nBuild the demo scene now?\n(You can also do it later: Tools > Spatial Sound Hunt > Build Complete Demo)",
                        "Build now", "Later"))
                    Build(true);
            };
        }

        // =====================================================================================
        //  Main build
        // =====================================================================================
        static void Build(bool interactive)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogWarning("[SpatialSoundHunt] Leave Play Mode before building the demo.");
                if (interactive) EditorUtility.DisplayDialog("Spatial Sound Hunt", "Leave Play Mode first.", "OK");
                return;
            }

            if (EditorApplication.isCompiling)
            {
                Debug.LogWarning("[SpatialSoundHunt] Scripts are still compiling. Wait for the spinner to stop and try again.");
                if (interactive) EditorUtility.DisplayDialog("Spatial Sound Hunt", "Scripts are still compiling. Try again in a moment.", "OK");
                return;
            }

            if (interactive)
            {
                if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

                if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) != null &&
                    !EditorUtility.DisplayDialog("Spatial Sound Hunt",
                        "The scene already exists:\n" + ScenePath + "\n\nRebuild it from scratch? (The VR Template scenes are not touched.)",
                        "Rebuild", "Cancel"))
                    return;
            }

            try
            {
                Progress("Creating folders", 0.05f);
                EnsureFolder("Assets/Scenes");
                EnsureFolder(MaterialsFolder);
                EnsureFolder(TexturesFolder);

                Progress("Creating procedural materials", 0.15f);
                Mats mats = CreateMaterials();

                Progress("Creating scene from the VR Template", 0.30f);
                Scene scene = CreateScene();
                RemoveTemplateGround();

                XROrigin origin = Object.FindAnyObjectByType<XROrigin>();
                if (origin == null)
                    throw new System.InvalidOperationException("No XR Origin found in the scene. Is the VR Template / XRI Starter Assets sample imported?");

                Camera headCamera = origin.Camera != null ? origin.Camera : origin.GetComponentInChildren<Camera>();
                if (headCamera == null)
                    throw new System.InvalidOperationException("The XR Origin has no camera.");

                origin.transform.SetPositionAndRotation(StartPosition, Quaternion.identity);

                Progress("Lighting", 0.40f);
                ConfigureLighting();

                Progress("Building the laboratory", 0.50f);
                Transform environment = new GameObject("Lab_Environment").transform;
                BuildEnvironment(environment, mats);

                Progress("Creating the sound targets", 0.65f);
                Transform targetsRoot = new GameObject("Targets").transform;
                List<SoundTarget> targets = BuildTargets(targetsRoot, mats);

                Progress("Creating experiment logic", 0.75f);
                ExperimentManager manager;
                SessionRecorder recorder;
                BuildExperiment(targets, mats, out manager, out recorder);

                Progress("Creating world-space UI", 0.85f);
                BuildUi(mats, manager, recorder, headCamera.transform);

                Progress("Configuring the XR Interaction Simulator", 0.92f);
                EnsureSimulator();
                EnsureAudioListener(headCamera);

                Progress("Saving", 0.97f);
                EditorSceneManager.MarkSceneDirty(scene);
                if (!EditorSceneManager.SaveScene(scene, ScenePath))
                    throw new System.IO.IOException("Could not save " + ScenePath);

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                string report;
                bool ok = Validate(out report);
                Debug.Log("[SpatialSoundHunt] Demo built: " + ScenePath + "\n" + report);

                if (interactive)
                {
                    EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath));
                    EditorUtility.DisplayDialog("Spatial Sound Hunt",
                        (ok ? "Demo scene built and saved.\n\n" : "Demo scene built, but some checks failed:\n\n" + report + "\n\n") +
                        "Press Play. In the Game view:\n" +
                        "  WASD = move, hold , . (or U / O) = turn left / right,\n" +
                        "  (or hold Right Mouse = look),\n" +
                        "  Left Click = select / press,\n" +
                        "  Enter = start, M = mode, N = reset, F = gaze-select.",
                        "OK");
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError("[SpatialSoundHunt] Build failed: " + e);
                if (interactive) EditorUtility.DisplayDialog("Spatial Sound Hunt - build failed", e.Message + "\n\nSee the Console for details.", "OK");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        static void Progress(string info, float value)
        {
            EditorUtility.DisplayProgressBar("Spatial Sound Hunt", info, value);
        }

        // =====================================================================================
        //  Scene / template handling
        // =====================================================================================
        static Scene CreateScene()
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) != null)
                AssetDatabase.DeleteAsset(ScenePath);

            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(TemplateScenePath) != null && AssetDatabase.CopyAsset(TemplateScenePath, ScenePath))
                return EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            Debug.LogWarning("[SpatialSoundHunt] " + TemplateScenePath + " not found - building a minimal XR rig from the XRI Starter Assets instead.");
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            BuildFallbackXrRig();
            EditorSceneManager.SaveScene(scene, ScenePath);
            return scene;
        }

        static void BuildFallbackXrRig()
        {
            string prefabPath = FindAssetPath("XR Origin (XR Rig) t:Prefab", "/XR Origin (XR Rig).prefab");
            if (prefabPath == null)
                throw new System.InvalidOperationException("Cannot find the 'XR Origin (XR Rig)' prefab. Import Package Manager > XR Interaction Toolkit > Samples > Starter Assets.");

            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            PrefabUtility.InstantiatePrefab(prefab, SceneManager.GetActiveScene());

            GameObject manager = new GameObject("XR Interaction Manager");
            manager.AddComponent<XRInteractionManager>();

            GameObject eventSystem = new GameObject("EventSystem");
            eventSystem.AddComponent<UnityEngine.EventSystems.EventSystem>();
            eventSystem.AddComponent<XRUIInputModule>();

            GameObject sun = new GameObject("Directional Light");
            Light light = sun.AddComponent<Light>();
            light.type = LightType.Directional;
        }

        /// <summary>The template's grid floor is a big TeleportationArea; we replace it with our own floor.</summary>
        static void RemoveTemplateGround()
        {
            foreach (TeleportationArea area in Object.FindObjectsByType<TeleportationArea>())
            {
                if (area.GetComponentInParent<XROrigin>() != null) continue;
                Object.DestroyImmediate(area.gameObject);
            }
        }

        static void ConfigureLighting()
        {
            Light sun = null;
            foreach (Light light in Object.FindObjectsByType<Light>())
            {
                if (light.type == LightType.Directional)
                {
                    sun = light;
                    break;
                }
            }

            if (sun == null)
            {
                sun = new GameObject("Directional Light").AddComponent<Light>();
                sun.type = LightType.Directional;
            }

            sun.name = "Sun (Key Light)";
            sun.color = new Color(1f, 0.96f, 0.90f);
            sun.intensity = 0.7f;
            sun.shadows = LightShadows.Soft;
            sun.transform.rotation = Quaternion.Euler(52f, -28f, 0f);

            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.42f, 0.47f, 0.55f);
            RenderSettings.fog = false;
        }

        // =====================================================================================
        //  Materials / textures
        // =====================================================================================
        static Mats CreateMaterials()
        {
            Texture2D floorTexture = CreateFloorTexture();
            Mats m = new Mats();

            m.Floor = Mat("Lab_Floor", Color.white, 0f, 0.55f, default(Color), floorTexture, new Vector2(RoomW, RoomD));
            m.Wall = Mat("Lab_Wall", new Color(0.88f, 0.90f, 0.93f), 0f, 0.25f);
            m.FeatureWall = Mat("Lab_FeatureWall", new Color(0.10f, 0.16f, 0.23f), 0.1f, 0.45f);
            m.Partition = Mat("Lab_Partition", new Color(0.18f, 0.27f, 0.36f), 0.1f, 0.40f);
            m.Ceiling = Mat("Lab_Ceiling", new Color(0.93f, 0.94f, 0.96f), 0f, 0.2f);
            m.Trim = Mat("Lab_TrimTeal", new Color(0.05f, 0.30f, 0.33f), 0f, 0.5f, new Color(0.08f, 0.85f, 0.90f) * 1.4f);
            m.LightPanel = Mat("Lab_LightPanel", new Color(0.95f, 0.97f, 1f), 0f, 0.3f, new Color(1f, 0.98f, 0.94f) * 2.2f);
            m.Metal = Mat("Lab_DarkMetal", new Color(0.12f, 0.13f, 0.15f), 0.8f, 0.60f);
            m.Wood = Mat("Lab_Wood", new Color(0.55f, 0.38f, 0.24f), 0f, 0.35f);
            m.Plant = Mat("Lab_Plant", new Color(0.18f, 0.50f, 0.25f), 0f, 0.3f);
            m.Crate = Mat("Lab_Crate", new Color(0.30f, 0.40f, 0.55f), 0.1f, 0.35f);
            m.Rack = Mat("Lab_Rack", new Color(0.08f, 0.09f, 0.11f), 0.7f, 0.55f);
            m.Led = Mat("Lab_Led", new Color(0.05f, 0.30f, 0.33f), 0f, 0.5f, new Color(0.1f, 1f, 0.8f) * 2f);
            m.Holo = Mat("Lab_Holo", new Color(0.10f, 0.60f, 0.70f), 0f, 0.5f, new Color(0.1f, 0.9f, 1f) * 1.8f);
            m.Pad = Mat("Lab_StartPad", new Color(0.04f, 0.25f, 0.28f), 0f, 0.6f, new Color(0.08f, 0.85f, 0.90f) * 0.9f);

            // Target materials: identical for all three targets, colours are driven at runtime via MaterialPropertyBlock.
            // They need emission ENABLED on the asset so the emission keyword is compiled in.
            m.Orb = Mat("Target_Orb", new Color(0.45f, 0.50f, 0.58f), 0.1f, 0.75f, new Color(0.15f, 0.17f, 0.20f));
            m.Ring = Mat("Target_Ring", new Color(0.25f, 0.30f, 0.36f), 0f, 0.6f, new Color(0.06f, 0.07f, 0.09f));
            m.Beam = Mat("Target_Beam", new Color(0.10f, 0.90f, 1.00f), 0f, 0.5f, new Color(0.10f, 0.90f, 1.00f) * 1.5f);
            m.Pedestal = Mat("Target_Pedestal", new Color(0.20f, 0.22f, 0.26f), 0.6f, 0.55f);
            m.Arrow = Mat("Guidance_Arrow", new Color(0.10f, 0.90f, 1.00f), 0f, 0.5f, new Color(0.10f, 0.90f, 1.00f) * 2f);

            return m;
        }

        static Material Mat(string name, Color color, float metallic, float smoothness, Color emission = default(Color), Texture2D baseMap = null, Vector2? tiling = null)
        {
            string path = MaterialsFolder + "/" + name + ".mat";
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(LitShader());
                AssetDatabase.CreateAsset(material, path);
            }

            material.shader = LitShader();
            material.SetColor("_BaseColor", color);
            material.SetFloat("_Metallic", metallic);
            material.SetFloat("_Smoothness", smoothness);

            if (baseMap != null)
            {
                material.SetTexture("_BaseMap", baseMap);
                Vector2 scale = tiling.HasValue ? tiling.Value : Vector2.one;
                material.SetTextureScale("_BaseMap", scale);
            }

            if (emission.maxColorComponent > 0.001f)
            {
                material.EnableKeyword("_EMISSION");
                material.SetColor("_EmissionColor", emission);
                material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            }
            else
            {
                material.DisableKeyword("_EMISSION");
                material.SetColor("_EmissionColor", Color.black);
            }

            EditorUtility.SetDirty(material);
            return material;
        }

        static Shader LitShader()
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                Debug.LogWarning("[SpatialSoundHunt] URP Lit shader not found - falling back to Standard (will look wrong in URP).");
                shader = Shader.Find("Standard");
            }

            return shader;
        }

        static Texture2D CreateFloorTexture()
        {
            const string assetPath = TexturesFolder + "/LabFloor.png";
            const int size = 512;

            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            Color32[] pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int noise = ((x * 73856093) ^ (y * 19349663)) & 7;
                    byte v = (byte)(226 + noise);
                    byte r = v;
                    byte g = (byte)(v + 3);
                    byte b = (byte)(v + 6);

                    bool edge = x < 3 || y < 3 || x >= size - 3 || y >= size - 3;
                    bool half = (x % 256) < 1 || (y % 256) < 1;
                    if (edge)
                    {
                        r = 150; g = 160; b = 172;
                    }
                    else if (half)
                    {
                        r = 205; g = 212; b = 220;
                    }

                    pixels[y * size + x] = new Color32(r, g, b, 255);
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply();
            File.WriteAllBytes(AbsolutePath(assetPath), texture.EncodeToPNG());
            Object.DestroyImmediate(texture);

            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
            TextureImporter importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer != null)
            {
                importer.wrapMode = TextureWrapMode.Repeat;
                importer.filterMode = FilterMode.Bilinear;
                importer.anisoLevel = 8;
                importer.mipmapEnabled = true;
                importer.SaveAndReimport();
            }

            return AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
        }

        // =====================================================================================
        //  Environment
        // =====================================================================================
        static void BuildEnvironment(Transform env, Mats m)
        {
            // ---- floor (also the teleport surface of the VR template locomotion) ----
            GameObject floor = Prim(PrimitiveType.Plane, "Floor", env, Vector3.zero, new Vector3(RoomW / 10f, 1f, RoomD / 10f), m.Floor);
            if (InteractionLayerMask.NameToLayer("Teleport") >= 0)
            {
                TeleportationArea area = floor.AddComponent<TeleportationArea>();
                area.interactionLayers = InteractionLayerMask.GetMask("Teleport"); // only the teleport ray reacts, not target selection
            }

            // ---- shell ----
            Prim(PrimitiveType.Cube, "Wall_North", env, new Vector3(0f, RoomH / 2f, RoomD / 2f + WallT / 2f), new Vector3(RoomW + 2f * WallT, RoomH, WallT), m.Wall);
            Prim(PrimitiveType.Cube, "Wall_South", env, new Vector3(0f, RoomH / 2f, -RoomD / 2f - WallT / 2f), new Vector3(RoomW + 2f * WallT, RoomH, WallT), m.Wall);
            Prim(PrimitiveType.Cube, "Wall_East", env, new Vector3(RoomW / 2f + WallT / 2f, RoomH / 2f, 0f), new Vector3(WallT, RoomH, RoomD), m.Wall);
            Prim(PrimitiveType.Cube, "Wall_West", env, new Vector3(-RoomW / 2f - WallT / 2f, RoomH / 2f, 0f), new Vector3(WallT, RoomH, RoomD), m.Wall);
            Prim(PrimitiveType.Cube, "Ceiling", env, new Vector3(0f, RoomH + WallT / 2f, 0f), new Vector3(RoomW + 2f * WallT, WallT, RoomD + 2f * WallT), m.Ceiling, true, false);
            Prim(PrimitiveType.Cube, "FeatureWall", env, new Vector3(0f, 2f, RoomD / 2f - 0.03f), new Vector3(12f, 3.2f, 0.06f), m.FeatureWall, false);

            // teal baseboard trims
            Prim(PrimitiveType.Cube, "Trim_North", env, new Vector3(0f, 0.08f, RoomD / 2f - 0.02f), new Vector3(RoomW, 0.16f, 0.04f), m.Trim, false, false);
            Prim(PrimitiveType.Cube, "Trim_South", env, new Vector3(0f, 0.08f, -RoomD / 2f + 0.02f), new Vector3(RoomW, 0.16f, 0.04f), m.Trim, false, false);
            Prim(PrimitiveType.Cube, "Trim_East", env, new Vector3(RoomW / 2f - 0.02f, 0.08f, 0f), new Vector3(0.04f, 0.16f, RoomD), m.Trim, false, false);
            Prim(PrimitiveType.Cube, "Trim_West", env, new Vector3(-RoomW / 2f + 0.02f, 0.08f, 0f), new Vector3(0.04f, 0.16f, RoomD), m.Trim, false, false);

            // ---- zones ----
            Prim(PrimitiveType.Cylinder, "StartPad", env, new Vector3(StartPosition.x, 0.012f, StartPosition.z), new Vector3(2.6f, 0.012f, 2.6f), m.Pad, false, false);
            Prim(PrimitiveType.Cylinder, "StartPadRing", env, new Vector3(StartPosition.x, 0.02f, StartPosition.z), new Vector3(2.9f, 0.008f, 2.9f), m.Trim, false, false);

            // partitions that hide the three sound sources from the start position (6-DoF: you have to walk around)
            Prim(PrimitiveType.Cube, "Partition_West", env, new Vector3(-4.2f, 1.1f, 1.6f), new Vector3(0.2f, 2.2f, 6.4f), m.Partition);
            Prim(PrimitiveType.Cube, "Partition_East", env, new Vector3(4.2f, 1.3f, 2.0f), new Vector3(0.2f, 2.6f, 6.0f), m.Partition);
            Prim(PrimitiveType.Cube, "Partition_Center", env, new Vector3(0f, 1.1f, 4.6f), new Vector3(5.0f, 2.2f, 0.2f), m.Partition);

            // raised dais for the elevated target in the east bay (target C stands on top of it)
            Prim(PrimitiveType.Cube, "Dais_East", env, new Vector3(7.6f, 0.5f, 2.5f), new Vector3(3.6f, 1.0f, 3.6f), m.Partition);
            Prim(PrimitiveType.Cube, "Dais_East_Trim", env, new Vector3(7.6f, 1.005f, 2.5f), new Vector3(3.64f, 0.01f, 3.64f), m.Trim, false, false);

            // ---- columns ----
            float[] columnX = { -6f, 6f };
            float[] columnZ = { -3.5f, 5.5f };
            foreach (float x in columnX)
            {
                foreach (float z in columnZ)
                {
                    Prim(PrimitiveType.Cylinder, "Column", env, new Vector3(x, 2f, z), new Vector3(0.5f, 2f, 0.5f), m.Metal);
                    Prim(PrimitiveType.Cylinder, "Column_CollarLow", env, new Vector3(x, 0.12f, z), new Vector3(0.62f, 0.03f, 0.62f), m.Trim, false, false);
                    Prim(PrimitiveType.Cylinder, "Column_CollarHigh", env, new Vector3(x, 3.6f, z), new Vector3(0.62f, 0.03f, 0.62f), m.Trim, false, false);
                }
            }

            // ---- workbenches ----
            BuildWorkbench(env, m, new Vector3(-2.5f, 0f, -2.0f));
            BuildWorkbench(env, m, new Vector3(2.5f, 0f, -2.0f));

            // ---- central holo console (decor, rotating ring) ----
            Prim(PrimitiveType.Cylinder, "HoloConsole_Base", env, new Vector3(0f, 0.25f, -0.2f), new Vector3(1.4f, 0.25f, 1.4f), m.Metal);
            GameObject ring = Prim(PrimitiveType.Cylinder, "HoloConsole_Ring", env, new Vector3(0f, 1.15f, -0.2f), new Vector3(1.1f, 0.01f, 1.1f), m.Holo, false, false);
            ring.AddComponent<SimpleSpin>();
            Prim(PrimitiveType.Sphere, "HoloConsole_Core", env, new Vector3(0f, 1.15f, -0.2f), Vector3.one * 0.25f, m.Holo, false, false);

            // ---- crates and bench near the start zone ----
            Prim(PrimitiveType.Cube, "Crate_1", env, new Vector3(8.4f, 0.4f, -6.4f), new Vector3(0.8f, 0.8f, 0.8f), m.Crate);
            Prim(PrimitiveType.Cube, "Crate_2", env, new Vector3(8.5f, 0.3f, -5.5f), new Vector3(0.6f, 0.6f, 0.6f), m.Crate);
            Prim(PrimitiveType.Cube, "Crate_3", env, new Vector3(8.4f, 1.0f, -6.4f), new Vector3(0.5f, 0.4f, 0.5f), m.Crate, true, true, new Vector3(0f, 25f, 0f));
            Prim(PrimitiveType.Cube, "Bench_Top", env, new Vector3(-6.8f, 0.45f, -7.8f), new Vector3(2.4f, 0.08f, 0.5f), m.Wood);
            Prim(PrimitiveType.Cube, "Bench_Base", env, new Vector3(-6.8f, 0.2f, -7.8f), new Vector3(2.2f, 0.4f, 0.4f), m.Metal);

            // ---- server racks along the north wall ----
            float[] rackX = { -8.2f, -7.2f, -6.2f, 6.2f, 7.2f, 8.2f };
            foreach (float x in rackX)
            {
                Prim(PrimitiveType.Cube, "Rack", env, new Vector3(x, 1.1f, 8.45f), new Vector3(0.85f, 2.2f, 0.6f), m.Rack);
                Prim(PrimitiveType.Cube, "Rack_Led", env, new Vector3(x, 1.8f, 8.14f), new Vector3(0.6f, 0.04f, 0.02f), m.Led, false, false);
            }

            // ---- plants in the corners ----
            Vector3[] plantPositions =
            {
                new Vector3(-9.2f, 0f, -8.2f), new Vector3(9.2f, 0f, -8.2f),
                new Vector3(-9.2f, 0f, 8.2f), new Vector3(9.2f, 0f, 8.2f)
            };
            foreach (Vector3 p in plantPositions)
            {
                Prim(PrimitiveType.Cylinder, "Plant_Pot", env, p + new Vector3(0f, 0.25f, 0f), new Vector3(0.5f, 0.25f, 0.5f), m.Metal);
                Prim(PrimitiveType.Sphere, "Plant_Leaves", env, p + new Vector3(0f, 0.95f, 0f), new Vector3(0.9f, 0.9f, 0.9f), m.Plant);
            }

            // ---- ceiling light panels + real lights ----
            float[] panelX = { -6f, 0f, 6f };
            float[] panelZ = { -6f, 0f, 6f };
            foreach (float x in panelX)
                foreach (float z in panelZ)
                    Prim(PrimitiveType.Cube, "CeilingPanel", env, new Vector3(x, RoomH - 0.03f, z), new Vector3(2.4f, 0.05f, 1.2f), m.LightPanel, false, false);

            Vector3[] lightPositions =
            {
                new Vector3(-6f, 3.5f, -5f), new Vector3(6f, 3.5f, -5f), new Vector3(0f, 3.5f, 0f),
                new Vector3(-6f, 3.5f, 5f), new Vector3(6f, 3.5f, 5f), new Vector3(0f, 3.5f, 7f)
            };
            for (int i = 0; i < lightPositions.Length; i++)
            {
                GameObject go = new GameObject("RoomLight_" + (i + 1));
                go.transform.SetParent(env, false);
                go.transform.localPosition = lightPositions[i];
                Light light = go.AddComponent<Light>();
                light.type = LightType.Point;
                light.range = 11f;
                light.intensity = 1.6f;
                light.color = new Color(1f, 0.97f, 0.92f);
                light.shadows = LightShadows.None;
            }

            // ---- floor labels (flat world-space text) ----
            Color label = new Color(0.15f, 0.60f, 0.70f, 0.75f);
            MakeWorldText(env, "Label_StartZone", "START ZONE", new Vector3(0f, 0.03f, -8.35f), new Vector3(90f, 0f, 0f), 90f, new Vector2(800f, 200f), 0.005f, label);
            MakeWorldText(env, "Label_WestBay", "WEST BAY", new Vector3(-7.4f, 0.03f, -0.8f), new Vector3(90f, 0f, 0f), 90f, new Vector2(800f, 200f), 0.005f, label);
            MakeWorldText(env, "Label_CentralHall", "CENTRAL HALL", new Vector3(0f, 0.03f, 2.2f), new Vector3(90f, 0f, 0f), 90f, new Vector2(900f, 200f), 0.005f, label);
            MakeWorldText(env, "Label_EastBay", "EAST BAY", new Vector3(7.6f, 0.03f, -1.6f), new Vector3(90f, 0f, 0f), 90f, new Vector2(800f, 200f), 0.005f, label);
        }

        static void BuildWorkbench(Transform env, Mats m, Vector3 position)
        {
            Prim(PrimitiveType.Cube, "Workbench_Body", env, position + new Vector3(0f, 0.425f, 0f), new Vector3(1.8f, 0.85f, 0.7f), m.Metal);
            Prim(PrimitiveType.Cube, "Workbench_Top", env, position + new Vector3(0f, 0.88f, 0f), new Vector3(1.9f, 0.06f, 0.8f), m.Wood);
            Prim(PrimitiveType.Cube, "Workbench_Monitor", env, position + new Vector3(0f, 1.2f, 0.2f), new Vector3(0.55f, 0.32f, 0.03f), m.Holo, false, false);
        }

        // =====================================================================================
        //  Sound targets
        // =====================================================================================
        static List<SoundTarget> BuildTargets(Transform parent, Mats m)
        {
            TargetSpec[] specs =
            {
                new TargetSpec { id = "A", index = 0, position = new Vector3(-7.4f, 0f, 3.0f), pedestalHeight = 1.0f, frequency = 392.00f, noise = 0.35f }, // west bay, behind partition
                new TargetSpec { id = "B", index = 1, position = new Vector3(0f, 0f, 7.2f), pedestalHeight = 0.9f, frequency = 523.25f, noise = 0.30f },    // north, behind central partition
                new TargetSpec { id = "C", index = 2, position = new Vector3(7.6f, 1.0f, 2.5f), pedestalHeight = 0.9f, frequency = 659.25f, noise = 0.40f } // east bay, elevated on the dais
            };

            List<SoundTarget> result = new List<SoundTarget>();
            foreach (TargetSpec spec in specs)
                result.Add(BuildTarget(parent, m, spec));
            return result;
        }

        static SoundTarget BuildTarget(Transform parent, Mats m, TargetSpec s)
        {
            GameObject root = new GameObject("Target_" + s.id);
            root.transform.SetParent(parent, false);
            root.transform.position = s.position;

            float orbY = s.pedestalHeight + 0.30f;

            Prim(PrimitiveType.Cylinder, "Base", root.transform, new Vector3(0f, 0.04f, 0f), new Vector3(1.0f, 0.04f, 1.0f), m.Metal, false);
            Prim(PrimitiveType.Cylinder, "Pedestal", root.transform, new Vector3(0f, s.pedestalHeight * 0.5f, 0f), new Vector3(0.32f, s.pedestalHeight * 0.5f, 0.32f), m.Pedestal);
            GameObject ring = Prim(PrimitiveType.Cylinder, "Ring", root.transform, new Vector3(0f, s.pedestalHeight + 0.02f, 0f), new Vector3(0.9f, 0.01f, 0.9f), m.Ring, false, false);
            GameObject orb = Prim(PrimitiveType.Sphere, "Orb", root.transform, new Vector3(0f, orbY, 0f), Vector3.one * 0.34f, m.Orb);

            // generous selection volume (local radius 1.4 * 0.34 scale = ~0.48 m)
            SphereCollider selectCollider = orb.GetComponent<SphereCollider>();
            if (selectCollider != null) selectCollider.radius = 1.4f;

            XRSimpleInteractable interactable = orb.AddComponent<XRSimpleInteractable>();

            float beamBottom = orbY + 0.30f;
            float beamTop = RoomH - 0.1f - s.position.y;
            GameObject beam = Prim(PrimitiveType.Cylinder, "Beam", root.transform,
                new Vector3(0f, (beamBottom + beamTop) * 0.5f, 0f), new Vector3(0.07f, (beamTop - beamBottom) * 0.5f, 0.07f), m.Beam, false, false);
            beam.GetComponent<Renderer>().enabled = false; // only shown in the Audio + Visual condition

            // audio emitter sits at the height of the orb so elevation is audible
            GameObject emitter = new GameObject("AudioEmitter");
            emitter.transform.SetParent(root.transform, false);
            emitter.transform.localPosition = new Vector3(0f, orbY, 0f);
            AudioSource beacon = emitter.AddComponent<AudioSource>();
            AudioSource sfx = emitter.AddComponent<AudioSource>();
            ConfigureAudioSource(beacon, true);
            ConfigureAudioSource(sfx, false);

            SoundTarget target = root.AddComponent<SoundTarget>();
            target.targetId = s.id;
            target.targetIndex = s.index;
            target.baseFrequency = s.frequency;
            target.noiseMix = s.noise;
            target.orbTransform = orb.transform;
            target.orbRenderer = orb.GetComponent<Renderer>();
            target.ringRenderer = ring.GetComponent<Renderer>();
            target.beamRenderer = beam.GetComponent<Renderer>();
            target.interactable = interactable;
            target.beaconSource = beacon;
            target.sfxSource = sfx;
            return target;
        }

        static void ConfigureAudioSource(AudioSource source, bool loop)
        {
            source.playOnAwake = false;
            source.loop = loop;
            source.spatialBlend = 1f;
            source.rolloffMode = AudioRolloffMode.Logarithmic;
            source.minDistance = 1.2f;
            source.maxDistance = 35f;
            source.dopplerLevel = 0f;
            source.spread = 0f;
        }

        // =====================================================================================
        //  Experiment objects
        // =====================================================================================
        static void BuildExperiment(List<SoundTarget> targets, Mats m, out ExperimentManager manager, out SessionRecorder recorder)
        {
            GameObject go = new GameObject("Experiment");
            manager = go.AddComponent<ExperimentManager>();
            recorder = go.AddComponent<SessionRecorder>();
            VisualGuidance guidance = go.AddComponent<VisualGuidance>();
            go.AddComponent<LookAssist>();

            AudioSource uiAudio = go.AddComponent<AudioSource>();
            uiAudio.playOnAwake = false;
            uiAudio.spatialBlend = 0f;

            manager.targets = new List<SoundTarget>(targets);
            manager.recorder = recorder;
            manager.uiAudioSource = uiAudio;
            manager.condition = ExperimentCondition.AudioOnly;

            recorder.manager = manager;
            guidance.manager = manager;

            // Python 6-DoF analysis: runs after the CSV of a completed session is closed (Analytics/analyze_session.py)
            AnalysisBridge bridge = go.AddComponent<AnalysisBridge>();
            bridge.manager = manager;
            bridge.recorder = recorder;
            guidance.arrowMaterial = m.Arrow;
        }

        // =====================================================================================
        //  World-space UI
        // =====================================================================================
        static void BuildUi(Mats m, ExperimentManager manager, SessionRecorder recorder, Transform headCamera)
        {
            Transform uiRoot = new GameObject("TaskUI_Root").transform;

            // console desk + panel
            Prim(PrimitiveType.Cube, "ConsoleStand", uiRoot, new Vector3(0f, 0.5f, -4.7f), new Vector3(1.9f, 1.0f, 0.35f), m.Metal);
            Prim(PrimitiveType.Cube, "ConsoleStand_Trim", uiRoot, new Vector3(0f, 1.005f, -4.7f), new Vector3(1.94f, 0.01f, 0.39f), m.Trim, false, false);

            GameObject canvasObject = new GameObject("MissionConsole", typeof(RectTransform), typeof(Canvas), typeof(GraphicRaycaster));
            canvasObject.transform.SetParent(uiRoot, false);
            canvasObject.transform.position = new Vector3(0f, 1.62f, -4.9f);
            canvasObject.transform.localScale = Vector3.one * 0.0016f;

            Canvas canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            ((RectTransform)canvasObject.transform).sizeDelta = new Vector2(1100f, 680f);
            canvasObject.AddComponent<TrackedDeviceGraphicRaycaster>(); // lets XR controller rays click the buttons

            Transform c = canvasObject.transform;
            Color teal = new Color(0.10f, 0.85f, 0.90f, 1f);
            Color text = new Color(0.90f, 0.95f, 1.00f, 1f);
            Color dim = new Color(0.60f, 0.70f, 0.80f, 1f);

            MakeImage(c, "Background", new Color(0.04f, 0.07f, 0.11f, 0.94f), Vector2.zero, Vector2.zero, true);
            MakeImage(c, "AccentBar", teal, new Vector2(0f, 332f), new Vector2(1100f, 8f), false);

            MakeText(c, "Title", "SPATIAL SOUND HUNT", 56f, text, TextAlignmentOptions.Left, new Vector2(-170f, 285f), new Vector2(700f, 70f), FontStyles.Bold);
            MakeText(c, "Subtitle", "Interactive 6-DoF spatial-audio search task", 26f, dim, TextAlignmentOptions.Left, new Vector2(-170f, 232f), new Vector2(700f, 40f), FontStyles.Normal);
            TextMeshProUGUI condition = MakeText(c, "ConditionText", "CONDITION:  AUDIO ONLY", 30f, teal, TextAlignmentOptions.Right, new Vector2(290f, 262f), new Vector2(480f, 60f), FontStyles.Bold);

            TextMeshProUGUI state = MakeText(c, "StateText", "READY", 64f, Color.white, TextAlignmentOptions.Left, new Vector2(-165f, 160f), new Vector2(690f, 80f), FontStyles.Bold);
            TextMeshProUGUI instruction = MakeText(c, "InstructionText", "", 27f, text, TextAlignmentOptions.TopLeft, new Vector2(-165f, 20f), new Vector2(690f, 190f), FontStyles.Normal);

            TextMeshProUGUI progress = MakeText(c, "ProgressText", "Sources found: 0 / 3", 28f, text, TextAlignmentOptions.TopLeft, new Vector2(355f, 150f), new Vector2(330f, 110f), FontStyles.Normal);
            Image barBackground = MakeImage(c, "ProgressBarBackground", new Color(0.15f, 0.20f, 0.27f, 1f), new Vector2(355f, 70f), new Vector2(330f, 26f), false);
            // the fill is a child of the bar and stretches over it; TaskUI moves its right anchor to show progress
            RectTransform fill = MakeImage(barBackground.transform, "ProgressBarFill", teal, Vector2.zero, Vector2.zero, true).rectTransform;

            TextMeshProUGUI timer = MakeText(c, "TimerText", "Time  00:00.0", 44f, Color.white, TextAlignmentOptions.Center, new Vector2(355f, 5f), new Vector2(330f, 60f), FontStyles.Bold);
            TextMeshProUGUI errors = MakeText(c, "ErrorsText", "Wrong selections: 0", 26f, dim, TextAlignmentOptions.Center, new Vector2(355f, -45f), new Vector2(330f, 40f), FontStyles.Normal);

            TextMeshProUGUI feedback = MakeText(c, "FeedbackText", "", 30f, teal, TextAlignmentOptions.Center, new Vector2(0f, -125f), new Vector2(1000f, 100f), FontStyles.Bold);

            TextMeshProUGUI startLabel;
            TextMeshProUGUI modeLabel;
            TextMeshProUGUI resetLabel;
            Button startButton = MakeButton(c, "StartButton", "START", new Vector2(-330f, -225f), new Vector2(300f, 80f), new Color(0.05f, 0.45f, 0.50f), new Color(0.10f, 0.80f, 0.88f), out startLabel);
            Button modeButton = MakeButton(c, "ConditionButton", "SWITCH MODE", new Vector2(0f, -225f), new Vector2(300f, 80f), new Color(0.22f, 0.26f, 0.55f), new Color(0.38f, 0.45f, 0.95f), out modeLabel);
            Button resetButton = MakeButton(c, "ResetButton", "RESET", new Vector2(330f, -225f), new Vector2(300f, 80f), new Color(0.50f, 0.25f, 0.10f), new Color(0.90f, 0.50f, 0.20f), out resetLabel);

            MakeText(c, "ControlsHint",
                "Simulator:  W A S D move  |  hold  ,  /  .  (or U / O) = turn  |  mouse pointer + Left Click = select / press\n" +
                "Enter = start / restart   |   M = switch mode   |   N = reset   |   F = select what you look at",
                20f, dim, TextAlignmentOptions.Center, new Vector2(0f, -305f), new Vector2(1040f, 56f), FontStyles.Normal);

            // head-locked, tiny status line (child of the XR camera)
            GameObject hud = new GameObject("StatusHUD", typeof(RectTransform), typeof(Canvas));
            hud.transform.SetParent(headCamera, false);
            hud.transform.localPosition = new Vector3(0f, -0.30f, 1.0f);
            hud.transform.localRotation = Quaternion.identity;
            hud.transform.localScale = Vector3.one * 0.001f;
            hud.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            ((RectTransform)hud.transform).sizeDelta = new Vector2(1000f, 90f);
            TextMeshProUGUI hudText = MakeText(hud.transform, "HudText", "", 38f, new Color(0.80f, 0.95f, 1f, 0.9f), TextAlignmentOptions.Center, Vector2.zero, new Vector2(1000f, 90f), FontStyles.Bold);

            TaskUI ui = canvasObject.AddComponent<TaskUI>();
            ui.manager = manager;
            ui.recorder = recorder;
            ui.analysis = Object.FindAnyObjectByType<AnalysisBridge>();
            ui.worldCanvas = canvas;
            ui.stateText = state;
            ui.conditionText = condition;
            ui.progressText = progress;
            ui.timerText = timer;
            ui.errorsText = errors;
            ui.instructionText = instruction;
            ui.feedbackText = feedback;
            ui.hudText = hudText;
            ui.progressFill = fill;
            ui.startButton = startButton;
            ui.conditionButton = modeButton;
            ui.resetButton = resetButton;
            ui.startButtonLabel = startLabel;
            ui.conditionButtonLabel = modeLabel;
        }

        static Image MakeImage(Transform parent, string name, Color color, Vector2 position, Vector2 size, bool stretch)
        {
            GameObject go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            RectTransform rt = (RectTransform)go.transform;
            if (stretch)
            {
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.offsetMin = Vector2.zero;
                rt.offsetMax = Vector2.zero;
            }
            else
            {
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.anchoredPosition = position;
                rt.sizeDelta = size;
            }

            Image image = go.GetComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            return image;
        }

        static TextMeshProUGUI MakeText(Transform parent, string name, string content, float fontSize, Color color, TextAlignmentOptions alignment, Vector2 position, Vector2 size, FontStyles style)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            RectTransform rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = position;
            rt.sizeDelta = size;

            TextMeshProUGUI tmp = go.AddComponent<TextMeshProUGUI>();
            if (TMP_Settings.defaultFontAsset != null) tmp.font = TMP_Settings.defaultFontAsset;
            tmp.text = content;
            tmp.fontSize = fontSize;
            tmp.color = color;
            tmp.alignment = alignment;
            tmp.fontStyle = style;
            tmp.textWrappingMode = TextWrappingModes.Normal;
            tmp.raycastTarget = false;
            return tmp;
        }

        static Button MakeButton(Transform parent, string name, string label, Vector2 position, Vector2 size, Color normal, Color highlighted, out TextMeshProUGUI labelText)
        {
            GameObject go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            RectTransform rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = position;
            rt.sizeDelta = size;

            Image image = go.GetComponent<Image>();
            image.color = Color.white; // the button's colour tint is multiplied onto white

            Button button = go.GetComponent<Button>();
            button.targetGraphic = image;
            ColorBlock colors = button.colors;
            colors.normalColor = normal;
            colors.highlightedColor = highlighted;
            colors.selectedColor = normal;
            colors.pressedColor = Color.white;
            colors.disabledColor = new Color(0.3f, 0.3f, 0.3f, 0.6f);
            colors.colorMultiplier = 1f;
            colors.fadeDuration = 0.08f;
            button.colors = colors;

            Navigation navigation = new Navigation();
            navigation.mode = Navigation.Mode.None;
            button.navigation = navigation;

            labelText = MakeText(go.transform, "Label", label, 34f, Color.white, TextAlignmentOptions.Center, Vector2.zero, size, FontStyles.Bold);
            return button;
        }

        static void MakeWorldText(Transform parent, string name, string content, Vector3 position, Vector3 euler, float fontSizePx, Vector2 sizePx, float scale, Color color)
        {
            GameObject go = new GameObject(name, typeof(RectTransform), typeof(Canvas));
            go.transform.SetParent(parent, false);
            go.transform.localPosition = position;
            go.transform.localEulerAngles = euler;
            go.transform.localScale = Vector3.one * scale;
            go.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            ((RectTransform)go.transform).sizeDelta = sizePx;
            MakeText(go.transform, "Text", content, fontSizePx, color, TextAlignmentOptions.Center, Vector2.zero, sizePx, FontStyles.Bold);
        }

        // =====================================================================================
        //  XR helpers
        // =====================================================================================
        static void EnsureSimulator()
        {
            if (Object.FindAnyObjectByType<XRInteractionSimulator>() != null) return;

            string path = FindAssetPath("XR Interaction Simulator t:Prefab", "/XR Interaction Simulator.prefab");
            if (path == null)
            {
                Debug.LogWarning("[SpatialSoundHunt] The XR Interaction Simulator prefab was not found.\n" +
                                 "Import it: Window > Package Manager > XR Interaction Toolkit > Samples > XR Interaction Simulator,\n" +
                                 "then run the builder again (or enable Project Settings > XR Plug-in Management > XR Interaction Toolkit > Use XR Interaction Simulator in scene).");
                return;
            }

            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            PrefabUtility.InstantiatePrefab(prefab, SceneManager.GetActiveScene());
        }

        static void EnsureAudioListener(Camera headCamera)
        {
            AudioListener[] listeners = Object.FindObjectsByType<AudioListener>();
            if (listeners.Length == 0)
                headCamera.gameObject.AddComponent<AudioListener>();
            else if (listeners.Length > 1)
                Debug.LogWarning("[SpatialSoundHunt] More than one AudioListener in the scene - disable the extra ones.");
        }

        // =====================================================================================
        //  Validation
        // =====================================================================================
        static bool Validate(out string report)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            bool ok = true;

            ok &= Check(sb, "XR Origin (exactly one)", Object.FindObjectsByType<XROrigin>().Length == 1);
            ok &= Check(sb, "EventSystem (exactly one)", Object.FindObjectsByType<UnityEngine.EventSystems.EventSystem>().Length == 1);
            ok &= Check(sb, "XR Interaction Manager (exactly one)", Object.FindObjectsByType<XRInteractionManager>().Length == 1);
            ok &= Check(sb, "XR Interaction Simulator in scene", Object.FindObjectsByType<XRInteractionSimulator>().Length == 1);
            ok &= Check(sb, "AudioListener (exactly one)", Object.FindObjectsByType<AudioListener>().Length == 1);

            ExperimentManager manager = Object.FindAnyObjectByType<ExperimentManager>();
            ok &= Check(sb, "ExperimentManager with 3 targets", manager != null && manager.targets != null && manager.targets.Count == 3);
            ok &= Check(sb, "SessionRecorder wired", manager != null && manager.recorder != null && manager.recorder.manager == manager);
            AnalysisBridge analysisBridge = manager != null ? manager.GetComponent<AnalysisBridge>() : null;
            ok &= Check(sb, "AnalysisBridge wired (completed CSV -> Python report)", analysisBridge != null && analysisBridge.manager == manager && analysisBridge.recorder == manager.recorder);

            bool targetsOk = manager != null && manager.targets != null;
            if (targetsOk)
            {
                foreach (SoundTarget t in manager.targets)
                {
                    if (t == null || t.interactable == null || t.beaconSource == null || t.orbRenderer == null || t.interactable.GetComponent<Collider>() == null)
                        targetsOk = false;
                }
            }

            ok &= Check(sb, "Targets: interactable + collider + audio + renderer", targetsOk);
            ok &= Check(sb, "TaskUI wired", Object.FindAnyObjectByType<TaskUI>() != null && Object.FindAnyObjectByType<TaskUI>().manager != null);
            ok &= Check(sb, "Teleport floor (TeleportationArea)", Object.FindAnyObjectByType<TeleportationArea>() != null);

            report = sb.ToString();
            return ok;
        }

        static bool Check(System.Text.StringBuilder sb, string label, bool pass)
        {
            sb.AppendLine((pass ? "[ok]   " : "[FAIL] ") + label);
            return pass;
        }

        // =====================================================================================
        //  Generic helpers
        // =====================================================================================
        static GameObject Prim(PrimitiveType type, string name, Transform parent, Vector3 localPosition, Vector3 localScale, Material material,
            bool collider = true, bool castShadows = true, Vector3? localEuler = null)
        {
            GameObject go = GameObject.CreatePrimitive(type);
            go.name = name;
            if (parent != null) go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            go.transform.localEulerAngles = localEuler.HasValue ? localEuler.Value : Vector3.zero;
            go.transform.localScale = localScale;

            Renderer renderer = go.GetComponent<Renderer>();
            if (material != null) renderer.sharedMaterial = material;
            if (!castShadows) renderer.shadowCastingMode = ShadowCastingMode.Off;

            if (!collider)
            {
                Collider existing = go.GetComponent<Collider>();
                if (existing != null) Object.DestroyImmediate(existing);
            }

            return go;
        }

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            string name = Path.GetFileName(path);
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, name);
        }

        static string AbsolutePath(string assetPath)
        {
            return Path.Combine(Directory.GetParent(Application.dataPath).FullName, assetPath);
        }

        static string FindAssetPath(string filter, string endsWith)
        {
            foreach (string guid in AssetDatabase.FindAssets(filter))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.EndsWith(endsWith)) return path;
            }

            return null;
        }
    }
}
