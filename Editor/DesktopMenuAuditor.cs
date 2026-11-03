using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace UIAgent.Tools.Editor
{
    /// <summary>Read-only rules specific to the first desktop main-menu profile.</summary>
    public static class DesktopMenuAuditor
    {
        public sealed class Finding
        {
            public string ruleId, severity, objectPath, message, recommendedAction, source;
            public object evidence;
            public bool requiresRuntimeValidation;
        }

        public sealed class Report
        {
            public int schemaVersion = 1;
            public string reportType = "desktop_menu_audit";
            public string sourcePath, capturedAt, status;
            public bool complete;
            public string upstreamStatus = "not_collected";
            public int errorCount, warningCount, infoCount;
            public List<Finding> findings = new List<Finding>();
            public string[] limitations = {
                "Profile: active uGUI ScreenSpaceOverlay main menu, 1920x1080 reference resolution.",
                "Geometry describes current Canvas layout, not proof of a fixed-size screenshot or successful input.",
                "Funplay text/transparent-raycast checks, Console, screenshots and Play Mode checks are separate inputs.",
                "Inactive UI is skipped. Minimum button size (100x40 design units) is a project policy, not a Unity standard."
            };
        }

        public static string AuditActiveSceneJson()
        {
            return JsonConvert.SerializeObject(AuditScene(SceneManager.GetActiveScene()));
        }

        public static string AuditLoadedSceneJson(string path)
        {
            return JsonConvert.SerializeObject(AuditScene(SceneManager.GetSceneByPath(path)));
        }

        public static Report AuditScene(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded)
                throw new ArgumentException("Expected a loaded scene.", nameof(scene));
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                throw new InvalidOperationException("Unity must be ready before auditing.");
            var report = new Report { sourcePath = scene.path, capturedAt = DateTime.UtcNow.ToString("o"), complete = true };
            GameObject[] roots = scene.GetRootGameObjects();
            Canvas[] canvases = roots.SelectMany(r => r.GetComponentsInChildren<Canvas>(false))
                .Where(c => c.isActiveAndEnabled && c.rootCanvas == c).ToArray();
            if (canvases.Length == 0)
            {
                report.complete = false;
                report.status = "no_ui";
                return report;
            }
            EventSystem[] systems = roots.SelectMany(r => r.GetComponentsInChildren<EventSystem>(false))
                .Where(e => e.isActiveAndEnabled).ToArray();
            if (systems.Length != 1)
                Add(report, "MENU_EVENT_SYSTEM_COUNT", "error", scene.path,
                    "主菜单场景需要一个有效 EventSystem。", new { activeCount = systems.Length }, "保留一个启用的 EventSystem。所有已加载场景的总数还需另查。");
            foreach (EventSystem system in systems)
                if (!system.GetComponents<BaseInputModule>().Any(m => m.isActiveAndEnabled))
                    Add(report, "MENU_INPUT_MODULE", "error", Path(system.transform),
                        "EventSystem 缺少有效输入模块。", new { enabledModuleCount = 0 }, "按项目输入系统配置并启用输入模块。");
            int scanned = 0;
            foreach (Canvas canvas in canvases)
            {
                if (canvas.renderMode != RenderMode.ScreenSpaceOverlay)
                {
                    report.complete = false;
                    Add(report, "MENU_UNSUPPORTED_CANVAS", "info", Path(canvas.transform),
                        "此版本只检查 Screen Space Overlay。", new { mode = canvas.renderMode.ToString() }, "选择支持的主菜单画布或增加该模式的验收规则。");
                    continue;
                }
                CanvasScaler scaler = canvas.GetComponent<CanvasScaler>();
                if (scaler == null || !scaler.enabled || scaler.uiScaleMode != CanvasScaler.ScaleMode.ScaleWithScreenSize ||
                    Vector2.Distance(scaler.referenceResolution, new Vector2(1920, 1080)) > .1f)
                    Add(report, "MENU_CANVAS_PROFILE", "error", Path(canvas.transform),
                        "CanvasScaler 不符合 1920×1080 主菜单配置。",
                        new { present = scaler != null, enabled = scaler != null && scaler.enabled,
                            scaleMode = scaler != null ? scaler.uiScaleMode.ToString() : null,
                            width = scaler != null ? scaler.referenceResolution.x : 0,
                            height = scaler != null ? scaler.referenceResolution.y : 0 },
                        "启用 CanvasScaler，使用 Scale With Screen Size 和 1920×1080 参考分辨率。");
                GraphicRaycaster raycaster = canvas.GetComponent<GraphicRaycaster>();
                if (raycaster == null || !raycaster.isActiveAndEnabled)
                    Add(report, "MENU_GRAPHIC_RAYCASTER", "error", Path(canvas.transform),
                        "主菜单画布缺少有效 GraphicRaycaster。", new { enabled = false }, "添加并启用 GraphicRaycaster。");
                RectTransform canvasRect = canvas.transform as RectTransform;
                if (canvasRect == null || canvasRect.rect.width <= 0 || canvasRect.rect.height <= 0)
                {
                    report.complete = false;
                    continue;
                }
                foreach (Transform node in canvas.GetComponentsInChildren<Transform>(false))
                {
                    if (++scanned > 500)
                    {
                        report.complete = false;
                        Add(report, "MENU_SCAN_LIMIT", "info", Path(canvas.transform), "扫描超过 500 个节点。", new { limit = 500 }, "缩小检查范围。");
                        Finish(report);
                        return report;
                    }
                    TMP_Text text = node.GetComponent<TMP_Text>();
                    if (text != null && text.isActiveAndEnabled && text.font != null && !string.IsNullOrWhiteSpace(text.text))
                    {
                        Rect textBounds = text.rectTransform.rect;
                        if (text.enableAutoSizing)
                        {
                            report.complete = false;
                            Add(report, "MENU_TEXT_AUTOSIZE", "info", Path(node), "自动字号超出当前固定字号验收范围。", new { autoSize = true }, "在目标尺寸重建布局后验收自动字号文本。", true);
                        }
                        else if (text.preferredHeight > textBounds.height + 1 ||
                            (!text.enableWordWrapping && text.preferredWidth > textBounds.width + 1) || text.isTextTruncated)
                            Add(report, "MENU_TEXT_FIT", "error", Path(node), "主菜单文本不适合当前容器或已经截断。",
                                new { width = textBounds.width, height = textBounds.height, preferredWidth = text.preferredWidth,
                                    preferredHeight = text.preferredHeight, wordWrapping = text.enableWordWrapping,
                                    truncated = text.isTextTruncated, overflowMode = text.overflowMode.ToString() },
                                "增大文本区域、缩短内容或调整字号，并通过目标尺寸截图确认。", true);
                    }
                    Button button = node.GetComponent<Button>();
                    if (button == null) continue;
                    if (!button.isActiveAndEnabled || !button.interactable || button.targetGraphic == null)
                        Add(report, "MENU_BUTTON_READY", "error", Path(node), "主菜单按钮未启用、不可交互或缺少 Target Graphic。",
                            new { enabled = button.isActiveAndEnabled, button.interactable, hasTargetGraphic = button.targetGraphic != null }, "检查按钮的启用状态、交互状态和 Target Graphic。");
                    RectTransform rect = node as RectTransform;
                    if (rect == null) continue;
                    var corners = new Vector3[4];
                    rect.GetWorldCorners(corners);
                    Vector3[] local = corners.Select(canvasRect.InverseTransformPoint).ToArray();
                    float xMin = local.Min(p => p.x), xMax = local.Max(p => p.x);
                    float yMin = local.Min(p => p.y), yMax = local.Max(p => p.y);
                    Rect bounds = canvasRect.rect;
                    var measured = new { xMin, xMax, yMin, yMax, canvasXMin = bounds.xMin, canvasXMax = bounds.xMax,
                        canvasYMin = bounds.yMin, canvasYMax = bounds.yMax };
                    if (xMin < bounds.xMin - 1 || xMax > bounds.xMax + 1 || yMin < bounds.yMin - 1 || yMax > bounds.yMax + 1)
                        Add(report, "MENU_BUTTON_OUTSIDE_CANVAS", "error", Path(node), "按钮边界超出当前画布。", measured, "调整锚点、位置或尺寸，并在目标尺寸截图复核。");
                    if (xMax - xMin < 100 || yMax - yMin < 40)
                        Add(report, "MENU_BUTTON_MIN_SIZE", "warning", Path(node), "按钮小于项目规定的 100×40 最小区域。", measured, "增大按钮交互区域，并测试实际点击。", true);
                }
            }
            Finish(report);
            return report;
        }

        /// <summary>Caller must supply the completed Funplay result for the SAME live scene.</summary>
        public static string MergeFunplayJson(string localJson, string funplayJson)
        {
            Report report = JsonConvert.DeserializeObject<Report>(localJson);
            if (report == null) throw new ArgumentException("Missing local report.");
            JObject upstream = JObject.Parse(funplayJson);
            JObject data = upstream["data"] as JObject;
            JArray nativeFindings = data?["findings"] as JArray;
            report.upstreamStatus = data != null ? (string)data["status"] : "invalid";
            if ((bool?)upstream["success"] != true || data == null || (bool?)data["complete"] != true ||
                (string)data["status"] != "completed" || data["next_offset"]?.Type != JTokenType.Null ||
                ((JArray)data["skipped"] ?? new JArray()).Count > 0 || (string)data["scope"] != "scene" ||
                nativeFindings == null || (int?)data["offset"] != 0 || (int?)data["total_findings"] != nativeFindings.Count)
            {
                report.complete = false;
                Add(report, "UPSTREAM_INCOMPLETE", "info", report.sourcePath, "Funplay 审计未完整收集，不能判定验收通过。",
                    new { upstream = report.upstreamStatus }, "取得同一场景全部结果页后再合并。");
            }
            foreach (JObject finding in nativeFindings ?? new JArray())
            {
                if ((bool?)finding["suppressed"] == true) continue;
                if (!new[] { "error", "warning", "info" }.Contains((string)finding["severity"])) report.complete = false;
                report.findings.Add(new Finding {
                    ruleId = "FUNPLAY_" + (string)finding["rule"], severity = (string)finding["severity"],
                    objectPath = (string)finding["object_path"], message = (string)finding["message"],
                    evidence = finding["evidence"], recommendedAction = (string)finding["suggestion"],
                    source = "funplay.audit_ui", requiresRuntimeValidation = (bool?)finding["requires_runtime_validation"] == true
                });
            }
            Finish(report);
            return JsonConvert.SerializeObject(report);
        }

        private static void Add(Report report, string rule, string severity, string path, string message,
            object evidence, string action, bool runtime = false)
        {
            report.findings.Add(new Finding { ruleId = rule, severity = severity, objectPath = path,
                message = message, evidence = evidence, recommendedAction = action,
                source = "ui-agent.desktop-menu", requiresRuntimeValidation = runtime });
        }

        private static void Finish(Report report)
        {
            report.errorCount = report.findings.Count(f => f.severity == "error");
            report.warningCount = report.findings.Count(f => f.severity == "warning");
            report.infoCount = report.findings.Count(f => f.severity == "info");
            if (report.status == "no_ui") return;
            report.status = !report.complete ? "incomplete" : report.errorCount > 0 ? "failed" :
                report.warningCount > 0 ? "needs_review" : report.upstreamStatus == "completed" ? "passed" : "local_checks_passed";
        }

        private static string Path(Transform node)
        {
            string path = node.name;
            while (node.parent != null) { node = node.parent; path = node.name + "/" + path; }
            return path;
        }
    }
}
