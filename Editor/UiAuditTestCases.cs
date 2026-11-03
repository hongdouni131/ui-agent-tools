using System;
using System.Linq;
using Newtonsoft.Json;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace UIAgent.Tools.Editor
{
    /// <summary>Explicit development fixtures; never called by the read-only auditor.</summary>
    public static class UiAuditTestCases
    {
        public static readonly string[] Cases = { "Correct", "MissingEventSystem", "ButtonOutsideCanvas", "TextOverflow", "MissingFont", "TransparentRaycast" };

        public static string CreateSavedScenesJson(string outputFolder)
        {
            if (string.IsNullOrWhiteSpace(outputFolder) || !outputFolder.StartsWith("Assets/AgentDemos/UIAgent/TestCases/", StringComparison.Ordinal) ||
                outputFolder.Contains("..") || outputFolder.Contains("\\") || outputFolder.EndsWith("/"))
                throw new ArgumentException("Use a new folder below Assets/AgentDemos/UIAgent/TestCases/.");
            if (AssetDatabase.IsValidFolder(outputFolder))
                throw new InvalidOperationException("Folder already exists; fixtures will not overwrite it: " + outputFolder);
            for (int i = 0; i < SceneManager.sceneCount; i++)
                if (SceneManager.GetSceneAt(i).isDirty)
                    throw new InvalidOperationException("Save your own dirty scenes before creating fixtures.");
            // Check dependencies before creating any scene or folder.
            RequiredFont();
            if (ResolveInputModule() == null)
                throw new InvalidOperationException("No UI input module is available for the project's active input handling setting.");
            Scene original = SceneManager.GetActiveScene();
            EnsureFolder(outputFolder);
            var paths = new System.Collections.Generic.List<string>();
            try
            {
                foreach (string name in Cases)
                {
                    Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                    try
                    {
                        Build(scene, name);
                        string path = outputFolder + "/" + name + ".unity";
                        if (!EditorSceneManager.SaveScene(scene, path)) throw new InvalidOperationException("Could not save " + path);
                        paths.Add(path);
                    }
                    finally { EditorSceneManager.CloseScene(scene, true); }
                }
            }
            finally { if (original.IsValid() && original.isLoaded) SceneManager.SetActiveScene(original); }
            return JsonConvert.SerializeObject(new { scenePaths = paths, activeSceneRestored = SceneManager.GetActiveScene().path == original.path });
        }

        public static void Build(Scene scene, string caseName)
        {
            if (!scene.IsValid() || !scene.isLoaded) throw new ArgumentException("Expected a loaded owned test scene.");
            if (scene.rootCount != 0) throw new InvalidOperationException("Fixture builder only accepts an empty owned scene.");
            if (!Cases.Contains(caseName)) throw new ArgumentException("Unknown audit fixture: " + caseName);
            TMP_FontAsset font = RequiredFont();
            Type inputType = ResolveInputModule();
            if (inputType == null) throw new InvalidOperationException("No UI input module is available for the project's active input handling setting.");
            var root = new GameObject("AuditCase_" + caseName);
            SceneManager.MoveGameObjectToScene(root, scene);
            var canvasObject = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasObject.transform.SetParent(root.transform, false);
            canvasObject.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            CanvasScaler scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = .5f;
            TextMeshProUGUI title = MakeText("Title", canvasObject.transform, font, "UI Agent Audit", new Vector2(600, 100), new Vector2(0, 250), 48);
            for (int i = 0; i < 3; i++)
            {
                var go = new GameObject("Button" + i, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
                go.transform.SetParent(canvasObject.transform, false);
                SetRect(go.transform as RectTransform, new Vector2(320, 72), new Vector2(0, 80 - i * 110));
                Image image = go.GetComponent<Image>();
                image.color = new Color(.12f, .25f, .42f, 1);
                go.GetComponent<Button>().targetGraphic = image;
                MakeText("Label", go.transform, font, new[] { "Start", "Settings", "Quit" }[i], new Vector2(300, 64), Vector2.zero, 32);
            }
            if (caseName != "MissingEventSystem")
            {
                var system = new GameObject("EventSystem", typeof(EventSystem), inputType);
                system.transform.SetParent(root.transform, false);
            }
            if (caseName == "ButtonOutsideCanvas")
                (canvasObject.transform.Find("Button0") as RectTransform).anchoredPosition = new Vector2(1500, 80);
            if (caseName == "TextOverflow")
            {
                title.text = "This deliberately long title cannot fit in this small text rectangle";
                title.enableWordWrapping = false;
                title.overflowMode = TextOverflowModes.Truncate;
                title.rectTransform.sizeDelta = new Vector2(100, 40);
            }
            if (caseName == "TransparentRaycast")
            {
                var blocker = new GameObject("TransparentBlocker", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                blocker.transform.SetParent(canvasObject.transform, false);
                SetRect(blocker.transform as RectTransform, new Vector2(400, 300), Vector2.zero);
                blocker.GetComponent<Image>().color = Color.clear;
                blocker.GetComponent<Image>().raycastTarget = true;
            }
            // Fixture preparation is allowed to lay out its OWN newly-created objects.
            Canvas.ForceUpdateCanvases();
            foreach (TextMeshProUGUI text in canvasObject.GetComponentsInChildren<TextMeshProUGUI>()) text.ForceMeshUpdate();
            if (caseName == "MissingFont")
            {
                // TMP automatically restores its default font. Use a NEW Legacy Text fixture
                // so an explicitly empty font remains reproducible after scene loading.
                GameObject label = title.gameObject;
                UnityEngine.Object.DestroyImmediate(title);
                Text legacy = label.AddComponent<Text>();
                legacy.text = "Missing font fixture";
                legacy.font = null;
                legacy.fontSize = 48;
                legacy.raycastTarget = false;
            }
        }

        private static TextMeshProUGUI MakeText(string name, Transform parent, TMP_FontAsset font, string value, Vector2 size, Vector2 position, float fontSize)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.SetActive(false);
            go.transform.SetParent(parent, false);
            SetRect(go.transform as RectTransform, size, position);
            TextMeshProUGUI text = go.AddComponent<TextMeshProUGUI>();
            text.font = font;
            text.text = value;
            text.fontSize = fontSize;
            text.alignment = TextAlignmentOptions.Center;
            text.raycastTarget = false;
            go.SetActive(true);
            return text;
        }

        private static void SetRect(RectTransform rect, Vector2 size, Vector2 position)
        {
            if (rect == null) throw new InvalidOperationException("Missing RectTransform.");
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(.5f, .5f);
            rect.sizeDelta = size;
            rect.anchoredPosition = position;
        }

        private static TMP_FontAsset RequiredFont()
        {
            const string path = "Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF.asset";
            TMP_FontAsset font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(path);
            if (font == null) throw new InvalidOperationException("Fixture font missing: " + path);
            return font;
        }

        internal static Type ResolveInputModule()
        {
            var settings = new SerializedObject(Unsupported.GetSerializedAssetInterfaceSingleton("PlayerSettings"));
            SerializedProperty input = settings.FindProperty("activeInputHandler");
            if (input == null) return null;
            if (input.intValue == 0) return typeof(StandaloneInputModule);
            return AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("UnityEngine.InputSystem.UI.InputSystemUIInputModule"))
                .FirstOrDefault(type => type != null);
        }

        private static void EnsureFolder(string path)
        {
            string[] parts = path.Split('/');
            string parent = "Assets";
            foreach (string part in parts.Skip(1))
            {
                string next = parent + "/" + part;
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(parent, part);
                parent = next;
            }
        }
    }
}
