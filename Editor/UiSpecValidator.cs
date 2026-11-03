using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace UIAgent.Tools.Editor
{
    /// <summary>Read-only validation. A plan is a proposal, never permission to generate.</summary>
    public static class UiSpecValidator
    {
        public const string OutputPrefix = "Assets/AgentDemos/UIAgent/Prefabs/";
        public const int MaxJsonLength = 16384;
        private static readonly string[] Fields = { "schemaVersion", "screenType", "targetPlatform", "referenceResolution", "title", "buttons", "theme", "outputPrefabPath" };
        private static readonly Regex Segment = new Regex(@"\A[A-Za-z0-9_-]{1,64}\z", RegexOptions.CultureInvariant);
        private static readonly Regex Reserved = new Regex(@"\A(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex Number = new Regex(@"^-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?$", RegexOptions.CultureInvariant);

        public sealed class ValidationError
        {
            public string code, field, message;
        }

        public sealed class CreationPlan
        {
            public string outputPrefabPath, title, theme, specFingerprint;
            public string[] buttons;
            public int width = 1920, height = 1080;
            public bool overwriteAllowed = false;
            public bool bindGameLogic = false;
            public string[] operations = {
                "Create a new Canvas with CanvasScaler and GraphicRaycaster.",
                "Create background, title and a button group from the confirmed Spec.",
                "Save a new Prefab at outputPrefabPath; recheck collisions immediately before writing."
            };
        }

        public sealed class Report
        {
            public int schemaVersion = 1;
            public string reportType = "ui_spec_validation";
            public string status = "invalid";
            public string validationScope = "spec_only";
            public string projectRoot;
            public bool valid;
            public bool requiresConfirmation = true;
            public bool projectChecksPerformed;
            public List<ValidationError> errors = new List<ValidationError>();
            public JObject validatedSpec;
            public CreationPlan creationPlan;
            public string[] limitations = {
                "Validation never creates UI, folders, Prefabs or input/gameplay bindings.",
                "Spec validity does not prove font glyph coverage, text fit, visual quality or successful input.",
                "Project preflight is a snapshot; the future generator must revalidate and obtain confirmation for the same Spec and project."
            };
        }

        public static string ValidateJson(string json)
        {
            return JsonConvert.SerializeObject(Validate(json));
        }

        public static string ValidateForProjectJson(string json)
        {
            Report report = Validate(json);
            report.validationScope = "unity_project";
            report.projectRoot = Path.GetDirectoryName(Application.dataPath);
            if (report.valid)
            {
                if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode)
                    Error(report, "EDITOR_NOT_READY", "$", "请在空闲 Edit Mode 校验目标项目。");
                else
                {
                    report.projectChecksPerformed = true;
                    ValidateDestination(report, Application.dataPath);
                }
            }
            Finish(report);
            return JsonConvert.SerializeObject(report);
        }

        public static Report Validate(string json)
        {
            var report = new Report();
            if (string.IsNullOrWhiteSpace(json))
            {
                Error(report, "JSON_REQUIRED", "$", "请提供 UI Spec JSON。");
                return report;
            }
            if (json.Length > MaxJsonLength)
            {
                Error(report, "JSON_TOO_LARGE", "$", "JSON 最多 16384 个 UTF-16 单元。");
                return report;
            }
            JObject spec;
            try
            {
                CheckJsonLexemes(json);
                using (var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None, MaxDepth = 12 })
                {
                    spec = JToken.ReadFrom(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error }) as JObject;
                    if (spec == null) throw new JsonReaderException("Spec root must be an object.");
                    if (reader.Read()) throw new JsonReaderException("Extra content after Spec.");
                }
            }
            catch (JsonException)
            {
                Error(report, "JSON_INVALID", "$", "JSON 格式无效：必须为单个对象，禁止重复字段、注释、尾逗号和非标准字面量。");
                return report;
            }
            CheckFields(spec, Fields, "$", report);
            IntegerValue(spec["schemaVersion"], 1, "schemaVersion", report);
            EnumValue(spec["screenType"], new[] { "main_menu" }, "screenType", report);
            EnumValue(spec["targetPlatform"], new[] { "desktop" }, "targetPlatform", report);
            JObject resolution = spec["referenceResolution"] as JObject;
            if (resolution == null)
                Error(report, "FIELD_TYPE", "referenceResolution", "参考分辨率必须是对象。");
            else
            {
                CheckFields(resolution, new[] { "width", "height" }, "referenceResolution", report);
                IntegerValue(resolution["width"], 1920, "referenceResolution.width", report);
                IntegerValue(resolution["height"], 1080, "referenceResolution.height", report);
            }
            Label(spec["title"], 80, "title", report);
            JArray buttons = spec["buttons"] as JArray;
            if (buttons == null) Error(report, "FIELD_TYPE", "buttons", "按钮必须是字符串数组。");
            else
            {
                if (buttons.Count < 1 || buttons.Count > 5) Error(report, "BUTTON_COUNT", "buttons", "按钮数量必须为 1～5。");
                for (int i = 0; i < Math.Min(buttons.Count, 5); i++) Label(buttons[i], 32, "buttons[" + i + "]", report);
            }
            EnumValue(spec["theme"], new[] { "dark", "light" }, "theme", report);
            if (spec["outputPrefabPath"]?.Type != JTokenType.String)
                Error(report, "FIELD_TYPE", "outputPrefabPath", "输出路径必须是字符串。");
            else if (!ValidOutputPath((string)spec["outputPrefabPath"]))
                Error(report, "OUTPUT_PATH_INVALID", "outputPrefabPath", "使用 Assets/AgentDemos/UIAgent/Prefabs/ 下的 .prefab 路径；目录/文件基名仅限英文字母、数字、下划线、连字符，禁止保留名称和路径跳转。");

            if (report.errors.Count == 0)
            {
                // Fixed field ordering makes fingerprints independent of JSON property order.
                report.validatedSpec = new JObject {
                    ["schemaVersion"] = 1, ["screenType"] = "main_menu", ["targetPlatform"] = "desktop",
                    ["referenceResolution"] = new JObject { ["width"] = 1920, ["height"] = 1080 },
                    ["title"] = (string)spec["title"], ["buttons"] = buttons.DeepClone(),
                    ["theme"] = (string)spec["theme"], ["outputPrefabPath"] = (string)spec["outputPrefabPath"]
                };
                string canonical = report.validatedSpec.ToString(Formatting.None);
                string fingerprint;
                using (SHA256 sha = SHA256.Create())
                    fingerprint = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(canonical))).Replace("-", "").ToLowerInvariant();
                report.creationPlan = new CreationPlan {
                    outputPrefabPath = (string)spec["outputPrefabPath"], title = (string)spec["title"],
                    buttons = buttons.Select(b => (string)b).ToArray(), theme = (string)spec["theme"], specFingerprint = fingerprint
                };
            }
            Finish(report);
            return report;
        }

        private static bool ValidOutputPath(string path)
        {
            if (path == null || path.Length > 240 || !path.StartsWith(OutputPrefix, StringComparison.Ordinal) ||
                !path.EndsWith(".prefab", StringComparison.Ordinal)) return false;
            string[] parts = path.Substring(OutputPrefix.Length, path.Length - OutputPrefix.Length - 7).Split('/');
            return parts.Length <= 8 && parts.All(p => Segment.IsMatch(p) && !Reserved.IsMatch(p));
        }

        private static void ValidateDestination(Report report, string assetsPath)
        {
            string relative = report.creationPlan.outputPrefabPath.Substring("Assets/".Length);
            try
            {
                string current = Path.GetFullPath(assetsPath);
                // Reject redirection in Assets or any existing target ancestor.
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    Error(report, "OUTPUT_PATH_REDIRECTED", "outputPrefabPath", "Assets 是链接目录，第一版不允许此输出位置。");
                    return;
                }
                string[] parts = relative.Split('/');
                for (int i = 0; i < parts.Length; i++)
                {
                    current = Path.Combine(current, parts[i]);
                    FileAttributes attributes;
                    try { attributes = File.GetAttributes(current); }
                    catch (FileNotFoundException) { continue; }
                    catch (DirectoryNotFoundException) { continue; }
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        Error(report, "OUTPUT_PATH_REDIRECTED", "outputPrefabPath", "目标路径包含链接或 junction，第一版不支持。");
                        return;
                    }
                    bool directory = (attributes & FileAttributes.Directory) != 0;
                    if (i == parts.Length - 1)
                        Error(report, "OUTPUT_ALREADY_EXISTS", "outputPrefabPath", "目标文件或目录已存在，禁止覆盖。");
                    else if (!directory)
                    {
                        Error(report, "OUTPUT_PARENT_NOT_DIRECTORY", "outputPrefabPath", "目标父路径被文件占用。");
                        return;
                    }
                }
                // Preserve orphaned metadata/GUIDs as well as currently imported assets.
                try
                {
                    File.GetAttributes(current + ".meta");
                    Error(report, "OUTPUT_METADATA_EXISTS", "outputPrefabPath", "目标已有 .meta，禁止复用或覆盖其 GUID。");
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                if (!string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(report.creationPlan.outputPrefabPath)))
                    if (!report.errors.Any(e => e.code == "OUTPUT_ALREADY_EXISTS"))
                        Error(report, "OUTPUT_ALREADY_EXISTS", "outputPrefabPath", "Unity 已登记目标资源，禁止覆盖。");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException || ex is System.Security.SecurityException)
            {
                Error(report, "PROJECT_CHECK_FAILED", "outputPrefabPath", "无法完整读取目标路径；请检查路径和读取权限。");
            }
        }

        private static void CheckFields(JObject obj, string[] expected, string prefix, Report report)
        {
            foreach (string name in expected)
                if (obj.Property(name, StringComparison.Ordinal) == null) Error(report, "FIELD_REQUIRED", Field(prefix, name), "缺少必填字段。");
            foreach (JProperty property in obj.Properties())
                if (!expected.Contains(property.Name)) Error(report, "FIELD_UNKNOWN", Field(prefix, property.Name), "第一版不支持此字段。");
        }

        private static string Field(string prefix, string name) { return prefix == "$" ? name : prefix + "." + name; }

        private static void IntegerValue(JToken token, int value, string field, Report report)
        {
            if (token?.Type != JTokenType.Integer) Error(report, "FIELD_TYPE", field, "必须使用 JSON 整数。");
            else if (token.ToString(Formatting.None) != value.ToString(System.Globalization.CultureInfo.InvariantCulture))
                Error(report, "FIELD_VALUE", field, "第一版仅支持 " + value + "。");
        }

        private static void EnumValue(JToken token, string[] values, string field, Report report)
        {
            if (token?.Type != JTokenType.String) Error(report, "FIELD_TYPE", field, "必须使用字符串。");
            else if (!values.Contains((string)token)) Error(report, "FIELD_VALUE", field, "支持值：" + string.Join(", ", values) + "。");
        }

        private static void Label(JToken token, int limit, string field, Report report)
        {
            if (token?.Type != JTokenType.String) { Error(report, "FIELD_TYPE", field, "必须使用文本字符串。"); return; }
            string text = (string)token;
            if (string.IsNullOrWhiteSpace(text) || text.Length > limit || text != text.Trim() ||
                text.Any(c => char.IsControl(c) || c == '<' || c == '>') || HasInvalidSurrogate(text))
                Error(report, "TEXT_INVALID", field, "文本须为非空单行，无首尾空白、控制字符或富文本标签；最多 " + limit + " 个 UTF-16 单元。");
        }

        private static bool HasInvalidSurrogate(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                if (char.IsHighSurrogate(text[i])) { if (i + 1 >= text.Length || !char.IsLowSurrogate(text[++i])) return true; }
                else if (char.IsLowSurrogate(text[i])) return true;
            }
            return false;
        }

        private static void CheckJsonLexemes(string json)
        {
            // Json.NET accepts JavaScript extensions. Limit them before structural parsing.
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
                if (literal != "true" && literal != "false" && literal != "null" && !Number.IsMatch(literal))
                    throw new JsonReaderException("Invalid literal.");
            }
        }

        private static void Error(Report report, string code, string field, string message)
        {
            report.errors.Add(new ValidationError { code = code, field = field, message = message });
        }

        private static void Finish(Report report)
        {
            report.valid = report.errors.Count == 0;
            report.status = !report.valid ? "invalid" : report.projectChecksPerformed ? "ready_for_confirmation" : "valid";
            if (!report.valid) { report.validatedSpec = null; report.creationPlan = null; }
        }
    }
}
