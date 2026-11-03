using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.EventSystems;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace UIAgent.Tools.Editor.Tests
{
    public class UiCompositionGeneratorTests
    {
        private const string FontPath = "Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF.asset";
        private const string BorderGuid = "0123456789abcdef0123456789abcdef";
        private Scene original, scene;
        private string id, outputFolder, testFolder, spritePath, spriteGuid, prefabPath;

        [SetUp]
        public void SetUp()
        {
            original = SceneManager.GetActiveScene();
            id = Guid.NewGuid().ToString("N");
            outputFolder = UiSpecValidator.OutputPrefix + "CompositionTest_" + id;
            testFolder = "Assets/AgentDemos/UIAgent/TestCases/CompositionTest_" + id;
            prefabPath = outputFolder + "/PF_Dialog.prefab";
            TestAssetFolders.Ensure(UiSpecValidator.OutputPrefix.TrimEnd('/'));
            TestAssetFolders.Ensure(testFolder);
            // Unity batchmode starts from an unsaved bootstrap scene that may contain a root object.
            // This is an isolated editor process, so replace only that unsaved bootstrap scene.
            if (Application.isBatchMode && string.IsNullOrEmpty(original.path) && original.rootCount > 0)
                original = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Assert.That(original.path, Is.Empty, "Expected Test Runner scratch scene, not user work.");
            Assert.That(original.rootCount, Is.Zero, "Refuse to save a nonempty scratch scene. Start Test Runner from a saved scene so Unity creates an empty additive scratch scene.");
            Assert.That(EditorSceneManager.SaveScene(original, testFolder + "/RunnerScratch.unity"), Is.True);
            scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            SceneManager.SetActiveScene(scene);
            Assert.That(EditorSceneManager.SaveScene(scene, testFolder + "/Test.unity"), Is.True);
            spritePath = testFolder + "/NineSlice.png";
            WriteSpriteFixture(spritePath);
            spriteGuid = AssetDatabase.AssetPathToGUID(spritePath);
            Assert.That(spriteGuid, Has.Length.EqualTo(32));
        }

        [TearDown]
        public void TearDown()
        {
            if (original.IsValid() && original.isLoaded) SceneManager.SetActiveScene(original);
            if (scene.IsValid() && scene.isLoaded) EditorSceneManager.CloseScene(scene, true);
            if (original.IsValid() && original.isLoaded && original.path == testFolder + "/RunnerScratch.unity")
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            if (AssetDatabase.IsValidFolder(outputFolder)) AssetDatabase.DeleteAsset(outputFolder);
            if (AssetDatabase.IsValidFolder(testFolder)) AssetDatabase.DeleteAsset(testFolder);
            TestAssetFolders.CleanupCreatedEmptyFolders();
        }

        private static void WriteSpriteFixture(string path)
        {
            var texture = new Texture2D(48, 48, TextureFormat.RGBA32, false);
            Color[] pixels = Enumerable.Repeat(new Color(0.2f, 0.5f, 0.8f, 1f), 48 * 48).ToArray();
            texture.SetPixels(pixels);
            texture.Apply();
            try { File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(Application.dataPath), path), texture.EncodeToPNG()); }
            finally { UnityEngine.Object.DestroyImmediate(texture); }
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.spriteBorder = new Vector4(8, 8, 8, 8);
            importer.mipmapEnabled = false;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.SaveAndReimport();
            Assert.That(AssetDatabase.LoadAssetAtPath<Sprite>(path), Is.Not.Null);
        }

        private JObject Example()
        {
            JObject root = Panel("Screen"); root["placement"] = Anchors(true);
            JObject dialog = Panel("Dialog");
            dialog["childrenLayout"] = Layout();
            dialog["style"] = new JObject { ["spritePath"] = spritePath, ["spriteGuid"] = spriteGuid,
                ["imageType"] = "sliced", ["border"] = new JArray(8, 8, 8, 8) };
            JObject heading = Text("Heading", "Confirm action");
            heading["placement"] = new JObject { ["mode"] = "layout", ["preferredSize"] = new JArray(560, 72) };
            JObject button = Panel("ConfirmButton"); button["type"] = "Button";
            button["placement"] = new JObject { ["mode"] = "layout", ["preferredSize"] = new JArray(560, 80) };
            button["children"] = new JArray(Text("ConfirmLabel", "Confirm", true));
            dialog["children"] = new JArray(heading, button);
            root["children"] = new JArray(dialog);
            return new JObject {
                ["schemaVersion"] = 2, ["screenType"] = "composition", ["targetPlatform"] = "desktop",
                ["referenceResolution"] = new JObject { ["width"] = 1920, ["height"] = 1080 },
                ["fontAssetPath"] = FontPath, ["outputPrefabPath"] = prefabPath, ["root"] = root
            };
        }

        [Test]
        public void Diagnosis_MeasuresSavedPrefabAndProposesNewVersionWithoutSideEffects()
        {
            JObject spec = Example();
            Dialog(spec)["placement"]["anchoredPosition"] = new JArray(1000, 0);
            JObject plan = JObject.Parse(UiCompositionGenerator.PrepareJson(spec.ToString()));
            Assert.That((string)plan["status"], Is.EqualTo("ready_for_confirmation"), plan.ToString());
            Assert.That((bool)JObject.Parse(UiCompositionGenerator.GenerateJson(spec.ToString(),
                new UiCompositionGenerator.Confirmation { userConfirmed = true, planFingerprint = (string)plan["plan"]["planFingerprint"] }))["success"], Is.True);
            byte[] before = File.ReadAllBytes(prefabPath);
            int roots = scene.rootCount, previews = EditorSceneManager.previewSceneCount;
            bool dirty = scene.isDirty;
            JObject diagnosis = JObject.Parse(UiCompositionDiagnostics.DiagnoseJson(spec.ToString()));
            Assert.That((bool)diagnosis["complete"], Is.True, diagnosis.ToString());
            JObject issue = diagnosis["issues"].OfType<JObject>().Single(i => (string)i["issueId"] == "OUTSIDE_REFERENCE_CANVAS:Dialog");
            Assert.That((double)issue["evidence"]["boundsInCanvas"][2], Is.GreaterThan(960));
            Assert.That((float)issue["suggestedChanges"][0]["after"][0], Is.EqualTo(600));
            Assert.That((string)JObject.Parse(UiCompositionDiagnostics.DiagnoseJson(spec.ToString()))["diagnosisFingerprint"],
                Is.EqualTo((string)diagnosis["diagnosisFingerprint"]));
            Assert.That(File.ReadAllBytes(prefabPath), Is.EqualTo(before));
            Assert.That(scene.rootCount, Is.EqualTo(roots)); Assert.That(scene.isDirty, Is.EqualTo(dirty));
            Assert.That(EditorSceneManager.previewSceneCount, Is.EqualTo(previews));
            Dialog(spec)["placement"]["anchoredPosition"] = issue["suggestedChanges"][0]["after"].DeepClone();
            spec["outputPrefabPath"] = outputFolder + "/PF_Revised.prefab";
            plan = JObject.Parse(UiCompositionGenerator.PrepareJson(spec.ToString()));
            Assert.That((string)plan["status"], Is.EqualTo("ready_for_confirmation"), plan.ToString());
            Assert.That((bool)JObject.Parse(UiCompositionGenerator.GenerateJson(spec.ToString(),
                new UiCompositionGenerator.Confirmation { userConfirmed = true, planFingerprint = (string)plan["plan"]["planFingerprint"] }))["success"], Is.True);
            diagnosis = JObject.Parse(UiCompositionDiagnostics.DiagnoseJson(spec.ToString()));
            Assert.That((bool)diagnosis["complete"], Is.True, diagnosis.ToString());
            Assert.That(diagnosis["issues"].Any(i => (string)i["code"] == "OUTSIDE_REFERENCE_CANVAS"), Is.False, diagnosis.ToString());
            Assert.That(File.ReadAllBytes(prefabPath), Is.EqualTo(before));
        }

        [Test]
        public void Diagnosis_RejectsMismatchedSpecAndMissingPrefab()
        {
            JObject spec = Example();
            Assert.That((bool)JObject.Parse(UiCompositionDiagnostics.DiagnoseJson(spec.ToString()))["complete"], Is.False);
            JObject plan = JObject.Parse(UiCompositionGenerator.PrepareJson(spec.ToString()));
            UiCompositionGenerator.GenerateJson(spec.ToString(), new UiCompositionGenerator.Confirmation {
                userConfirmed = true, planFingerprint = (string)plan["plan"]["planFingerprint"] });
            Dialog(spec)["placement"]["anchoredPosition"] = new JArray(20, 0);
            JObject result = JObject.Parse(UiCompositionDiagnostics.DiagnoseJson(spec.ToString()));
            Assert.That((bool)result["complete"], Is.False);
            Assert.That(result["diagnosisFingerprint"], Is.Null);
            Assert.That((int)result["assetAudit"]["errorCount"], Is.GreaterThan(0));
        }

        [Test]
        public void Diagnosis_ReportsStretchWithRealBorderAndDependencyChange()
        {
            JObject spec = Example();
            Dialog(spec)["style"]["imageType"] = "simple";
            ((JObject)Dialog(spec)["style"]).Remove("border");
            JObject plan = JObject.Parse(UiCompositionGenerator.PrepareJson(spec.ToString()));
            UiCompositionGenerator.GenerateJson(spec.ToString(), new UiCompositionGenerator.Confirmation {
                userConfirmed = true, planFingerprint = (string)plan["plan"]["planFingerprint"] });
            JObject result = JObject.Parse(UiCompositionDiagnostics.DiagnoseJson(spec.ToString()));
            Assert.That((bool)result["complete"], Is.True, result.ToString());
            JObject issue = result["issues"].OfType<JObject>().Single(i => (string)i["issueId"] == "SIMPLE_SPRITE_STRETCH:Dialog");
            Assert.That(issue["suggestedChanges"][1]["after"].ToObject<int[]>(), Is.EqualTo(new[] { 8, 8, 8, 8 }));
            var importer = (TextureImporter)AssetImporter.GetAtPath(spritePath);
            importer.spritePixelsPerUnit = 80; importer.SaveAndReimport();
            JObject changed = JObject.Parse(UiCompositionDiagnostics.DiagnoseJson(spec.ToString()));
            Assert.That((bool)changed["complete"], Is.True, changed.ToString());
            Assert.That((string)changed["diagnosisFingerprint"], Is.Not.EqualTo((string)result["diagnosisFingerprint"]));
        }

        [Test]
        public void Diagnosis_DoesNotSuggestMovingLayoutChildrenOrOversizedRootChildren()
        {
            JObject spec = Example();
            Dialog(spec)["placement"]["sizeDelta"] = new JArray(2200, 420);
            JObject plan = JObject.Parse(UiCompositionGenerator.PrepareJson(spec.ToString()));
            Assert.That((string)plan["status"], Is.EqualTo("ready_for_confirmation"), plan.ToString());
            UiCompositionGenerator.GenerateJson(spec.ToString(), new UiCompositionGenerator.Confirmation {
                userConfirmed = true, planFingerprint = (string)plan["plan"]["planFingerprint"] });
            JObject result = JObject.Parse(UiCompositionDiagnostics.DiagnoseJson(spec.ToString()));
            Assert.That((bool)result["complete"], Is.True, result.ToString());
            Assert.That(result["issues"].Where(i => (string)i["code"] == "OUTSIDE_REFERENCE_CANVAS")
                .All(i => !i["suggestedChanges"].Any()), Is.True, result.ToString());
        }

        [Test]
        public void Diagnosis_MissingGlyphDoesNotPopulateOrDirtyFont()
        {
            JObject spec = Example();
            TMP_FontAsset font = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontPath));
            string fontPath = testFolder + "/DiagnosisFont.asset";
            AssetDatabase.CreateAsset(font, fontPath);
            spec["fontAssetPath"] = fontPath;
            JObject plan = JObject.Parse(UiCompositionGenerator.PrepareJson(spec.ToString()));
            Assert.That((string)plan["status"], Is.EqualTo("ready_for_confirmation"), plan.ToString());
            UiCompositionGenerator.GenerateJson(spec.ToString(), new UiCompositionGenerator.Confirmation {
                userConfirmed = true, planFingerprint = (string)plan["plan"]["planFingerprint"] });
            font.characterTable.RemoveAll(c => c.unicode == 'C');
            font.ReadFontAssetDefinition();
            EditorUtility.SetDirty(font); AssetDatabase.SaveAssetIfDirty(font);
            int characters = font.characterTable.Count;
            byte[] before = File.ReadAllBytes(fontPath);
            JObject result = JObject.Parse(UiCompositionDiagnostics.DiagnoseJson(spec.ToString()));
            Assert.That((bool)result["complete"], Is.False, result.ToString());
            Assert.That((string)result["errors"][0]["message"], Does.Contain("U+0043"));
            Assert.That(font.characterTable.Count, Is.EqualTo(characters));
            Assert.That(EditorUtility.IsDirty(font), Is.False);
            Assert.That(File.ReadAllBytes(fontPath), Is.EqualTo(before));
        }

        [Test]
        public void Diagnosis_RefusesUnknownComponentsBeforeInstantiating()
        {
            JObject spec = Example();
            JObject plan = JObject.Parse(UiCompositionGenerator.PrepareJson(spec.ToString()));
            UiCompositionGenerator.GenerateJson(spec.ToString(), new UiCompositionGenerator.Confirmation {
                userConfirmed = true, planFingerprint = (string)plan["plan"]["planFingerprint"] });
            GameObject asset = PrefabUtility.LoadPrefabContents(prefabPath);
            try { asset.AddComponent<AudioSource>(); PrefabUtility.SaveAsPrefabAsset(asset, prefabPath); }
            finally { PrefabUtility.UnloadPrefabContents(asset); }
            JObject result = JObject.Parse(UiCompositionDiagnostics.DiagnoseJson(spec.ToString()));
            Assert.That((bool)result["complete"], Is.False, result.ToString());
            Assert.That(result["errors"].Any(), Is.True);
        }

        private JObject ConfirmationExample()
        {
            JObject spec = Example();
            JObject backdrop = Panel("Backdrop");
            backdrop["placement"] = Anchors(true);
            backdrop["style"] = new JObject { ["color"] = "#08121DCC" };
            JObject dialog = Dialog(spec);
            JObject cancel = Panel("CancelButton"); cancel["type"] = "Button";
            cancel["placement"] = new JObject { ["mode"] = "layout", ["preferredSize"] = new JArray(560, 80) };
            cancel["children"] = new JArray(Text("CancelLabel", "Cancel", true));
            ((JArray)dialog["children"]).Add(cancel);
            ((JArray)spec["root"]["children"]).Insert(0, backdrop);
            spec["confirmationDialog"] = new JObject {
                ["backdropId"] = "Backdrop", ["dialogPanelId"] = "Dialog",
                ["cancelButtonId"] = "CancelButton", ["confirmButtonId"] = "ConfirmButton",
                ["initialFocusId"] = "ConfirmButton"
            };
            return spec;
        }

        private static JObject Dialog(JObject spec) => (JObject)spec["root"]["children"][0];
        private static JObject Button(JObject spec) => (JObject)Dialog(spec)["children"][1];

        private JObject AddToggle(JObject spec)
        {
            JObject toggle = Panel("MusicToggle");
            toggle["type"] = "Toggle";
            toggle["placement"] = new JObject { ["mode"] = "layout", ["preferredSize"] = new JArray(320, 72) };
            toggle["style"] = new JObject { ["spritePath"] = spritePath, ["spriteGuid"] = spriteGuid,
                ["imageType"] = "sliced", ["border"] = new JArray(8, 8, 8, 8) };
            toggle["toggle"] = new JObject { ["initialValue"] = true, ["labelId"] = "MusicLabel", ["indicatorId"] = "MusicIndicator" };
            JObject label = Text("MusicLabel", "Music");
            label["placement"] = new JObject { ["mode"] = "anchors", ["anchorMin"] = new JArray(0.08, 0),
                ["anchorMax"] = new JArray(0.68, 1), ["pivot"] = new JArray(0.5, 0.5),
                ["anchoredPosition"] = new JArray(0, 0), ["sizeDelta"] = new JArray(0, 0) };
            JObject indicator = Text("MusicIndicator", "ON");
            indicator["placement"] = new JObject { ["mode"] = "anchors", ["anchorMin"] = new JArray(0.82, 0.5),
                ["anchorMax"] = new JArray(0.82, 0.5), ["pivot"] = new JArray(0.5, 0.5),
                ["anchoredPosition"] = new JArray(0, 0), ["sizeDelta"] = new JArray(48, 48) };
            toggle["children"] = new JArray(label, indicator);
            ((JArray)Dialog(spec)["children"]).Add(toggle);
            return toggle;
        }

        private static JObject Panel(string nodeId) => new JObject { ["id"] = nodeId, ["type"] = "Panel", ["placement"] = Anchors(),
            ["childrenLayout"] = new JObject { ["mode"] = "none" }, ["style"] = new JObject { ["color"] = "#253047" }, ["children"] = new JArray() };

        private static JObject Text(string nodeId, string value, bool fill = false)
        {
            JObject node = Panel(nodeId); node["type"] = "Text"; node["text"] = value;
            node["placement"] = fill ? Anchors(true) : new JObject { ["mode"] = "layout", ["preferredSize"] = new JArray(560, 64) };
            node["style"] = new JObject { ["color"] = "#FFFFFF", ["fontSize"] = 32, ["alignment"] = "center" };
            return node;
        }

        private static JObject Anchors(bool fill = false) => new JObject {
            ["mode"] = "anchors", ["anchorMin"] = fill ? new JArray(0, 0) : new JArray(0.5, 0.5),
            ["anchorMax"] = fill ? new JArray(1, 1) : new JArray(0.5, 0.5), ["pivot"] = new JArray(0.5, 0.5),
            ["anchoredPosition"] = new JArray(0, 0), ["sizeDelta"] = fill ? new JArray(0, 0) : new JArray(720, 420)
        };

        private static JObject Layout() => new JObject {
            ["mode"] = "vertical", ["spacing"] = 16, ["padding"] = new JArray(16, 16, 16, 16),
            ["childAlignment"] = "middleCenter", ["childControlWidth"] = true, ["childControlHeight"] = true,
            ["childForceExpandWidth"] = false, ["childForceExpandHeight"] = false
        };

        private JObject Prepare(JObject spec = null) => JObject.Parse(UiCompositionGenerator.PrepareJson((spec ?? Example()).ToString()));

        private static UiCompositionGenerator.Confirmation Confirm(JObject prepared) => new UiCompositionGenerator.Confirmation {
            userConfirmed = true, planFingerprint = (string)prepared["plan"]["planFingerprint"]
        };

        private Sprite[] MultipleFixture()
        {
            var importer = (TextureImporter)AssetImporter.GetAtPath(spritePath);
            importer.spriteImportMode = SpriteImportMode.Multiple;
#pragma warning disable 618
            importer.spritesheet = new[] {
                new SpriteMetaData { name = "Left", rect = new Rect(0, 0, 24, 48), alignment = 9, pivot = new Vector2(0.5f, 0.5f), border = new Vector4(4, 4, 4, 4) },
                new SpriteMetaData { name = "Right", rect = new Rect(24, 0, 24, 48), alignment = 9, pivot = new Vector2(0.5f, 0.5f), border = new Vector4(6, 6, 6, 6) }
            };
#pragma warning restore 618
            importer.SaveAndReimport();
            return AssetDatabase.LoadAllAssetsAtPath(spritePath).OfType<Sprite>().OrderBy(x => x.name).ToArray();
        }

        private static void SelectSlice(JObject node, Sprite sprite)
        {
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(sprite, out string guid, out long localId);
            node["style"] = new JObject { ["spritePath"] = AssetDatabase.GetAssetPath(sprite), ["spriteGuid"] = guid,
                ["spriteName"] = sprite.name, ["spriteLocalFileId"] = localId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["imageType"] = "sliced", ["border"] = new JArray((int)sprite.border.x, (int)sprite.border.y, (int)sprite.border.z, (int)sprite.border.w) };
        }

        private JObject MultipleExample(out Sprite[] slices)
        {
            slices = MultipleFixture(); Assert.That(slices.Length, Is.EqualTo(2));
            var spec = Example(); SelectSlice(Dialog(spec), slices[0]); SelectSlice(Button(spec), slices[1]);
            return spec;
        }

        [Test]
        public void MultipleSlicesSharingOnePathPersistIndependentlyAndAuditWithoutWrites()
        {
            var spec = MultipleExample(out var slices); var prepared = Prepare(spec);
            Assert.That((string)prepared["status"], Is.EqualTo("ready_for_confirmation"), prepared.ToString());
            var refs = (JArray)prepared["plan"]["spriteReferences"];
            Assert.That(refs.Count, Is.EqualTo(2));
            Assert.That(refs.Select(r => (string)r["localFileId"]).Distinct().Count(), Is.EqualTo(2));
            Assert.That(refs.All(r => r["localFileId"].Type == JTokenType.String), Is.True);
            var result = JObject.Parse(UiCompositionGenerator.GenerateJson(spec.ToString(), Confirm(prepared)));
            Assert.That((string)result["status"], Is.EqualTo("generated"), result.ToString());
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            Assert.That(prefab.transform.Find("Dialog").GetComponent<Image>().sprite, Is.EqualTo(slices[0]));
            Assert.That(prefab.transform.Find("Dialog/ConfirmButton").GetComponent<Image>().sprite, Is.EqualTo(slices[1]));
            var bytes = File.ReadAllBytes(prefabPath); var meta = File.ReadAllBytes(spritePath + ".meta");
            int handle = scene.handle, roots = scene.rootCount; bool dirty = scene.isDirty;
            var audit = UiCompositionAssetAuditor.AuditPrefab(spec.ToString());
            Assert.That(audit.status, Is.EqualTo("passed"), UiCompositionAssetAuditor.AuditPrefabJson(spec.ToString()));
            Assert.That(File.ReadAllBytes(prefabPath), Is.EqualTo(bytes)); Assert.That(File.ReadAllBytes(spritePath + ".meta"), Is.EqualTo(meta));
            Assert.That(scene.handle, Is.EqualTo(handle)); Assert.That(scene.rootCount, Is.EqualTo(roots)); Assert.That(scene.isDirty, Is.EqualTo(dirty));
            // A no-op reimport must retain the captured identity and the saved reference.
            AssetDatabase.ImportAsset(spritePath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            Assert.That(UiCompositionAssetAuditor.AuditPrefab(spec.ToString()).status, Is.EqualTo("passed"));
        }

        [TestCase("missing_selector", "SPRITE_SUBASSET_REQUIRED")]
        [TestCase("missing_id", "SPRITE_SUBASSET_NOT_FOUND")]
        [TestCase("wrong_name", "SPRITE_NAME_MISMATCH")]
        [TestCase("wrong_guid", "SPRITE_GUID_MISMATCH")]
        [TestCase("border", "SPRITE_BORDER_MISMATCH")]
        [TestCase("bounds", "SPRITE_BORDER_EXCEEDS_RECT")]
        public void MultiplePreflightRejectsAmbiguousOrChangedIdentity(string mutation, string error)
        {
            var spec = MultipleExample(out var slices); var style = (JObject)Dialog(spec)["style"];
            if (mutation == "missing_selector") { style.Remove("spriteName"); style.Remove("spriteLocalFileId"); }
            if (mutation == "missing_id") style["spriteLocalFileId"] = "9223372036854775807";
            if (mutation == "wrong_name") style["spriteName"] = slices[1].name;
            if (mutation == "wrong_guid") style["spriteGuid"] = new string('0', 32);
            if (mutation == "border") style["border"] = new JArray(5, 4, 4, 4);
            if (mutation == "bounds") style["border"] = new JArray(20, 4, 20, 4);
            var prepared = Prepare(spec);
            Assert.That((string)prepared["status"], Is.EqualTo("blocked"), prepared.ToString());
            Assert.That(prepared["errors"].Any(e => (string)e["code"] == error), Is.True, prepared.ToString());
            Assert.That(AssetDatabase.IsValidFolder(outputFolder), Is.False);
        }

        [Test]
        public void MultipleAuditDetectsSwappedSliceWithinSameGuid()
        {
            var spec = MultipleExample(out var slices); var prepared = Prepare(spec);
            var result = JObject.Parse(UiCompositionGenerator.GenerateJson(spec.ToString(), Confirm(prepared)));
            Assert.That((string)result["status"], Is.EqualTo("generated"), result.ToString());
            var root = PrefabUtility.LoadPrefabContents(prefabPath);
            try { root.transform.Find("Dialog").GetComponent<Image>().sprite = slices[1]; PrefabUtility.SaveAsPrefabAsset(root, prefabPath); }
            finally { PrefabUtility.UnloadPrefabContents(root); }
            var audit = UiCompositionAssetAuditor.AuditPrefab(spec.ToString());
            Assert.That(audit.complete, Is.True); Assert.That(audit.status, Is.EqualTo("failed"));
            Assert.That(audit.findings.Any(f => f.ruleId == "SPRITE_REFERENCE_MISMATCH"), Is.True);
        }

        [Test]
        public void MultipleReimportChangesInvalidateThePreviouslyConfirmedPlan()
        {
            var spec = MultipleExample(out var slices); var prepared = Prepare(spec);
            var importer = (TextureImporter)AssetImporter.GetAtPath(spritePath);
            importer.filterMode = importer.filterMode == FilterMode.Point ? FilterMode.Bilinear : FilterMode.Point; importer.SaveAndReimport();
            var fresh = Prepare(spec);
            Assert.That((string)fresh["status"], Is.EqualTo("ready_for_confirmation"), fresh.ToString());
            Assert.That((string)fresh["plan"]["planFingerprint"], Is.Not.EqualTo((string)prepared["plan"]["planFingerprint"]));
            var result = JObject.Parse(UiCompositionGenerator.GenerateJson(spec.ToString(), Confirm(prepared)));
            Assert.That((string)result["status"], Is.EqualTo("confirmation_required"), result.ToString());
            Assert.That(AssetDatabase.IsValidFolder(outputFolder), Is.False);
        }

        [Test]
        public void SingleCannotSilentlyAcceptMultipleSelectors()
        {
            var spec = Example(); var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(spritePath); SelectSlice(Dialog(spec), sprite);
            var prepared = Prepare(spec);
            Assert.That(prepared["errors"].Any(e => (string)e["code"] == "SPRITE_SUBASSET_UNEXPECTED"), Is.True);
        }

        [Test]
        public void PrepareChecksSpriteIdentityBorderAndCreatesNothing()
        {
            int roots = scene.rootCount; bool dirty = scene.isDirty;
            JObject prepared = Prepare();
            Assert.That((string)prepared["status"], Is.EqualTo("ready_for_confirmation"), prepared.ToString());
            Assert.That((bool)prepared["valid"], Is.True);
            Assert.That((bool)prepared["requiresConfirmation"], Is.True);
            Assert.That((bool)prepared["plan"]["overwriteAllowed"], Is.False);
            Assert.That((bool)prepared["plan"]["sceneSaved"], Is.False);
            Assert.That((string)prepared["plan"]["dependencies"].First["guid"], Is.Not.Null);
            Assert.That(scene.rootCount, Is.EqualTo(roots)); Assert.That(scene.isDirty, Is.EqualTo(dirty));
            Assert.That(AssetDatabase.IsValidFolder(outputFolder), Is.False);
        }

        [Test]
        public void PrepareReportsSimpleSpriteAspectStretchWithoutChangingSpec()
        {
            JObject spec = Example();
            JObject dialog = Dialog(spec);
            dialog["style"] = new JObject {
                ["spritePath"] = spritePath,
                ["spriteGuid"] = spriteGuid,
                ["imageType"] = "simple"
            };

            JObject prepared = Prepare(spec);
            Assert.That((string)prepared["status"], Is.EqualTo("ready_for_confirmation"), prepared.ToString());
            JArray risks = (JArray)prepared["plan"]["visualRisks"];
            Assert.That(risks.Count, Is.EqualTo(1));
            Assert.That((string)risks[0]["code"], Is.EqualTo("SIMPLE_SPRITE_STRETCH"));
            Assert.That((string)risks[0]["nodeId"], Is.EqualTo("Dialog"));
            Assert.That((double)risks[0]["aspectRatioDistortionFactor"], Is.GreaterThan(1.2));
            Assert.That((string)dialog["style"]["imageType"], Is.EqualTo("simple"));
            Assert.That((string)prepared["plan"]["spec"]["root"]["children"][0]["style"]["imageType"], Is.EqualTo("simple"));
        }

        [Test]
        public void PrepareDoesNotReportStretchRiskForSlicedSprites()
        {
            JObject prepared = Prepare();
            Assert.That((string)prepared["status"], Is.EqualTo("ready_for_confirmation"), prepared.ToString());
            Assert.That(((JArray)prepared["plan"]["visualRisks"]).Count, Is.Zero);
        }

        private JObject LayoutMeasurementExample(bool expand)
        {
            JObject spec = Example();
            JObject dialog = Dialog(spec);
            dialog["childrenLayout"]["childForceExpandWidth"] = expand;
            dialog["childrenLayout"]["childForceExpandHeight"] = expand;
            JObject child = Panel("MeasuredChild");
            child["placement"] = new JObject { ["mode"] = "layout", ["preferredSize"] = new JArray(1000, 600) };
            child["style"] = new JObject { ["spritePath"] = spritePath, ["spriteGuid"] = spriteGuid, ["imageType"] = "simple" };
            dialog["children"] = new JArray(child);
            return spec;
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NativeLayoutMeasurementUsesCompressedOrExpandedRectInsteadOfPreferredSize(bool expand)
        {
            JObject spec = LayoutMeasurementExample(expand);
            if (expand) spec["root"]["children"][0]["children"][0]["placement"]["preferredSize"] = new JArray(100, 50);
            int count = scene.rootCount; int previews = EditorSceneManager.previewSceneCount;
            JObject measured = JObject.Parse(UiCompositionGenerator.MeasureLayoutJson(spec.ToString()));
            Assert.That((string)measured["status"], Is.EqualTo("measured"), measured.ToString());
            JObject child = measured["layout"]["nodes"].Cast<JObject>().Single(n => (string)n["nodeId"] == "MeasuredChild");
            Assert.That((float)child["targetSize"][0], Is.EqualTo(688).Within(0.01));
            Assert.That((float)child["targetSize"][1], Is.EqualTo(388).Within(0.01));
            JObject prepared = Prepare(spec);
            Assert.That(JToken.DeepEquals(prepared["plan"]["layout"], measured["layout"]), Is.True);
            JToken risk = prepared["plan"]["visualRisks"].Single(n => (string)n["nodeId"] == "MeasuredChild");
            Assert.That(JToken.DeepEquals(risk["targetSize"], child["targetSize"]), Is.True);
            Assert.That(scene.rootCount, Is.EqualTo(count)); Assert.That(scene.isDirty, Is.False);
            Assert.That(EditorSceneManager.previewSceneCount, Is.EqualTo(previews));
        }

        [Test]
        public void MeasuredNativeLayoutMatchesGeneratedPrefabInstance()
        {
            JObject spec = LayoutMeasurementExample(true);
            JObject prepared = Prepare(spec);
            JObject generated = JObject.Parse(UiCompositionGenerator.GenerateJson(spec.ToString(), Confirm(prepared)));
            Assert.That((bool)generated["success"], Is.True, generated.ToString());
            JObject measured = JObject.Parse(UiCompositionGenerator.MeasureLayoutJson(spec.ToString()));
            Assert.That((string)measured["status"], Is.EqualTo("measured"), "Existing destination must not prevent read-only measurement.");
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath), scene);
            try
            {
                instance.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
                instance.GetComponent<CanvasScaler>().enabled = false;
                instance.GetComponent<RectTransform>().sizeDelta = new Vector2(1920, 1080);
                foreach (LayoutGroup group in instance.GetComponentsInChildren<LayoutGroup>(true).Reverse())
                    LayoutRebuilder.ForceRebuildLayoutImmediate(group.GetComponent<RectTransform>());
                foreach (JObject node in prepared["plan"]["layout"]["nodes"])
                {
                    Rect rect = instance.GetComponentsInChildren<UIAgent.Tools.UiNodeIdentity>(true)
                        .Single(n => n.Id == (string)node["nodeId"]).GetComponent<RectTransform>().rect;
                    Assert.That(rect.width, Is.EqualTo((float)node["targetSize"][0]).Within(0.01), (string)node["nodeId"]);
                    Assert.That(rect.height, Is.EqualTo((float)node["targetSize"][1]).Within(0.01), (string)node["nodeId"]);
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(instance); }
        }

        [Test]
        public void LayoutFingerprintTracksPpuAndAnchoredDescendantsUseActualParentSize()
        {
            JObject spec = LayoutMeasurementExample(true);
            JObject child = (JObject)Dialog(spec)["children"][0];
            JObject nested = Panel("Nested"); nested["placement"] = Anchors(true);
            nested["placement"]["sizeDelta"] = new JArray(-20, -10); child["children"] = new JArray(nested);
            JObject first = JObject.Parse(UiCompositionGenerator.MeasureLayoutJson(spec.ToString()));
            JToken nestedSize = first["layout"]["nodes"].Single(n => (string)n["nodeId"] == "Nested")["targetSize"];
            Assert.That((float)nestedSize[0], Is.EqualTo(668).Within(0.01));
            Assert.That((float)nestedSize[1], Is.EqualTo(378).Within(0.01));
            var importer = (TextureImporter)AssetImporter.GetAtPath(spritePath);
            importer.spritePixelsPerUnit = 200; importer.SaveAndReimport();
            JObject second = JObject.Parse(UiCompositionGenerator.MeasureLayoutJson(spec.ToString()));
            Assert.That((string)second["layout"]["layoutFingerprint"], Is.Not.EqualTo((string)first["layout"]["layoutFingerprint"]));
        }

        [Test]
        public void PrepareOperationsMatchPanelOnlySpriteSpec()
        {
            JObject spec = Example();
            JObject spritePanel = Panel("SelectedPanelBackground");
            spritePanel["style"] = new JObject {
                ["spritePath"] = spritePath,
                ["spriteGuid"] = spriteGuid,
                ["imageType"] = "sliced",
                ["border"] = new JArray(8, 8, 8, 8)
            };
            spec["root"]["children"] = new JArray(spritePanel);

            JObject prepared = Prepare(spec);
            string operations = string.Join(" ", prepared["plan"]["operations"].Select(value => (string)value));
            string limitations = string.Join(" ", prepared["plan"]["limitations"].Select(value => (string)value));
            string reportLimitations = string.Join(" ", prepared["limitations"].Select(value => (string)value));

            Assert.That((string)prepared["status"], Is.EqualTo("ready_for_confirmation"), prepared.ToString());
            Assert.That(operations, Does.Contain("2 UI nodes (Panel: 2)"));
            Assert.That(operations, Does.Contain("1 declared Sprite reference"));
            Assert.That(operations, Does.Not.Contain("Text"));
            Assert.That(operations, Does.Not.Contain("Button"));
            Assert.That(operations, Does.Not.Contain("Toggle"));
            Assert.That(operations, Does.Not.Contain("TMP material"));
            Assert.That(limitations, Does.Not.Contain("Button"));
            Assert.That(limitations, Does.Not.Contain("Toggle"));
            Assert.That(reportLimitations, Does.Not.Contain("Button"));
            Assert.That(reportLimitations, Does.Not.Contain("Toggle"));
        }

        [Test]
        public void PrepareOperationsDescribeToggleAndTextEffectsWhenPresent()
        {
            JObject spec = Example();
            AddToggle(spec);
            ((JObject)Dialog(spec)["children"][0]["style"])["outlineColor"] = "#000000FF";
            ((JObject)Dialog(spec)["children"][0]["style"])["outlineWidth"] = 0.1;

            JObject prepared = Prepare(spec);
            string operations = string.Join(" ", prepared["plan"]["operations"].Select(value => (string)value));
            string limitations = string.Join(" ", prepared["plan"]["limitations"].Select(value => (string)value));
            string reportLimitations = string.Join(" ", prepared["limitations"].Select(value => (string)value));

            Assert.That((string)prepared["status"], Is.EqualTo("ready_for_confirmation"), prepared.ToString());
            Assert.That(operations, Does.Contain("Panel: 2"));
            Assert.That(operations, Does.Contain("Text: 4"));
            Assert.That(operations, Does.Contain("Button: 1"));
            Assert.That(operations, Does.Contain("Toggle: 1"));
            Assert.That(operations, Does.Contain("initial states and indicator references"));
            Assert.That(operations, Does.Contain("1 independent TMP material asset"));
            Assert.That(limitations, Does.Contain("Generated Buttons have no game or application callbacks"));
            Assert.That(limitations, Does.Contain("Toggles change only their native Unity UI value"));
            Assert.That(reportLimitations, Does.Contain("Generated Buttons have no game or application callbacks"));
            Assert.That(reportLimitations, Does.Contain("Toggles change only their native Unity UI value"));
        }

        [Test]
        public void ConfirmationDialogPrefabStoresReferencesAndRestoresFocusOnCancelAndConfirm()
        {
            JObject spec = ConfirmationExample();
            JObject prepared = Prepare(spec);
            Assert.That((string)prepared["status"], Is.EqualTo("ready_for_confirmation"), prepared.ToString());
            string operations = string.Join(" ", prepared["plan"]["operations"].Select(value => (string)value));
            Assert.That(operations, Does.Contain("full-screen raycast blocker"));
            Assert.That(operations, Does.Contain("serialized Confirm/Cancel UnityEvents"));

            JObject generated = JObject.Parse(UiCompositionGenerator.GenerateJson(spec.ToString(), Confirm(prepared)));
            Assert.That((string)generated["status"], Is.EqualTo("generated"), generated.ToString());
            Assert.That((string)generated["prefabPath"], Is.EqualTo(prefabPath));
            JObject audit = JObject.Parse(UiCompositionAssetAuditor.AuditPrefabJson(spec.ToString()));
            UnityEngine.Debug.Log("SLIDER_AUDIT:" + audit.ToString(Newtonsoft.Json.Formatting.None));
            Assert.That((string)audit["status"], Is.EqualTo("passed"), audit.ToString());

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath), scene);
            Assert.That(instance.activeSelf, Is.False);
            var dialog = instance.GetComponent<UIAgent.Tools.ConfirmationDialog>();
            Assert.That(dialog, Is.Not.Null);
            Assert.That(dialog.BackgroundBlocker.GetComponent<Image>().raycastTarget, Is.True);
            Assert.That(dialog.BackgroundBlocker.transition, Is.EqualTo(Selectable.Transition.None));
            Assert.That(dialog.OnCancelled.GetPersistentEventCount(), Is.Zero);
            Assert.That(dialog.OnConfirmed.GetPersistentEventCount(), Is.Zero);

            var eventSystemObject = new GameObject("TestEventSystem", typeof(EventSystem));
            SceneManager.MoveGameObjectToScene(eventSystemObject, scene);
            var eventSystem = eventSystemObject.GetComponent<EventSystem>();
            var previous = new GameObject("PreviousSelection", typeof(RectTransform), typeof(Image), typeof(Button));
            SceneManager.MoveGameObjectToScene(previous, scene);
            int cancelled = 0, confirmed = 0, underlyingClicks = 0;
            previous.GetComponent<Button>().onClick.AddListener(() => underlyingClicks++);
            dialog.OnCancelled.AddListener(() => cancelled++);
            dialog.OnConfirmed.AddListener(() => confirmed++);
            eventSystem.SetSelectedGameObject(previous);
            Navigation originalCancelNavigation = dialog.CancelButton.navigation;
            Navigation originalConfirmNavigation = dialog.ConfirmButton.navigation;
            int originalSortingOrder = instance.GetComponent<Canvas>().sortingOrder;
            bool originalOverrideSorting = instance.GetComponent<Canvas>().overrideSorting;

            Assert.That(dialog.Open(eventSystem), Is.True);
            Assert.That(instance.activeSelf, Is.True);
            Assert.That(eventSystem.currentSelectedGameObject, Is.EqualTo(dialog.InitialFocus.gameObject));
            Assert.That(instance.GetComponent<Canvas>().sortingOrder, Is.EqualTo(short.MaxValue));
            Assert.That(dialog.CancelButton.navigation.mode, Is.EqualTo(Navigation.Mode.Explicit));
            Assert.That(dialog.ConfirmButton.navigation.mode, Is.EqualTo(Navigation.Mode.Explicit));
            Assert.That(dialog.ConfirmButton.navigation.selectOnLeft, Is.EqualTo(dialog.CancelButton));
            Assert.That(dialog.CancelButton.navigation.selectOnRight, Is.EqualTo(dialog.ConfirmButton));

            var moveLeft = new AxisEventData(eventSystem) { moveVector = Vector2.left, moveDir = MoveDirection.Left };
            ExecuteEvents.Execute(eventSystem.currentSelectedGameObject, moveLeft, ExecuteEvents.moveHandler);
            Assert.That(eventSystem.currentSelectedGameObject, Is.EqualTo(dialog.CancelButton.gameObject), "Left navigation from the initial Confirm focus must reach Cancel.");
            var moveRight = new AxisEventData(eventSystem) { moveVector = Vector2.right, moveDir = MoveDirection.Right };
            ExecuteEvents.Execute(eventSystem.currentSelectedGameObject, moveRight, ExecuteEvents.moveHandler);
            Assert.That(eventSystem.currentSelectedGameObject, Is.EqualTo(dialog.ConfirmButton.gameObject), "Right navigation must return focus to Confirm.");

            dialog.BackgroundBlocker.onClick.Invoke();
            Assert.That(instance.activeSelf, Is.True, "Backdrop click must be consumed without closing the dialog.");
            Assert.That(underlyingClicks, Is.Zero, "Backdrop must not forward a click to underlying controls.");
            dialog.Cancel();
            Assert.That(cancelled, Is.EqualTo(1));
            Assert.That(instance.activeSelf, Is.False);
            Assert.That(eventSystem.currentSelectedGameObject, Is.EqualTo(previous));
            Assert.That(instance.GetComponent<Canvas>().sortingOrder, Is.EqualTo(originalSortingOrder));
            Assert.That(instance.GetComponent<Canvas>().overrideSorting, Is.EqualTo(originalOverrideSorting));
            Assert.That(dialog.CancelButton.navigation.mode, Is.EqualTo(originalCancelNavigation.mode));
            Assert.That(dialog.ConfirmButton.navigation.mode, Is.EqualTo(originalConfirmNavigation.mode));

            Assert.That(dialog.Open(eventSystem), Is.True);
            dialog.Confirm();
            Assert.That(confirmed, Is.EqualTo(1));
            Assert.That(instance.activeSelf, Is.False);
            Assert.That(eventSystem.currentSelectedGameObject, Is.EqualTo(previous));

            for (int cycle = 0; cycle < 3; cycle++)
            {
                Assert.That(dialog.Open(eventSystem), Is.True, $"Cancel cycle {cycle} should reopen.");
                dialog.Cancel();
                Assert.That(cancelled, Is.EqualTo(cycle + 2), "Cancel callback must be invoked once per click after repeated enable/disable cycles.");

                Assert.That(dialog.Open(eventSystem), Is.True, $"Confirm cycle {cycle} should reopen.");
                dialog.Confirm();
                Assert.That(confirmed, Is.EqualTo(cycle + 2), "Confirm callback must be invoked once per click after repeated enable/disable cycles.");
                Assert.That(eventSystem.currentSelectedGameObject, Is.EqualTo(previous));
            }

            GameObject editing = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                var serialized = new SerializedObject(editing.GetComponent<UIAgent.Tools.ConfirmationDialog>());
                serialized.FindProperty("cancelButton").objectReferenceValue = editing.transform.Find("Backdrop").GetComponent<Button>();
                serialized.ApplyModifiedPropertiesWithoutUndo();
                PrefabUtility.SaveAsPrefabAsset(editing, prefabPath);
            }
            finally { PrefabUtility.UnloadPrefabContents(editing); }
            JObject brokenAudit = JObject.Parse(UiCompositionAssetAuditor.AuditPrefabJson(spec.ToString()));
            Assert.That((string)brokenAudit["status"], Is.EqualTo("failed"), brokenAudit.ToString());
            Assert.That(brokenAudit["findings"].Any(f => (string)f["ruleId"] == "DIALOG_REFERENCE_MISMATCH"), Is.True);
        }

        [Test]
        public void PrepareDoesNotDirtyOrChangeANonEmptyCleanScene()
        {
            var sentinel = new GameObject("PreexistingSceneRoot");
            SceneManager.MoveGameObjectToScene(sentinel, scene);
            Assert.That(EditorSceneManager.SaveScene(scene), Is.True);
            int roots = scene.rootCount;
            int sceneHandle = scene.handle;

            JObject prepared = Prepare();

            Assert.That((string)prepared["status"], Is.EqualTo("ready_for_confirmation"), prepared.ToString());
            Assert.That(SceneManager.GetActiveScene().handle, Is.EqualTo(sceneHandle));
            Assert.That(scene.rootCount, Is.EqualTo(roots));
            Assert.That(scene.isDirty, Is.False);
            Assert.That(AssetDatabase.IsValidFolder(outputFolder), Is.False);
        }

        [Test]
        public void ConfirmedGenerationDoesNotDirtyOrChangeANonEmptyCleanScene()
        {
            var sentinel = new GameObject("PreexistingSceneRoot");
            SceneManager.MoveGameObjectToScene(sentinel, scene);
            Assert.That(EditorSceneManager.SaveScene(scene), Is.True);
            int roots = scene.rootCount;
            int sceneHandle = scene.handle;

            JObject prepared = Prepare();
            JObject result = JObject.Parse(UiCompositionGenerator.GenerateJson(
                Example().ToString(), Confirm(prepared)));

            Assert.That((string)result["status"], Is.EqualTo("generated"), result.ToString());
            Assert.That(SceneManager.GetActiveScene().handle, Is.EqualTo(sceneHandle));
            Assert.That(scene.rootCount, Is.EqualTo(roots));
            Assert.That(scene.isDirty, Is.False);
            Assert.That(AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath), Is.Not.Null);
        }

        [Test]
        public void MissingFalseOrStaleConfirmationCannotWrite()
        {
            JObject spec = Example(); JObject plan = Prepare(spec);
            JObject missing = JObject.Parse(UiCompositionGenerator.GenerateJson(spec.ToString(), null));
            Assert.That((string)missing["status"], Is.EqualTo("confirmation_required"));
            var denied = Confirm(plan); denied.userConfirmed = false;
            JObject notConfirmed = JObject.Parse(UiCompositionGenerator.GenerateJson(spec.ToString(), denied));
            Assert.That((string)notConfirmed["status"], Is.EqualTo("confirmation_required"));
            var stale = Confirm(plan); stale.planFingerprint = new string('0', 64);
            JObject mismatch = JObject.Parse(UiCompositionGenerator.GenerateJson(spec.ToString(), stale));
            Assert.That((string)mismatch["status"], Is.EqualTo("confirmation_required"));
            Assert.That(AssetDatabase.IsValidFolder(outputFolder), Is.False);
            Assert.That(scene.rootCount, Is.Zero);
        }

        [Test]
        public void ConfirmedGenerationPersistsHierarchyLayoutsAndSlicedReferenceOnlyAsPrefab()
        {
            JObject spec = Example(); JObject prepared = Prepare(spec);
            JObject result = JObject.Parse(UiCompositionGenerator.GenerateJson(spec.ToString(), Confirm(prepared)));
            Assert.That((string)result["status"], Is.EqualTo("generated"), string.Join("; ", result["errors"].Select(e => (string)e["code"] + ":" + (string)e["message"])));
            Assert.That((bool)result["success"], Is.True);
            Assert.That((string)result["prefabPath"], Is.EqualTo(prefabPath));
            Assert.That((bool)result["sceneSaved"], Is.False);
            Assert.That(scene.rootCount, Is.Zero);
            Assert.That(scene.isDirty, Is.False);
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            Assert.That(prefab, Is.Not.Null);
            Assert.That(prefab.GetComponent<UiNodeIdentity>().Id, Is.EqualTo("Screen"));
            Assert.That(prefab.GetComponentsInChildren<UiNodeIdentity>(true).Select(i => i.Id).Distinct().Count(), Is.EqualTo(5));
            Assert.That(AssetDatabase.AssetPathToGUID(prefabPath), Is.EqualTo((string)result["prefabGuid"]));
            Assert.That(prefab.GetComponent<CanvasScaler>().referenceResolution, Is.EqualTo(new Vector2(1920, 1080)));
            Assert.That(prefab.GetComponent<GraphicRaycaster>(), Is.Not.Null);
            var dialog = prefab.transform.Find("Dialog");
            Assert.That(dialog, Is.Not.Null);
            Assert.That(dialog.GetComponent<VerticalLayoutGroup>().spacing, Is.EqualTo(16));
            var image = dialog.GetComponent<Image>();
            Assert.That(image.sprite, Is.EqualTo(AssetDatabase.LoadAssetAtPath<Sprite>(spritePath)));
            Assert.That(image.type, Is.EqualTo(Image.Type.Sliced));
            Assert.That(image.sprite.border, Is.EqualTo(new Vector4(8, 8, 8, 8)));
            var button = dialog.Find("ConfirmButton").GetComponent<Button>();
            Assert.That(button, Is.Not.Null);
            Assert.That(button.onClick.GetPersistentEventCount(), Is.Zero);
            Assert.That(button.transform.Find("ConfirmLabel").GetComponent<TextMeshProUGUI>().font, Is.EqualTo(AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontPath)));
        }

        [Test]
        public void ToggleGenerationPersistsStateGraphicAndNoBusinessCallbacks()
        {
            JObject spec = Example();
            AddToggle(spec);
            JObject prepared = Prepare(spec);
            Assert.That((string)prepared["status"], Is.EqualTo("ready_for_confirmation"), prepared.ToString());
            JObject generated = JObject.Parse(UiCompositionGenerator.GenerateJson(spec.ToString(), Confirm(prepared)));
            Assert.That((string)generated["status"], Is.EqualTo("generated"), generated.ToString());

            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            Toggle toggle = prefab.transform.Find("Dialog/MusicToggle").GetComponent<Toggle>();
            Assert.That(toggle, Is.Not.Null);
            Assert.That(toggle.isOn, Is.True);
            Assert.That(toggle.interactable, Is.True);
            Assert.That(toggle.targetGraphic, Is.EqualTo(toggle.GetComponent<Image>()));
            Assert.That(toggle.graphic, Is.EqualTo(prefab.transform.Find("Dialog/MusicToggle/MusicIndicator").GetComponent<TextMeshProUGUI>()));
            Assert.That(toggle.onValueChanged.GetPersistentEventCount(), Is.Zero);

            JObject audit = JObject.Parse(UiCompositionAssetAuditor.AuditPrefabJson(spec.ToString()));
            Assert.That((string)audit["status"], Is.EqualTo("passed"), audit.ToString());
            Assert.That((int)audit["comparedNodes"], Is.EqualTo(8));
        }

        [Test]
        public void SliderGenerationPersistsRangeVisualReferencesAndNoBusinessCallbacks()
        {
            JObject spec = Example();
            JObject slider = Panel("MusicSlider");
            slider["type"] = "Slider";
            slider["placement"] = new JObject { ["mode"] = "layout", ["preferredSize"] = new JArray(320, 48) };
            JObject fill = Panel("MusicFill"); fill["placement"] = Anchors(true);
            JObject handle = Panel("MusicHandle"); handle["placement"] = Anchors(true);
            slider["children"] = new JArray(fill, handle);
            slider["slider"] = new JObject { ["minValue"] = 0, ["maxValue"] = 100, ["initialValue"] = 65,
                ["wholeNumbers"] = false, ["fillId"] = "MusicFill", ["handleId"] = "MusicHandle" };
            ((JArray)Dialog(spec)["children"]).Add(slider);
            JObject prepared = Prepare(spec);
            Assert.That((string)prepared["status"], Is.EqualTo("ready_for_confirmation"), prepared.ToString());
            string planOperations = string.Join(" ", prepared["plan"]["operations"].Select(value => (string)value));
            Assert.That(planOperations, Does.Contain("Slider: 1"));
            Assert.That(planOperations, Does.Contain("range, initial value, Fill/Handle references"));
            Assert.That(planOperations, Does.Contain("Slider range, value, and Fill/Handle references"));
            JObject generated = JObject.Parse(UiCompositionGenerator.GenerateJson(spec.ToString(), Confirm(prepared)));
            Assert.That((string)generated["status"], Is.EqualTo("generated"), generated.ToString());

            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            Slider actual = prefab.transform.Find("Dialog/MusicSlider").GetComponent<Slider>();
            Assert.That(actual.minValue, Is.EqualTo(0));
            Assert.That(actual.maxValue, Is.EqualTo(100));
            Assert.That(actual.value, Is.EqualTo(65));
            Assert.That(actual.fillRect, Is.EqualTo(prefab.transform.Find("Dialog/MusicSlider/MusicFill")));
            Assert.That(actual.handleRect, Is.EqualTo(prefab.transform.Find("Dialog/MusicSlider/MusicHandle")));
            Assert.That(actual.onValueChanged.GetPersistentEventCount(), Is.Zero);
            JObject audit = JObject.Parse(UiCompositionAssetAuditor.AuditPrefabJson(spec.ToString()));
            Assert.That((string)audit["status"], Is.EqualTo("passed"), audit["findings"].ToString(Newtonsoft.Json.Formatting.None));
        }

        [Test]
        public void LegacyTextWithoutEffectsKeepsSharedFontMaterial()
        {
            JObject spec = Example(); JObject prepared = Prepare(spec);
            JObject result = JObject.Parse(UiCompositionGenerator.GenerateJson(spec.ToString(), Confirm(prepared)));
            Assert.That((string)result["status"], Is.EqualTo("generated"), result.ToString());
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            TMP_FontAsset font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontPath);
            TextMeshProUGUI label = prefab.GetComponentsInChildren<TextMeshProUGUI>(true).Single(text => text.text == "Confirm");
            Assert.That(label.fontSharedMaterial, Is.EqualTo(font.material));
        }

        [Test]
        public void TextEffectsCreateIndependentAssetsAndReadBackAfterReload()
        {
            JObject spec = Example();
            JObject headingStyle = (JObject)Dialog(spec)["children"][0]["style"];
            headingStyle["outlineColor"] = "#000000FF";
            headingStyle["outlineWidth"] = 0.1;
            headingStyle["shadowColor"] = "#00000080";
            headingStyle["shadowOffset"] = new JArray(0.5, -0.5);
            JObject labelStyle = (JObject)Button(spec)["children"][0]["style"];
            labelStyle["outlineColor"] = "#112233FF";
            labelStyle["outlineWidth"] = 0.05;
            labelStyle["shadowColor"] = "#22334499";
            labelStyle["shadowOffset"] = new JArray(-0.25, 0.25);

            TMP_FontAsset font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontPath);
            Assert.That(font.material, Is.Not.Null);
            Assert.That(font.material.HasProperty("_OutlineColor"), Is.True);
            Color sourceOutline = font.material.GetColor("_OutlineColor");
            float sourceOutlineWidth = font.material.GetFloat("_OutlineWidth");
            Color sourceUnderlay = font.material.GetColor("_UnderlayColor");
            float sourceUnderlayX = font.material.GetFloat("_UnderlayOffsetX");
            float sourceUnderlayY = font.material.GetFloat("_UnderlayOffsetY");
            JObject prepared = Prepare(spec);
            JObject result = JObject.Parse(UiCompositionGenerator.GenerateJson(spec.ToString(), Confirm(prepared)));
            Assert.That((string)result["status"], Is.EqualTo("generated"), result.ToString());

            string headingMaterialPath = outputFolder + "/PF_Dialog__Heading.mat";
            string labelMaterialPath = outputFolder + "/PF_Dialog__ConfirmLabel.mat";
            Material headingMaterial = AssetDatabase.LoadAssetAtPath<Material>(headingMaterialPath);
            Material labelMaterial = AssetDatabase.LoadAssetAtPath<Material>(labelMaterialPath);
            Assert.That(headingMaterial, Is.Not.Null);
            Assert.That(labelMaterial, Is.Not.Null);
            Assert.That(headingMaterial, Is.Not.EqualTo(labelMaterial));
            Assert.That(headingMaterial.GetColor("_OutlineColor"), Is.EqualTo(new Color(0, 0, 0, 1)));
            Assert.That(headingMaterial.GetFloat("_OutlineWidth"), Is.EqualTo(0.1f).Within(0.0001f));
            Color underlay = headingMaterial.GetColor("_UnderlayColor");
            Assert.That(underlay.r, Is.EqualTo(0).Within(0.0001f));
            Assert.That(underlay.g, Is.EqualTo(0).Within(0.0001f));
            Assert.That(underlay.b, Is.EqualTo(0).Within(0.0001f));
            Assert.That(underlay.a, Is.EqualTo(0.5019608f).Within(0.0001f));
            Assert.That(headingMaterial.GetFloat("_UnderlayOffsetX"), Is.EqualTo(0.5f).Within(0.0001f));
            Assert.That(headingMaterial.GetFloat("_UnderlayOffsetY"), Is.EqualTo(-0.5f).Within(0.0001f));
            Assert.That(font.material.GetColor("_OutlineColor"), Is.EqualTo(sourceOutline));
            Assert.That(font.material.GetFloat("_OutlineWidth"), Is.EqualTo(sourceOutlineWidth).Within(0.0001f));
            Color sourceUnderlayAfter = font.material.GetColor("_UnderlayColor");
            Assert.That(sourceUnderlayAfter.r, Is.EqualTo(sourceUnderlay.r).Within(0.0001f));
            Assert.That(sourceUnderlayAfter.g, Is.EqualTo(sourceUnderlay.g).Within(0.0001f));
            Assert.That(sourceUnderlayAfter.b, Is.EqualTo(sourceUnderlay.b).Within(0.0001f));
            Assert.That(sourceUnderlayAfter.a, Is.EqualTo(sourceUnderlay.a).Within(0.0001f));
            Assert.That(font.material.GetFloat("_UnderlayOffsetX"), Is.EqualTo(sourceUnderlayX).Within(0.0001f));
            Assert.That(font.material.GetFloat("_UnderlayOffsetY"), Is.EqualTo(sourceUnderlayY).Within(0.0001f));

            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            TextMeshProUGUI heading = prefab.transform.Find("Dialog/Heading").GetComponent<TextMeshProUGUI>();
            TextMeshProUGUI label = prefab.transform.Find("Dialog/ConfirmButton/ConfirmLabel").GetComponent<TextMeshProUGUI>();
            Assert.That(heading.fontSharedMaterial, Is.EqualTo(headingMaterial));
            Assert.That(label.fontSharedMaterial, Is.EqualTo(labelMaterial));
            Assert.That(heading.fontSharedMaterial, Is.Not.EqualTo(label.fontSharedMaterial));
        }


        [Test]
        public void ChangedSpriteIdentityAndImporterBorderInvalidatePreparation()
        {
            JObject spec = Example(); spec["root"]["children"][0]["style"]["spriteGuid"] = BorderGuid;
            Assert.That(Prepare(spec)["errors"].Any(e => (string)e["code"] == "SPRITE_GUID_MISMATCH"), Is.True);
            spec = Example(); spec["root"]["children"][0]["style"]["border"] = new JArray(4, 8, 8, 8);
            Assert.That(Prepare(spec)["errors"].Any(e => (string)e["code"] == "SPRITE_BORDER_MISMATCH"), Is.True);
        }

        [Test]
        public void PrepareAcceptsUnityNativeSpriteMainAssets()
        {
            string path = testFolder + "/NativeFrame.asset";
            var texture = new Texture2D(64, 64, TextureFormat.RGBA32, false);
            texture.SetPixels(Enumerable.Repeat(Color.white, 64 * 64).ToArray());
            texture.Apply(false, false);
            AssetDatabase.CreateAsset(texture, path);
            var sprite = Sprite.Create(texture, new Rect(0, 0, 64, 64), new Vector2(0.5f, 0.5f), 100f, 0,
                SpriteMeshType.FullRect, new Vector4(8, 8, 8, 8));
            sprite.name = "NativeFrame";
            AssetDatabase.AddObjectToAsset(sprite, path);
            AssetDatabase.SetMainObject(sprite, path);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            Assert.That(AssetDatabase.LoadMainAssetAtPath(path), Is.TypeOf<Sprite>());

            JObject spec = Example();
            JObject style = (JObject)spec["root"]["children"][0]["style"];
            style["spritePath"] = path;
            style["spriteGuid"] = AssetDatabase.AssetPathToGUID(path);
            style["border"] = new JArray(8, 8, 8, 8);
            JObject prepared = Prepare(spec);
            Assert.That((string)prepared["status"], Is.EqualTo("ready_for_confirmation"), prepared.ToString());
        }

        [Test]
        public void BorderOutsideSpriteRectAndMissingSpritesAreBlocked()
        {
            JObject spec = Example(); spec["root"]["children"][0]["style"]["border"] = new JArray(32, 8, 32, 8);
            Assert.That(Prepare(spec)["errors"].Any(e => (string)e["code"] == "SPRITE_BORDER_EXCEEDS_RECT"), Is.True);
            spec = Example(); spec["root"]["children"][0]["style"]["spritePath"] = "Assets/Missing.png";
            spec["root"]["children"][0]["style"]["spriteGuid"] = BorderGuid;
            var result = Prepare(spec);
            Assert.That((string)result["status"], Is.EqualTo("blocked"));
            Assert.That(result["plan"].Type, Is.EqualTo(JTokenType.Null));
        }

        [Test]
        public void ExistingDestinationIsNeverOverwritten()
        {
            JObject spec = Example();
            AssetDatabase.CreateFolder(UiSpecValidator.OutputPrefix.TrimEnd('/'), "CompositionTest_" + id);
            string targetFolder = outputFolder;
            AssetDatabase.CreateFolder(targetFolder, "PF_Dialog.prefab");
            JObject result = Prepare(spec);
            Assert.That(result["errors"].Any(e => (string)e["code"] == "OUTPUT_ALREADY_EXISTS"), Is.True, result.ToString());
            Assert.That(AssetDatabase.IsValidFolder(targetFolder + "/PF_Dialog.prefab"), Is.True);
        }

        [TestCase("before_save")]
        [TestCase("saved")]
        public void FailureOnlyCleansTheOwnedOutput(string failAt)
        {
            JObject spec = Example(); JObject plan = Prepare(spec); var confirmation = Confirm(plan);
            var result = UiCompositionGenerator.Generate(spec.ToString(), confirmation, point => {
                if (point == failAt) throw new InvalidOperationException("Injected test failure at " + point);
            });
            Assert.That(result.success, Is.False);
            Assert.That(result.status, Is.EqualTo("failed_rolled_back"), string.Join(";", result.cleanupErrors));
            Assert.That(AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath), Is.Null);
            Assert.That(AssetDatabase.IsValidFolder(outputFolder), Is.False);
            Assert.That(scene.rootCount, Is.Zero);
            Assert.That(scene.isDirty, Is.False);
        }

        [Test]
        public void PlanFingerprintTracksAssetContentAndOutputPath()
        {
            JObject spec = Example(); string first = (string)Prepare(spec)["plan"]["planFingerprint"];
            var importer = (TextureImporter)AssetImporter.GetAtPath(spritePath);
            importer.filterMode = FilterMode.Point; importer.SaveAndReimport();
            string second = (string)Prepare(spec)["plan"]["planFingerprint"];
            Assert.That(second, Is.Not.EqualTo(first));
            spec["outputPrefabPath"] = outputFolder + "/PF_Other.prefab";
            Assert.That((string)Prepare(spec)["plan"]["planFingerprint"], Is.Not.EqualTo(first));
        }

        [Test]
        public void DirtyOrUnsavedSceneIsNeverSavedForAPlan()
        {
            EditorSceneManager.MarkSceneDirty(scene);
            JObject dirty = Prepare();
            Assert.That(dirty["errors"].Any(e => (string)e["code"] == "SCENE_DIRTY"), Is.True);
            Assert.That(scene.isDirty, Is.True);
            Scene untitled = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            SceneManager.SetActiveScene(untitled);
            JObject unsaved = Prepare();
            Assert.That(unsaved["errors"].Any(e => (string)e["code"] == "SCENE_REQUIRED"), Is.True);
            EditorSceneManager.CloseScene(untitled, true);
            SceneManager.SetActiveScene(scene);
        }
    }
}
