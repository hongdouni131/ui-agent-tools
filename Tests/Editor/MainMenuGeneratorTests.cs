using System;
using System.Collections;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.TestTools;

namespace UIAgent.Tools.Editor.Tests
{
    public class MainMenuGeneratorTests
    {
        private const string FontPath = "Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF.asset";
        private Scene original, scene;
        private string folder, sceneFolder;

        [SetUp]
        public void SetUp()
        {
            original = SceneManager.GetActiveScene();
            string id = Guid.NewGuid().ToString("N");
            folder = UiSpecValidator.OutputPrefix + "GeneratorTest_" + id;
            sceneFolder = "Assets/AgentDemos/UIAgent/TestCases/GeneratorTest_" + id;
            TestAssetFolders.Ensure(UiSpecValidator.OutputPrefix.TrimEnd('/'));
            TestAssetFolders.Ensure(sceneFolder);
            // Unity batchmode starts from an unsaved bootstrap scene that may contain a root object.
            // This is an isolated editor process, so replace only that unsaved bootstrap scene.
            if (Application.isBatchMode && string.IsNullOrEmpty(original.path) && original.rootCount > 0)
                original = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            // Unity 2022 forbids additive NewScene beside an untitled scene.
            // Persist only the verified EMPTY Test Runner scratch scene, never a user scene.
            Assert.That(original.path, Is.Empty, "Expected Test Runner scratch scene, not user work.");
            Assert.That(original.rootCount, Is.Zero, "Refuse to save a nonempty scratch scene. Start Test Runner from a saved scene so Unity creates an empty additive scratch scene.");
            Assert.That(EditorSceneManager.SaveScene(original, sceneFolder + "/RunnerScratch.unity"), Is.True);
            scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            SceneManager.SetActiveScene(scene);
            Assert.That(EditorSceneManager.SaveScene(scene, sceneFolder + "/Test.unity"), Is.True);
        }

