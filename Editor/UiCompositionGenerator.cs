using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace UIAgent.Tools.Editor
{
    /// <summary>Creates one new composition Prefab after a fresh plan and matching caller confirmation.</summary>
    public static class UiCompositionGenerator
    {
        public const string GeneratorVersion = "ui-composition-v2-prototype";

        public sealed class Confirmation
        {
            public bool userConfirmed;
            public string planFingerprint;
        }

        public sealed class Report
        {
            public int schemaVersion = 1;
            public string reportType = "ui_composition_generation";
            public string status = "blocked";
            public bool valid;
            public bool success;
            public bool requiresConfirmation = true;
            public JObject plan;
            public List<UiSpecValidator.ValidationError> errors = new List<UiSpecValidator.ValidationError>();
            public List<string> cleanupErrors = new List<string>();
            public string prefabPath;
            public string prefabGuid;
            public bool sceneSaved;
            public string[] limitations = {
                "The generator creates a new Prefab only; it does not modify or save the active scene.",
                "The caller must obtain user confirmation. A Boolean and hash do not prove identity or record consent.",
                "Preflight is not an OS file lock; concurrent external writes to the same output path are unsupported."
            };
        }

        private sealed class Context
        {
            public JObject spec;
            public Scene scene;
            public TMP_FontAsset font;
            public Dictionary<string, Sprite> sprites;
        }

        private sealed class OwnedMaterial
        {
            public string nodeId;
            public string assetPath;
            public Material material;
            public TextMeshProUGUI text;
            public bool assetCreated;
            public string assetGuid;
        }

        public static string PrepareJson(string specJson)
        {
            Context context;
            return JsonConvert.SerializeObject(Prepare(specJson, out context));
        }

        /// <summary>Read-only layout at reference resolution; permits an occupied output path.</summary>
        public static string MeasureLayoutJson(string specJson)
        {
            Context context;
            Report report = Prepare(specJson, out context, true);
            return new JObject {
                ["schemaVersion"] = 1, ["reportType"] = "ui_composition_layout",
                ["status"] = report.valid ? "measured" : "blocked",
                ["errors"] = JArray.FromObject(report.errors),
                ["layout"] = report.plan?["layout"]?.DeepClone()
            }.ToString(Formatting.None);
        }

        public static string GenerateJson(string specJson, Confirmation confirmation)
        {
            return JsonConvert.SerializeObject(Generate(specJson, confirmation, null));
        }

        internal static Report Generate(string specJson, Confirmation confirmation, Action<string> checkpoint)
        {
            Context context;
            Report report = Prepare(specJson, out context);
            if (report.errors.Count != 0) return report;
            string currentFingerprint = (string)report.plan["planFingerprint"];
            if (confirmation == null || !confirmation.userConfirmed ||
                !string.Equals(confirmation.planFingerprint, currentFingerprint, StringComparison.Ordinal))
            {
                Add(report, "CONFIRMATION_REQUIRED", "confirmation", "请先展示完整 v2 生成计划并取得确认；Spec、资源、目标项目或场景变化后必须重新确认。");
                report.status = "confirmation_required";
                return report;
            }

            string path = (string)context.spec["outputPrefabPath"];
            var folders = new List<KeyValuePair<string, string>>();
            GameObject temporary = null;
            string ownedGuid = null;
            bool saveAttempted = false;
            var ownedMaterials = new List<OwnedMaterial>();
            try
            {
                temporary = Build(context.spec, context.font, context.sprites, ownedMaterials);
                checkpoint?.Invoke("built");
                EnsureFolders(Path.GetDirectoryName(path).Replace('\\', '/'), folders);
                Report fresh = Prepare(specJson, out context);
                if (fresh.errors.Count != 0 || (string)fresh.plan["planFingerprint"] != currentFingerprint)
                    throw new InvalidOperationException("项目状态、Spec 或依赖资源在准备后发生变化，生成已停止；请重新准备并确认计划。");
                checkpoint?.Invoke("before_save");

                // No asynchronous yield occurs between the final collision check and this write.
                fresh = Prepare(specJson, out context);
                if (fresh.errors.Count != 0 || (string)fresh.plan["planFingerprint"] != currentFingerprint)
                    throw new InvalidOperationException("写入前的最终检查未通过；请重新准备并确认计划。");
                foreach (OwnedMaterial owned in ownedMaterials)
                {
                    AssetDatabase.CreateAsset(owned.material, owned.assetPath);
                    owned.assetCreated = true;
                    owned.assetGuid = AssetDatabase.AssetPathToGUID(owned.assetPath);
                    AssetDatabase.SaveAssetIfDirty(owned.material);
                    owned.text.fontSharedMaterial = AssetDatabase.LoadAssetAtPath<Material>(owned.assetPath);
                }
                saveAttempted = true;
                temporary.hideFlags = HideFlags.None;
                GameObject saved = PrefabUtility.SaveAsPrefabAsset(temporary, path);
                ownedGuid = AssetDatabase.AssetPathToGUID(path);
                if (saved == null || string.IsNullOrEmpty(ownedGuid))
                    throw new InvalidOperationException("Unity 未能保存 Prefab 或返回资源 GUID。");
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                checkpoint?.Invoke("saved");
                VerifySavedPrefab(path, ownedGuid, context.spec, context.font, context.sprites);
                report.status = "generated";
                report.valid = true;
                report.success = true;
                report.prefabPath = path;
                report.prefabGuid = ownedGuid;
                report.sceneSaved = false;
            }
            catch (Exception ex)
            {
                Add(report, "GENERATION_FAILED", "$", ex.Message);
                if (!string.IsNullOrEmpty(ownedGuid))
                {
                    try
                    {
                        if (AssetDatabase.AssetPathToGUID(path) != ownedGuid || !AssetDatabase.DeleteAsset(path))
                            report.cleanupErrors.Add("保存的 Prefab 未能按 GUID 安全清理，请人工检查：" + path);
                    }
                    catch (Exception cleanup) { report.cleanupErrors.Add(cleanup.Message); }
                }
                else if (saveAttempted && (File.Exists(DiskPath(path)) || File.Exists(DiskPath(path + ".meta"))))
                    report.cleanupErrors.Add("保存中断且无法确认资产 GUID，请人工检查：" + path);
                CleanupMaterials(ownedMaterials, report);
                CleanupFolders(folders, report);
                report.status = report.cleanupErrors.Count == 0 ? "failed_rolled_back" : "failed_cleanup_required";
            }
            finally
            {
                if (temporary != null) UnityEngine.Object.DestroyImmediate(temporary);
                foreach (OwnedMaterial owned in ownedMaterials)
                    if (!owned.assetCreated && owned.material != null) UnityEngine.Object.DestroyImmediate(owned.material);
            }
            return report;
        }

        private static Report Prepare(string specJson, out Context context, bool measurementOnly = false)
        {
            context = null;
            var report = new Report();
            UiCompositionSpecValidator.Report validation = UiCompositionSpecValidator.Validate(specJson);
            foreach (UiCompositionSpecValidator.ValidationError error in validation.errors)
                Add(report, error.code, error.field, error.message);
            if (report.errors.Count != 0) return report;

            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded || EditorSceneManager.IsPreviewScene(scene) || string.IsNullOrEmpty(scene.path))
                Add(report, "SCENE_REQUIRED", "scene", "请使用已保存的普通 Unity 场景进行项目预检。");
            else if (scene.isDirty)
                Add(report, "SCENE_DIRTY", "scene", "活动场景有未保存修改；为保证只读 Prepare，不会替你保存或丢弃。");
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode)
                Add(report, "EDITOR_NOT_READY", "editor", "请在空闲 Edit Mode 重新准备组合计划。");
            if (PrefabStageUtility.GetCurrentPrefabStage() != null)
                Add(report, "PREFAB_STAGE_OPEN", "scene", "请先退出 Prefab Mode。");

            JObject spec = validation.validatedSpec;
            List<JObject> nodes = CollectNodes(spec["root"]).ToList();
            report.limitations = BuildReportLimitations(nodes, (JObject)spec["confirmationDialog"]);
            string fontPath = (string)spec["fontAssetPath"];
            TMP_FontAsset font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(fontPath);
            if (font == null || font.material == null)
                Add(report, "FONT_REQUIRED", "fontAssetPath", "指定路径必须引用具有材质的 TMP 字体资源。");

            var sprites = new Dictionary<string, Sprite>(StringComparer.Ordinal);
            InspectSpriteNodes(spec["root"], sprites, report);
            string outputPath = (string)spec["outputPrefabPath"];
            if (!measurementOnly) CheckOutputPath(outputPath, report);
            foreach (JObject textNode in CollectNodes(spec["root"]).Where(n => (string)n["type"] == "Text"))
            {
                string materialPath = OwnedMaterialPath(outputPath, (string)textNode["id"]);
                if (!measurementOnly && HasTextEffect((JObject)textNode["style"])) CheckMaterialOutputPath(materialPath, report);
            }

            if (font != null)
            {
                string text = string.Concat(CollectText(spec["root"]));
                uint[] missing = CodePoints(text).Distinct().Where(c => !HasGlyph(font, c, new HashSet<TMP_FontAsset>())).ToArray();
                if (missing.Length != 0)
                    Add(report, "FONT_GLYPHS_MISSING", "fontAssetPath", "字体及显式 fallback 缺少字符：" + string.Join(", ", missing.Select(c => "U+" + c.ToString("X4"))) + "。");
            }
            if (report.errors.Count != 0) return report;

            context = new Context { spec = spec, scene = scene, font = font, sprites = sprites };
            GameObject preview = null;
            Scene measurementScene = default(Scene);
            JArray measurements = null;
            var previewMaterials = new List<OwnedMaterial>();
            try
            {
                measurementScene = EditorSceneManager.NewPreviewScene();
                preview = Build(spec, font, sprites, previewMaterials, measurementScene);
                measurements = ReadLayoutMeasurements(preview, nodes);
            }
            catch (Exception ex) { Add(report, "LAYOUT_PREFLIGHT_FAILED", "$", ex.Message); }
            finally
            {
                if (preview != null) UnityEngine.Object.DestroyImmediate(preview);
                foreach (OwnedMaterial owned in previewMaterials)
                    if (owned.material != null) UnityEngine.Object.DestroyImmediate(owned.material);
                if (measurementScene.IsValid()) EditorSceneManager.ClosePreviewScene(measurementScene);
            }
            if (report.errors.Count != 0) { context = null; return report; }

            var dependencies = new SortedDictionary<string, JObject>(StringComparer.Ordinal);
            AddDependency(dependencies, fontPath);
            CollectFonts(font, new HashSet<TMP_FontAsset>()).ToList().ForEach(f => AddDependency(dependencies, AssetDatabase.GetAssetPath(f)));
            foreach (string spritePath in sprites.Values.Select(s => AssetDatabase.GetAssetPath(s)).Distinct()) AddDependency(dependencies, spritePath);
            var layout = new JObject {
                ["basis"] = "unity_ugui_reference_resolution", ["measurementVersion"] = 1,
                ["referenceResolution"] = spec["referenceResolution"].DeepClone(),
                ["specFingerprint"] = validation.specFingerprint,
                ["dependencies"] = new JArray(dependencies.Values.Select(d => d.DeepClone())),
                ["nodes"] = measurements
            };
            using (SHA256 sha = SHA256.Create())
                layout["layoutFingerprint"] = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(layout.ToString(Formatting.None)))).Replace("-", "").ToLowerInvariant();
            var plan = new JObject {
                ["generatorVersion"] = GeneratorVersion,
                ["projectRoot"] = Path.GetDirectoryName(Application.dataPath),
                ["scenePath"] = scene.path,
                ["spec"] = spec.DeepClone(),
                ["specFingerprint"] = validation.specFingerprint,
                ["dependencies"] = new JArray(dependencies.Values),
                ["spriteReferences"] = new JArray(sprites.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p =>
                { var entry = UiCompositionSpriteResolver.Describe(p.Value); entry["nodeId"] = p.Key; return entry; })),
                ["layout"] = layout,
                ["visualRisks"] = CollectSimpleSpriteStretchRisks(nodes, measurements, sprites),
                ["outputPrefabPath"] = spec["outputPrefabPath"].DeepClone(),
                ["ownedMaterialPaths"] = new JArray(CollectNodes(spec["root"])
                    .Where(n => (string)n["type"] == "Text" && HasTextEffect((JObject)n["style"]))
                    .Select(n => new JValue(OwnedMaterialPath(outputPath, (string)n["id"]))).ToArray()),
                ["nodeCount"] = validation.nodeCount,
                ["overwriteAllowed"] = false,
                ["sceneSaved"] = false,
                ["bindGameLogic"] = false,
                ["operations"] = BuildPlanOperations(nodes, (JObject)spec["confirmationDialog"]),
                ["limitations"] = BuildPlanLimitations(nodes, (JObject)spec["confirmationDialog"])
            };
            using (SHA256 sha = SHA256.Create())
                plan["planFingerprint"] = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(plan.ToString(Formatting.None)))).Replace("-", "").ToLowerInvariant();
            report.valid = true;
            report.status = "ready_for_confirmation";
            report.plan = plan;
            return report;
        }

        private static JArray BuildPlanOperations(IReadOnlyCollection<JObject> nodes, JObject dialog)
        {
            int panelCount = CountNodes(nodes, "Panel");
            int textCount = CountNodes(nodes, "Text");
            int buttonCount = CountNodes(nodes, "Button");
            int toggleCount = CountNodes(nodes, "Toggle");
            int sliderCount = CountNodes(nodes, "Slider");
            int spriteCount = nodes.Count(n => ((JObject)n["style"])?["spritePath"]?.Type == JTokenType.String);
            int effectCount = nodes.Count(n => (string)n["type"] == "Text" && HasTextEffect((JObject)n["style"]));
            var typeCounts = new List<string>();
            AddTypeCount(typeCounts, "Panel", panelCount);
            AddTypeCount(typeCounts, "Text", textCount);
            AddTypeCount(typeCounts, "Button", buttonCount);
            AddTypeCount(typeCounts, "Toggle", toggleCount);
            AddTypeCount(typeCounts, "Slider", sliderCount);

            var operations = new JArray("Create Canvas, CanvasScaler and GraphicRaycaster on the root Panel.",
                "Create " + nodes.Count.ToString(CultureInfo.InvariantCulture) + " UI nodes (" + string.Join(", ", typeCounts) + ") with their declared transforms and layouts.");
            if (spriteCount > 0)
                operations.Add("Resolve and assign " + spriteCount.ToString(CultureInfo.InvariantCulture) +
                    " declared Sprite reference(s) with the requested Simple/Sliced Image types and borders.");
            if (toggleCount > 0)
                operations.Add("Apply the declared initial states and indicator references for " +
                    toggleCount.ToString(CultureInfo.InvariantCulture) + " Toggle(s); add no application-specific callbacks.");
            if (sliderCount > 0)
                operations.Add("Apply the declared range, initial value, Fill/Handle references, and empty application callback list for " +
                    sliderCount.ToString(CultureInfo.InvariantCulture) + " Slider(s); no settings persistence is added.");
            if (effectCount > 0)
                operations.Add("Create " + effectCount.ToString(CultureInfo.InvariantCulture) +
                    " independent TMP material asset(s) for declared Text effects without modifying the shared font material.");
            if (dialog != null)
                operations.Add("Add an inactive-by-default ConfirmationDialog with a full-screen raycast blocker, serialized Confirm/Cancel UnityEvents, initial focus, and temporary topmost Canvas ordering; no game callbacks are assigned.");

            string readBack = "Save one new Prefab, reimport it, and read back node IDs, component types and declared layout settings";
            if (textCount > 0) readBack += ", including Text content and font references";
            if (spriteCount > 0) readBack += ", including Sprite references";
            if (sliderCount > 0) readBack += ", including Slider range, value, and Fill/Handle references";
            if (effectCount > 0) readBack += " and effect material values";
            if (dialog != null) readBack += ", including modal Button and Selectable references, initial inactive state, and empty generated UnityEvents";
            operations.Add(readBack + ".");
            return operations;
        }

        private static JArray BuildPlanLimitations(IReadOnlyCollection<JObject> nodes, JObject dialog)
        {
            int buttonCount = CountNodes(nodes, "Button");
            int toggleCount = CountNodes(nodes, "Toggle");
            int sliderCount = CountNodes(nodes, "Slider");
            var limitations = new JArray();
            if (buttonCount > 0) limitations.Add("Generated Buttons have no game or application callbacks.");
            if (toggleCount > 0)
                limitations.Add("Toggles change only their native Unity UI value and indicator state; no application-specific callbacks or persistence are added.");
            if (sliderCount > 0)
                limitations.Add("Sliders change only their native Unity UI value; no application-specific callbacks or settings persistence are added.");
            if (dialog != null)
                limitations.Add("The developer must connect On Confirmed and On Cancelled UnityEvents to project-specific behavior in the Inspector or calling code; a missing EventSystem prevents keyboard/gamepad focus management.");
            limitations.Add(buttonCount > 0 || toggleCount > 0 || sliderCount > 0
                ? "Game View, visual design and runtime input still require separate review."
                : "Game View and visual design still require separate review.");
            return limitations;
        }

        private static string[] BuildReportLimitations(IReadOnlyCollection<JObject> nodes, JObject dialog)
        {
            var limitations = new List<string> {
                "The generator creates a new Prefab only; it does not modify or save the active scene.",
                "The caller must obtain user confirmation. A Boolean and hash do not prove identity or record consent."
            };
            if (nodes.Any(n => ((JObject)n["style"])?["spritePath"]?.Type == JTokenType.String))
                limitations.Add("Unity asset preflight checks the declared Sprite path, GUID, importer border and Sprite border; it does not prove visual quality.");
            if (CountNodes(nodes, "Button") > 0) limitations.Add("Generated Buttons have no game or application callbacks.");
            if (CountNodes(nodes, "Toggle") > 0)
                limitations.Add("Toggles change only their native Unity UI value and indicator state; no application-specific callbacks or persistence are added.");
            if (CountNodes(nodes, "Slider") > 0)
                limitations.Add("Sliders change only their native Unity UI value; no application-specific callbacks or settings persistence are added.");
            if (dialog != null)
                limitations.Add("ConfirmationDialog temporarily raises its Canvas above existing UI while open, then restores the previous sorting settings; the developer must connect its UnityEvents to game behavior.");
            limitations.Add("Preflight is not an OS file lock; concurrent external writes to the same output path are unsupported.");
            return limitations.ToArray();
        }

        private static int CountNodes(IEnumerable<JObject> nodes, string type)
        {
            return nodes.Count(n => (string)n["type"] == type);
        }

        private static void AddTypeCount(ICollection<string> counts, string type, int count)
        {
            if (count > 0) counts.Add(type + ": " + count.ToString(CultureInfo.InvariantCulture));
        }

        private static void InspectSpriteNodes(JToken nodeToken, Dictionary<string, Sprite> sprites, Report report)
        {
            JObject node = nodeToken as JObject;
            if (node == null) return;
            JObject style = node["style"] as JObject;
            if (style != null && style["spritePath"]?.Type == JTokenType.String)
            {
                var issues = new List<UiCompositionSpriteResolver.Issue>();
                Sprite sprite = UiCompositionSpriteResolver.Resolve(style, issues);
                foreach (var issue in issues) Add(report, issue.code, (string)node["id"] + "." + issue.property, issue.message);
                if (sprite != null && issues.Count == 0) sprites[(string)node["id"]] = sprite;
            }
            JArray children = node["children"] as JArray;
            if (children != null) foreach (JToken child in children) InspectSpriteNodes(child, sprites, report);
        }

        private static JArray ReadLayoutMeasurements(GameObject root, IEnumerable<JObject> nodes)
        {
            var objects = root.GetComponentsInChildren<UiNodeIdentity>(true)
                .ToDictionary(n => n.Id, n => n.gameObject, StringComparer.Ordinal);
            var result = new JArray();
            foreach (JObject node in nodes)
            {
                GameObject obj = objects[(string)node["id"]];
                RectTransform rect = obj.GetComponent<RectTransform>();
                Image image = obj.GetComponent<Image>();
                result.Add(new JObject {
                    ["nodeId"] = (string)node["id"], ["type"] = (string)node["type"],
                    ["placementMode"] = (string)node["placement"]["mode"],
                    ["targetSize"] = new JArray(rect.rect.width, rect.rect.height),
                    ["referencePixelsPerUnit"] = root.GetComponent<Canvas>().referencePixelsPerUnit,
                    ["pixelsPerUnitMultiplier"] = image != null ? image.pixelsPerUnitMultiplier : 1f
                });
            }
            return result;
        }

        private static JArray CollectSimpleSpriteStretchRisks(IEnumerable<JObject> nodes, JArray measurements,
            IReadOnlyDictionary<string, Sprite> sprites)
        {
            var sizes = measurements.Cast<JObject>().ToDictionary(n => (string)n["nodeId"], StringComparer.Ordinal);
            var risks = new JArray();
            foreach (JObject node in nodes)
            {
                string nodeId = (string)node["id"];
                JObject style = node["style"] as JObject;
                Sprite sprite;
                if ((string)style?["imageType"] != "simple" || !sprites.TryGetValue(nodeId, out sprite)) continue;
                JArray size = (JArray)sizes[nodeId]["targetSize"];
                float width = (float)size[0], height = (float)size[1];
                if (width <= 0 || height <= 0 || sprite.rect.width <= 0 || sprite.rect.height <= 0) continue;
                float spriteAspect = sprite.rect.width / sprite.rect.height;
                float targetAspect = width / height;
                float distortion = Mathf.Max(spriteAspect / targetAspect, targetAspect / spriteAspect);
                if (distortion < 1.2f) continue;
                JObject risk = UiCompositionSpriteResolver.Describe(sprite);
                risk["severity"] = "warning"; risk["code"] = "SIMPLE_SPRITE_STRETCH";
                risk["nodeId"] = nodeId; risk["spritePath"] = AssetDatabase.GetAssetPath(sprite);
                risk["spriteRectSize"] = new JArray(sprite.rect.width, sprite.rect.height);
                risk["targetSize"] = size.DeepClone();
                risk["aspectRatioDistortionFactor"] = Math.Round(distortion, 3);
                risk["message"] = "Simple 会将 Sprite 拉伸到 Unity 实际布局矩形；宽高比变化超过约 20%，可能造成边缘或图案变形。Spec 未被修改。";
                risks.Add(risk);
            }
            return risks;
        }

        private static GameObject Build(JObject spec, TMP_FontAsset font, Dictionary<string, Sprite> sprites, List<OwnedMaterial> ownedMaterials, Scene measurementScene = default(Scene))
        {
            JObject specRoot = (JObject)spec["root"];
            var root = new GameObject((string)specRoot["id"], typeof(RectTransform));
            root.hideFlags = HideFlags.HideAndDontSave;
            root.SetActive(false);
            if (measurementScene.IsValid()) SceneManager.MoveGameObjectToScene(root, measurementScene);
            try
            {
                Canvas canvas = root.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                CanvasScaler scaler = root.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2((int)spec["referenceResolution"]["width"], (int)spec["referenceResolution"]["height"]);
                scaler.matchWidthOrHeight = 0.5f;
                root.AddComponent<GraphicRaycaster>();
                var rootRect = root.GetComponent<RectTransform>();
                rootRect.sizeDelta = scaler.referenceResolution;

                var layoutGroups = new List<RectTransform>();
                var texts = new List<TextMeshProUGUI>();
                var nodeObjects = new Dictionary<string, GameObject>(StringComparer.Ordinal);
                string backdropId = (string)((JObject)spec["confirmationDialog"])?["backdropId"];
                BuildNode(specRoot, root.transform, true, font, sprites, layoutGroups, texts, ownedMaterials, nodeObjects,
                    (string)spec["outputPrefabPath"], backdropId);
                if (measurementScene.IsValid())
                {
                    // Fix reference coordinates before enabling any layout component. Never depend on the open Game View.
                    canvas.renderMode = RenderMode.WorldSpace;
                    scaler.enabled = false;
                    rootRect.anchorMin = rootRect.anchorMax = Vector2.zero;
                    rootRect.sizeDelta = scaler.referenceResolution;
                }
                root.SetActive(true);
                foreach (RectTransform group in layoutGroups.AsEnumerable().Reverse())
                    LayoutRebuilder.ForceRebuildLayoutImmediate(group);
                foreach (TextMeshProUGUI text in texts)
                {
                    Vector2 preferred = text.GetPreferredValues(text.text);
                    Rect rect = text.rectTransform.rect;
                    if (rect.width <= 0 || rect.height <= 0 || preferred.x > rect.width + 1 || preferred.y > rect.height + 1)
                        throw new InvalidOperationException("文本超出或没有有效容器尺寸：" + text.name + " (" + rect.width + "x" + rect.height + ")。");
                }
                if (spec["confirmationDialog"] is JObject dialogSpec)
                {
                    var dialog = root.AddComponent<UIAgent.Tools.ConfirmationDialog>();
                    var serialized = new SerializedObject(dialog);
                    serialized.FindProperty("backgroundBlocker").objectReferenceValue = nodeObjects[(string)dialogSpec["backdropId"]].GetComponent<Button>();
                    serialized.FindProperty("cancelButton").objectReferenceValue = nodeObjects[(string)dialogSpec["cancelButtonId"]].GetComponent<Button>();
                    serialized.FindProperty("confirmButton").objectReferenceValue = nodeObjects[(string)dialogSpec["confirmButtonId"]].GetComponent<Button>();
                    serialized.FindProperty("initialFocus").objectReferenceValue = nodeObjects[(string)dialogSpec["initialFocusId"]].GetComponent<Selectable>();
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                    root.SetActive(false);
                }
                return root;
            }
            catch
            {
                UnityEngine.Object.DestroyImmediate(root);
                throw;
            }
        }

        private static void BuildNode(JObject node, Transform parent, bool root, TMP_FontAsset font,
            Dictionary<string, Sprite> sprites, List<RectTransform> layoutGroups, List<TextMeshProUGUI> texts,
            List<OwnedMaterial> ownedMaterials, Dictionary<string, GameObject> nodeObjects, string outputPrefabPath,
            string backdropId)
        {
            GameObject gameObject = root ? parent.gameObject : new GameObject((string)node["id"], typeof(RectTransform));
            if (!root) gameObject.transform.SetParent(parent, false);
            var identity = gameObject.AddComponent<UiNodeIdentity>();
            var serializedIdentity = new SerializedObject(identity);
            serializedIdentity.FindProperty("id").stringValue = (string)node["id"];
            serializedIdentity.ApplyModifiedPropertiesWithoutUndo();
            nodeObjects.Add((string)node["id"], gameObject);
            RectTransform rect = gameObject.GetComponent<RectTransform>();
            ApplyPlacement(rect, (JObject)node["placement"]);

            string type = (string)node["type"];
            JObject style = (JObject)node["style"];
            if (type == "Text")
            {
                var text = gameObject.AddComponent<TextMeshProUGUI>();
                text.font = font;
                text.text = (string)node["text"];
                text.color = ReadColor((string)style["color"]);
                text.fontSize = (float)style["fontSize"];
                text.alignment = (string)style["alignment"] == "left" ? TextAlignmentOptions.MidlineLeft :
                    (string)style["alignment"] == "right" ? TextAlignmentOptions.MidlineRight : TextAlignmentOptions.Center;
                text.enableAutoSizing = false;
                text.enableWordWrapping = false;
                text.richText = false;
                text.overflowMode = TextOverflowModes.Truncate;
                text.raycastTarget = false;
                JObject effectStyle = style;
                if (HasTextEffect(effectStyle))
                {
                    Material effectMaterial = CreateEffectMaterial(font, effectStyle, (string)node["id"]);
                    text.fontSharedMaterial = effectMaterial;
                    ownedMaterials.Add(new OwnedMaterial {
                        nodeId = (string)node["id"],
                        assetPath = OwnedMaterialPath(outputPrefabPath, (string)node["id"]),
                        material = effectMaterial,
                        text = text
                    });
                }
                texts.Add(text);
            }
            else
            {
                var image = gameObject.AddComponent<Image>();
                bool isBackdrop = !string.IsNullOrEmpty(backdropId) && (string)node["id"] == backdropId;
                image.raycastTarget = type == "Button" || type == "Toggle" || isBackdrop;
                image.color = style["color"] == null ? Color.white : ReadColor((string)style["color"]);
                if (style["spritePath"] != null)
                {
                    image.sprite = sprites[(string)node["id"]];
                    image.type = (string)style["imageType"] == "sliced" ? Image.Type.Sliced : Image.Type.Simple;
                }
                if (type == "Button")
                {
                    var button = gameObject.AddComponent<Button>();
                    button.targetGraphic = image;
                    button.interactable = true;
                    button.transition = Selectable.Transition.ColorTint;
                    ColorBlock colors = button.colors;
                    colors.highlightedColor = new Color(0.78f, 0.9f, 1f, 1f);
                    colors.pressedColor = new Color(0.56f, 0.76f, 0.95f, 1f);
                    button.colors = colors;
                }
                else if (type == "Toggle")
                {
                    var toggle = gameObject.AddComponent<Toggle>();
                    toggle.targetGraphic = image;
                    toggle.interactable = true;
                    toggle.transition = Selectable.Transition.ColorTint;
                }
                else if (isBackdrop)
                {
                    var blocker = gameObject.AddComponent<Button>();
                    blocker.targetGraphic = image;
                    blocker.interactable = true;
                    blocker.transition = Selectable.Transition.None;
                }
            }

            JObject childrenLayout = (JObject)node["childrenLayout"];
            string mode = (string)childrenLayout["mode"];
            if (mode == "vertical" || mode == "horizontal")
            {
                HorizontalOrVerticalLayoutGroup group = mode == "vertical"
                    ? (HorizontalOrVerticalLayoutGroup)gameObject.AddComponent<VerticalLayoutGroup>()
                    : gameObject.AddComponent<HorizontalLayoutGroup>();
                group.spacing = (float)childrenLayout["spacing"];
                JArray padding = (JArray)childrenLayout["padding"];
                group.padding = new RectOffset((int)padding[0], (int)padding[2], (int)padding[1], (int)padding[3]);
                group.childAlignment = (TextAnchor)Enum.Parse(typeof(TextAnchor), (string)childrenLayout["childAlignment"], true);
                group.childControlWidth = (bool)childrenLayout["childControlWidth"];
                group.childControlHeight = (bool)childrenLayout["childControlHeight"];
                group.childForceExpandWidth = (bool)childrenLayout["childForceExpandWidth"];
                group.childForceExpandHeight = (bool)childrenLayout["childForceExpandHeight"];
                layoutGroups.Add(rect);
            }

            if ((string)node["placement"]["mode"] == "layout")
            {
                JArray size = (JArray)node["placement"]["preferredSize"];
                var element = gameObject.AddComponent<LayoutElement>();
                element.preferredWidth = (float)size[0];
                element.preferredHeight = (float)size[1];
            }

            JArray children = node["children"] as JArray;
            if (children != null) foreach (JObject child in children)
                BuildNode(child, gameObject.transform, false, font, sprites, layoutGroups, texts, ownedMaterials, nodeObjects, outputPrefabPath, backdropId);

            if (type == "Toggle")
            {
                JObject toggleSpec = (JObject)node["toggle"];
                Toggle toggle = gameObject.GetComponent<Toggle>();
                toggle.graphic = nodeObjects[(string)toggleSpec["indicatorId"]].GetComponent<TextMeshProUGUI>();
                toggle.isOn = (bool)toggleSpec["initialValue"];
            }
            if (type == "Slider")
            {
                JObject sliderSpec = (JObject)node["slider"];
                Slider slider = gameObject.AddComponent<Slider>();
                slider.targetGraphic = gameObject.GetComponent<Image>();
                slider.fillRect = nodeObjects[(string)sliderSpec["fillId"]].GetComponent<RectTransform>();
                slider.handleRect = nodeObjects[(string)sliderSpec["handleId"]].GetComponent<RectTransform>();
                slider.minValue = (float)sliderSpec["minValue"];
                slider.maxValue = (float)sliderSpec["maxValue"];
                slider.wholeNumbers = (bool)sliderSpec["wholeNumbers"];
                slider.value = (float)sliderSpec["initialValue"];
                slider.interactable = true;
                slider.transition = Selectable.Transition.ColorTint;
                gameObject.GetComponent<Image>().raycastTarget = true;
                nodeObjects[(string)sliderSpec["handleId"]].GetComponent<Image>().raycastTarget = true;
            }
        }

        private static void ApplyPlacement(RectTransform rect, JObject placement)
        {
            if ((string)placement["mode"] == "layout")
            {
                JArray size = (JArray)placement["preferredSize"];
                rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.pivot = new Vector2(0.5f, 0.5f);
                rect.anchoredPosition = Vector2.zero;
                rect.sizeDelta = new Vector2((float)size[0], (float)size[1]);
                return;
            }
            rect.anchorMin = ReadVector((JArray)placement["anchorMin"]);
            rect.anchorMax = ReadVector((JArray)placement["anchorMax"]);
            rect.pivot = ReadVector((JArray)placement["pivot"]);
            rect.anchoredPosition = ReadVector((JArray)placement["anchoredPosition"]);
            rect.sizeDelta = ReadVector((JArray)placement["sizeDelta"]);
        }

        private static void VerifySavedPrefab(string path, string guid, JObject spec, TMP_FontAsset font,
            Dictionary<string, Sprite> sprites)
        {
            if (AssetDatabase.AssetPathToGUID(path) != guid) throw new InvalidOperationException("保存后 Prefab GUID 不匹配。");
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null) throw new InvalidOperationException("保存后无法重新加载 Prefab。");
            var nodes = prefab.GetComponentsInChildren<UiNodeIdentity>(true).ToDictionary(i => i.Id, i => i.gameObject, StringComparer.Ordinal);
            var expectedIds = CollectNodes(spec["root"]).Select(n => (string)n["id"]).ToArray();
            // Unity renames the saved Prefab root to match the asset filename.
            if (prefab.name != Path.GetFileNameWithoutExtension(path)) throw new InvalidOperationException("保存后 Prefab 根对象名称与资产文件名不一致。");
            foreach (string id in expectedIds) if (!nodes.ContainsKey(id)) throw new InvalidOperationException("保存后缺少稳定节点 ID：" + id);
            if (nodes.Count != expectedIds.Length) throw new InvalidOperationException("保存后的节点数量不匹配。");
            if (prefab.GetComponent<Canvas>() == null || prefab.GetComponent<CanvasScaler>() == null || prefab.GetComponent<GraphicRaycaster>() == null)
                throw new InvalidOperationException("保存后缺少 Canvas 根组件。");
            JObject dialogSpec = spec["confirmationDialog"] as JObject;
            UIAgent.Tools.ConfirmationDialog dialog = prefab.GetComponent<UIAgent.Tools.ConfirmationDialog>();
            if ((dialogSpec == null && dialog != null) || (dialogSpec != null && dialog == null))
                throw new InvalidOperationException("保存后 ConfirmationDialog 组件与 Spec 声明不一致。");
            if (dialogSpec == null && !prefab.activeSelf)
                throw new InvalidOperationException("未声明确认弹窗的 Prefab 根对象必须保持激活状态。");
            if (dialogSpec != null)
            {
                Button blocker = nodes[(string)dialogSpec["backdropId"]].GetComponent<Button>();
                Button cancel = nodes[(string)dialogSpec["cancelButtonId"]].GetComponent<Button>();
                Button confirm = nodes[(string)dialogSpec["confirmButtonId"]].GetComponent<Button>();
                Selectable focus = nodes[(string)dialogSpec["initialFocusId"]].GetComponent<Selectable>();
                if (prefab.activeSelf || dialog.BackgroundBlocker != blocker || dialog.CancelButton != cancel ||
                    dialog.ConfirmButton != confirm || dialog.InitialFocus != focus ||
                    dialog.OnCancelled == null || dialog.OnConfirmed == null ||
                    dialog.OnCancelled.GetPersistentEventCount() != 0 || dialog.OnConfirmed.GetPersistentEventCount() != 0 ||
                    cancel == null || confirm == null || cancel.onClick.GetPersistentEventCount() != 0 || confirm.onClick.GetPersistentEventCount() != 0 ||
                    blocker == null || !blocker.interactable || blocker.transition != Selectable.Transition.None ||
                    blocker.targetGraphic != blocker.GetComponent<Image>() || blocker.onClick.GetPersistentEventCount() != 0)
                    throw new InvalidOperationException("保存后确认弹窗组件引用、隐藏状态、背景拦截器或初始 UnityEvent 与契约不一致。");
            }
            foreach (JObject node in CollectNodes(spec["root"]))
            {
                GameObject gameObject = nodes[(string)node["id"]];
                string type = (string)node["type"];
                if (type == "Text")
                {
                    TextMeshProUGUI text = gameObject.GetComponent<TextMeshProUGUI>();
                    if (text == null || text.font != font || text.text != (string)node["text"])
                        throw new InvalidOperationException("保存后 Text 组件或字体引用不匹配：" + node["id"]);
                    JObject style = (JObject)node["style"];
                    if (HasTextEffect(style))
                    {
                        string materialPath = OwnedMaterialPath(path, (string)node["id"]);
                        Material material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
                        if (material == null || text.fontSharedMaterial != material)
                            throw new InvalidOperationException("保存后 Text 效果材质引用不匹配：" + node["id"]);
                        VerifyEffectMaterial(material, style, node["id"].ToString());
                    }
                    else if (text.fontSharedMaterial != font.material)
                        throw new InvalidOperationException("未声明效果的 Text 不应覆盖共享字体材质：" + node["id"]);
                }
                else
                {
                    Image image = gameObject.GetComponent<Image>();
                    if (image == null) throw new InvalidOperationException("保存后 Panel/Button 缺少 Image：" + node["id"]);
                    JObject style = (JObject)node["style"];
                    if (style["spritePath"] != null && (image.sprite != sprites[(string)node["id"]] ||
                        image.type != ((string)style["imageType"] == "sliced" ? Image.Type.Sliced : Image.Type.Simple)))
                        throw new InvalidOperationException("保存后 Sprite 引用或 Image 类型不匹配：" + node["id"]);
                    if (type == "Button" && gameObject.GetComponent<Button>() == null)
                        throw new InvalidOperationException("保存后 Button 组件丢失：" + node["id"]);
                    if (dialogSpec != null && (string)node["id"] == (string)dialogSpec["backdropId"] &&
                        (gameObject.GetComponent<Button>() == null || !image.raycastTarget))
                        throw new InvalidOperationException("保存后背景拦截器缺少可命中的 Button/Image：" + node["id"]);
                    if (type == "Toggle")
                    {
                        Toggle toggle = gameObject.GetComponent<Toggle>();
                        JObject toggleSpec = (JObject)node["toggle"];
                        TextMeshProUGUI indicator = nodes[(string)toggleSpec["indicatorId"]].GetComponent<TextMeshProUGUI>();
                        if (toggle == null || !toggle.interactable || toggle.isOn != (bool)toggleSpec["initialValue"] ||
                            toggle.targetGraphic != image || toggle.graphic != indicator || toggle.onValueChanged.GetPersistentEventCount() != 0)
                            throw new InvalidOperationException("保存后 Toggle 状态或图形引用不匹配：" + node["id"]);
                    }
                    if (type == "Slider")
                    {
                        Slider slider = gameObject.GetComponent<Slider>();
                        JObject sliderSpec = (JObject)node["slider"];
                        RectTransform fill = nodes[(string)sliderSpec["fillId"]].GetComponent<RectTransform>();
                        RectTransform handle = nodes[(string)sliderSpec["handleId"]].GetComponent<RectTransform>();
                        if (slider == null || !slider.interactable || slider.minValue != (float)sliderSpec["minValue"] ||
                            slider.maxValue != (float)sliderSpec["maxValue"] || slider.value != (float)sliderSpec["initialValue"] ||
                            slider.wholeNumbers != (bool)sliderSpec["wholeNumbers"] || slider.fillRect != fill || slider.handleRect != handle ||
                            slider.targetGraphic != image || slider.onValueChanged.GetPersistentEventCount() != 0)
                            throw new InvalidOperationException("保存后 Slider 范围、状态或视觉引用与 Spec 契约不匹配：" + node["id"]);
                    }
                }
            }
            var effectMaterials = CollectNodes(spec["root"])
                .Where(n => (string)n["type"] == "Text" && HasTextEffect((JObject)n["style"]))
                .Select(n => AssetDatabase.LoadAssetAtPath<Material>(OwnedMaterialPath(path, (string)n["id"])))
                .ToList();
            if (effectMaterials.Any(m => m == null) || effectMaterials.Distinct().Count() != effectMaterials.Count)
                throw new InvalidOperationException("带效果的 Text 节点必须各自拥有独立材质资产。");
        }

        private static bool HasTextEffect(JObject style)
        {
            return style != null && (style["outlineColor"] != null || style["outlineWidth"] != null ||
                style["shadowColor"] != null || style["shadowOffset"] != null);
        }

        private static Material CreateEffectMaterial(TMP_FontAsset font, JObject style, string nodeId)
        {
            if (font == null || font.material == null)
                throw new InvalidOperationException("无法为 Text 效果创建材质：字体没有共享材质。");
            var material = new Material(font.material) { name = "UIAgent_" + nodeId + "_TextEffects" };
            if (style["outlineColor"] != null)
            {
                RequireMaterialProperty(material, "_OutlineColor", nodeId);
                RequireMaterialProperty(material, "_OutlineWidth", nodeId);
                material.SetColor("_OutlineColor", ReadColor((string)style["outlineColor"]));
                material.SetFloat("_OutlineWidth", (float)style["outlineWidth"]);
            }
            if (style["shadowColor"] != null)
            {
                RequireMaterialProperty(material, "_UnderlayColor", nodeId);
                RequireMaterialProperty(material, "_UnderlayOffsetX", nodeId);
                RequireMaterialProperty(material, "_UnderlayOffsetY", nodeId);
                material.SetColor("_UnderlayColor", ReadColor((string)style["shadowColor"]));
                JArray offset = (JArray)style["shadowOffset"];
                material.SetFloat("_UnderlayOffsetX", (float)offset[0]);
                material.SetFloat("_UnderlayOffsetY", (float)offset[1]);
            }
            return material;
        }

        private static void RequireMaterialProperty(Material material, string property, string nodeId)
        {
            if (!material.HasProperty(property))
                throw new InvalidOperationException("字体材质不支持 Text 效果属性 " + property + "（节点 " + nodeId + "）。");
        }

        private static void VerifyEffectMaterial(Material material, JObject style, string nodeId)
        {
            if (style["outlineColor"] != null)
            {
                RequireMaterialProperty(material, "_OutlineColor", nodeId);
                RequireMaterialProperty(material, "_OutlineWidth", nodeId);
                if (material.GetColor("_OutlineColor") != ReadColor((string)style["outlineColor"]) ||
                    !Mathf.Approximately(material.GetFloat("_OutlineWidth"), (float)style["outlineWidth"]))
                    throw new InvalidOperationException("保存后描边材质参数不匹配：" + nodeId);
            }
            if (style["shadowColor"] != null)
            {
                RequireMaterialProperty(material, "_UnderlayColor", nodeId);
                RequireMaterialProperty(material, "_UnderlayOffsetX", nodeId);
                RequireMaterialProperty(material, "_UnderlayOffsetY", nodeId);
                JArray offset = (JArray)style["shadowOffset"];
                if (material.GetColor("_UnderlayColor") != ReadColor((string)style["shadowColor"]) ||
                    !Mathf.Approximately(material.GetFloat("_UnderlayOffsetX"), (float)offset[0]) ||
                    !Mathf.Approximately(material.GetFloat("_UnderlayOffsetY"), (float)offset[1]))
                    throw new InvalidOperationException("保存后阴影材质参数不匹配：" + nodeId);
            }
        }

        private static string OwnedMaterialPath(string prefabPath, string nodeId)
        {
            return Path.GetDirectoryName(prefabPath).Replace('\\', '/') + "/" +
                Path.GetFileNameWithoutExtension(prefabPath) + "__" + nodeId + ".mat";
        }

        private static void CheckMaterialOutputPath(string assetPath, Report report)
        {
            try
            {
                string disk = DiskPath(assetPath);
                if (File.Exists(disk) || Directory.Exists(disk) || File.Exists(disk + ".meta") ||
                    !string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(assetPath)))
                    Add(report, "MATERIAL_OUTPUT_ALREADY_EXISTS", assetPath, "Text 效果材质目标已存在，禁止覆盖。");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
            { Add(report, "PROJECT_CHECK_FAILED", assetPath, "无法检查 Text 效果材质目标：" + ex.Message); }
        }

        private static void CleanupMaterials(List<OwnedMaterial> materials, Report report)
        {
            foreach (OwnedMaterial owned in materials.AsEnumerable().Reverse())
            {
                if (!owned.assetCreated) continue;
                try
                {
                    if (string.IsNullOrEmpty(owned.assetGuid) || AssetDatabase.AssetPathToGUID(owned.assetPath) != owned.assetGuid ||
                        !AssetDatabase.DeleteAsset(owned.assetPath))
                        report.cleanupErrors.Add("Text 效果材质未能按 GUID 安全清理，请人工检查：" + owned.assetPath);
                }
                catch (Exception ex) { report.cleanupErrors.Add(ex.Message); }
            }
        }

        private static void CheckOutputPath(string assetPath, Report report)
        {
            try
            {
                string projectRoot = Path.GetFullPath(Path.GetDirectoryName(Application.dataPath));
                string current = Path.Combine(projectRoot, "Assets");
                if (IsReparsePoint(projectRoot) || IsReparsePoint(current))
                { Add(report, "OUTPUT_PATH_REDIRECTED", "outputPrefabPath", "Assets 或项目根目录为链接路径，拒绝写入。"); return; }
                string relative = assetPath.Substring("Assets/".Length);
                string[] parts = relative.Split('/');
                for (int i = 0; i < parts.Length; i++)
                {
                    current = Path.Combine(current, parts[i]);
                    if (!File.Exists(current) && !Directory.Exists(current))
                    {
                        string meta = current + ".meta";
                        if (File.Exists(meta) || Directory.Exists(meta)) Add(report, "OUTPUT_METADATA_EXISTS", "outputPrefabPath", "目标或其父路径已有残留 .meta。");
                        continue;
                    }
                    if (IsReparsePoint(current)) { Add(report, "OUTPUT_PATH_REDIRECTED", "outputPrefabPath", "目标路径包含链接或 junction，拒绝写入。"); return; }
                    bool isLast = i == parts.Length - 1;
                    bool directory = Directory.Exists(current);
                    if (isLast) Add(report, "OUTPUT_ALREADY_EXISTS", "outputPrefabPath", "目标文件或目录已存在，禁止覆盖。");
                    else if (!directory) { Add(report, "OUTPUT_PARENT_NOT_DIRECTORY", "outputPrefabPath", "目标父路径已被文件占用。"); return; }
                }
                if (!string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(assetPath)) && !report.errors.Any(e => e.field == "outputPrefabPath"))
                    Add(report, "OUTPUT_ALREADY_EXISTS", "outputPrefabPath", "Unity 已登记目标资源，禁止覆盖。");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
            { Add(report, "PROJECT_CHECK_FAILED", "outputPrefabPath", "无法完整检查目标路径：" + ex.Message); }
        }

        private static void EnsureFolders(string assetPath, List<KeyValuePair<string, string>> created)
        {
            string parent = "Assets";
            foreach (string part in assetPath.Substring("Assets/".Length).Split('/'))
            {
                string next = parent + "/" + part;
                if (!AssetDatabase.IsValidFolder(next))
                {
                    string disk = DiskPath(next);
                    if (File.Exists(disk) || Directory.Exists(disk) || File.Exists(disk + ".meta"))
                        throw new InvalidOperationException("输出父目录或元数据冲突：" + next);
                    string guid = AssetDatabase.CreateFolder(parent, part);
                    if (string.IsNullOrEmpty(guid) || AssetDatabase.GUIDToAssetPath(guid) != next)
                        throw new InvalidOperationException("Unity 未能创建目标文件夹：" + next);
                    created.Add(new KeyValuePair<string, string>(next, guid));
                }
                parent = next;
            }
        }

        private static void CleanupFolders(List<KeyValuePair<string, string>> folders, Report report)
        {
            foreach (var folder in folders.AsEnumerable().Reverse())
            {
                try
                {
                    string disk = DiskPath(folder.Key);
                    if (AssetDatabase.AssetPathToGUID(folder.Key) != folder.Value || Directory.GetFileSystemEntries(disk).Length != 0 || !AssetDatabase.DeleteAsset(folder.Key))
                        report.cleanupErrors.Add("保留无法确认所有权或非空的目录：" + folder.Key);
                }
                catch (Exception ex) { report.cleanupErrors.Add(ex.Message); }
            }
        }

        private static bool IsReparsePoint(string path)
        {
            try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
        }

        private static void AddDependency(IDictionary<string, JObject> values, string path)
        {
            if (string.IsNullOrEmpty(path) || values.ContainsKey(path)) return;
            values[path] = new JObject { ["path"] = path, ["guid"] = AssetDatabase.AssetPathToGUID(path), ["hash"] = AssetDatabase.GetAssetDependencyHash(path).ToString() };
        }

        private static IEnumerable<TMP_FontAsset> CollectFonts(TMP_FontAsset font, HashSet<TMP_FontAsset> seen)
        {
            if (font == null || !seen.Add(font)) yield break;
            yield return font;
            if (font.fallbackFontAssetTable != null)
                foreach (TMP_FontAsset fallback in font.fallbackFontAssetTable)
                    foreach (TMP_FontAsset nested in CollectFonts(fallback, seen)) yield return nested;
        }

        private static bool HasGlyph(TMP_FontAsset font, uint value, HashSet<TMP_FontAsset> seen)
        {
            if (font == null || !seen.Add(font)) return false;
            if (font.characterLookupTable.ContainsKey(value)) return true;
            return font.fallbackFontAssetTable != null && font.fallbackFontAssetTable.Any(f => HasGlyph(f, value, seen));
        }

        private static IEnumerable<uint> CodePoints(string value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                if (char.IsHighSurrogate(value[i])) yield return (uint)char.ConvertToUtf32(value[i], value[++i]);
                else yield return value[i];
            }
        }

        private static IEnumerable<string> CollectText(JToken nodeToken)
        {
            JObject node = nodeToken as JObject;
            if (node == null) yield break;
            if (node["type"]?.Type == JTokenType.String && (string)node["type"] == "Text") yield return (string)node["text"];
            JArray children = node["children"] as JArray;
            if (children != null) foreach (JToken child in children)
                foreach (string text in CollectText(child)) yield return text;
        }

        private static IEnumerable<JObject> CollectNodes(JToken nodeToken)
        {
            JObject node = nodeToken as JObject;
            if (node == null) yield break;
            yield return node;
            JArray children = node["children"] as JArray;
            if (children != null) foreach (JToken child in children)
                foreach (JObject nested in CollectNodes(child)) yield return nested;
        }

        private static Vector2 ReadVector(JArray value) => new Vector2((float)value[0], (float)value[1]);

        private static Color ReadColor(string value)
        {
            Color color;
            if (!ColorUtility.TryParseHtmlString(value, out color)) throw new InvalidOperationException("无效颜色：" + value);
            return color;
        }

        private static void Add(Report report, string code, string field, string message)
        {
            report.errors.Add(new UiSpecValidator.ValidationError { code = code, field = field, message = message });
        }

        private static string DiskPath(string assetPath)
        {
            return Path.Combine(Path.GetDirectoryName(Application.dataPath), assetPath.Replace('/', Path.DirectorySeparatorChar));
        }
    }
}
