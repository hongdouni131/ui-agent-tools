using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace UIAgent.Tools.Editor
{
    public static class UiHierarchyInspector
    {
        private const int MaxObjects = 500;
        private const int MaxDepth = 20;

        [Serializable]
        public sealed class Report
        {
            public int schemaVersion = 1;
            public string reportType = "ui_hierarchy";
            public string capturedAt;
            public string project;
            public string sourceType;
            public string sourcePath;
            public string status;
            public bool editorReady;
            public int objectCount;
            public bool truncated;
            public Node[] canvases;
            public EventSystemInfo[] eventSystems;
            public string[] limitations =
            {
                "Console entries must be read separately with Funplay get_console_logs.",
                "This report describes UI structure; it does not audit correctness."
            };
        }

        [Serializable]
        public sealed class Node
        {
            public string objectPath;
            public bool activeSelf;
            public bool activeInHierarchy;
            public string[] components;
            public RectInfo rectTransform;
            public CanvasInfo canvas;
            public TextInfo text;
            public ButtonInfo button;
            public Node[] children;
        }

        [Serializable]
        public sealed class RectInfo
        {
            public Coordinate2 anchorMin;
            public Coordinate2 anchorMax;
            public Coordinate2 anchoredPosition;
            public Coordinate2 sizeDelta;
            public Coordinate2 pivot;
        }

        [Serializable]
        public sealed class Coordinate2
        {
            public float x;
            public float y;
        }

        [Serializable]
        public sealed class CanvasInfo
        {
            public string renderMode;
            public bool enabled;
            public bool hasCanvasScaler;
            public string scaleMode;
            public Coordinate2 referenceResolution;
            public float matchWidthOrHeight;
            public bool hasGraphicRaycaster;
            public bool graphicRaycasterEnabled;
        }

        [Serializable]
        public sealed class TextInfo
        {
            public string componentType;
            public string value;
            public string fontAsset;
            public float fontSize;
            public bool raycastTarget;
        }

        [Serializable]
        public sealed class ButtonInfo
        {
            public bool enabled;
            public bool interactable;
            public string targetGraphic;
        }

        [Serializable]
        public sealed class EventSystemInfo
        {
            public string objectPath;
            public bool activeInHierarchy;
            public bool enabled;
            public string inputModule;
        }

        public static string InspectActiveSceneJson()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded)
                throw new InvalidOperationException("No loaded active scene is available.");

            return JsonConvert.SerializeObject(InspectRoots(scene.GetRootGameObjects(), "scene", scene.path));
        }

        public static string InspectGameObjectJson(GameObject root)
        {
            if (root == null)
                throw new ArgumentNullException(nameof(root));

            return JsonConvert.SerializeObject(InspectRoots(new[] { root }, "gameObject", GetPath(root.transform)));
        }

        public static string InspectPrefabJson(string assetPath)
        {
            if (string.IsNullOrWhiteSpace(assetPath) || !assetPath.StartsWith("Assets/", StringComparison.Ordinal) ||
                !assetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Expected an Assets-relative .prefab path.", nameof(assetPath));
            if (AssetDatabase.LoadAssetAtPath<GameObject>(assetPath) == null)
                throw new ArgumentException("Prefab does not exist: " + assetPath, nameof(assetPath));

            GameObject root = PrefabUtility.LoadPrefabContents(assetPath);
            try
            {
                return JsonConvert.SerializeObject(InspectRoots(new[] { root }, "prefab", assetPath));
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static Report InspectRoots(GameObject[] roots, string sourceType, string sourcePath)
        {
            var report = new Report
            {
                capturedAt = DateTime.UtcNow.ToString("o"),
                project = Application.productName,
                sourceType = sourceType,
                sourcePath = sourcePath,
                editorReady = !EditorApplication.isCompiling && !EditorApplication.isUpdating,
            };

            var canvases = new List<Node>();
            var eventSystems = new List<EventSystemInfo>();
            foreach (GameObject root in roots)
            {
                foreach (Canvas canvas in root.GetComponentsInChildren<Canvas>(true))
                {
                    Transform parent = canvas.transform.parent;
                    Canvas ancestorCanvas = parent != null ? parent.GetComponentInParent<Canvas>() : null;
                    if (ancestorCanvas != null && ancestorCanvas.transform.IsChildOf(root.transform))
                        continue;

                    Node node = BuildNode(canvas.gameObject, GetPath(canvas.transform), 0, report);
                    if (node != null)
                        canvases.Add(node);
                }

                foreach (EventSystem eventSystem in root.GetComponentsInChildren<EventSystem>(true))
                {
                    BaseInputModule inputModule = eventSystem.GetComponent<BaseInputModule>();
                    eventSystems.Add(new EventSystemInfo
                    {
                        objectPath = GetPath(eventSystem.transform),
                        activeInHierarchy = eventSystem.gameObject.activeInHierarchy,
                        enabled = eventSystem.enabled,
                        inputModule = inputModule != null ? inputModule.GetType().Name : null,
                    });
                }
            }

            report.canvases = canvases.ToArray();
            report.eventSystems = eventSystems.ToArray();
            report.status = canvases.Count == 0 ? "no_ui" : "ok";
            return report;
        }

        private static Node BuildNode(GameObject gameObject, string path, int depth, Report report)
        {
            if (report.objectCount >= MaxObjects || depth > MaxDepth)
            {
                report.truncated = true;
                return null;
            }

            report.objectCount++;
            var components = new List<string>();
            foreach (Component component in gameObject.GetComponents<Component>())
                components.Add(component != null ? component.GetType().Name : "MissingScript");

            var node = new Node
            {
                objectPath = path,
                activeSelf = gameObject.activeSelf,
                activeInHierarchy = gameObject.activeInHierarchy,
                components = components.ToArray(),
            };

            RectTransform rect = gameObject.GetComponent<RectTransform>();
            if (rect != null)
            {
                node.rectTransform = new RectInfo
                {
                    anchorMin = Coordinate(rect.anchorMin),
                    anchorMax = Coordinate(rect.anchorMax),
                    anchoredPosition = Coordinate(rect.anchoredPosition),
                    sizeDelta = Coordinate(rect.sizeDelta),
                    pivot = Coordinate(rect.pivot),
                };
            }

            Canvas canvas = gameObject.GetComponent<Canvas>();
            if (canvas != null)
            {
                CanvasScaler scaler = gameObject.GetComponent<CanvasScaler>();
                GraphicRaycaster raycaster = gameObject.GetComponent<GraphicRaycaster>();
                node.canvas = new CanvasInfo
                {
                    renderMode = canvas.renderMode.ToString(),
                    enabled = canvas.enabled,
                    hasCanvasScaler = scaler != null,
                    scaleMode = scaler != null ? scaler.uiScaleMode.ToString() : null,
                    referenceResolution = scaler != null ? Coordinate(scaler.referenceResolution) : null,
                    matchWidthOrHeight = scaler != null ? scaler.matchWidthOrHeight : 0f,
                    hasGraphicRaycaster = raycaster != null,
                    graphicRaycasterEnabled = raycaster != null && raycaster.enabled,
                };
            }

            TMP_Text tmp = gameObject.GetComponent<TMP_Text>();
            if (tmp != null)
            {
                Graphic graphic = tmp as Graphic;
                node.text = new TextInfo
                {
                    componentType = tmp.GetType().Name,
                    value = tmp.text,
                    fontAsset = tmp.font != null ? tmp.font.name : null,
                    fontSize = tmp.fontSize,
                    raycastTarget = graphic != null && graphic.raycastTarget,
                };
            }
            else
            {
                Text legacy = gameObject.GetComponent<Text>();
                if (legacy != null)
                {
                    node.text = new TextInfo
                    {
                        componentType = "Text",
                        value = legacy.text,
                        fontAsset = legacy.font != null ? legacy.font.name : null,
                        fontSize = legacy.fontSize,
                        raycastTarget = legacy.raycastTarget,
                    };
                }
            }

            Button button = gameObject.GetComponent<Button>();
            if (button != null)
            {
                node.button = new ButtonInfo
                {
                    enabled = button.enabled,
                    interactable = button.interactable,
                    targetGraphic = button.targetGraphic != null ? button.targetGraphic.GetType().Name : null,
                };
            }

            var children = new List<Node>();
            foreach (Transform child in gameObject.transform)
            {
                Node childNode = BuildNode(child.gameObject, path + "/" + child.name, depth + 1, report);
                if (childNode != null)
                    children.Add(childNode);
            }
            node.children = children.ToArray();
            return node;
        }

        private static string GetPath(Transform transform)
        {
            string path = transform.name;
            while (transform.parent != null)
            {
                transform = transform.parent;
                path = transform.name + "/" + path;
            }
            return path;
        }

        private static Coordinate2 Coordinate(Vector2 value)
        {
            return new Coordinate2 { x = value.x, y = value.y };
        }
    }
}