        [TearDown]
        public void TearDown()
        {
            if (original.IsValid() && original.isLoaded) SceneManager.SetActiveScene(original);
            if (scene.IsValid() && scene.isLoaded) EditorSceneManager.CloseScene(scene, true);
            if (original.IsValid() && original.isLoaded && original.path == sceneFolder + "/RunnerScratch.unity")
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            if (AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
            if (AssetDatabase.IsValidFolder(sceneFolder)) AssetDatabase.DeleteAsset(sceneFolder);
            TestAssetFolders.CleanupCreatedEmptyFolders();
        }

        private JObject Spec(int buttons = 3, string theme = "dark")
        {
            return new JObject {
                ["schemaVersion"] = 1, ["screenType"] = "main_menu", ["targetPlatform"] = "desktop",
                ["referenceResolution"] = new JObject { ["width"] = 1920, ["height"] = 1080 },
                ["title"] = "Knight Adventure", ["buttons"] = new JArray(Enumerable.Range(1, buttons).Select(i => "Action " + i)),
                ["theme"] = theme, ["outputPrefabPath"] = folder + "/Menu.prefab"
            };
        }

        private static MainMenuGenerator.Confirmation ApprovePrepared(JObject prepared)
        {
            Assert.That((string)prepared["status"], Is.EqualTo("ready_for_confirmation"), prepared.ToString());
            return new MainMenuGenerator.Confirmation { userConfirmed = true, planFingerprint = (string)prepared["plan"]["planFingerprint"] };
        }

        private MainMenuGenerator.Confirmation Approve(JObject spec)
        {
            return ApprovePrepared(JObject.Parse(MainMenuGenerator.PrepareJson(spec.ToString(), FontPath)));
        }

        [Test]
        public void PreparationIsRepeatableAndLeavesSceneAndDiskUntouched()
        {
            JObject spec = Spec();
            JObject first = JObject.Parse(MainMenuGenerator.PrepareJson(spec.ToString(), FontPath));
            JObject second = JObject.Parse(MainMenuGenerator.PrepareJson(spec.ToString(), FontPath));
            Assert.That((string)first["status"], Is.EqualTo("ready_for_confirmation"), first.ToString());
            Assert.That((string)second["plan"]["planFingerprint"], Is.EqualTo((string)first["plan"]["planFingerprint"]));
            Assert.That(scene.rootCount, Is.Zero);
            Assert.That(scene.isDirty, Is.False);
            Assert.That(Directory.Exists(folder), Is.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void MissingOrFalseConfirmationCannotWrite(bool missing)
        {
            JObject spec = Spec();
            var confirmation = Approve(spec);
            confirmation.userConfirmed = false;
            JObject result = JObject.Parse(MainMenuGenerator.GenerateJson(spec.ToString(), FontPath, missing ? null : confirmation));
            Assert.That((string)result["status"], Is.EqualTo("confirmation_required"));
            Assert.That(scene.rootCount, Is.Zero);
            Assert.That(Directory.Exists(folder), Is.False);
        }

        [Test]
        public void SpecChangesInvalidateConfirmation()
        {
            JObject spec = Spec();
            var confirmation = Approve(spec);
            spec["title"] = "Different title";
            JObject result = JObject.Parse(MainMenuGenerator.GenerateJson(spec.ToString(), FontPath, confirmation));
            Assert.That((string)result["status"], Is.EqualTo("confirmation_required"));
            Assert.That(Directory.Exists(folder), Is.False);
        }

        [Test]
        public void SceneChangesInvalidateConfirmation()
        {
            JObject spec = Spec(); var confirmation = Approve(spec);
            SceneManager.SetActiveScene(original);
            JObject result = JObject.Parse(MainMenuGenerator.GenerateJson(spec.ToString(), FontPath, confirmation));
            Assert.That((bool)result["success"], Is.False);
            Assert.That(Directory.Exists(folder), Is.False);
        }

        [Test]
        public void MissingFontAndUnsupportedGlyphsAreBlocked()
        {
            JObject spec = Spec();
            JObject missing = JObject.Parse(MainMenuGenerator.PrepareJson(spec.ToString(), "Assets/MissingFont.asset"));
            Assert.That(((JArray)missing["errors"]).Any(e => (string)e["code"] == "FONT_REQUIRED"), Is.True);
            spec["title"] = "\u4e2d\u6587";
            JObject glyphs = JObject.Parse(MainMenuGenerator.PrepareJson(spec.ToString(), FontPath));
            Assert.That(((JArray)glyphs["errors"]).Any(e => (string)e["code"] == "FONT_GLYPHS_MISSING"), Is.True, glyphs.ToString());
            Assert.That(scene.rootCount, Is.Zero);
            Assert.That(Directory.Exists(folder), Is.False);
        }

        [Test]
        public void ValidSpecWithTextTooWideForTemplateIsBlocked()
        {
            JObject spec = Spec(); spec["title"] = new string('W', 80);
            JObject result = JObject.Parse(MainMenuGenerator.PrepareJson(spec.ToString(), FontPath));
            Assert.That(((JArray)result["errors"]).Any(e => (string)e["code"] == "LAYOUT_PREFLIGHT_FAILED"), Is.True, result.ToString());
            Assert.That(scene.rootCount, Is.Zero);
            Assert.That(Directory.Exists(folder), Is.False);
        }

        [Test]
        public void DirtySceneIsBlockedWithoutSavingIt()
        {
            EditorSceneManager.MarkSceneDirty(scene);
            JObject result = JObject.Parse(MainMenuGenerator.PrepareJson(Spec().ToString(), FontPath));
            Assert.That(((JArray)result["errors"]).Any(e => (string)e["code"] == "SCENE_DIRTY"), Is.True);
            Assert.That(scene.isDirty, Is.True);
            Assert.That(scene.rootCount, Is.Zero);
        }

        [Test]
        public void MultipleEventSystemsAreNotAutoRepaired()
        {
            var first = new GameObject("First", typeof(EventSystem));
            var second = new GameObject("Second", typeof(EventSystem));
            SceneManager.MoveGameObjectToScene(first, scene);
            SceneManager.MoveGameObjectToScene(second, scene);
            EditorSceneManager.SaveScene(scene);
            JObject result = JObject.Parse(MainMenuGenerator.PrepareJson(Spec().ToString(), FontPath));
            Assert.That(((JArray)result["errors"]).Any(e => (string)e["code"] == "EVENT_SYSTEM_CONFLICT"), Is.True, result.ToString());
            Assert.That(scene.rootCount, Is.EqualTo(2));
        }

        [Test]
        public void ExistingInvalidInputModuleIsNotReplaced()
        {
            var system = new GameObject("KeepSystem", typeof(EventSystem));
            SceneManager.MoveGameObjectToScene(system, scene);
            EditorSceneManager.SaveScene(scene);
            JObject result = JObject.Parse(MainMenuGenerator.PrepareJson(Spec().ToString(), FontPath));
            Assert.That(((JArray)result["errors"]).Any(e => (string)e["code"] == "INPUT_MODULE_CONFLICT"), Is.True, result.ToString());
            Assert.That(system.GetComponents<BaseInputModule>(), Is.Empty);
        }

        [Test]
        public void ValidExistingEventSystemIsReusedAndPreserved()
        {
            var settings = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset")[0]);
            var handler = settings.FindProperty("activeInputHandler");
            Type module = handler == null || handler.intValue == 0
                ? typeof(StandaloneInputModule)
                : AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("UnityEngine.InputSystem.UI.InputSystemUIInputModule")).FirstOrDefault(t => t != null);
            Assert.That(module, Is.Not.Null, "The project's selected input module must be installed.");
            var system = new GameObject("KeepSystem", typeof(EventSystem), module);
            SceneManager.MoveGameObjectToScene(system, scene);
            EditorSceneManager.SaveScene(scene);
            JObject spec = Spec();
            JObject result = JObject.Parse(MainMenuGenerator.GenerateJson(spec.ToString(), FontPath, Approve(spec)));
            Assert.That((bool)result["success"], Is.True, result.ToString());
            Assert.That(result["createdEventSystemId"].Type, Is.EqualTo(JTokenType.Null));
            Assert.That(scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<EventSystem>(true)).Single(), Is.SameAs(system.GetComponent<EventSystem>()));
        }

        [TestCase(1, "dark")]
        [TestCase(3, "light")]
        [TestCase(5, "dark")]
        public void ConfirmedGenerationSavesWiredPrefabAndSceneInstance(int count, string theme)
        {
            JObject spec = Spec(count, theme);
            JObject result = JObject.Parse(MainMenuGenerator.GenerateJson(spec.ToString(), FontPath, Approve(spec)));
            Assert.That((string)result["status"], Is.EqualTo("generated"), result.ToString());
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>((string)spec["outputPrefabPath"]);
            Assert.That(prefab, Is.Not.Null);
            Assert.That(prefab.GetComponent<Canvas>().renderMode, Is.EqualTo(RenderMode.ScreenSpaceOverlay));
            Assert.That(prefab.GetComponent<CanvasScaler>().referenceResolution, Is.EqualTo(new Vector2(1920, 1080)));
            Assert.That(prefab.GetComponent<GraphicRaycaster>(), Is.Not.Null);
            Assert.That(prefab.GetComponentsInChildren<Button>().Length, Is.EqualTo(count));
            Assert.That(prefab.GetComponentsInChildren<EventSystem>(), Is.Empty);
            Button[] buttons = prefab.GetComponentsInChildren<Button>();
            for (int i = 0; i < count; i++)
            {
                Assert.That(buttons[i].targetGraphic, Is.SameAs(buttons[i].GetComponent<Image>()));
                Assert.That(buttons[i].onClick.GetPersistentEventCount(), Is.Zero);
                Assert.That(buttons[i].navigation.selectOnDown, Is.SameAs(buttons[(i + 1) % count]));
                Assert.That(buttons[i].GetComponentInChildren<TextMeshProUGUI>().text, Is.EqualTo("Action " + (i + 1)));
            }
            Assert.That(prefab.GetComponentsInChildren<TextMeshProUGUI>().All(t => t.font != null && !t.raycastTarget && !t.richText), Is.True);
            Assert.That(prefab.transform.Find("Background").GetComponent<Image>().raycastTarget, Is.False);
            Assert.That(scene.isDirty, Is.True);
            Assert.That((bool)result["sceneSaved"], Is.False);
            Assert.That((bool)result["acceptancePerformed"], Is.False);
            Assert.That(scene.GetRootGameObjects().Any(o => o.GetInstanceID().ToString() == (string)result["instanceId"]), Is.True);
        }

        [Test]
        public void EndToEndWorkflowValidatesRequiresConfirmationGeneratesAndAudits()
        {
            JObject spec = Spec();
            JObject validation = JObject.Parse(UiSpecValidator.ValidateForProjectJson(spec.ToString()));
            Assert.That((bool)validation["valid"], Is.True, validation.ToString());
            Assert.That((bool)validation["projectChecksPerformed"], Is.True);

            JObject prepared = JObject.Parse(MainMenuGenerator.PrepareJson(spec.ToString(), FontPath));
            Assert.That((string)prepared["status"], Is.EqualTo("ready_for_confirmation"), prepared.ToString());
            Assert.That(scene.rootCount, Is.Zero);

            JObject rejected = JObject.Parse(MainMenuGenerator.GenerateJson(spec.ToString(), FontPath, null));
            Assert.That((string)rejected["status"], Is.EqualTo("confirmation_required"), rejected.ToString());
            Assert.That(scene.rootCount, Is.Zero);
            Assert.That(AssetDatabase.LoadAssetAtPath<GameObject>((string)spec["outputPrefabPath"]), Is.Null);

            MainMenuGenerator.Confirmation confirmation = ApprovePrepared(prepared);
            JObject generated = JObject.Parse(MainMenuGenerator.GenerateJson(spec.ToString(), FontPath, confirmation));
            Assert.That((string)generated["status"], Is.EqualTo("generated"), generated.ToString());
            Assert.That((bool)generated["acceptancePerformed"], Is.False);

            // Test Runner may expose an EventSystem from its separate scratch scene.
            foreach (EventSystem system in scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<EventSystem>(true)))
                UnityEngine.Object.DestroyImmediate(system.gameObject);
            Type resolvedModule = UiAuditTestCases.ResolveInputModule();
            Assert.That(resolvedModule, Is.Not.Null, "The project's selected UI input module must be installed.");
            SceneManager.MoveGameObjectToScene(new GameObject("TestEventSystem", typeof(EventSystem), resolvedModule), scene);

            JObject hierarchy = JObject.Parse(UiHierarchyInspector.InspectActiveSceneJson());
            Assert.That((string)hierarchy["status"], Is.EqualTo("ok"), hierarchy.ToString());
            Assert.That((int)hierarchy["objectCount"], Is.GreaterThan(0));

            Canvas canvas = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Canvas>(true)).Single();
            (canvas.transform as RectTransform).sizeDelta = new Vector2(1920, 1080);
            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(canvas.transform.Find("ButtonGroup") as RectTransform);
            DesktopMenuAuditor.Report audit = DesktopMenuAuditor.AuditScene(scene);
            string findings = string.Join(", ", audit.findings.Select(f => f.ruleId + "@" + f.objectPath));
            Assert.That(audit.complete, Is.True, findings);
            Assert.That(audit.errorCount, Is.Zero, findings);
            Assert.That(audit.warningCount, Is.EqualTo(3), "Test Runner has no fixed Game View size; preserve the three button hit-area warnings instead of treating them as a visual pass. " + findings);
            Assert.That(audit.findings.Where(f => f.severity == "warning").All(f => f.ruleId == "MENU_BUTTON_MIN_SIZE"), Is.True, findings);
            Assert.That(audit.status, Is.EqualTo("needs_review"));
        }

