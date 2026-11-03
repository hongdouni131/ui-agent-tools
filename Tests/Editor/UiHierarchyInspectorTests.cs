using System;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace UIAgent.Tools.Editor.Tests
{
    public class UiHierarchyInspectorTests
    {
        private GameObject root;

        [TearDown]
        public void TearDown()
        {
            if (root != null)
                UnityEngine.Object.DestroyImmediate(root);
            TestAssetFolders.CleanupCreatedEmptyFolders();
        }

        [Test]
        public void EmptyRootReportsNoUiAndEmptyArrays()
        {
            root = new GameObject("EmptyFixture");

            JObject report = ReadGameObject(root);

            Assert.That((string)report["status"], Is.EqualTo("no_ui"));
            Assert.That(((JArray)report["canvases"]).Count, Is.Zero);
            Assert.That(((JArray)report["eventSystems"]).Count, Is.Zero);
        }

        [Test]
        public void ReportsMultipleAndNestedCanvasesInactiveButtonsAndDuplicateEventSystems()
        {
            root = new GameObject("FixtureRoot");
            GameObject firstCanvasObject = MakeCanvas("FirstCanvas", root.transform);
            GameObject nestedCanvasObject = MakeCanvas("NestedCanvas", firstCanvasObject.transform);
            GameObject secondCanvasObject = MakeCanvas("SecondCanvas", root.transform);
            GameObject inactiveButtonObject = MakeButton("InactiveButton", firstCanvasObject.transform);
            inactiveButtonObject.SetActive(false);
            MakeEventSystem("EventSystemA", root.transform);
            MakeEventSystem("EventSystemB", root.transform);

            JObject report = ReadGameObject(root);
            JArray canvases = (JArray)report["canvases"];
            JArray eventSystems = (JArray)report["eventSystems"];
            JObject firstCanvas = FindNode(canvases, "FixtureRoot/FirstCanvas");
            JObject inactiveButton = FindNode((JArray)firstCanvas["children"], "FixtureRoot/FirstCanvas/InactiveButton");

            Assert.That((string)report["status"], Is.EqualTo("ok"));
            Assert.That(canvases.Count, Is.EqualTo(2), "Nested canvases should be represented within their outer canvas tree.");
            Assert.That(FindNode((JArray)firstCanvas["children"], "FixtureRoot/FirstCanvas/NestedCanvas"), Is.Not.Null);
            Assert.That(FindNode(canvases, "FixtureRoot/SecondCanvas"), Is.Not.Null);
            Assert.That((bool)inactiveButton["activeInHierarchy"], Is.False);
            Assert.That((bool)inactiveButton["button"]["interactable"], Is.True);
            Assert.That(eventSystems.Count, Is.EqualTo(2));
            Assert.That((int)report["objectCount"], Is.EqualTo(4));

            JObject nestedReport = ReadGameObject(nestedCanvasObject);
            Assert.That((string)nestedReport["status"], Is.EqualTo("ok"));
            Assert.That(((JArray)nestedReport["canvases"]).Count, Is.EqualTo(1));
            Assert.That(FindNode((JArray)nestedReport["canvases"], "FixtureRoot/FirstCanvas/NestedCanvas"), Is.Not.Null);

            UnityEngine.Object.DestroyImmediate(secondCanvasObject);
            UnityEngine.Object.DestroyImmediate(nestedCanvasObject);
        }

        [Test]
        public void ReadsPrefabWithoutChangingTheAsset()
        {
            const string folder = "Assets/AgentDemos/UIAgent/TestCases";
            TestAssetFolders.Ensure(folder);

            string assetPath = folder + "/UIAgentInspectorTest_" + Guid.NewGuid().ToString("N") + ".prefab";
            root = MakeCanvas("PrefabCanvas", null);
            GameObject child = new GameObject("PrefabChild", typeof(RectTransform));
            child.transform.SetParent(root.transform, false);
            GameObject savedPrefab = PrefabUtility.SaveAsPrefabAsset(root, assetPath);
            Assert.That(savedPrefab, Is.Not.Null);
            UnityEngine.Object.DestroyImmediate(root);
            root = null;
            AssetDatabase.Refresh();

            try
            {
                JObject report = JObject.Parse(UiHierarchyInspector.InspectPrefabJson(assetPath));
                GameObject loadedPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                Assert.That((string)report["sourceType"], Is.EqualTo("prefab"));
                Assert.That((string)report["sourcePath"], Is.EqualTo(assetPath));
                Assert.That(loadedPrefab, Is.Not.Null);
                Assert.That(FindNode((JArray)report["canvases"], loadedPrefab.name + "/PrefabChild"), Is.Not.Null, report.ToString());
            }
            finally
            {
                AssetDatabase.DeleteAsset(assetPath);
            }
        }

        private JObject ReadGameObject(GameObject target)
        {
            return JObject.Parse(UiHierarchyInspector.InspectGameObjectJson(target));
        }

        private static GameObject MakeCanvas(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            if (parent != null)
                go.transform.SetParent(parent, false);
            return go;
        }

        private static GameObject MakeButton(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            Button button = go.GetComponent<Button>();
            button.targetGraphic = go.GetComponent<Image>();
            return go;
        }

        private static void MakeEventSystem(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(EventSystem), typeof(StandaloneInputModule));
            go.transform.SetParent(parent, false);
        }

        private static JObject FindNode(JArray nodes, string path)
        {
            foreach (JObject node in nodes)
            {
                if ((string)node["objectPath"] == path)
                    return node;

                JObject match = FindNode((JArray)node["children"], path);
                if (match != null)
                    return match;
            }
            return null;
        }
    }
}
