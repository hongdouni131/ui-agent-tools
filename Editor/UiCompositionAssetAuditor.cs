using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace UIAgent.Tools.Editor
{
    /// <summary>Read-only comparison of a saved composition against its caller-supplied v2 Spec.</summary>
    public static class UiCompositionAssetAuditor
    {
        public sealed class Finding
        {
            public string ruleId;
            public string severity = "error";
            public string nodeId;
            public string objectPath;
            public string property;
            public string message;
            public object expected;
            public object actual;
        }

        public sealed class Report
        {
            public int schemaVersion = 1;
            public string reportType = "ui_composition_asset_audit";
            public string status = "incomplete";
            public bool complete;
            public string prefabPath;
            public string prefabGuid;
            public string specFingerprint;
            public int expectedNodes, scannedNodes, comparedNodes, errorCount;
            public List<Finding> findings = new List<Finding>();
            public string[] limitations = {
                "Checks only the saved Prefab against the supplied Spec; no scene overrides or runtime changes are checked.",
                "No asset writes, reimports, Canvas rebuilds, repairs or generation are performed.",
                "Checks serialized RectTransform and declared LayoutElement/LayoutGroup settings, but does not evaluate final Canvas layout, visual quality, licensing or runtime input.",
                "Matching a GUID does not prove unchanged image content; SpriteAtlas packing contracts remain unsupported."
            };
        }

        public static string AuditPrefabJson(string specJson) => JsonConvert.SerializeObject(AuditPrefab(specJson));

        public static Report AuditPrefab(string specJson)
        {
            var report = new Report();
            var validation = UiCompositionSpecValidator.Validate(specJson);
            if (!validation.valid)
            {
                foreach (var e in validation.errors)
                    Add(report, "SPEC_INVALID", null, null, e.field, e.code + ": " + e.message, null, null);
                return Finish(report);
            }
            report.prefabPath = (string)validation.validatedSpec["outputPrefabPath"];
            report.specFingerprint = validation.specFingerprint;
            report.expectedNodes = validation.nodeCount;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Add(report, "EDITOR_NOT_READY", null, null, "editor", "请在空闲 Edit Mode 审计已保存 Prefab。", "idle Edit Mode", null);
                return Finish(report);
            }
            try
            {
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(report.prefabPath);
                report.prefabGuid = AssetDatabase.AssetPathToGUID(report.prefabPath);
                if (prefab == null || !PrefabUtility.IsPartOfPrefabAsset(prefab))
                    Add(report, "PREFAB_NOT_FOUND", null, null, "outputPrefabPath", "指定路径没有可读取的 Prefab。", report.prefabPath, null);
                else CompareRoot(validation.validatedSpec, prefab, report);
            }
            catch (Exception e)
            {
                report.complete = false;
                Add(report, "AUDIT_EXCEPTION", null, null, "audit", "审计中断：" + e.Message, null, e.GetType().Name);
            }
            return Finish(report);
        }

        private static void CompareRoot(JObject spec, GameObject root, Report report)
        {
            var expected = Nodes((JObject)spec["root"]).ToDictionary(n => (string)n["id"], StringComparer.Ordinal);
            var rootCanvas = root.GetComponent<Canvas>();
            var rootScaler = root.GetComponent<CanvasScaler>();
            var rootRaycaster = root.GetComponent<GraphicRaycaster>();
            if (rootCanvas == null || rootScaler == null || rootRaycaster == null)
                Add(report, "CANVAS_COMPONENTS_MISSING", null, root.transform, "Canvas/CanvasScaler/GraphicRaycaster",
                    "根节点必须包含 Canvas、CanvasScaler 与 GraphicRaycaster。", "all present", new { canvas = rootCanvas != null, scaler = rootScaler != null, raycaster = rootRaycaster != null });
            JObject dialogSpec = spec["confirmationDialog"] as JObject;
            bool expectedRootActive = dialogSpec == null;
            if (rootCanvas != null && rootScaler != null && rootRaycaster != null &&
                (root.activeSelf != expectedRootActive || !rootCanvas.enabled || rootCanvas.renderMode != RenderMode.ScreenSpaceOverlay || !rootRaycaster.enabled))
                Add(report, "CANVAS_CONFIGURATION_MISMATCH", null, root.transform, "Canvas/CanvasScaler/GraphicRaycaster",
                    "普通 Prefab 根对象必须启用；确认弹窗根对象默认隐藏；Canvas 使用 Screen Space Overlay；GraphicRaycaster 组件启用。", new { rootActive = expectedRootActive, canvas = "enabled Screen Space Overlay", raycaster = "enabled" }, new { rootActive = root.activeSelf, canvasEnabled = rootCanvas.enabled, mode = rootCanvas.renderMode.ToString(), raycasterEnabled = rootRaycaster.enabled });
            if (rootScaler != null)
            {
                Vector2 expectedResolution = new Vector2(
                    (float)spec["referenceResolution"]["width"], (float)spec["referenceResolution"]["height"]);
                bool scalerMatches = rootScaler.enabled &&
                    rootScaler.uiScaleMode == CanvasScaler.ScaleMode.ScaleWithScreenSize &&
                    Vector2.Distance(rootScaler.referenceResolution, expectedResolution) <= 0.1f &&
                    Math.Abs(rootScaler.matchWidthOrHeight - 0.5f) <= 0.001f;
                if (!scalerMatches)
                    Add(report, "CANVAS_SCALER_MISMATCH", null, root.transform, "CanvasScaler",
                        "CanvasScaler 必须启用、按屏幕尺寸缩放、使用 Spec 分辨率和 0.5 匹配比例。",
                        new { enabled = true, mode = CanvasScaler.ScaleMode.ScaleWithScreenSize.ToString(),
                            width = expectedResolution.x, height = expectedResolution.y, matchWidthOrHeight = 0.5f },
                        new { enabled = rootScaler.enabled, mode = rootScaler.uiScaleMode.ToString(),
                            width = rootScaler.referenceResolution.x, height = rootScaler.referenceResolution.y,
                            matchWidthOrHeight = rootScaler.matchWidthOrHeight });
            }
            var actual = new Dictionary<string, List<Transform>>(StringComparer.Ordinal);
            var stack = new Stack<Transform>();
            stack.Push(root.transform);
            while (stack.Count > 0)
            {
                Transform t = stack.Pop();
                if (++report.scannedNodes > 1024)
                {
                    report.complete = false;
                    Add(report, "OBJECT_LIMIT", null, t, "hierarchy", "超过 1024 个对象，报告不完整。", 1024, report.scannedNodes);
                    return;
                }
                var identity = t.GetComponent<UIAgent.Tools.UiNodeIdentity>();
                if (identity == null || string.IsNullOrWhiteSpace(identity.Id))
                    Add(report, "NODE_ID_MISSING", null, t, "UiNodeIdentity.Id", "对象缺少非空稳定节点身份。", "nonempty node id", identity == null ? null : identity.Id);
                else
                {
                    if (!actual.TryGetValue(identity.Id, out var matches))
                        actual.Add(identity.Id, matches = new List<Transform>());
                    matches.Add(t);
                }
                for (int i = t.childCount - 1; i >= 0; --i) stack.Push(t.GetChild(i));
            }
            foreach (var entry in actual)
            {
                if (entry.Value.Count > 1)
                    Add(report, "NODE_ID_DUPLICATE", entry.Key, entry.Value[0], "UiNodeIdentity.Id", "节点身份重复，拒绝猜测匹配对象。", 1, entry.Value.Count);
                if (!expected.ContainsKey(entry.Key))
                    Add(report, "NODE_UNEXPECTED", entry.Key, entry.Value[0], "UiNodeIdentity.Id", "Prefab 包含 Spec 未声明的节点。", null, entry.Key);
            }
            foreach (var pair in expected)
            {
                if (!actual.TryGetValue(pair.Key, out var matches))
                    Add(report, "NODE_MISSING", pair.Key, null, "UiNodeIdentity.Id", "Spec 节点在 Prefab 中缺失。", pair.Key, null);
                else if (matches.Count == 1)
                {
                    bool isBackdrop = dialogSpec != null && pair.Key == (string)dialogSpec["backdropId"];
                    CompareNode(pair.Value, matches[0], report, matches[0] == root.transform, isBackdrop);
                    report.comparedNodes++;
                }
            }
            CompareConfirmationDialog(dialogSpec, root, actual, report);
            if (!report.findings.Any(f => f.ruleId == "OBJECT_LIMIT")) report.complete = true;
        }

        private static void CompareConfirmationDialog(JObject spec, GameObject root,
            IDictionary<string, List<Transform>> nodes, Report report)
        {
            var dialog = root.GetComponent<UIAgent.Tools.ConfirmationDialog>();
            if (spec == null)
            {
                if (dialog != null)
                    Add(report, "DIALOG_COMPONENT_UNEXPECTED", null, root.transform, "ConfirmationDialog",
                        "未声明 confirmationDialog 的 Spec 不应添加弹窗控制器。", null, "present");
                return;
            }
            if (dialog == null)
            {
                Add(report, "DIALOG_COMPONENT_MISSING", null, root.transform, "ConfirmationDialog",
                    "Spec 声明了确认弹窗，但 Prefab 根节点缺少运行时控制器。", "present", null);
                return;
            }
            Button blocker = FindNodeButton(nodes, (string)spec["backdropId"]);
            Button cancel = FindNodeButton(nodes, (string)spec["cancelButtonId"]);
            Button confirm = FindNodeButton(nodes, (string)spec["confirmButtonId"]);
            Selectable focus = FindNodeSelectable(nodes, (string)spec["initialFocusId"]);
            bool matches = blocker != null && cancel != null && confirm != null && focus != null &&
                dialog.BackgroundBlocker == blocker && dialog.CancelButton == cancel &&
                dialog.ConfirmButton == confirm && dialog.InitialFocus == focus &&
                dialog.OnCancelled != null && dialog.OnConfirmed != null;
            if (!matches)
                Add(report, "DIALOG_REFERENCE_MISMATCH", null, root.transform, "ConfirmationDialog",
                    "运行时控制器必须引用 Spec 指定的背景、确认/取消按钮和初始焦点。",
                    new { backdropId = (string)spec["backdropId"], cancelButtonId = (string)spec["cancelButtonId"],
                        confirmButtonId = (string)spec["confirmButtonId"], initialFocusId = (string)spec["initialFocusId"] },
                    new { blockerMatches = dialog.BackgroundBlocker == blocker, cancelMatches = dialog.CancelButton == cancel,
                        confirmMatches = dialog.ConfirmButton == confirm, focusMatches = dialog.InitialFocus == focus,
                        eventsPresent = dialog.OnCancelled != null && dialog.OnConfirmed != null });
        }

        private static Button FindNodeButton(IDictionary<string, List<Transform>> nodes, string id)
        {
            return nodes.TryGetValue(id, out List<Transform> matches) && matches.Count == 1
                ? matches[0].GetComponent<Button>() : null;
        }

        private static Selectable FindNodeSelectable(IDictionary<string, List<Transform>> nodes, string id)
        {
            return nodes.TryGetValue(id, out List<Transform> matches) && matches.Count == 1
                ? matches[0].GetComponent<Selectable>() : null;
        }

        private static void CompareNode(JObject node, Transform t, Report report, bool isCanvasRoot, bool isBackdrop)
        {
            string id = (string)node["id"], type = (string)node["type"];
            ComparePlacement(node, t, report, isCanvasRoot);
            CompareChildrenLayout(node, t, report);
            Image image = t.GetComponent<Image>();
            TextMeshProUGUI text = t.GetComponent<TextMeshProUGUI>();
            Button button = t.GetComponent<Button>();
            Toggle toggle = t.GetComponent<Toggle>();
            Slider slider = t.GetComponent<Slider>();
            bool componentsMatch = type == "Text" ? text != null && image == null && button == null :
                image != null && text == null && ((type == "Button" || isBackdrop) == (button != null)) &&
                ((type == "Toggle") == (toggle != null)) && ((type == "Slider") == (slider != null));
            if (!componentsMatch || t.GetComponents<Graphic>().Length != 1)
                Add(report, "NODE_COMPONENT_MISMATCH", id, t, "components", "节点组件与 Spec 类型不一致。", type,
                    t.GetComponents<Component>().Where(c => c != null).Select(c => c.GetType().Name).ToArray());
            foreach (Graphic g in t.GetComponents<Graphic>())
            {
                bool sliderHandle = type == "Panel" && t.parent != null && t.parent.GetComponent<Slider>() != null &&
                    (t.parent.GetComponent<Slider>().handleRect == t as RectTransform);
                if (g.raycastTarget != ((type == "Button" || type == "Toggle" || type == "Slider" || isBackdrop) && g == image || sliderHandle && g == image))
                    Add(report, "RAYCAST_CONTRACT_MISMATCH", id, t, "Graphic.raycastTarget",
                        "v2 契约要求仅 Button、Toggle、Slider、Slider 手柄或声明的弹窗背景拦截器 Graphic 接收 Raycast。",
                        (type == "Button" || type == "Toggle" || type == "Slider" || isBackdrop) && g == image || sliderHandle && g == image, g.raycastTarget);
            }
            if (type == "Button" && button != null && (image == null || button.targetGraphic != image))
                Add(report, "BUTTON_TARGET_MISMATCH", id, t, "Button.targetGraphic", "Button 的 targetGraphic 必须引用自身 Image。", "own Image", button.targetGraphic == null ? null : button.targetGraphic.name);
            if (isBackdrop && button != null && (button.targetGraphic != image || !button.interactable ||
                button.transition != Selectable.Transition.None || button.onClick.GetPersistentEventCount() != 0))
                Add(report, "DIALOG_BLOCKER_MISMATCH", id, t, "Button", "背景拦截 Button 必须启用、使用自身 Image、无视觉状态切换且不绑定持久化回调。",
                    new { interactable = true, targetGraphic = "own Image", transition = "None", persistentCallbacks = 0 },
                    new { interactable = button.interactable, targetMatches = button.targetGraphic == image,
                        transition = button.transition.ToString(), persistentCallbacks = button.onClick.GetPersistentEventCount() });
            if (type == "Toggle" && toggle != null)
            {
                JObject toggleSpec = (JObject)node["toggle"];
                string indicatorId = (string)toggleSpec["indicatorId"];
                Transform indicatorNode = t.Find(indicatorId);
                Graphic expectedGraphic = indicatorNode == null ? null : indicatorNode.GetComponent<TextMeshProUGUI>();
                bool toggleMatches = toggle.interactable && toggle.isOn == (bool)toggleSpec["initialValue"] &&
                    toggle.transition == Selectable.Transition.ColorTint && toggle.targetGraphic == image &&
                    toggle.graphic == expectedGraphic && toggle.onValueChanged.GetPersistentEventCount() == 0;
                if (!toggleMatches)
                    Add(report, "TOGGLE_CONTRACT_MISMATCH", id, t, "Toggle",
                        "Toggle 初始值、可交互状态、状态标记、targetGraphic 或持久化回调与 Spec 契约不一致。",
                        new { initialValue = (bool)toggleSpec["initialValue"], interactable = true,
                            indicatorId, targetGraphic = "own Image", persistentCallbacks = 0 },
                        new { isOn = toggle.isOn, interactable = toggle.interactable,
                            indicatorMatches = toggle.graphic == expectedGraphic, targetGraphicMatches = toggle.targetGraphic == image,
                            transition = toggle.transition.ToString(), persistentCallbacks = toggle.onValueChanged.GetPersistentEventCount() });
            }
            if (type == "Slider" && slider != null)
            {
                JObject sliderSpec = (JObject)node["slider"];
                string fillId = (string)sliderSpec["fillId"], handleId = (string)sliderSpec["handleId"];
                Transform fill = t.Find(fillId), handle = t.Find(handleId);
                bool matches = slider.interactable && slider.minValue == (float)sliderSpec["minValue"] &&
                    slider.maxValue == (float)sliderSpec["maxValue"] && slider.value == (float)sliderSpec["initialValue"] &&
                    slider.wholeNumbers == (bool)sliderSpec["wholeNumbers"] && slider.targetGraphic == image &&
                    slider.fillRect == (fill == null ? null : fill as RectTransform) &&
                    slider.handleRect == (handle == null ? null : handle as RectTransform) &&
                    slider.onValueChanged.GetPersistentEventCount() == 0;
                if (!matches)
                    Add(report, "SLIDER_CONTRACT_MISMATCH", id, t, "Slider", "Slider 范围、初始值、填充/手柄引用或持久化回调与 Spec 不一致。",
                        new { minValue = (float)sliderSpec["minValue"], maxValue = (float)sliderSpec["maxValue"], initialValue = (float)sliderSpec["initialValue"], fillId, handleId, persistentCallbacks = 0 },
                        new { minValue = slider.minValue, maxValue = slider.maxValue, value = slider.value, fillMatches = slider.fillRect == (fill as RectTransform), handleMatches = slider.handleRect == (handle as RectTransform), persistentCallbacks = slider.onValueChanged.GetPersistentEventCount() });
            }
            JObject style = (JObject)node["style"];
            if (type == "Text" && text != null)
            {
                if (text.text != (string)node["text"])
                    Add(report, "TEXT_CONTENT_MISMATCH", id, t, "TMP.text", "文字内容与 Spec 不一致。", (string)node["text"], text.text);
                float expectedFontSize = (float)style["fontSize"];
                if (Math.Abs(text.fontSize - expectedFontSize) > 0.01f)
                    Add(report, "TEXT_STYLE_MISMATCH", id, t, "TMP.fontSize", "字体尺寸与 Spec 不一致。", style["fontSize"], text.fontSize);
                Color expectedColor = ColorUtility.TryParseHtmlString((string)style["color"], out Color parsed) ? parsed : Color.white;
                if (text.color != expectedColor)
                    Add(report, "TEXT_STYLE_MISMATCH", id, t, "TMP.color", "文字颜色与 Spec 不一致。", expectedColor.ToString(), text.color.ToString());
                string expectedFontPath = (string)node.Root["fontAssetPath"];
                string actualFontPath = text.font == null ? null : AssetDatabase.GetAssetPath(text.font);
                if (actualFontPath != expectedFontPath)
                    Add(report, "TEXT_FONT_MISMATCH", id, t, "TMP.font", "字体资源与 Spec 声明不一致。", expectedFontPath, actualFontPath);
                return;
            }
            if (image == null) return;
            if (style["color"] != null)
            {
                Color expectedColor = ColorUtility.TryParseHtmlString((string)style["color"], out Color parsed) ? parsed : Color.white;
                if (image.color != expectedColor)
                    Add(report, "IMAGE_COLOR_MISMATCH", id, t, "Image.color", "纯色或着色值与 Spec 不一致。", expectedColor.ToString(), image.color.ToString());
            }
            Image.Type expectedType = (string)style["imageType"] == "sliced" ? Image.Type.Sliced : Image.Type.Simple;
            if (image.type != expectedType)
                Add(report, "IMAGE_TYPE_MISMATCH", id, t, "Image.type", "Image 类型与 Spec 不一致。", expectedType.ToString(), image.type.ToString());
            string path = (string)style["spritePath"];
            if (path == null)
            {
                if (image.sprite != null || image.overrideSprite != null)
                    Add(report, "SPRITE_UNEXPECTED", id, t, "Image.sprite/overrideSprite", "纯色节点出现了 Spec 未声明的 Sprite。", null, SpriteInfo(image.overrideSprite));
                return;
            }
            var issues = new List<UiCompositionSpriteResolver.Issue>();
            Sprite sprite = UiCompositionSpriteResolver.Resolve(style, issues);
            foreach (var issue in issues)
                Add(report, issue.code == "SPRITE_BORDER_EXCEEDS_RECT" ? "SPRITE_BORDER_OUT_OF_BOUNDS" : issue.code,
                    id, t, issue.property, issue.message, style.DeepClone(), SpriteInfo(image.sprite));
            if (image.sprite == null)
                Add(report, "SPRITE_REFERENCE_MISSING", id, t, "Image.sprite", "Declared Sprite reference is missing.", style.DeepClone(), null);
            else if (sprite == null || image.sprite != sprite)
                Add(report, "SPRITE_REFERENCE_MISMATCH", id, t, "Image.sprite", "Actual Sprite (including sub-asset identity) differs from the Spec.", UiCompositionSpriteResolver.Describe(sprite), SpriteInfo(image.sprite));
        }

        private static void ComparePlacement(JObject node, Transform t, Report report, bool isCanvasRoot)
        {
            JObject placement = (JObject)node["placement"];
            string mode = (string)placement["mode"];
            RectTransform actual = t as RectTransform;
            if (actual == null)
            {
                Add(report, "RECT_TRANSFORM_MISMATCH", (string)node["id"], t, "RectTransform",
                    "Spec 节点必须使用 RectTransform。", "RectTransform", t.GetType().Name);
                return;
            }

            Vector2 expectedAnchorMin, expectedAnchorMax, expectedPivot, expectedPosition, expectedSize;
            if (mode == "layout")
            {
                JArray preferredSize = (JArray)placement["preferredSize"];
                expectedAnchorMin = expectedAnchorMax = expectedPivot = new Vector2(0.5f, 0.5f);
                expectedPosition = Vector2.zero;
                expectedSize = new Vector2((float)preferredSize[0], (float)preferredSize[1]);
            }
            else
            {
                expectedAnchorMin = ReadVector((JArray)placement["anchorMin"]);
                expectedAnchorMax = ReadVector((JArray)placement["anchorMax"]);
                expectedPivot = ReadVector((JArray)placement["pivot"]);
                expectedPosition = ReadVector((JArray)placement["anchoredPosition"]);
                expectedSize = ReadVector((JArray)placement["sizeDelta"]);
            }

            // Overlay Canvas owns the root rectangle. LayoutGroup owns its children's
            // anchors, positions and sizes; their serialized driven values can be zero.
            // Compare explicit anchors only, and validate layout inputs below instead.
            bool sliderDrivesAnchors = t.parent != null && t.parent.GetComponent<Slider>() is Slider parentSlider &&
                (parentSlider.fillRect == actual || parentSlider.handleRect == actual);
            if (!isCanvasRoot && !sliderDrivesAnchors && mode == "anchors" &&
                (!Approximately(actual.anchorMin, expectedAnchorMin) ||
                !Approximately(actual.anchorMax, expectedAnchorMax) ||
                !Approximately(actual.pivot, expectedPivot) ||
                !Approximately(actual.anchoredPosition, expectedPosition) ||
                !Approximately(actual.sizeDelta, expectedSize)))
                Add(report, "RECT_TRANSFORM_MISMATCH", (string)node["id"], t, "RectTransform",
                    "RectTransform 锚点、Pivot、位置或尺寸与 Spec 不一致。",
                    new { anchorMin = VectorInfo(expectedAnchorMin), anchorMax = VectorInfo(expectedAnchorMax), pivot = VectorInfo(expectedPivot), anchoredPosition = VectorInfo(expectedPosition), sizeDelta = VectorInfo(expectedSize) },
                    new { anchorMin = VectorInfo(actual.anchorMin), anchorMax = VectorInfo(actual.anchorMax), pivot = VectorInfo(actual.pivot), anchoredPosition = VectorInfo(actual.anchoredPosition), sizeDelta = VectorInfo(actual.sizeDelta) });

            LayoutElement element = t.GetComponent<LayoutElement>();
            if (mode != "layout")
            {
                if (element != null)
                    Add(report, "LAYOUT_ELEMENT_MISMATCH", (string)node["id"], t, "LayoutElement",
                        "锚点驱动节点存在 Spec 未声明的 LayoutElement。", null, LayoutElementInfo(element));
                return;
            }

            JArray size = (JArray)placement["preferredSize"];
            if (element == null || !element.enabled || element.ignoreLayout ||
                !Mathf.Approximately(element.preferredWidth, (float)size[0]) ||
                !Mathf.Approximately(element.preferredHeight, (float)size[1]))
                Add(report, "LAYOUT_ELEMENT_MISMATCH", (string)node["id"], t, "LayoutElement",
                    "LayoutElement 必须启用、参与布局，且 preferredWidth/Height 与 Spec 一致。",
                    new { enabled = true, ignoreLayout = false, preferredWidth = (float)size[0], preferredHeight = (float)size[1] },
                    element == null ? null : LayoutElementInfo(element));
        }

        private static Vector2 ReadVector(JArray value) => new Vector2((float)value[0], (float)value[1]);

        private static float[] VectorInfo(Vector2 value) => new[] { value.x, value.y };

        private static bool Approximately(Vector2 actual, Vector2 expected) =>
            Vector2.Distance(actual, expected) <= 0.01f;

        private static object LayoutElementInfo(LayoutElement element) => new
        {
            enabled = element.enabled,
            preferredWidth = element.preferredWidth,
            preferredHeight = element.preferredHeight,
            minWidth = element.minWidth,
            minHeight = element.minHeight,
            flexibleWidth = element.flexibleWidth,
            flexibleHeight = element.flexibleHeight,
            ignoreLayout = element.ignoreLayout
        };

        private static void CompareChildrenLayout(JObject node, Transform t, Report report)
        {
            JObject expected = (JObject)node["childrenLayout"];
            string mode = (string)expected["mode"];
            HorizontalOrVerticalLayoutGroup actual = t.GetComponent<HorizontalOrVerticalLayoutGroup>();
            bool expectedGroup = mode == "vertical" || mode == "horizontal";
            if (!expectedGroup)
            {
                if (actual != null)
                    Add(report, "CHILDREN_LAYOUT_MISMATCH", (string)node["id"], t, "LayoutGroup",
                        "Prefab 存在 Spec 未声明的子项布局组件。", mode, actual.GetType().Name);
                return;
            }

            Type expectedType = mode == "vertical" ? typeof(VerticalLayoutGroup) : typeof(HorizontalLayoutGroup);
            bool matches = actual != null && actual.GetType() == expectedType &&
                Mathf.Approximately(actual.spacing, (float)expected["spacing"]) &&
                actual.padding.left == (int)expected["padding"][0] && actual.padding.bottom == (int)expected["padding"][1] &&
                actual.padding.right == (int)expected["padding"][2] && actual.padding.top == (int)expected["padding"][3] &&
                actual.childAlignment.ToString().Equals((string)expected["childAlignment"], StringComparison.OrdinalIgnoreCase) &&
                actual.childControlWidth == (bool)expected["childControlWidth"] &&
                actual.childControlHeight == (bool)expected["childControlHeight"] &&
                actual.childForceExpandWidth == (bool)expected["childForceExpandWidth"] &&
                actual.childForceExpandHeight == (bool)expected["childForceExpandHeight"];
            if (!matches)
                Add(report, "CHILDREN_LAYOUT_MISMATCH", (string)node["id"], t, "LayoutGroup",
                    "LayoutGroup 类型或参数与 Spec 不一致。", expected.DeepClone(), actual == null ? null : actual.GetType().Name);
        }

        private static object SpriteInfo(Sprite sprite)
        {
            if (sprite == null) return null;
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(sprite, out string guid, out long localId);
            return new { path = AssetDatabase.GetAssetPath(sprite), guid, localId = localId.ToString(System.Globalization.CultureInfo.InvariantCulture), sprite.name };
        }

        private static IEnumerable<JObject> Nodes(JObject node)
        {
            yield return node;
            if (node["children"] is JArray children)
                foreach (JObject child in children) foreach (JObject descendant in Nodes(child)) yield return descendant;
        }

        private static void Add(Report report, string rule, string id, Transform node, string property, string message, object expected, object actual)
        {
            string path = node == null ? null : node.name;
            if (node != null) for (Transform p = node.parent; p != null; p = p.parent) path = p.name + "/" + path;
            report.findings.Add(new Finding { ruleId = rule, nodeId = id, objectPath = path, property = property, message = message, expected = expected, actual = actual });
        }

        private static Report Finish(Report report)
        {
            report.errorCount = report.findings.Count;
            report.status = !report.complete ? "incomplete" : report.errorCount > 0 ? "failed" : "passed";
            return report;
        }
    }
}