        [Test]
        public void RepeatCallDoesNotOverwriteExistingPrefab()
        {
            JObject spec = Spec(); var confirmation = Approve(spec);
            JObject first = JObject.Parse(MainMenuGenerator.GenerateJson(spec.ToString(), FontPath, confirmation));
            string guid = (string)first["prefabGuid"];
            Assert.That((bool)first["success"], Is.True, first.ToString());
            int roots = scene.rootCount;
            JObject second = JObject.Parse(MainMenuGenerator.GenerateJson(spec.ToString(), FontPath, confirmation));
            Assert.That((bool)second["success"], Is.False);
            Assert.That(((JArray)second["errors"]).Any(e => (string)e["code"] == "OUTPUT_ALREADY_EXISTS"), Is.True);
            Assert.That(AssetDatabase.AssetPathToGUID((string)spec["outputPrefabPath"]), Is.EqualTo(guid));
            Assert.That(scene.rootCount, Is.EqualTo(roots));
        }

        [TestCase("built")]
        [TestCase("saved")]
        [TestCase("instantiated")]
        public void InjectedFailureCleansOnlyOwnedObjectsAssetsAndFolders(string checkpoint)
        {
            JObject spec = Spec();
            var report = MainMenuGenerator.Generate(spec.ToString(), FontPath, Approve(spec), phase => {
                if (phase == checkpoint) throw new InvalidOperationException("Injected failure at " + phase);
            });
            Assert.That(report.status, Is.EqualTo("failed_rolled_back"), Newtonsoft.Json.JsonConvert.SerializeObject(report));
            Assert.That(report.cleanupErrors, Is.Empty);
            Assert.That(Directory.Exists(folder), Is.False);
            Assert.That(AssetDatabase.LoadAssetAtPath<GameObject>((string)spec["outputPrefabPath"]), Is.Null);
            Assert.That(scene.rootCount, Is.Zero);
            Assert.That(original.IsValid() && original.isLoaded, Is.True);
        }

