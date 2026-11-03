using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UIAgent.Tools.Editor
{
    /// <summary>Read-only validation for the compositional UI contract. It never creates assets.</summary>
    public static class UiCompositionSpecValidator
    {
        public const int MaxJsonLength = 65536;
        public const int MaxNodes = 64;
        public const int MaxNodeDepth = 8;
        private const string OutputPrefix = "Assets/AgentDemos/UIAgent/Prefabs/";
        private static readonly Regex IdPattern = new Regex(@"\A[A-Za-z][A-Za-z0-9_]{0,47}\z", RegexOptions.CultureInvariant);
        private static readonly Regex ColorPattern = new Regex(@"\A#[0-9A-Fa-f]{6}([0-9A-Fa-f]{2})?\z", RegexOptions.CultureInvariant);
        private static readonly Regex PathPartPattern = new Regex(@"\A[A-Za-z0-9_-]{1,64}\z", RegexOptions.CultureInvariant);
        private static readonly Regex Reserved = new Regex(@"\A(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex JsonNumber = new Regex(@"\A-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?\z", RegexOptions.CultureInvariant);
        private static readonly string[] RootFields = { "schemaVersion", "screenType", "targetPlatform", "referenceResolution", "outputPrefabPath", "fontAssetPath", "root", "confirmationDialog" };
        private static readonly string[] NodeFields = { "id", "type", "text", "placement", "childrenLayout", "style", "children", "toggle", "slider" };

        public sealed class ValidationError
        {
            public string code;
            public string field;
            public string message;
        }

        public sealed class Report
        {
            public int schemaVersion = 1;
            public string reportType = "ui_composition_spec_validation";
            public string status = "invalid";
            public bool valid;
            public bool requiresConfirmation = true;
            public string validationScope = "spec_only";
            public bool projectChecksPerformed = false;
            public List<ValidationError> errors = new List<ValidationError>();
            public JObject validatedSpec;
            public string specFingerprint;
            public int nodeCount;
            public string[] limitations = {
                "Validation does not prove imported sprite borders, font glyph coverage, visual quality, or runtime input.",
                "A valid Spec is a proposal; generation requires a separately displayed plan and explicit user confirmation.",
                "Toggle and Slider provide native Unity UI state only; application callbacks, settings persistence and arbitrary C# are not part of this version."
            };
        }

        private sealed class Context
        {
            public readonly HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
            public readonly Dictionary<string, JObject> nodes = new Dictionary<string, JObject>(StringComparer.Ordinal);
            public Report report;
        }

        public static string ValidateJson(string json)
        {
            return JsonConvert.SerializeObject(Validate(json));
        }

        public static Report Validate(string json)
        {
            var report = new Report();
            if (json != null && json.Length > MaxJsonLength)
            {
                Error(report, "JSON_TOO_LARGE", "$", "JSON 超过 65536 个 UTF-16 单元。");
                return report;
            }
            if (string.IsNullOrWhiteSpace(json))
            {
                Error(report, "JSON_REQUIRED", "$", "请提供 UI Composition Spec JSON。");
                return report;
            }
            JObject spec;
            try
            {
                CheckJsonLexemes(json);
                using (var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None, MaxDepth = 40 })
                {
                    spec = JToken.ReadFrom(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error }) as JObject;
                    if (spec == null || reader.Read()) throw new JsonReaderException("Expected one JSON object.");
                }
            }
            catch (JsonException)
            {
                Error(report, "JSON_INVALID", "$", "JSON 必须是单个对象，且不能含重复字段或尾随内容。");
                return report;
            }

            CheckFields(spec, RootFields,
                new[] { "schemaVersion", "screenType", "targetPlatform", "referenceResolution", "outputPrefabPath", "fontAssetPath", "root" },
                "$", report);
            Integer(spec["schemaVersion"], 2, "schemaVersion", report);
            Enum(spec["screenType"], new[] { "composition" }, "screenType", report);
            Enum(spec["targetPlatform"], new[] { "desktop" }, "targetPlatform", report);
            Resolution(spec["referenceResolution"], report);
            CheckAssetPath(spec["fontAssetPath"], "fontAssetPath", report);
            CheckOutputPath(spec["outputPrefabPath"], report);

            var context = new Context { report = report };
            JObject root = spec["root"] as JObject;
            if (root == null) Error(report, "FIELD_TYPE", "root", "root 必须是一个 Panel 节点。");
            else
            {
                ValidateNode(root, "root", null, 0, context);
                if (root["type"]?.Type != JTokenType.String || (string)root["type"] != "Panel")
                    Error(report, "ROOT_TYPE", "root.type", "根节点必须是 Panel。");
                ValidateConfirmationDialog(spec["confirmationDialog"], root, context, report);
            }

            if (report.errors.Count == 0)
            {
                report.validatedSpec = (JObject)Canonicalize(spec);
                using (SHA256 sha = SHA256.Create())
                    report.specFingerprint = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(report.validatedSpec.ToString(Formatting.None)))).Replace("-", "").ToLowerInvariant();
                report.valid = true;
                report.status = "valid";
            }
            return report;
        }

        private static void ValidateNode(JObject node, string path, string parentLayout, int depth, Context context)
        {
            Report report = context.report;
            report.nodeCount++;
            if (depth > MaxNodeDepth) { Error(report, "NODE_DEPTH", path, "根节点深度为 0，最大允许深度为 8。"); return; }
            if (report.nodeCount > MaxNodes) { Error(report, "NODE_COUNT", path, "一个 Spec 最多包含 64 个 UI 节点。"); return; }
            CheckFields(node, NodeFields, new[] { "id", "type", "placement", "childrenLayout", "style" }, path, report);

            string id = StringValue(node["id"], path + ".id", report);
            if (id != null)
            {
                if (!IdPattern.IsMatch(id)) Error(report, "NODE_ID_INVALID", path + ".id", "id 必须以英文字母开头，只能包含英文字母、数字和下划线，最多 48 字符。");
                else if (!context.ids.Add(id)) Error(report, "NODE_ID_DUPLICATE", path + ".id", "每个节点 id 必须唯一。");
                else context.nodes.Add(id, node);
            }

            string type = StringValue(node["type"], path + ".type", report);
            if (!new[] { "Panel", "Text", "Button", "Toggle", "Slider" }.Contains(type, StringComparer.Ordinal))
                Error(report, "COMPONENT_UNSUPPORTED", path + ".type", "当前 v2 原型只允许 Panel、Text、Button、Toggle 和 Slider。");
            if (type != "Text" && node["text"] != null) Error(report, "FIELD_UNSUPPORTED", path + ".text", "只有 Text 节点可以声明 text。");
            ValidatePlacement(node["placement"] as JObject, path + ".placement", parentLayout, report);

            JObject childLayoutObject = node["childrenLayout"] as JObject;
            if (childLayoutObject == null) Error(report, "FIELD_TYPE", path + ".childrenLayout", "childrenLayout 必须是布局对象。");
            string ownLayout = childLayoutObject == null || childLayoutObject["mode"]?.Type != JTokenType.String
                ? "none" : (string)childLayoutObject["mode"];
            ValidateChildrenLayout(childLayoutObject, path + ".childrenLayout", type, report);

            if (type == "Text")
            {
                if (node["text"] == null) Error(report, "FIELD_REQUIRED", path + ".text", "Text 节点必须包含 text。");
                if (node["text"]?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)node["text"]))
                    Error(report, "TEXT_REQUIRED", path + ".text", "Text 节点必须包含非空 text。");
                else if (!ValidText((string)node["text"]))
                    Error(report, "TEXT_INVALID", path + ".text", "Text 最多 256 个 UTF-16 单元，不能有首尾空白、控制字符、富文本标签或无效代理字符。");
            }
            if (!(node["style"] is JObject)) Error(report, "FIELD_TYPE", path + ".style", "style 必须是对象。");
            ValidateStyle(node["style"] as JObject, path + ".style", type, report);
            JArray children = node["children"] as JArray;
            if (node["children"] != null && children == null)
            {
                Error(report, "FIELD_TYPE", path + ".children", "children 必须是数组。");
                return;
            }
            children = children ?? new JArray();
            if (children.Count > MaxNodes) Error(report, "NODE_COUNT", path + ".children", "子节点数量超过限制。");
            for (int i = 0; i < children.Count; i++)
            {
                JToken token = children[i];
                JObject child = token as JObject;
                if (child == null) { Error(report, "FIELD_TYPE", path + ".children", "每个子节点必须是对象。"); continue; }
                ValidateNode(child, path + ".children[" + i + "]", ownLayout, depth + 1, context);
                if (report.nodeCount > MaxNodes) break;
            }

            if (type == "Text" && children.Count != 0)
                Error(report, "CHILDREN_UNSUPPORTED", path + ".children", "Text 必须是叶节点。");

            if (type == "Button")
            {
                int labels = children.Count(c => c is JObject child && StringOrNull(child["type"]) == "Text");
                if (labels != 1 || children.Count != 1)
                    Error(report, "BUTTON_LABEL_SHAPE", path + ".children", "Button 必须且只能有一个直接 Text 子节点作为标签。");
                else
                {
                    JObject label = children[0] as JObject;
                    JObject placement = label["placement"] as JObject;
                    if (!FillsParent(placement))
                        Error(report, "BUTTON_LABEL_LAYOUT", path + ".children[0].placement", "Button 标签必须用 anchors 锚定并完整填充按钮区域。");
                }
            }
            if (type == "Toggle")
            {
                JObject toggle = node["toggle"] as JObject;
                if (toggle == null) Error(report, "FIELD_REQUIRED", path + ".toggle", "Toggle 必须声明 toggle 配置。");
                else
                {
                    CheckFields(toggle, new[] { "initialValue", "labelId", "indicatorId" },
                        new[] { "initialValue", "labelId", "indicatorId" }, path + ".toggle", report);
                    if (toggle["initialValue"]?.Type != JTokenType.Boolean)
                        Error(report, "FIELD_TYPE", path + ".toggle.initialValue", "initialValue 必须是 Boolean。");
                    string labelId = StringOrNull(toggle["labelId"]), indicatorId = StringOrNull(toggle["indicatorId"]);
                    JObject[] textChildren = children.OfType<JObject>().Where(c => StringOrNull(c["type"]) == "Text").ToArray();
                    if (children.Count != 2 || textChildren.Length != 2 || labelId == null || indicatorId == null ||
                        labelId == indicatorId || StringOrNull(textChildren[0]["id"]) != labelId ||
                        StringOrNull(textChildren[1]["id"]) != indicatorId)
                        Error(report, "TOGGLE_CHILD_SHAPE", path + ".children", "Toggle 必须且只能有两个直接 Text 子节点，并按顺序与 labelId、indicatorId 对应。");
                }
            }
            else if (node["toggle"] != null)
                Error(report, "FIELD_UNSUPPORTED", path + ".toggle", "只有 Toggle 节点可以声明 toggle 配置。");
            if (type == "Slider")
            {
                JObject slider = node["slider"] as JObject;
                if (slider == null) Error(report, "FIELD_REQUIRED", path + ".slider", "Slider 必须声明 slider 配置。");
                else
                {
                    CheckFields(slider, new[] { "minValue", "maxValue", "initialValue", "wholeNumbers", "fillId", "handleId" },
                        new[] { "minValue", "maxValue", "initialValue", "wholeNumbers", "fillId", "handleId" }, path + ".slider", report);
                    Number(slider["minValue"], path + ".slider.minValue", report, -100000, 100000);
                    Number(slider["maxValue"], path + ".slider.maxValue", report, -100000, 100000);
                    Number(slider["initialValue"], path + ".slider.initialValue", report, -100000, 100000);
                    bool wholeNumbers = slider["wholeNumbers"]?.Type == JTokenType.Boolean && (bool)slider["wholeNumbers"];
                    if (slider["wholeNumbers"]?.Type != JTokenType.Boolean)
                        Error(report, "FIELD_TYPE", path + ".slider.wholeNumbers", "wholeNumbers 必须是 Boolean。");
                    double min = NumberOrNaN(slider["minValue"]), max = NumberOrNaN(slider["maxValue"]), value = NumberOrNaN(slider["initialValue"]);
                    if (!double.IsNaN(min) && !double.IsNaN(max) && max <= min)
                        Error(report, "SLIDER_RANGE_INVALID", path + ".slider", "maxValue 必须大于 minValue。");
                    if (!double.IsNaN(min) && !double.IsNaN(max) && !double.IsNaN(value) && (value < min || value > max))
                        Error(report, "SLIDER_VALUE_RANGE", path + ".slider.initialValue", "initialValue 必须位于 minValue 与 maxValue 之间。");
                    if (wholeNumbers && !double.IsNaN(value) && value != Math.Truncate(value))
                        Error(report, "SLIDER_INTEGER_VALUE", path + ".slider.initialValue", "wholeNumbers 为 true 时 initialValue 必须为整数。");
                    string fillId = StringOrNull(slider["fillId"]), handleId = StringOrNull(slider["handleId"]);
                    JObject[] visualChildren = children.OfType<JObject>().Where(c => StringOrNull(c["type"]) != "Text").ToArray();
                    if (children.Count != 2 || visualChildren.Length != 2 || fillId == null || handleId == null || fillId == handleId ||
                        StringOrNull(visualChildren[0]["id"]) != fillId || StringOrNull(visualChildren[1]["id"]) != handleId ||
                        StringOrNull(visualChildren[0]["type"]) != "Panel" || StringOrNull(visualChildren[1]["type"]) != "Panel" ||
                        StringOrNull(visualChildren[0]["placement"]?["mode"]) != "anchors" ||
                        StringOrNull(visualChildren[1]["placement"]?["mode"]) != "anchors")
                        Error(report, "SLIDER_CHILD_SHAPE", path + ".children", "Slider 必须按顺序包含使用 anchors 布局的两个直接 Panel 子节点，分别由 fillId 和 handleId 引用。");
                }
            }
            else if (node["slider"] != null)
                Error(report, "FIELD_UNSUPPORTED", path + ".slider", "只有 Slider 节点可以声明 slider 配置。");
        }

        private static double NumberOrNaN(JToken token)
        {
            return IsNumber(token) ? (double)token : double.NaN;
        }

        private static void ValidateConfirmationDialog(JToken token, JObject root, Context context, Report report)
        {
            if (token == null) return;
            const string path = "confirmationDialog";
            JObject config = token as JObject;
            if (config == null)
            {
                Error(report, "FIELD_TYPE", path, "confirmationDialog 必须是对象。");
                return;
            }
            string[] fields = { "backdropId", "dialogPanelId", "cancelButtonId", "confirmButtonId", "initialFocusId" };
            CheckFields(config, fields, fields, path, report);
            string backdropId = StringOrNull(config["backdropId"]);
            string dialogId = StringOrNull(config["dialogPanelId"]);
            string cancelId = StringOrNull(config["cancelButtonId"]);
            string confirmId = StringOrNull(config["confirmButtonId"]);
            string focusId = StringOrNull(config["initialFocusId"]);
            foreach (string field in fields)
                if (StringOrNull(config[field]) == null) Error(report, "FIELD_TYPE", path + "." + field, "节点引用必须是字符串 id。");
            if (fields.Take(4).Select(field => StringOrNull(config[field])).Where(value => value != null).Distinct(StringComparer.Ordinal).Count() != 4)
                Error(report, "DIALOG_NODE_ID_DUPLICATE", path, "弹窗角色必须引用互不相同的节点。");

            JObject backdrop = ResolveDialogNode(backdropId, "Panel", path + ".backdropId", context, report);
            JObject dialog = ResolveDialogNode(dialogId, "Panel", path + ".dialogPanelId", context, report);
            JObject cancel = ResolveDialogNode(cancelId, "Button", path + ".cancelButtonId", context, report);
            JObject confirm = ResolveDialogNode(confirmId, "Button", path + ".confirmButtonId", context, report);
            JObject focus = ResolveDialogNode(focusId, "Button", path + ".initialFocusId", context, report);

            JArray rootChildren = root["children"] as JArray;
            if (rootChildren == null || rootChildren.Count != 2 ||
                rootChildren[0] is not JObject first || (string)first["id"] != backdropId ||
                rootChildren[1] is not JObject second || (string)second["id"] != dialogId)
                Error(report, "DIALOG_LAYER_ORDER", path, "根节点必须且只能包含背景拦截器和弹窗面板两个直接子项，且拦截器排在面板之前。");
            if ((string)root["childrenLayout"]?["mode"] != "none")
                Error(report, "DIALOG_ROOT_LAYOUT", "root.childrenLayout", "声明确认弹窗时根节点不能使用子项布局组。");
            if (backdrop != null)
            {
                JObject placement = backdrop["placement"] as JObject;
                if (!FillsParent(placement)) Error(report, "DIALOG_BACKDROP_STRETCH", path + ".backdropId", "背景拦截器必须锚定并完整填充屏幕。");
                if (((JArray)backdrop["children"] ?? new JArray()).Count != 0)
                    Error(report, "DIALOG_BACKDROP_CHILDREN", path + ".backdropId", "背景拦截器不能包含子节点，以免遮罩内容覆盖其点击区域。");
                JObject backdropStyle = backdrop["style"] as JObject;
                if (backdropStyle == null || backdropStyle["spritePath"] != null || backdropStyle["color"] == null)
                    Error(report, "DIALOG_BACKDROP_STYLE", path + ".backdropId", "背景拦截器必须使用纯色 Image，以保证全屏 Raycast 命中。");
            }
            if (dialog != null && rootChildren != null && !rootChildren.OfType<JObject>().Any(n => (string)n["id"] == dialogId))
                Error(report, "DIALOG_PANEL_PARENT", path + ".dialogPanelId", "弹窗面板必须是根 Canvas 的直接子项。");
            if (cancel != null && confirm != null && dialog != null)
            {
                string cancelParent = ParentId(root, cancelId);
                string confirmParent = ParentId(root, confirmId);
                if (cancelParent == null || cancelParent != confirmParent || !IsDescendantId(root, dialogId, cancelParent))
                    Error(report, "DIALOG_BUTTON_PARENT", path, "确认与取消按钮必须位于弹窗面板下的同一个父节点。");
            }
            if (focusId != cancelId && focusId != confirmId)
                Error(report, "DIALOG_INITIAL_FOCUS", path + ".initialFocusId", "初始焦点必须指向确认或取消按钮。");
        }

        private static JObject ResolveDialogNode(string id, string type, string field, Context context, Report report)
        {
            if (id == null) return null;
            if (!context.nodes.TryGetValue(id, out JObject node))
            {
                Error(report, "DIALOG_NODE_MISSING", field, "引用的弹窗节点 id 不存在：" + id + "。");
                return null;
            }
            if ((string)node["type"] != type)
                Error(report, "DIALOG_NODE_TYPE", field, "节点 " + id + " 必须为 " + type + "。");
            return node;
        }

        private static string ParentId(JObject root, string childId)
        {
            if (childId == null) return null;
            JArray children = root["children"] as JArray;
            if (children == null) return null;
            foreach (JObject parent in children.OfType<JObject>())
            {
                JArray nested = parent["children"] as JArray;
                if (nested != null && nested.OfType<JObject>().Any(n => (string)n["id"] == childId)) return (string)parent["id"];
                string found = ParentId(parent, childId);
                if (found != null) return found;
            }
            return null;
        }

        private static bool IsDescendantId(JObject root, string ancestorId, string candidateId)
        {
            if (ancestorId == candidateId) return true;
            JObject node = root.DescendantsAndSelf().OfType<JObject>().FirstOrDefault(n => (string)n["id"] == ancestorId);
            return node != null && node.Descendants().OfType<JObject>().Any(n => (string)n["id"] == candidateId);
        }

        private static void ValidateChildrenLayout(JObject layout, string path, string type, Report report)
        {
            if (layout == null) return;
            string mode = StringOrNull(layout["mode"]);
            if (mode == "none")
            {
                CheckFields(layout, new[] { "mode" }, new[] { "mode" }, path, report);
                return;
            }
            if (!new[] { "vertical", "horizontal" }.Contains(mode, StringComparer.Ordinal))
            {
                Error(report, "CHILDREN_LAYOUT_INVALID", path + ".mode", "布局模式只允许 none、vertical 或 horizontal。");
                return;
            }
            if (type != "Panel") Error(report, "CHILDREN_LAYOUT_UNSUPPORTED", path, "第一版只允许 Panel 驱动子节点布局。");
            string[] fields = { "mode", "spacing", "padding", "childAlignment", "childControlWidth", "childControlHeight", "childForceExpandWidth", "childForceExpandHeight" };
            CheckFields(layout, fields, fields, path, report);
            Number(layout["spacing"], path + ".spacing", report, 0, 512);
            JArray padding = layout["padding"] as JArray;
            if (padding == null || padding.Count != 4 || padding.Any(v => !IsNumber(v) || (double)v < 0 || (double)v > 512 || Math.Floor((double)v) != (double)v))
                Error(report, "PADDING_INVALID", path + ".padding", "padding 必须为 [left,bottom,right,top] 四个 0～512 的整数。");
            Enum(layout["childAlignment"], new[] { "upperLeft", "upperCenter", "upperRight", "middleLeft", "middleCenter", "middleRight", "lowerLeft", "lowerCenter", "lowerRight" }, path + ".childAlignment", report);
            foreach (string field in new[] { "childControlWidth", "childControlHeight", "childForceExpandWidth", "childForceExpandHeight" })
                if (layout[field]?.Type != JTokenType.Boolean) Error(report, "FIELD_TYPE", path + "." + field, "布局控制字段必须是 Boolean。");
        }

        private static void ValidatePlacement(JObject placement, string path, string parentLayout, Report report)
        {
            if (placement == null) { Error(report, "PLACEMENT_REQUIRED", path, "每个节点必须显式声明 placement 对象。"); return; }
            string mode = StringOrNull(placement["mode"]);
            if (mode == "anchors")
            {
                CheckFields(placement, new[] { "mode", "anchorMin", "anchorMax", "pivot", "anchoredPosition", "sizeDelta" }, new[] { "mode", "anchorMin", "anchorMax", "pivot", "anchoredPosition", "sizeDelta" }, path, report);
                if (parentLayout != null && parentLayout != "none") Error(report, "LAYOUT_MODE_CONFLICT", path, "父节点的 childrenLayout 驱动子项；子节点必须使用 layout placement。");
                Vector2(placement["anchorMin"], path + ".anchorMin", true, report);
                Vector2(placement["anchorMax"], path + ".anchorMax", true, report);
                Vector2(placement["pivot"], path + ".pivot", true, report);
                Vector2(placement["anchoredPosition"], path + ".anchoredPosition", false, report);
                Vector2(placement["sizeDelta"], path + ".sizeDelta", false, report);
                JArray min = placement["anchorMin"] as JArray, max = placement["anchorMax"] as JArray;
                if (min != null && max != null && min.Count == 2 && max.Count == 2 && min.All(IsNumber) && max.All(IsNumber) &&
                    ((double)min[0] > (double)max[0] || (double)min[1] > (double)max[1]))
                    Error(report, "ANCHOR_ORDER", path, "anchorMin 分量不能大于 anchorMax。");
            }
            else if (mode == "layout")
            {
                CheckFields(placement, new[] { "mode", "preferredSize" }, new[] { "mode", "preferredSize" }, path, report);
                if (parentLayout == null || parentLayout == "none") Error(report, "LAYOUT_MODE_CONFLICT", path, "父节点未声明子项布局；子节点必须使用 anchors placement。");
                Vector2(placement["preferredSize"], path + ".preferredSize", false, report, positive: true);
            }
            else Error(report, "PLACEMENT_MODE_INVALID", path + ".mode", "placement.mode 只允许 anchors 或 layout。");
        }

        private static void ValidateStyle(JObject style, string path, string type, Report report)
        {
            if (style == null) style = new JObject();
            if (type == "Text")
            {
                CheckFields(style, new[] { "color", "fontSize", "alignment", "outlineColor", "outlineWidth", "shadowColor", "shadowOffset" }, new[] { "color", "fontSize", "alignment" }, path, report);
                Color(style["color"], path + ".color", report);
                Number(style["fontSize"], path + ".fontSize", report, 8, 128);
                if (style["alignment"] != null) Enum(style["alignment"], new[] { "left", "center", "right" }, path + ".alignment", report);

                bool hasOutlineColor = style["outlineColor"] != null;
                bool hasOutlineWidth = style["outlineWidth"] != null;
                if (hasOutlineColor != hasOutlineWidth)
                    Error(report, "OUTLINE_FIELDS_INCOMPLETE", path, "outlineColor 与 outlineWidth 必须同时提供或同时省略。");
                if (hasOutlineColor) Color(style["outlineColor"], path + ".outlineColor", report);
                if (hasOutlineWidth) Number(style["outlineWidth"], path + ".outlineWidth", report, 0, 0.2);

                bool hasShadowColor = style["shadowColor"] != null;
                bool hasShadowOffset = style["shadowOffset"] != null;
                if (hasShadowColor != hasShadowOffset)
                    Error(report, "SHADOW_FIELDS_INCOMPLETE", path, "shadowColor 与 shadowOffset 必须同时提供或同时省略。");
                if (hasShadowColor) Color(style["shadowColor"], path + ".shadowColor", report);
                if (hasShadowOffset) NormalizedEffectVector(style["shadowOffset"], path + ".shadowOffset", report);
            }
            else if (type == "Panel" || type == "Button" || type == "Toggle" || type == "Slider")
            {
                CheckFields(style, new[] { "color", "spritePath", "spriteGuid", "spriteName", "spriteLocalFileId", "imageType", "border" }, Array.Empty<string>(), path, report);
                if (style["color"] != null) Color(style["color"], path + ".color", report);
                bool hasSprite = style["spritePath"] != null;
                if (hasSprite)
                {
                    CheckAssetPath(style["spritePath"], path + ".spritePath", report);
                    string guid = StringOrNull(style["spriteGuid"]);
                    if (guid == null || !Regex.IsMatch(guid, @"\A[0-9a-f]{32}\z", RegexOptions.CultureInvariant))
                        Error(report, "SPRITE_GUID_REQUIRED", path + ".spriteGuid", "Sprite 必须记录所选资源的 32 位小写十六进制 GUID。");
                    bool hasName = style["spriteName"] != null, hasLocalId = style["spriteLocalFileId"] != null;
                    if (hasName != hasLocalId)
                        Error(report, "SPRITE_SUBASSET_FIELDS_INCOMPLETE", path, "spriteName and spriteLocalFileId must be supplied together.");
                    if (hasName && (style["spriteName"].Type != JTokenType.String || string.IsNullOrWhiteSpace((string)style["spriteName"]) || ((string)style["spriteName"]).Length > 256))
                        Error(report, "SPRITE_NAME_INVALID", path + ".spriteName", "spriteName must be a nonempty string of at most 256 characters.");
                    if (hasLocalId)
                    {
                        string local = StringOrNull(style["spriteLocalFileId"]);
                        if (local == null || !long.TryParse(local, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long parsed) ||
                            parsed == 0 || parsed.ToString(CultureInfo.InvariantCulture) != local)
                            Error(report, "SPRITE_LOCAL_FILE_ID_INVALID", path + ".spriteLocalFileId", "Use a nonzero signed Int64 in canonical decimal string form, never a JSON number.");
                    }
                    Enum(style["imageType"], new[] { "simple", "sliced" }, path + ".imageType", report);
                    if (StringOrNull(style["imageType"]) == "sliced")
                    {
                        JArray border = style["border"] as JArray;
                        if (border == null || border.Count != 4 || border.Any(v => v?.Type != JTokenType.Integer || !IsNumber(v) || (double)v < 0 || (double)v > 4096))
                            Error(report, "SPRITE_BORDER_REQUIRED", path + ".border", "九宫格 Border 必须是 [left,bottom,right,top] 四个 0～4096 的整数像素值。");
                        else if (border.All(v => (double)v == 0)) Error(report, "SPRITE_BORDER_EMPTY", path + ".border", "Sliced Sprite 的 Border 不能全部为零。");
                    }
                    else if (style["border"] != null) Error(report, "FIELD_UNSUPPORTED", path + ".border", "Simple Sprite 不接受九宫格 border。");
                }
                else if (style["imageType"] != null || style["border"] != null || style["spriteGuid"] != null || style["spriteName"] != null || style["spriteLocalFileId"] != null)
                    Error(report, "SPRITE_REQUIRED", path + ".spritePath", "imageType 和 border 需要 spritePath。");
                else if (style["color"] == null)
                    Error(report, "STYLE_REQUIRED", path, "Panel/Button/Toggle/Slider 必须指定颜色或 Sprite。");
            }
            else if (style.Properties().Any()) Error(report, "STYLE_UNSUPPORTED", path, "不支持的节点类型不能声明样式。");
        }

        private static bool FillsParent(JObject placement)
        {
            if (placement == null || StringOrNull(placement["mode"]) != "anchors") return false;
            return ExactPair(placement["anchorMin"], 0, 0) && ExactPair(placement["anchorMax"], 1, 1) &&
                   ExactPair(placement["pivot"], 0.5, 0.5) && ExactPair(placement["anchoredPosition"], 0, 0) &&
                   ExactPair(placement["sizeDelta"], 0, 0);
        }

        private static bool ExactPair(JToken token, double x, double y)
        {
            JArray pair = token as JArray;
            return pair != null && pair.Count == 2 && IsNumber(pair[0]) && IsNumber(pair[1]) &&
                   (double)pair[0] == x && (double)pair[1] == y;
        }

        private static void CheckOutputPath(JToken token, Report report)
        {
            string path = token?.Type == JTokenType.String ? (string)token : null;
            if (path == null || path.Length > 240 || !path.StartsWith(OutputPrefix, StringComparison.Ordinal) || !path.EndsWith(".prefab", StringComparison.Ordinal))
            { Error(report, "OUTPUT_PATH_INVALID", "outputPrefabPath", "输出必须位于 Prefabs 白名单目录并以 .prefab 结尾。"); return; }
            string[] parts = path.Substring(OutputPrefix.Length, path.Length - OutputPrefix.Length - 7).Split('/');
            if (parts.Length < 1 || parts.Length > 8 || parts.Any(p => !PathPartPattern.IsMatch(p) || Reserved.IsMatch(p)))
                Error(report, "OUTPUT_PATH_INVALID", "outputPrefabPath", "输出路径层级或名称不符合限制。");
        }

        private static void CheckAssetPath(JToken token, string field, Report report)
        {
            string path = token?.Type == JTokenType.String ? (string)token : null;
            if (path == null || path.Length > 240 || !path.StartsWith("Assets/", StringComparison.Ordinal) ||
                path.Split('/').Any(p => p.Length == 0 || p == "." || p == ".." || p != p.Trim() || p.EndsWith(".", StringComparison.Ordinal) ||
                    Reserved.IsMatch(p) || p.Any(c => char.IsControl(c) || "<>:\"\\|?*%".Contains(c))))
                Error(report, "ASSET_PATH_INVALID", field, "资源必须使用 Assets/ 下的规范项目相对路径。");
        }

        private static void Resolution(JToken token, Report report)
        {
            JObject value = token as JObject;
            if (value == null) { Error(report, "FIELD_TYPE", "referenceResolution", "参考分辨率必须是对象。"); return; }
            CheckFields(value, new[] { "width", "height" }, new[] { "width", "height" }, "referenceResolution", report);
            Integer(value["width"], 1920, "referenceResolution.width", report);
            Integer(value["height"], 1080, "referenceResolution.height", report);
        }

        private static void CheckFields(JObject obj, IEnumerable<string> allowed, IEnumerable<string> required, string path, Report report)
        {
            var names = new HashSet<string>(allowed, StringComparer.Ordinal);
            foreach (JProperty property in obj.Properties())
                if (!names.Contains(property.Name)) Error(report, "FIELD_UNKNOWN", path + "." + property.Name, "存在未支持字段。");
            foreach (string name in required)
                if (obj[name] == null) Error(report, "FIELD_REQUIRED", path + "." + name, "缺少必填字段。");
        }

        private static string StringValue(JToken token, string field, Report report)
        {
            if (token?.Type != JTokenType.String) { Error(report, "FIELD_TYPE", field, "字段必须是字符串。"); return null; }
            return (string)token;
        }

        private static void Integer(JToken token, int expected, string field, Report report)
        {
            if (token?.Type != JTokenType.Integer || token.ToString(Formatting.None) != expected.ToString(CultureInfo.InvariantCulture))
                Error(report, "FIELD_VALUE", field, "字段值必须为 " + expected + "。");
        }

        private static void Number(JToken token, string field, Report report, double min, double max)
        {
            if (!IsNumber(token) || (double)token < min || (double)token > max) Error(report, "NUMBER_RANGE", field, "数值必须在 " + min + " 到 " + max + " 之间。");
        }

        private static void Vector2(JToken token, string field, bool unit, Report report, bool positive = false)
        {
            JArray pair = token as JArray;
            if (pair == null || pair.Count != 2 || pair.Any(v => !IsNumber(v))) { Error(report, "VECTOR_INVALID", field, "字段必须是包含两个数字的数组。"); return; }
            if (unit && pair.Any(v => (double)v < 0 || (double)v > 1)) Error(report, "VECTOR_RANGE", field, "锚点与 Pivot 分量必须在 0 到 1 之间。");
            if (!unit && pair.Any(v => Math.Abs((double)v) > 8192)) Error(report, "VECTOR_RANGE", field, "位置与尺寸分量的绝对值不能超过 8192。");
            if (positive && pair.Any(v => (double)v <= 0)) Error(report, "VECTOR_RANGE", field, "尺寸必须大于零。");
        }

        private static void NormalizedEffectVector(JToken token, string field, Report report)
        {
            JArray pair = token as JArray;
            if (pair == null || pair.Count != 2 || pair.Any(v => !IsNumber(v)))
            {
                Error(report, "VECTOR_INVALID", field, "字段必须是包含两个数字的数组。");
                return;
            }
            if (pair.Any(v => Math.Abs((double)v) > 1))
                Error(report, "VECTOR_RANGE", field, "描边阴影偏移的每个分量必须在 -1 到 1 之间。");
        }

        private static string StringOrNull(JToken token) => token?.Type == JTokenType.String ? (string)token : null;

        private static bool IsNumber(JToken value)
        {
            if (value == null || (value.Type != JTokenType.Integer && value.Type != JTokenType.Float)) return false;
            double number;
            return double.TryParse(value.ToString(Formatting.None), NumberStyles.Float, CultureInfo.InvariantCulture, out number) &&
                   !double.IsNaN(number) && !double.IsInfinity(number) && Math.Abs(number) <= 1e9;
        }

        private static bool ValidText(string value)
        {
            if (value.Length > 256 || value != value.Trim() || value.Any(c => char.IsControl(c) || c == '<' || c == '>')) return false;
            for (int i = 0; i < value.Length; i++)
            {
                if (char.IsHighSurrogate(value[i])) { if (++i >= value.Length || !char.IsLowSurrogate(value[i])) return false; }
                else if (char.IsLowSurrogate(value[i])) return false;
            }
            return true;
        }

        // Json.NET accepts JavaScript extensions; the public contract accepts strict JSON only.
        private static void CheckJsonLexemes(string json)
        {
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n') continue;
                if (c == '"')
                {
                    bool closed = false;
                    for (i++; i < json.Length; i++)
                    {
                        if (json[i] == '\\')
                        {
                            if (++i >= json.Length || "\"\\/bfnrtu".IndexOf(json[i]) < 0) throw new JsonReaderException("Invalid escape.");
                            if (json[i] == 'u')
                                for (int n = 0; n < 4; n++)
                                    if (++i >= json.Length || !Uri.IsHexDigit(json[i])) throw new JsonReaderException("Invalid Unicode escape.");
                            continue;
                        }
                        if (json[i] == '"') { closed = true; break; }
                        if (json[i] < 32) throw new JsonReaderException("Unescaped control.");
                    }
                    if (!closed) throw new JsonReaderException("Unterminated string.");
                    continue;
                }
                if (c == ',')
                {
                    int next = i + 1;
                    while (next < json.Length && char.IsWhiteSpace(json[next])) next++;
                    if (next < json.Length && (json[next] == '}' || json[next] == ']')) throw new JsonReaderException("Trailing comma.");
                    continue;
                }
                if (c == '{' || c == '}' || c == '[' || c == ']' || c == ':') continue;
                int start = i;
                while (i + 1 < json.Length && "{}[],: \t\r\n".IndexOf(json[i + 1]) < 0) i++;
                string literal = json.Substring(start, i - start + 1);
                if (literal != "true" && literal != "false" && literal != "null" && !JsonNumber.IsMatch(literal))
                    throw new JsonReaderException("Invalid literal.");
            }
        }

        private static void Color(JToken token, string field, Report report)
        {
            if (token?.Type != JTokenType.String || !ColorPattern.IsMatch((string)token)) Error(report, "COLOR_INVALID", field, "颜色使用 #RRGGBB 或 #RRGGBBAA。");
        }

        private static void Enum(JToken token, IEnumerable<string> values, string field, Report report)
        {
            if (token?.Type != JTokenType.String || !values.Contains((string)token, StringComparer.Ordinal)) Error(report, "FIELD_VALUE", field, "字段值不在支持范围内。");
        }

        private static JToken Canonicalize(JToken token)
        {
            if (token is JObject obj) return new JObject(obj.Properties().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => new JProperty(p.Name, Canonicalize(p.Value))));
            if (token is JArray array) return new JArray(array.Select(Canonicalize));
            return token.DeepClone();
        }

        private static void Error(Report report, string code, string field, string message)
        {
            report.errors.Add(new ValidationError { code = code, field = field, message = message });
        }
    }
}
