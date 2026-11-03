using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UIAgent.Tools.Editor.Tests
{
    public class UiSpecValidatorTests
    {
        [TearDown]
        public void TearDown() { TestAssetFolders.CleanupCreatedEmptyFolders(); }

        private static JObject Example()
        {
            return JObject.Parse("{\"schemaVersion\":1,\"screenType\":\"main_menu\",\"targetPlatform\":\"desktop\",\"referenceResolution\":{\"width\":1920,\"height\":1080},\"title\":\"Knight Adventure\",\"buttons\":[\"开始游戏\",\"设置\",\"退出游戏\"],\"theme\":\"dark\",\"outputPrefabPath\":\"Assets/AgentDemos/UIAgent/Prefabs/PF_MainMenu.prefab\"}");
        }

        [TestCase("dark", 1)]
        [TestCase("light", 5)]
        public void ValidSpecProducesProposalRequiringConfirmation(string theme, int count)
        {
            JObject spec = Example();
            spec["theme"] = theme;
            spec["buttons"] = new JArray(Enumerable.Range(0, count).Select(n => "按钮" + n));
            var result = UiSpecValidator.Validate(spec.ToString());
            Assert.That(result.valid, Is.True, JsonConvert.SerializeObject(result.errors));
            Assert.That(result.status, Is.EqualTo("valid"));
            Assert.That(result.projectChecksPerformed, Is.False);
            Assert.That(result.requiresConfirmation, Is.True);
            Assert.That(result.creationPlan.overwriteAllowed, Is.False);
            Assert.That(result.creationPlan.bindGameLogic, Is.False);
            Assert.That(result.creationPlan.buttons.Length, Is.EqualTo(count));
        }

        [TestCase("schemaVersion")]
        [TestCase("screenType")]
        [TestCase("targetPlatform")]
        [TestCase("referenceResolution")]
        [TestCase("title")]
        [TestCase("buttons")]
        [TestCase("theme")]
        [TestCase("outputPrefabPath")]
        public void EveryFieldIsRequired(string field)
        {
            JObject spec = Example(); spec.Remove(field);
            AssertInvalid(spec, "FIELD_REQUIRED", field);
        }

        [TestCase("schemaVersion", "\"1\"")]
        [TestCase("schemaVersion", "1.0")]
        [TestCase("schemaVersion", "2")]
        [TestCase("screenType", "\"inventory\"")]
        [TestCase("targetPlatform", "\"mobile\"")]
        [TestCase("theme", "\"Dark\"")]
        [TestCase("theme", "\"custom\"")]
        [TestCase("title", "null")]
        [TestCase("buttons", "{}")]
        [TestCase("referenceResolution", "null")]
        [TestCase("outputPrefabPath", "42")]
        public void InvalidFieldTypeOrValueIsRejected(string field, string value)
        {
            JObject spec = Example(); spec[field] = JToken.Parse(value);
            Assert.That(UiSpecValidator.Validate(spec.ToString()).valid, Is.False);
        }

        [TestCase("width", "\"1920\"")]
        [TestCase("width", "1920.0")]
        [TestCase("width", "1280")]
        [TestCase("height", "720")]
        public void ResolutionIsExactIntegerProfile(string field, string value)
        {
            JObject spec = Example(); spec["referenceResolution"][field] = JToken.Parse(value);
            Assert.That(UiSpecValidator.Validate(spec.ToString()).valid, Is.False);
        }

        [Test]
        public void UnknownFieldsAtBothLevelsAreRejected()
        {
            JObject spec = Example(); spec["overwrite"] = true; spec["referenceResolution"]["dpi"] = 96;
            var errors = UiSpecValidator.Validate(spec.ToString()).errors;
            Assert.That(errors.Count(e => e.code == "FIELD_UNKNOWN"), Is.EqualTo(2));
        }

        [TestCase(0)]
        [TestCase(6)]
        public void ButtonCountIsBounded(int count)
        {
            JObject spec = Example(); spec["buttons"] = new JArray(Enumerable.Repeat("Start", count));
            AssertInvalid(spec, "BUTTON_COUNT", "buttons");
        }

        [TestCase("\"\"")]
        [TestCase("\"   \"")]
        [TestCase("\" Start\"")]
        [TestCase("\"Start\\nQuit\"")]
        [TestCase("\"<b>Start</b>\"")]
        [TestCase("null")]
        [TestCase("123")]
        [TestCase("{\"label\":\"Start\"}")]
        public void ButtonLabelsArePlainNonemptyStrings(string value)
        {
            JObject spec = Example(); spec["buttons"] = new JArray(JToken.Parse(value));
            Assert.That(UiSpecValidator.Validate(spec.ToString()).valid, Is.False);
        }

        [Test]
        public void TextLengthBoundariesAndUnicodeAreExplicit()
        {
            JObject spec = Example(); spec["title"] = new string('中', 80); spec["buttons"] = new JArray(new string('A', 32), "Start 🚀");
            Assert.That(UiSpecValidator.Validate(spec.ToString()).valid, Is.True);
            spec["title"] = new string('中', 81);
            AssertInvalid(spec, "TEXT_INVALID", "title");
            spec["title"] = "Title"; spec["buttons"] = new JArray(new string('A', 33));
            AssertInvalid(spec, "TEXT_INVALID", "buttons[0]");
        }

        [TestCase("Assets/AgentDemos/UIAgent/Prefabs/PF_Menu.prefab")]
        [TestCase("Assets/AgentDemos/UIAgent/Prefabs/Theme_1/Sub-2/PF_3.prefab")]
        public void SafeOutputPathsAreAccepted(string path)
        {
            JObject spec = Example(); spec["outputPrefabPath"] = path;
            Assert.That(UiSpecValidator.Validate(spec.ToString()).valid, Is.True);
        }

        [TestCase("D:/Outside.prefab")]
        [TestCase("/Assets/AgentDemos/UIAgent/Prefabs/Menu.prefab")]
        [TestCase("Assets/Other/Menu.prefab")]
        [TestCase("Assets/AgentDemos/UIAgent/Prefabs2/Menu.prefab")]
        [TestCase("Assets/AgentDemos/UIAgent/Prefabs/../Menu.prefab")]
        [TestCase("Assets/AgentDemos/UIAgent/Prefabs/%2e%2e/Menu.prefab")]
        [TestCase("Assets/AgentDemos/UIAgent/Prefabs//Menu.prefab")]
        [TestCase("Assets/AgentDemos/UIAgent/Prefabs/./Menu.prefab")]
        [TestCase("Assets\\AgentDemos\\UIAgent\\Prefabs\\Menu.prefab")]
        [TestCase("Assets/AgentDemos/UIAgent/Prefabs/Menu.PREFAB")]
        [TestCase("Assets/AgentDemos/UIAgent/Prefabs/Menu.prefab:stream")]
        [TestCase("Assets/AgentDemos/UIAgent/Prefabs/Menu .prefab")]
        [TestCase("Assets/AgentDemos/UIAgent/Prefabs/Menu.prefab.")]
        [TestCase("Assets/AgentDemos/UIAgent/Prefabs/CON.prefab")]
        [TestCase("Assets/AgentDemos/UIAgent/Prefabs/lpt1/Menu.prefab")]
        [TestCase("Assets/AgentDemos/UIAgent/Prefabs/.prefab")]
        [TestCase("Assets/AgentDemos/UIAgent/Prefabs/菜单.prefab")]
        public void UnsafeOrUnsupportedOutputPathsAreRejected(string path)
        {
            JObject spec = Example(); spec["outputPrefabPath"] = path;
            AssertInvalid(spec, "OUTPUT_PATH_INVALID", "outputPrefabPath");
        }

        [TestCase("[]")]
        [TestCase("null")]
        [TestCase("{} {}")]
        [TestCase("{'title':'x'}")]
        [TestCase("{\"title\":\"a\",\"title\":\"b\"}")]
        [TestCase("{\"x\":1,}")]
        [TestCase("{\"x\":[1,]}")]
        [TestCase("{/* comment */\"x\":1}")]
        [TestCase("{\"x\":NaN}")]
        [TestCase("{\"x\":0x10}")]
        [TestCase("{\"x\":01}")]
        [TestCase("{\"x\":+1}")]
        [TestCase("{\"x\":.1}")]
        [TestCase("{\"x\":\"\\v\"}")]
        public void NonstandardOrAmbiguousJsonIsRejected(string json)
        {
            var result = UiSpecValidator.Validate(json);
            Assert.That(result.errors.Any(e => e.code == "JSON_INVALID"), Is.True);
            Assert.That(result.creationPlan, Is.Null);
        }

        [Test]
        public void EmptyOversizedAndDeepJsonAreRejected()
        {
            Assert.That(UiSpecValidator.Validate(null).errors[0].code, Is.EqualTo("JSON_REQUIRED"));
            Assert.That(UiSpecValidator.Validate(new string(' ', UiSpecValidator.MaxJsonLength + 1) + "{}").errors[0].code, Is.EqualTo("JSON_TOO_LARGE"));
            string deep = new string('[', 20) + "0" + new string(']', 20);
            Assert.That(UiSpecValidator.Validate(deep).errors[0].code, Is.EqualTo("JSON_INVALID"));
        }

        [Test]
        public void PathLengthAndSegmentDepthAreBounded()
        {
            JObject spec = Example();
            spec["outputPrefabPath"] = UiSpecValidator.OutputPrefix + string.Join("/", Enumerable.Repeat("A", 9)) + ".prefab";
            AssertInvalid(spec, "OUTPUT_PATH_INVALID", "outputPrefabPath");
            spec["outputPrefabPath"] = UiSpecValidator.OutputPrefix + new string('A', 65) + ".prefab";
            AssertInvalid(spec, "OUTPUT_PATH_INVALID", "outputPrefabPath");
            spec["outputPrefabPath"] = UiSpecValidator.OutputPrefix + string.Join("/", Enumerable.Repeat(new string('A', 64), 4)) + ".prefab";
            AssertInvalid(spec, "OUTPUT_PATH_INVALID", "outputPrefabPath");
        }

        [Test]
        public void MultipleErrorsAreCollectedWithoutAPlan()
        {
            JObject spec = Example(); spec["title"] = ""; spec["buttons"] = new JArray(); spec["theme"] = "custom";
            var result = UiSpecValidator.Validate(spec.ToString());
            Assert.That(result.errors.Count, Is.EqualTo(3));
            Assert.That(result.validatedSpec, Is.Null);
            Assert.That(result.creationPlan, Is.Null);
            Assert.That(result.requiresConfirmation, Is.True);
        }

        [Test]
        public void FingerprintIsOrderIndependentAndChangesWithContent()
        {
            JObject spec = Example();
            string first = UiSpecValidator.Validate(spec.ToString()).creationPlan.specFingerprint;
            var reordered = new JObject(spec.Properties().Reverse().Select(p => new JProperty(p.Name, p.Value.DeepClone())));
            Assert.That(UiSpecValidator.Validate(reordered.ToString()).creationPlan.specFingerprint, Is.EqualTo(first));
            spec["buttons"] = new JArray("Quit", "Start");
            Assert.That(UiSpecValidator.Validate(spec.ToString()).creationPlan.specFingerprint, Is.Not.EqualTo(first));
        }

        [Test]
        public void ProjectPreflightIsReadOnlyAndLeavesMissingFoldersAbsent()
        {
            string folder = UiSpecValidator.OutputPrefix + "SpecTest_" + Guid.NewGuid().ToString("N");
            JObject spec = Example(); spec["outputPrefabPath"] = folder + "/PF_Menu.prefab";
            Scene scene = SceneManager.GetActiveScene(); bool dirty = scene.isDirty; int rootCount = scene.rootCount;
            JObject result = JObject.Parse(UiSpecValidator.ValidateForProjectJson(spec.ToString()));
            Assert.That((string)result["status"], Is.EqualTo("ready_for_confirmation"), result.ToString());
            Assert.That((bool)result["projectChecksPerformed"], Is.True);
            Assert.That((bool)result["requiresConfirmation"], Is.True);
            Assert.That(AssetDatabase.IsValidFolder(folder), Is.False);
            Assert.That(scene.isDirty, Is.EqualTo(dirty)); Assert.That(scene.rootCount, Is.EqualTo(rootCount));
        }

        [Test]
        public void ProjectPreflightRejectsExistingPrefabWithoutModifyingIt()
        {
            string parentFolder = UiSpecValidator.OutputPrefix.TrimEnd('/');
            TestAssetFolders.Ensure(parentFolder);
            string leaf = "SpecCollision_" + Guid.NewGuid().ToString("N");
            string targetFolder = parentFolder + "/" + leaf;
            AssetDatabase.CreateFolder(parentFolder, leaf);
            string path = targetFolder + "/Collision.prefab";
            var root = new GameObject("Collision", typeof(RectTransform));
            try
            {
                GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, path);
                Assert.That(saved, Is.Not.Null);
                string guid = AssetDatabase.AssetPathToGUID(path);
                JObject spec = Example(); spec["outputPrefabPath"] = path;
                JObject report = JObject.Parse(UiSpecValidator.ValidateForProjectJson(spec.ToString()));
                Assert.That((bool)report["valid"], Is.False);
                Assert.That(((JArray)report["errors"]).Any(e => (string)e["code"] == "OUTPUT_ALREADY_EXISTS"), Is.True, report.ToString());
                Assert.That(report["creationPlan"].Type, Is.EqualTo(JTokenType.Null));
                Assert.That(AssetDatabase.AssetPathToGUID(path), Is.EqualTo(guid));
                Assert.That(AssetDatabase.LoadAssetAtPath<GameObject>(path), Is.Not.Null);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                AssetDatabase.DeleteAsset(path);
                AssetDatabase.DeleteAsset(targetFolder);
            }
        }

        [Test]
        public void DirectoryAtPrefabDestinationIsAlsoACollision()
        {
            string parent = UiSpecValidator.OutputPrefix.TrimEnd('/');
            TestAssetFolders.Ensure(parent);
            string name = "SpecDirectoryCollision_" + Guid.NewGuid().ToString("N") + ".prefab";
            string path = parent + "/" + name;
            AssetDatabase.CreateFolder(parent, name);
            try
            {
                JObject spec = Example(); spec["outputPrefabPath"] = path;
                JObject result = JObject.Parse(UiSpecValidator.ValidateForProjectJson(spec.ToString()));
                Assert.That((bool)result["valid"], Is.False);
                Assert.That(((JArray)result["errors"]).Any(e => (string)e["code"] == "OUTPUT_ALREADY_EXISTS"), Is.True, result.ToString());
                Assert.That(AssetDatabase.IsValidFolder(path), Is.True);
            }
            finally { AssetDatabase.DeleteAsset(path); }
        }

        private static void AssertInvalid(JObject spec, string code, string field)
        {
            var result = UiSpecValidator.Validate(spec.ToString());
            Assert.That(result.valid, Is.False);
            Assert.That(result.errors.Any(e => e.code == code && e.field == field), Is.True, JsonConvert.SerializeObject(result.errors));
            Assert.That(result.creationPlan, Is.Null);
        }
    }
}