        [Test]
        public void CollisionIntroducedAfterConfirmationIsPreserved()
        {
            JObject spec = Spec(); var approval = Approve(spec);
            string path = (string)spec["outputPrefabPath"];
            GameObject competitor = null;
            string competitorGuid = null;
            try
            {
                var report = MainMenuGenerator.Generate(spec.ToString(), FontPath, approval, phase => {
                    if (phase == "before_save")
                    {
                        competitor = new GameObject("KeepMe");
                        PrefabUtility.SaveAsPrefabAsset(competitor, path);
                        competitorGuid = AssetDatabase.AssetPathToGUID(path);
                    }
                });
                Assert.That(report.success, Is.False);
                Assert.That(AssetDatabase.AssetPathToGUID(path), Is.EqualTo(competitorGuid));
                Assert.That(AssetDatabase.LoadAssetAtPath<GameObject>(path), Is.Not.Null);
                Assert.That(report.cleanupErrors, Is.Not.Empty, "Nonempty newly-created folder must be retained.");
            }
            finally { if (competitor != null) UnityEngine.Object.DestroyImmediate(competitor); }
        }

        [UnityTest]
        public IEnumerator PreparationPreservesPriorSceneUndo()
        {
            var sentinel = new GameObject("PriorUserObject");
            SceneManager.MoveGameObjectToScene(sentinel, scene);
            Undo.IncrementCurrentGroup();
            Undo.RegisterCreatedObjectUndo(sentinel, "Prior user creation");
            EditorSceneManager.SaveScene(scene);
            Undo.FlushUndoRecordObjects();
            yield return null;
            JObject prepared = JObject.Parse(MainMenuGenerator.PrepareJson(Spec().ToString(), FontPath));
            Assert.That((string)prepared["status"], Is.EqualTo("ready_for_confirmation"), prepared.ToString());
            Undo.PerformUndo();
            yield return null;
            Assert.That(sentinel == null, Is.True, "Preparation must preserve the caller's prior Undo history.");
        }

        [UnityTest]
        public IEnumerator SceneUndoRemovesGeneratedInstanceButPreservesPrefab()
        {
            var sentinel = new GameObject("KeepExistingObject");
            SceneManager.MoveGameObjectToScene(sentinel, scene);
            EditorSceneManager.SaveScene(scene);
            JObject spec = Spec();
            JObject result = JObject.Parse(MainMenuGenerator.GenerateJson(spec.ToString(), FontPath, Approve(spec)));
            Assert.That((bool)result["success"], Is.True, result.ToString());
            Undo.FlushUndoRecordObjects();
            yield return null;
            Undo.PerformUndo();
            yield return null;
            Assert.That(scene.rootCount, Is.EqualTo(1));
            Assert.That(scene.GetRootGameObjects()[0], Is.SameAs(sentinel));
            Assert.That(AssetDatabase.LoadAssetAtPath<GameObject>((string)spec["outputPrefabPath"]), Is.Not.Null);
        }
    }
}
