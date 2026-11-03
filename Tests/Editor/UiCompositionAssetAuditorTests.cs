using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace UIAgent.Tools.Editor.Tests
{
    public class UiCompositionAssetAuditorTests
    {
        private string folder, prefabPath, spritePath, otherPath;
        private JObject spec;

        [SetUp]
        public void SetUp()
        {
            string name = "AssetAuditTest_" + Guid.NewGuid().ToString("N");
            folder = "Assets/AgentDemos/UIAgent/Prefabs/" + name;
            TestAssetFolders.Ensure(folder);
            prefabPath = folder + "/PF_Test.prefab";
            spritePath = folder + "/Frame.png";
            otherPath = folder + "/Other.png";
            WriteSprite(spritePath, Color.white);
            WriteSprite(otherPath, Color.blue);
            var scene = EditorSceneManager.NewPreviewScene();
            var root = new GameObject("RenamableRoot", typeof(RectTransform));
            var rootRect = root.GetComponent<RectTransform>();
            rootRect.anchorMin = new Vector2(0.5f, 0.5f); rootRect.anchorMax = Vector2.one;
            rootRect.pivot = new Vector2(0.5f, 0.5f); rootRect.anchoredPosition = Vector2.zero; rootRect.sizeDelta = Vector2.zero;
            SceneManager.MoveGameObjectToScene(root, scene);
            root.AddComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = root.AddComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080); scaler.matchWidthOrHeight = 0.5f;
            root.AddComponent<GraphicRaycaster>();
            try
            {
                SetId(root, "Screen");
                root.AddComponent<Image>().raycastTarget = false;
                var artwork = Node(root, "Artwork");
                var image = artwork.AddComponent<Image>();
                image.sprite = AssetDatabase.LoadAssetAtPath<Sprite>(spritePath);
                image.type = Image.Type.Sliced; image.raycastTarget = false;
                AddText(Node(root, "Caption"), "Test").raycastTarget = false;
                var action = Node(root, "Action");
                var graphic = action.AddComponent<Image>(); graphic.raycastTarget = true;
                action.AddComponent<Button>().targetGraphic = graphic;
                GameObject labelNode = Node(action, "ActionLabel");
                labelNode.GetComponent<RectTransform>().anchorMin = Vector2.zero;
                AddText(labelNode, "Confirm").raycastTarget = false;
                foreach (RectTransform rect in root.GetComponentsInChildren<RectTransform>())
                {
                    if (rect == root.transform) continue;
                    rect.anchorMin = rect.gameObject == labelNode ? Vector2.zero : new Vector2(0.5f, 0.5f);
                    rect.anchorMax = Vector2.one; rect.pivot = new Vector2(0.5f, 0.5f);
                    rect.anchoredPosition = Vector2.zero; rect.sizeDelta = Vector2.zero;
                }
                Assert.That(PrefabUtility.SaveAsPrefabAsset(root, prefabPath), Is.Not.Null);
            }
            finally { EditorSceneManager.ClosePreviewScene(scene); }
            var screen = SpecNode("Screen", "Panel");
            var art = SpecNode("Artwork", "Panel");
            art["style"] = new JObject { ["spritePath"] = spritePath, ["spriteGuid"] = AssetDatabase.AssetPathToGUID(spritePath),
                ["imageType"] = "sliced", ["border"] = new JArray(8, 8, 8, 8) };
            var actionNode = SpecNode("Action", "Button");
            var actionLabel = SpecNode("ActionLabel", "Text"); actionLabel["placement"]["anchorMin"] = new JArray(0, 0);
            actionLabel["text"] = "Confirm"; actionNode["children"] = new JArray(actionLabel);
            screen["children"] = new JArray(art, SpecNode("Caption", "Text"), actionNode);
            var font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>("Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF.asset");
            spec = new JObject { ["schemaVersion"] = 2, ["screenType"] = "composition", ["targetPlatform"] = "desktop",
                ["referenceResolution"] = new JObject { ["width"] = 1920, ["height"] = 1080 },
                ["fontAssetPath"] = AssetDatabase.GetAssetPath(font), ["outputPrefabPath"] = prefabPath, ["root"] = screen };
            Assert.That(font, Is.Not.Null);
            Assert.That(UiCompositionSpecValidator.Validate(spec.ToString()).valid, Is.True,
                UiCompositionSpecValidator.ValidateJson(spec.ToString()));
        }

        [TearDown]
        public void TearDown()
        {
            if (AssetDatabase.IsValidFolder(folder)) Assert.That(AssetDatabase.DeleteAsset(folder), Is.True);
            TestAssetFolders.CleanupCreatedEmptyFolders();
        }

        private static void WriteSprite(string path, Color color)
        {
            var texture = new Texture2D(48, 48);
            texture.SetPixels(Enumerable.Repeat(color, 48 * 48).ToArray()); texture.Apply();
            try { File.WriteAllBytes(path, texture.EncodeToPNG()); }
            finally { UnityEngine.Object.DestroyImmediate(texture); }
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.textureType = TextureImporterType.Sprite; importer.spriteImportMode = SpriteImportMode.Single;
            importer.spriteBorder = new Vector4(8, 8, 8, 8); importer.mipmapEnabled = false;
            importer.textureCompression = TextureImporterCompression.Uncompressed; importer.SaveAndReimport();
        }

        private static GameObject Node(GameObject root, string id)
        {
            var node = new GameObject(id, typeof(RectTransform));
            node.transform.SetParent(root.transform, false);
            var rect = node.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f); rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f); rect.anchoredPosition = Vector2.zero; rect.sizeDelta = Vector2.zero;
            SetId(node, id); return node;
        }

        private static void SetId(GameObject node, string id)
        {
            var identity = node.GetComponent<UIAgent.Tools.UiNodeIdentity>();
            if (identity == null) identity = node.AddComponent<UIAgent.Tools.UiNodeIdentity>();
            var so = new SerializedObject(identity); so.FindProperty("id").stringValue = id; so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static TextMeshProUGUI AddText(GameObject node, string value)
        {
            var text = node.AddComponent<TextMeshProUGUI>();
            text.font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>("Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF.asset");
            text.fontSize = 24; text.text = value; text.raycastTarget = false;
            return text;
        }

        private static JObject SpecNode(string id, string type)
        {
            var result = new JObject { ["id"] = id, ["type"] = type,
                ["placement"] = new JObject { ["mode"] = "anchors", ["anchorMin"] = new JArray(0.5, 0.5),
                    ["anchorMax"] = new JArray(1, 1), ["pivot"] = new JArray(0.5, 0.5),
                    ["anchoredPosition"] = new JArray(0, 0), ["sizeDelta"] = new JArray(0, 0) },
                ["childrenLayout"] = new JObject { ["mode"] = "none" }, ["style"] = new JObject { ["color"] = "#FFFFFF" } };
            if (type == "Text") { result["text"] = id == "ActionLabel" ? "Confirm" : "Test"; result["style"]["fontSize"] = 24; result["style"]["alignment"] = "center"; }
            return result;
        }

        private void Edit(Action<GameObject> action)
        {
            GameObject root = PrefabUtility.LoadPrefabContents(prefabPath);
            try { action(root); PrefabUtility.SaveAsPrefabAsset(root, prefabPath); }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        private UiCompositionAssetAuditor.Report Audit() => UiCompositionAssetAuditor.AuditPrefab(spec.ToString());

        [TestCase("valid")]
        [TestCase("disabled")]
        [TestCase("ignored")]
        [TestCase("preferredSize")]
        public void LayoutDrivenRectanglesUseDeclaredLayoutInputs(string mutation)
        {
            spec["root"]["childrenLayout"] = new JObject { ["mode"] = "vertical",
                ["spacing"] = 12, ["padding"] = new JArray(0, 0, 0, 0),
                ["childAlignment"] = "middleCenter", ["childControlWidth"] = true,
                ["childControlHeight"] = true, ["childForceExpandWidth"] = false,
                ["childForceExpandHeight"] = false };
            foreach (JObject child in spec["root"]["children"])
                child["placement"] = new JObject { ["mode"] = "layout",
                    ["preferredSize"] = new JArray(240, 96) };
            Edit(root =>
            {
                var group = root.AddComponent<VerticalLayoutGroup>();
                group.spacing = 12; group.padding = new RectOffset();
                group.childAlignment = TextAnchor.MiddleCenter;
                group.childControlWidth = group.childControlHeight = true;
                group.childForceExpandWidth = group.childForceExpandHeight = false;
                foreach (Transform child in root.transform)
                {
                    var element = child.gameObject.AddComponent<LayoutElement>();
                    element.preferredWidth = 240; element.preferredHeight = 96;
                    // Driven rectangle fields are not an independent layout contract.
                    var rect = (RectTransform)child;
                    rect.anchorMin = rect.anchorMax = Vector2.zero;
                    rect.anchoredPosition = new Vector2(23, -42); rect.sizeDelta = Vector2.zero;
                }
                var changed = root.transform.Find("Artwork").GetComponent<LayoutElement>();
                if (mutation == "disabled") changed.enabled = false;
                if (mutation == "ignored") changed.ignoreLayout = true;
                if (mutation == "preferredSize") changed.preferredWidth = 241;
            });
            var report = Audit();
            Assert.That(report.complete, Is.True);
            Assert.That(report.status, Is.EqualTo(mutation == "valid" ? "passed" : "failed"),
                UiCompositionAssetAuditor.AuditPrefabJson(spec.ToString()));
            if (mutation != "valid")
                Assert.That(report.findings.Any(f => f.ruleId == "LAYOUT_ELEMENT_MISMATCH"), Is.True);
        }

        [Test]
        public void CorrectPrefabAndPureColorNodesPassWithoutChangingAssetsOrScene()
        {
            byte[] before = File.ReadAllBytes(prefabPath), meta = File.ReadAllBytes(spritePath + ".meta");
            Scene scene = SceneManager.GetActiveScene(); int handle = scene.handle, count = scene.rootCount; bool dirty = scene.isDirty;
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            bool assetDirty = EditorUtility.IsDirty(prefab);
            var report = Audit();
            Assert.That(report.status, Is.EqualTo("passed"), Newtonsoft.Json.JsonConvert.SerializeObject(report.findings, Newtonsoft.Json.Formatting.Indented)); Assert.That(report.complete, Is.True);
            Assert.That(report.comparedNodes, Is.EqualTo(5)); Assert.That(report.findings, Is.Empty);
            Assert.That(File.ReadAllBytes(prefabPath), Is.EqualTo(before)); Assert.That(File.ReadAllBytes(spritePath + ".meta"), Is.EqualTo(meta));
            Assert.That(EditorUtility.IsDirty(prefab), Is.EqualTo(assetDirty));
            Assert.That(SceneManager.GetActiveScene().handle, Is.EqualTo(handle));
            Assert.That(scene.rootCount, Is.EqualTo(count)); Assert.That(scene.isDirty, Is.EqualTo(dirty));
            Assert.That(JObject.Parse(UiCompositionAssetAuditor.AuditPrefabJson(spec.ToString()))["status"].Value<string>(), Is.EqualTo("passed"));
        }

        [TestCase("missing_sprite", "SPRITE_REFERENCE_MISSING")]
        [TestCase("swapped_sprite", "SPRITE_REFERENCE_MISMATCH")]
        [TestCase("pure_sprite", "SPRITE_UNEXPECTED")]
        [TestCase("image_type", "IMAGE_TYPE_MISMATCH")]
        [TestCase("panel_raycast", "RAYCAST_CONTRACT_MISMATCH")]
        [TestCase("text_raycast", "RAYCAST_CONTRACT_MISMATCH")]
        [TestCase("button_raycast", "RAYCAST_CONTRACT_MISMATCH")]
        [TestCase("button_target", "BUTTON_TARGET_MISMATCH")]
        [TestCase("canvas_scaler_disabled", "CANVAS_SCALER_MISMATCH")]
        [TestCase("canvas_scaler_mode", "CANVAS_SCALER_MISMATCH")]
        [TestCase("canvas_scaler_resolution", "CANVAS_SCALER_MISMATCH")]
        [TestCase("canvas_scaler_match", "CANVAS_SCALER_MISMATCH")]
        [TestCase("anchored_position", "RECT_TRANSFORM_MISMATCH")]
        [TestCase("anchor_min", "RECT_TRANSFORM_MISMATCH")]
        [TestCase("size_delta", "RECT_TRANSFORM_MISMATCH")]
        [TestCase("unexpected_layout_element", "LAYOUT_ELEMENT_MISMATCH")]
        [TestCase("unexpected_layout_group", "CHILDREN_LAYOUT_MISMATCH")]
        [TestCase("missing_image", "NODE_COMPONENT_MISMATCH")]
        [TestCase("missing_text", "NODE_COMPONENT_MISMATCH")]
        [TestCase("unexpected_button", "NODE_COMPONENT_MISMATCH")]
        [TestCase("missing_id", "NODE_ID_MISSING")]
        [TestCase("duplicate_id", "NODE_ID_DUPLICATE")]
        [TestCase("missing_node", "NODE_MISSING")]
        [TestCase("extra_node", "NODE_UNEXPECTED")]
        public void SavedMutationIsDetected(string mutation, string rule)
        {
            Edit(root =>
            {
                GameObject art = root.transform.Find("Artwork").gameObject;
                Image image = art.GetComponent<Image>();
                var caption = root.transform.Find("Caption"); var button = root.transform.Find("Action").GetComponent<Button>();
                switch (mutation)
                {
                    case "missing_sprite": image.sprite = null; break;
                    case "swapped_sprite": image.sprite = AssetDatabase.LoadAssetAtPath<Sprite>(otherPath); break;
                    case "pure_sprite": root.GetComponent<Image>().sprite = image.sprite; break;
                    case "image_type": image.type = Image.Type.Simple; break;
                    case "panel_raycast": image.raycastTarget = true; break;
                    case "text_raycast": caption.GetComponent<TextMeshProUGUI>().raycastTarget = true; break;
                    case "button_raycast": button.GetComponent<Image>().raycastTarget = false; break;
                    case "button_target": button.targetGraphic = image; break;
                    case "canvas_scaler_disabled": root.GetComponent<CanvasScaler>().enabled = false; break;
                    case "canvas_scaler_mode": root.GetComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize; break;
                    case "canvas_scaler_resolution": root.GetComponent<CanvasScaler>().referenceResolution = new Vector2(1280, 720); break;
                    case "canvas_scaler_match": root.GetComponent<CanvasScaler>().matchWidthOrHeight = 1f; break;
                    case "anchored_position": caption.GetComponent<RectTransform>().anchoredPosition += Vector2.one; break;
                    case "anchor_min": caption.GetComponent<RectTransform>().anchorMin = Vector2.zero; break;
                    case "size_delta": caption.GetComponent<RectTransform>().sizeDelta += Vector2.one; break;
                    case "unexpected_layout_element": art.AddComponent<LayoutElement>().preferredWidth = 120; break;
                    case "unexpected_layout_group": root.AddComponent<VerticalLayoutGroup>(); break;
                    case "missing_image": UnityEngine.Object.DestroyImmediate(image); break;
                    case "missing_text": UnityEngine.Object.DestroyImmediate(caption.GetComponent<TextMeshProUGUI>()); break;
                    case "unexpected_button": art.AddComponent<Button>(); break;
                    case "missing_id": UnityEngine.Object.DestroyImmediate(art.GetComponent<UIAgent.Tools.UiNodeIdentity>()); break;
                    case "duplicate_id": SetId(caption.gameObject, "Artwork"); break;
                    case "missing_node": UnityEngine.Object.DestroyImmediate(art); break;
                    case "extra_node": Node(root, "Extra").AddComponent<Image>(); break;
                }
            });
            var report = Audit();
            Assert.That(report.complete, Is.True); Assert.That(report.status, Is.EqualTo("failed"));
            Assert.That(report.findings.Any(f => f.ruleId == rule), Is.True, UiCompositionAssetAuditor.AuditPrefabJson(spec.ToString()));
            Assert.That(report.findings.All(f => !string.IsNullOrEmpty(f.property)), Is.True);
        }

        [Test]
        public void NamesAndInactiveStateDoNotReplaceStableIdentity()
        {
            Edit(root => { var art = root.transform.Find("Artwork"); art.name = "Renamed"; art.gameObject.SetActive(false); });
            Assert.That(Audit().status, Is.EqualTo("passed"), UiCompositionAssetAuditor.AuditPrefabJson(spec.ToString()));
        }

        [Test]
        public void ToggleInitialStateAndVisualReferenceAreAudited()
        {
            JObject actionSpec = (JObject)spec["root"]["children"][2];
            actionSpec["type"] = "Toggle";
            actionSpec["toggle"] = new JObject { ["initialValue"] = true,
                ["labelId"] = "ActionLabel", ["indicatorId"] = "ActionIndicator" };
            JObject labelSpec = (JObject)actionSpec["children"][0];
            JObject indicatorSpec = SpecNode("ActionIndicator", "Text");
            indicatorSpec["text"] = "ON";
            indicatorSpec["style"]["fontSize"] = 24;
            actionSpec["children"] = new JArray(labelSpec, indicatorSpec);

            Edit(root =>
            {
                Transform action = root.transform.Find("Action");
                Image image = action.GetComponent<Image>();
                UnityEngine.Object.DestroyImmediate(action.GetComponent<Button>());
                GameObject indicator = Node(action.gameObject, "ActionIndicator");
                AddText(indicator, "ON");
                Toggle toggle = action.gameObject.AddComponent<Toggle>();
                toggle.targetGraphic = image;
                toggle.graphic = indicator.GetComponent<TextMeshProUGUI>();
                toggle.isOn = true;
            });
            Assert.That(Audit().status, Is.EqualTo("passed"), UiCompositionAssetAuditor.AuditPrefabJson(spec.ToString()));

            Edit(root => root.transform.Find("Action").GetComponent<Toggle>().isOn = false);
            var report = Audit();
            Assert.That(report.status, Is.EqualTo("failed"));
            Assert.That(report.findings.Any(f => f.ruleId == "TOGGLE_CONTRACT_MISMATCH"), Is.True);
        }

        [TestCase("guid", "SPRITE_GUID_MISMATCH")]
        [TestCase("border", "SPRITE_BORDER_MISMATCH")]
        [TestCase("bounds", "SPRITE_BORDER_OUT_OF_BOUNDS")]
        [TestCase("missing_path", "SPRITE_NOT_FOUND")]
        public void AssetAndSpecDisagreementIsDetected(string change, string rule)
        {
            var style = spec["root"]["children"][0]["style"];
            if (change == "guid") style["spriteGuid"] = new string('0', 32);
            if (change == "border")
            {
                var importer = (TextureImporter)AssetImporter.GetAtPath(spritePath);
                importer.spriteBorder = new Vector4(9, 8, 8, 8); importer.SaveAndReimport();
            }
            if (change == "bounds") style["border"] = new JArray(30, 8, 30, 8);
            if (change == "missing_path") style["spritePath"] = folder + "/Missing.png";
            Assert.That(Audit().findings.Any(f => f.ruleId == rule), Is.True);
        }

        [Test]
        public void WrongTextureImportModeIsRejected()
        {
            var importer = (TextureImporter)AssetImporter.GetAtPath(spritePath);
            importer.textureType = TextureImporterType.Default; importer.SaveAndReimport();
            Assert.That(Audit().findings.Any(f => f.ruleId == "SPRITE_IMPORT_SETTINGS"), Is.True);
        }

        [Test]
        public void SimpleSpriteWithoutBorderAndNativeSpriteAssetsAreSupported()
        {
            var style = spec["root"]["children"][0]["style"]; style["imageType"] = "simple"; ((JObject)style).Remove("border");
            Edit(root => root.transform.Find("Artwork").GetComponent<Image>().type = Image.Type.Simple);
            Assert.That(Audit().status, Is.EqualTo("passed"), UiCompositionAssetAuditor.AuditPrefabJson(spec.ToString()));
            var texture = new Texture2D(48, 48); texture.Apply();
            string texturePath = folder + "/NativeTexture.asset"; AssetDatabase.CreateAsset(texture, texturePath);
            var sprite = Sprite.Create(texture, new Rect(0, 0, 48, 48), new Vector2(0.5f, 0.5f));
            string nativePath = folder + "/Native.asset"; AssetDatabase.CreateAsset(sprite, nativePath);
            style["spritePath"] = nativePath; style["spriteGuid"] = AssetDatabase.AssetPathToGUID(nativePath);
            Edit(root => root.transform.Find("Artwork").GetComponent<Image>().sprite = sprite);
            Assert.That(Audit().status, Is.EqualTo("passed"));
        }

        [Test]
        public void InvalidSpecAndMissingPrefabNeverPass()
        {
            var invalid = UiCompositionAssetAuditor.AuditPrefab("{}");
            Assert.That(invalid.complete, Is.False); Assert.That(invalid.status, Is.EqualTo("incomplete"));
            Assert.That(invalid.findings.Any(f => f.ruleId == "SPEC_INVALID"), Is.True);
            spec["outputPrefabPath"] = folder + "/Missing.prefab";
            Assert.That(Audit().complete, Is.False); Assert.That(Audit().findings[0].ruleId, Is.EqualTo("PREFAB_NOT_FOUND"));
        }
    }
}
