using System;
using System.Collections.Generic;
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
    /// <summary>Evidence from an isolated copy of a saved, Spec-matching Prefab. Never edits the source.</summary>
    public static class UiCompositionDiagnostics
    {
        public static string DiagnoseJson(string specJson)
        {
            var audit = UiCompositionAssetAuditor.AuditPrefab(specJson);
            var report = new JObject {
                ["schemaVersion"] = 1, ["reportType"] = "ui_composition_diagnosis",
                ["status"] = "blocked", ["complete"] = false,
                ["projectRoot"] = Path.GetDirectoryName(Application.dataPath),
                ["prefabPath"] = audit.prefabPath, ["prefabGuid"] = audit.prefabGuid,
                ["specFingerprint"] = audit.specFingerprint,
                ["assetAudit"] = JObject.FromObject(audit),
                ["basis"] = "saved_prefab_isolated_reference_resolution",
                ["nodes"] = new JArray(), ["issues"] = new JArray(), ["errors"] = new JArray(),
                ["limitations"] = new JArray(
                    "Reference-resolution geometry, not a runtime screenshot or input test; inactive dialog roots are temporarily shown in the isolated copy.",
                    "Outside-canvas rectangles and text capacity are measured; Simple aspect changes are risks, not proof of incorrect art.",
                    "Suggested patches are proposals. Moving a parent can introduce overlap; rerun diagnosis, inspect screenshots and test input on the NEW Prefab.",
                    "No arbitrary scripts, aesthetic scoring, automatic asset repair or existing Prefab edits are supported.")
            };
            // Do not attribute hand-edited asset differences to the Spec or guess node identity.
            if (!audit.complete || audit.errorCount != 0) return report.ToString(Formatting.None);
            JObject spec = UiCompositionSpecValidator.Validate(specJson).validatedSpec;
            GameObject copy = null;
            Scene preview = default(Scene);
            try
            {
                var source = AssetDatabase.LoadAssetAtPath<GameObject>(audit.prefabPath);
                var allowed = new HashSet<Type> { typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler),
                    typeof(CanvasRenderer), typeof(GraphicRaycaster), typeof(Image), typeof(TextMeshProUGUI),
                    typeof(Button), typeof(Toggle), typeof(Slider), typeof(LayoutElement),
                    typeof(VerticalLayoutGroup), typeof(HorizontalLayoutGroup), typeof(UiNodeIdentity), typeof(ConfirmationDialog) };
                if (source == null || source.GetComponentsInChildren<Component>(true).Any(c => c == null || !allowed.Contains(c.GetType())))
                    throw new InvalidOperationException("Prefab 含未支持或缺失的组件；不实例化任意脚本。请先人工检查。");
                // TMP can populate dynamic font assets during OnEnable/mesh measurement. Check existing glyphs first.
                foreach (TextMeshProUGUI text in source.GetComponentsInChildren<TextMeshProUGUI>(true))
                    for (int i = 0; i < text.text.Length; ++i)
                    {
                        uint codePoint = char.IsHighSurrogate(text.text[i])
                            ? (uint)char.ConvertToUtf32(text.text[i], text.text[++i]) : text.text[i];
                        if (!HasGlyph(text.font, codePoint, new HashSet<TMP_FontAsset>()))
                            throw new InvalidOperationException("字体缺少已存在字形；为避免诊断时动态烘焙而停止：" + text.name + " U+" + codePoint.ToString("X4"));
                    }
                report["prefabDependencyHash"] = AssetDatabase.GetAssetDependencyHash(audit.prefabPath).ToString();
                // Hash each dependency explicitly: an already-loaded Prefab's aggregate hash can lag a texture reimport.
                report["dependencies"] = new JArray(AssetDatabase.GetDependencies(audit.prefabPath, true)
                    .OrderBy(p => p, StringComparer.Ordinal).Select(p => new JObject {
                        ["path"] = p, ["guid"] = AssetDatabase.AssetPathToGUID(p),
                        ["dependencyHash"] = AssetDatabase.GetAssetDependencyHash(p).ToString() }));
                report["referenceResolution"] = spec["referenceResolution"].DeepClone();
                preview = EditorSceneManager.NewPreviewScene();
                copy = (GameObject)PrefabUtility.InstantiatePrefab(source, preview);
                copy.hideFlags = HideFlags.HideAndDontSave;
                copy.SetActive(false);
                Canvas canvas = copy.GetComponent<Canvas>();
                CanvasScaler scaler = copy.GetComponent<CanvasScaler>();
                RectTransform root = copy.GetComponent<RectTransform>();
                scaler.enabled = false;
                canvas.renderMode = RenderMode.WorldSpace;
                root.anchorMin = root.anchorMax = Vector2.zero;
                root.localPosition = Vector3.zero;
                root.localRotation = Quaternion.identity;
                root.localScale = Vector3.one;
                root.sizeDelta = new Vector2((float)spec["referenceResolution"]["width"], (float)spec["referenceResolution"]["height"]);
                copy.SetActive(true);
                foreach (LayoutGroup group in copy.GetComponentsInChildren<LayoutGroup>(true).Reverse())
                    LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)group.transform);
                var nodes = Walk((JObject)spec["root"]).ToDictionary(n => (string)n["id"], StringComparer.Ordinal);
                foreach (UiNodeIdentity identity in copy.GetComponentsInChildren<UiNodeIdentity>(true))
                {
                    RectTransform rect = identity.GetComponent<RectTransform>();
                    JObject node = nodes[identity.Id];
                    Vector3[] corners = new Vector3[4]; rect.GetWorldCorners(corners);
                    Vector3[] points = corners.Select(p => root.InverseTransformPoint(p)).ToArray();
                    float minX = points.Min(p => p.x), maxX = points.Max(p => p.x);
                    float minY = points.Min(p => p.y), maxY = points.Max(p => p.y);
                    var evidence = new JObject { ["nodeId"] = identity.Id, ["type"] = (string)node["type"],
                        ["size"] = new JArray(rect.rect.width, rect.rect.height),
                        ["boundsInCanvas"] = new JArray(minX, minY, maxX, maxY),
                        ["canvasBounds"] = new JArray(root.rect.xMin, root.rect.yMin, root.rect.xMax, root.rect.yMax) };
                    ((JArray)report["nodes"]).Add(evidence);
                    bool outside = minX < root.rect.xMin - 0.5f || maxX > root.rect.xMax + 0.5f ||
                        minY < root.rect.yMin - 0.5f || maxY > root.rect.yMax + 0.5f;
                    if (rect != root && outside)
                    {
                        JObject issue = Issue("OUTSIDE_REFERENCE_CANVAS", identity.Id, "warning", "节点矩形部分位于参考画布之外；是否允许装饰出血需结合用途判断。", evidence);
                        JObject placement = (JObject)node["placement"];
                        if (rect.parent == root && (string)placement["mode"] == "anchors" &&
                            JToken.DeepEquals(placement["anchorMin"], placement["anchorMax"]) &&
                            maxX - minX <= root.rect.width && maxY - minY <= root.rect.height)
                        {
                            float dx = minX < root.rect.xMin ? root.rect.xMin - minX : maxX > root.rect.xMax ? root.rect.xMax - maxX : 0;
                            float dy = minY < root.rect.yMin ? root.rect.yMin - minY : maxY > root.rect.yMax ? root.rect.yMax - maxY : 0;
                            issue["suggestedChanges"] = new JArray(Patch(identity.Id, "placement.anchoredPosition", placement["anchoredPosition"],
                                new JArray(Math.Round((float)placement["anchoredPosition"][0] + dx, 3), Math.Round((float)placement["anchoredPosition"][1] + dy, 3))));
                            issue["suggestionReason"] = "对画布直属、固定锚点且尺寸可容纳的节点，给出移回画布所需的最小平移；不改变尺寸或文案。";
                        }
                        ((JArray)report["issues"]).Add(issue);
                    }
                    TextMeshProUGUI text = rect.GetComponent<TextMeshProUGUI>();
                    if (text != null)
                    {
                        Vector2 preferred = text.GetPreferredValues(text.text);
                        text.ForceMeshUpdate(true, true);
                        evidence["text"] = text.text;
                        evidence["fontSize"] = text.fontSize;
                        evidence["preferredSize"] = new JArray(preferred.x, preferred.y);
                        evidence["isTextOverflowing"] = text.isTextOverflowing;
                        if (rect.rect.width <= 0 || rect.rect.height <= 0 || preferred.x > rect.rect.width + 1 || preferred.y > rect.rect.height + 1 || text.isTextOverflowing)
                            ((JArray)report["issues"]).Add(Issue("TEXT_CAPACITY", identity.Id, "error", "TMP 实测文字超出容器；布局驱动和拉伸锚点需由调用方明确修订尺寸或文案。", evidence));
                    }
                    Image image = rect.GetComponent<Image>();
                    if (image != null && image.sprite != null && image.type == Image.Type.Simple && rect.rect.width > 0 && rect.rect.height > 0)
                    {
                        Sprite sprite = image.sprite;
                        float sourceAspect = sprite.rect.width / sprite.rect.height;
                        float aspect = rect.rect.width / rect.rect.height;
                        float factor = Mathf.Max(sourceAspect / aspect, aspect / sourceAspect);
                        if (factor >= 1.2f)
                        {
                            var detail = (JObject)evidence.DeepClone();
                            detail["sprite"] = UiCompositionSpriteResolver.Describe(sprite);
                            detail["aspectRatioDistortionFactor"] = Math.Round(factor, 3);
                            JObject issue = Issue("SIMPLE_SPRITE_STRETCH", identity.Id, "warning", "Simple 拉伸改变宽高比超过约20%；若边框应保持粗细，可考虑已有九宫格。", detail);
                            Vector4 border = sprite.border;
                            float[] values = { border.x, border.y, border.z, border.w };
                            if (values.Any(v => v > 0) && values.All(v => v >= 0 && v <= 4096 && v == Mathf.Floor(v)) &&
                                border.x + border.z < sprite.rect.width && border.y + border.w < sprite.rect.height)
                            {
                                JObject style = (JObject)node["style"];
                                issue["suggestedChanges"] = new JArray(Patch(identity.Id, "style.imageType", style["imageType"], new JValue("sliced")),
                                    Patch(identity.Id, "style.border", style["border"], new JArray(values.Select(v => new JValue((int)v)))));
                                issue["suggestionReason"] = "复用已导入的整数Border，不猜测/修改切图；仅在设计确实需要保边框时采用。";
                            }
                            ((JArray)report["issues"]).Add(issue);
                        }
                    }
                }
                report["complete"] = true; report["status"] = "diagnosed";
            }
            catch (Exception e) { ((JArray)report["errors"]).Add(new JObject { ["code"] = "DIAGNOSIS_FAILED", ["message"] = e.Message }); }
            finally
            {
                if (copy != null) UnityEngine.Object.DestroyImmediate(copy);
                if (preview.IsValid()) EditorSceneManager.ClosePreviewScene(preview);
            }
            if ((bool)report["complete"])
                using (SHA256 sha = SHA256.Create())
                    report["diagnosisFingerprint"] = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(report.ToString(Formatting.None)))).Replace("-", "").ToLowerInvariant();
            return report.ToString(Formatting.None);
        }

        private static JObject Issue(string code, string id, string severity, string message, JObject evidence) => new JObject {
            ["issueId"] = code + ":" + id, ["code"] = code, ["nodeId"] = id, ["severity"] = severity,
            ["message"] = message, ["evidence"] = evidence.DeepClone(), ["suggestedChanges"] = new JArray()
        };
        private static bool HasGlyph(TMP_FontAsset font, uint codePoint, HashSet<TMP_FontAsset> seen)
        {
            if (font == null || !seen.Add(font)) return false;
            return font.characterLookupTable.ContainsKey(codePoint) || font.fallbackFontAssetTable != null &&
                font.fallbackFontAssetTable.Any(f => HasGlyph(f, codePoint, seen));
        }
        private static JObject Patch(string id, string field, JToken before, JToken after) => new JObject {
            ["nodeId"] = id, ["field"] = field, ["beforeExists"] = before != null,
            ["before"] = before?.DeepClone(), ["after"] = after.DeepClone()
        };
        private static IEnumerable<JObject> Walk(JObject node)
        {
            yield return node;
            foreach (JObject child in (node["children"] as JArray ?? new JArray()).OfType<JObject>())
                foreach (JObject descendant in Walk(child)) yield return descendant;
        }
    }
}
