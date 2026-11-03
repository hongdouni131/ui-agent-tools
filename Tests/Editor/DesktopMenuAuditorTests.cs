using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace UIAgent.Tools.Editor.Tests
{
    public class DesktopMenuAuditorTests
    {
        private Scene scene;

        [SetUp]
        public void SetUp() { scene = EditorSceneManager.NewPreviewScene(); }

        [TearDown]
        public void TearDown()
        {
            if (scene.IsValid()) EditorSceneManager.ClosePreviewScene(scene);
            TestAssetFolders.CleanupCreatedEmptyFolders();
        }

        [Test]
        public void CorrectFixtureHasNoLocalFindingsAndNeedsNativeChecks()
        {
            UiAuditTestCases.Build(scene, "Correct");
            DesktopMenuAuditor.Report report = DesktopMenuAuditor.AuditScene(scene);
            Assert.That(report.findings, Is.Empty, JsonConvert.SerializeObject(report));
            Assert.That(report.status, Is.EqualTo("local_checks_passed"));
        }

        [TestCase("MissingEventSystem", "MENU_EVENT_SYSTEM_COUNT")]
        [TestCase("ButtonOutsideCanvas", "MENU_BUTTON_OUTSIDE_CANVAS")]
        [TestCase("TextOverflow", "MENU_TEXT_FIT")]
        public void DetectsLocalFaults(string fixture, string rule)
        {
            UiAuditTestCases.Build(scene, fixture);
            var report = DesktopMenuAuditor.AuditScene(scene);
            Assert.That(report.findings.Any(f => f.ruleId == rule), Is.True, JsonConvert.SerializeObject(report));
            Assert.That(report.status, Is.EqualTo("failed"));
        }

        [Test]
        public void EmptySceneDoesNotPass()
        {
            Assert.That(DesktopMenuAuditor.AuditScene(scene).status, Is.EqualTo("no_ui"));
        }

        [Test]
        public void DisabledInputModuleDoesNotPass()
        {
            UiAuditTestCases.Build(scene, "Correct");
            Find<EventSystem>().GetComponent<BaseInputModule>().enabled = false;
            Assert.That(DesktopMenuAuditor.AuditScene(scene).findings.Any(f => f.ruleId == "MENU_INPUT_MODULE"), Is.True);
        }

        [Test]
        public void SmallButtonNeedsReview()
        {
            UiAuditTestCases.Build(scene, "Correct");
            (Find<Button>().transform as RectTransform).sizeDelta = new Vector2(30, 20);
            Assert.That(DesktopMenuAuditor.AuditScene(scene).findings.Any(f => f.ruleId == "MENU_BUTTON_MIN_SIZE"), Is.True);
        }

        [Test]
        public void UnsupportedCanvasIsIncomplete()
        {
            UiAuditTestCases.Build(scene, "Correct");
            Find<Canvas>().renderMode = RenderMode.WorldSpace;
            Assert.That(DesktopMenuAuditor.AuditScene(scene).status, Is.EqualTo("incomplete"));
        }

        [TestCase("partial")]
        [TestCase("page")]
        [TestCase("skipped")]
        [TestCase("count")]
        public void IncompleteNativeResultsNeverPass(string fault)
        {
            UiAuditTestCases.Build(scene, "Correct");
            JObject upstream = NativeEmpty();
            JObject data = (JObject)upstream["data"];
            if (fault == "partial") data["complete"] = false;
            if (fault == "page") data["next_offset"] = 100;
            if (fault == "skipped") data["skipped"] = new JArray("failed to load");
            if (fault == "count") data["total_findings"] = 2;
            JObject report = Merge(upstream);
            Assert.That((string)report["status"], Is.EqualTo("incomplete"));
        }

        [Test]
        public void CompleteNativeResultsCanPass()
        {
            UiAuditTestCases.Build(scene, "Correct");
            Assert.That((string)Merge(NativeEmpty())["status"], Is.EqualTo("passed"));
        }

        [Test]
        public void NativeWarningKeepsEvidenceAndRequiresReview()
        {
            UiAuditTestCases.Build(scene, "Correct");
            JObject native = NativeEmpty();
            native["data"]["total_findings"] = 1;
            native["data"]["findings"] = new JArray(new JObject {
                ["rule"] = "transparent_raycast", ["severity"] = "warning", ["object_path"] = "Canvas/Blocker",
                ["message"] = "Potential blocker", ["suggestion"] = "Check runtime raycast",
                ["requires_runtime_validation"] = true, ["evidence"] = new JObject { ["alpha"] = 0 }
            });
            JObject report = Merge(native);
            Assert.That((string)report["status"], Is.EqualTo("needs_review"));
            var finding = report["findings"][0];
            Assert.That((string)finding["ruleId"], Is.EqualTo("FUNPLAY_transparent_raycast"));
            Assert.That((bool)finding["requiresRuntimeValidation"], Is.True);
            Assert.That((int)finding["evidence"]["alpha"], Is.Zero);
        }

        [Test]
        public void AuditDoesNotChangeSceneDirtyStateOrButtonPosition()
        {
            UiAuditTestCases.Build(scene, "Correct");
            bool dirty = scene.isDirty;
            Vector3 position = Find<Button>().transform.localPosition;
            DesktopMenuAuditor.AuditScene(scene);
            Assert.That(scene.isDirty, Is.EqualTo(dirty));
            Assert.That(Find<Button>().transform.localPosition, Is.EqualTo(position));
        }

        private T Find<T>() where T : Component
        {
            T value = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<T>(true)).FirstOrDefault();
            if (value == null) throw new InvalidOperationException("Fixture component missing: " + typeof(T).Name);
            return value;
        }

        private JObject Merge(JObject upstream)
        {
            return JObject.Parse(DesktopMenuAuditor.MergeFunplayJson(JsonConvert.SerializeObject(DesktopMenuAuditor.AuditScene(scene)), upstream.ToString()));
        }

        private static JObject NativeEmpty()
        {
            return JObject.Parse("{\"success\":true,\"data\":{\"scope\":\"scene\",\"status\":\"completed\",\"complete\":true,\"offset\":0,\"total_findings\":0,\"next_offset\":null,\"skipped\":[],\"findings\":[]}}");
        }
    }
}
