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
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace UIAgent.Tools.Editor
{
    /// <summary>Editor authoring only. Confirmation is a caller assertion, not authentication.</summary>
    public static class MainMenuGenerator
    {
        public const string TemplateVersion = "desktop-menu-v1";

        public sealed class Confirmation
        {
            public bool userConfirmed;
            public string planFingerprint;
        }

        public sealed class Report
        {
            public int schemaVersion = 1;
            public string reportType = "main_menu_generation";
            public string status = "blocked";
            public bool success;
            public JObject plan;
            public List<UiSpecValidator.ValidationError> errors = new List<UiSpecValidator.ValidationError>();
            public string prefabPath, prefabGuid, instanceId, createdEventSystemId;
            public bool sceneSaved = false;
            public bool sceneDirty;
            public bool acceptancePerformed = false;
            public List<string> cleanupErrors = new List<string>();
            public string[] limitations = {
                "User confirmation must be obtained by the caller; a hash is not proof of human approval.",
                "Undo removes generated scene objects only; the saved Prefab and folders remain.",
                "Failure cleanup may leave the scene dirty; it never saves or reloads user scenes.",
                "Buttons have no gameplay listeners. Generation is not visual or runtime acceptance.",
                "Do not concurrently write the same output path from another process. Preflight is not an OS file lock."
            };
        }

        private sealed class Context
        {
            public Scene scene;
            public TMP_FontAsset font;
            public Type inputType;
            public EventSystem existingSystem;
            public JObject spec;
        }

        public static string PrepareJson(string specJson, string fontAssetPath)
        {
            Context context;
            return JsonConvert.SerializeObject(Prepare(specJson, fontAssetPath, out context));
        }

        public static string GenerateJson(string specJson, string fontAssetPath, Confirmation confirmation)
        {
            return JsonConvert.SerializeObject(Generate(specJson, fontAssetPath, confirmation, null));
        }

        // Fault checkpoints are only available to the friend test assembly.
        internal static Report Generate(string specJson, string fontAssetPath, Confirmation confirmation, Action<string> checkpoint)
        {
            Context context;
            Report report = Prepare(specJson, fontAssetPath, out context);
            if (report.errors.Count != 0) return report;
            if (confirmation == null || !confirmation.userConfirmed ||
                !string.Equals(confirmation.planFingerprint, (string)report.plan["planFingerprint"], StringComparison.Ordinal))
            {
                Add(report, "CONFIRMATION_REQUIRED", "confirmation", "请先展示完整计划并取得用户确认；项目、场景、字体或 Spec 变化后必须重新确认。");
                report.status = "confirmation_required";
                return report;
            }

            string path = (string)context.spec["outputPrefabPath"];
            var createdFolders = new List<KeyValuePair<string, string>>();
            GameObject instance = null, eventObject = null;
            string ownedGuid = null;
            bool saveAttempted = false;
            GameObject temporary = null;
            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("UI Agent: Generate Main Menu");
            try
            {
                temporary = Build(context.scene, context.spec, context.font);
                checkpoint?.Invoke("built");
                // No folders or assets are touched until layout and font checks pass.
                EnsureFolders(path.Substring(0, path.LastIndexOf('/')), createdFolders);
                var fresh = JObject.Parse(UiSpecValidator.ValidateForProjectJson(specJson));
                if (!(bool)fresh["valid"]) throw new InvalidOperationException("输出路径在生成前已被占用或不再安全：" + fresh["errors"]);
                checkpoint?.Invoke("before_save");
                // Recheck after the test seam too. No asynchronous yield occurs in this transaction.
                fresh = JObject.Parse(UiSpecValidator.ValidateForProjectJson(specJson));
                if (!(bool)fresh["valid"]) throw new InvalidOperationException("输出冲突：" + fresh["errors"]);
                saveAttempted = true;
                bool saved;
                temporary.hideFlags = HideFlags.None;
                temporary.GetComponent<Canvas>().enabled = true;
                GameObject prefab = PrefabUtility.SaveAsPrefabAsset(temporary, path, out saved);
                ownedGuid = AssetDatabase.AssetPathToGUID(path);
                if (!saved || prefab == null || string.IsNullOrEmpty(ownedGuid))
                    throw new InvalidOperationException("Prefab 保存失败。");
                UnityEngine.Object.DestroyImmediate(temporary);
                temporary = null;
                checkpoint?.Invoke("saved");
                instance = PrefabUtility.InstantiatePrefab(prefab, context.scene) as GameObject;
                if (instance == null) throw new InvalidOperationException("Prefab 实例创建失败。");
                Undo.RegisterCreatedObjectUndo(instance, "UI Agent: Create Menu");
                if (context.existingSystem == null)
                {
                    eventObject = new GameObject("UIAgent_EventSystem", typeof(EventSystem), context.inputType);
                    SceneManager.MoveGameObjectToScene(eventObject, context.scene);
                    Undo.RegisterCreatedObjectUndo(eventObject, "UI Agent: Create EventSystem");
                }
                checkpoint?.Invoke("instantiated");
                EditorSceneManager.MarkSceneDirty(context.scene);
                Undo.CollapseUndoOperations(undoGroup);
                report.status = "generated";
                report.success = true;
                report.prefabPath = path;
                report.prefabGuid = ownedGuid;
                report.instanceId = instance.GetInstanceID().ToString();
                report.createdEventSystemId = eventObject != null ? eventObject.GetInstanceID().ToString() : null;
            }
            catch (Exception ex)
            {
                Add(report, "GENERATION_FAILED", "$", ex.Message);
                // Never undo the shared editor stack or delete another asset on failure.
                CleanupObject(eventObject, report);
                CleanupObject(instance, report);
                if (!string.IsNullOrEmpty(ownedGuid))
                {
                    try
                    {
                        if (AssetDatabase.AssetPathToGUID(path) != ownedGuid || !AssetDatabase.DeleteAsset(path))
                            report.cleanupErrors.Add("Prefab 未能安全清理，请检查：" + path);
                    }
                    catch (Exception cleanup) { report.cleanupErrors.Add(cleanup.Message); }
                }
                else if (saveAttempted && (File.Exists(DiskPath(path)) || File.Exists(DiskPath(path + ".meta"))))
                    report.cleanupErrors.Add("保存中断且无法确认资产 GUID，请人工检查：" + path);
                CleanupFolders(createdFolders, report);
                report.status = report.cleanupErrors.Count == 0 ? "failed_rolled_back" : "failed_cleanup_required";
            }
            finally
            {
                if (temporary != null) UnityEngine.Object.DestroyImmediate(temporary);
                report.sceneDirty = context.scene.isDirty;
                Undo.IncrementCurrentGroup();
            }
            return report;
        }

        private static Report Prepare(string specJson, string fontAssetPath, out Context context)
        {
            context = null;
            var report = new Report();
            JObject validation = JObject.Parse(UiSpecValidator.ValidateForProjectJson(specJson));
            foreach (JToken error in (JArray)validation["errors"])
                report.errors.Add(error.ToObject<UiSpecValidator.ValidationError>());
            if (report.errors.Count > 0) return report;
            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded || EditorSceneManager.IsPreviewScene(scene) || string.IsNullOrEmpty(scene.path))
                Add(report, "SCENE_REQUIRED", "scene", "请使用已保存的普通测试场景。");
            else if (scene.isDirty)
                Add(report, "SCENE_DIRTY", "scene", "请先自行保存或处理目标场景中的未保存修改。");
            if (PrefabStageUtility.GetCurrentPrefabStage() != null)
                Add(report, "PREFAB_STAGE_OPEN", "scene", "请先关闭 Prefab Mode。");
            TMP_FontAsset font = null;
            if (!string.IsNullOrEmpty(fontAssetPath) && fontAssetPath.StartsWith("Assets/", StringComparison.Ordinal) &&
                !fontAssetPath.Contains("..") && !fontAssetPath.Contains("\\"))
                font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(fontAssetPath);
            if (font == null || font.material == null)
                Add(report, "FONT_REQUIRED", "fontAssetPath", "请选择项目中已有且具有材质的 TMP 字体资源；不会自动安装或替换字体。");

            var systems = Enumerable.Range(0, SceneManager.sceneCount)
                .SelectMany(i => SceneManager.GetSceneAt(i).GetRootGameObjects())
                .SelectMany(r => r.GetComponentsInChildren<EventSystem>(true)).ToArray();
            EventSystem existing = systems.Length == 1 ? systems[0] : null;
            Type inputType = ResolveInputType();
            if (systems.Length > 1)
                Add(report, "EVENT_SYSTEM_CONFLICT", "scene", "已加载场景有多个 EventSystem，生成器不会自动删除或替换。");
            if (existing != null && (!existing.isActiveAndEnabled ||
                existing.GetComponents<BaseInputModule>().Count(m => m.isActiveAndEnabled && m.GetType() == inputType) != 1 ||
                existing.GetComponents<BaseInputModule>().Count(m => m.isActiveAndEnabled) != 1))
                Add(report, "INPUT_MODULE_CONFLICT", "scene", "现有 EventSystem 未启用，或输入模块与项目设置不一致；不会自动修复。");
            if (inputType == null)
                Add(report, "INPUT_MODULE_UNAVAILABLE", "scene", "项目所需输入模块不可用。");
            JObject spec = (JObject)validation["validatedSpec"];
            if (font != null)
            {
                string content = (string)spec["title"] + string.Concat(((JArray)spec["buttons"]).Select(t => (string)t));
                uint[] missing = CodePoints(content).Distinct().Where(c => !HasGlyph(font, c, new HashSet<TMP_FontAsset>())).ToArray();
                if (missing.Length > 0)
                    Add(report, "FONT_GLYPHS_MISSING", "fontAssetPath", "现有字体与显式 fallback 缺少字符：" + string.Join(", ", missing.Select(c => "U+" + c.ToString("X4"))) + "。请配置字体后重新确认；不会修改共享字库。");
            }
            if (report.errors.Count > 0) return report;
            context = new Context { scene = scene, font = font, inputType = inputType, existingSystem = existing, spec = spec };
            GameObject temporary = null;
            try
            {
                temporary = Build(scene, spec, font);
            }
            catch (Exception ex) { Add(report, "LAYOUT_PREFLIGHT_FAILED", "$", ex.Message); }
            finally { if (temporary != null) UnityEngine.Object.DestroyImmediate(temporary); }
            if (report.errors.Count > 0) return report;
            var fonts = new HashSet<TMP_FontAsset>();
            CollectFonts(font, fonts);
            var plan = new JObject {
                ["templateVersion"] = TemplateVersion,
                ["projectRoot"] = (string)validation["projectRoot"],
                ["scenePath"] = scene.path, ["sceneHandle"] = scene.handle,
                ["specFingerprint"] = validation["creationPlan"]["specFingerprint"],
                ["spec"] = spec.DeepClone(),
                ["fontAssetPath"] = fontAssetPath,
                ["fontDependencies"] = new JArray(fonts.Select(f => AssetDatabase.GetAssetPath(f)).OrderBy(p => p, StringComparer.Ordinal).Select(p => new JObject {
                    ["path"] = p, ["guid"] = AssetDatabase.AssetPathToGUID(p), ["hash"] = AssetDatabase.GetAssetDependencyHash(p).ToString()
                })),
                ["inputModule"] = inputType.FullName,
                ["eventSystemAction"] = existing == null ? "create" : "reuse",
                ["existingEventSystemId"] = existing != null ? existing.GetInstanceID().ToString() : null,
                ["existingCanvasIds"] = new JArray(scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Canvas>(true)).Select(c => c.GetInstanceID()).OrderBy(i => i)),
                ["layout"] = new JObject { ["titleSize"] = "1440x120", ["titleFontSize"] = 64, ["buttonSize"] = "640x88", ["buttonFontSize"] = 32, ["buttonGap"] = 24 },
                ["overwriteAllowed"] = false, ["bindGameLogic"] = false, ["saveScene"] = false,
                ["undoScope"] = "Scene menu instance and newly created EventSystem only; saved Prefab/folders remain.",
                ["warnings"] = new JArray("Existing UI is preserved; use an isolated preview for acceptance.", "No start/settings/quit gameplay logic is bound.")
            };
            using (SHA256 sha = SHA256.Create())
                plan["planFingerprint"] = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(plan.ToString(Formatting.None)))).Replace("-", "").ToLowerInvariant();
            report.plan = plan;
            report.status = "ready_for_confirmation";
            return report;
        }

        internal static GameObject Build(Scene scene, JObject spec, TMP_FontAsset font)
        {
            if (SceneManager.GetActiveScene() != scene) throw new InvalidOperationException("活动场景已变化。");
            bool dark = (string)spec["theme"] == "dark";
            Color background = dark ? new Color(.035f, .055f, .09f) : new Color(.93f, .95f, .98f);
            Color foreground = dark ? new Color(.94f, .97f, 1f) : new Color(.08f, .12f, .20f);
            Color buttonColor = dark ? new Color(.12f, .22f, .36f) : new Color(.16f, .32f, .54f);
            var root = new GameObject("UIAgent_MainMenu", typeof(RectTransform));
            root.hideFlags = HideFlags.HideAndDontSave;
            root.SetActive(false);
            try
            {
            var canvas = root.AddComponent<Canvas>();
            canvas.enabled = false;
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            CanvasScaler scaler = root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = .5f;
            root.AddComponent<GraphicRaycaster>();
            Image bg = Rect("Background", root.transform, Vector2.zero, Vector2.zero).gameObject.AddComponent<Image>();
            Stretch(bg.rectTransform, 0);
            bg.color = background; bg.raycastTarget = false;
            Text("Title", root.transform, font, (string)spec["title"], new Vector2(1440, 120), new Vector2(0, 320), 64, foreground);
            JArray labels = (JArray)spec["buttons"];
            RectTransform group = Rect("ButtonGroup", root.transform, new Vector2(640, labels.Count * 88 + (labels.Count - 1) * 24), new Vector2(0, -60));
            var layout = group.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.spacing = 24; layout.childControlWidth = layout.childControlHeight = true;
            layout.childForceExpandWidth = layout.childForceExpandHeight = false;
            var buttons = new List<Button>();
            for (int i = 0; i < labels.Count; i++)
            {
                RectTransform rect = Rect("Button_" + (i + 1).ToString("00"), group, new Vector2(640, 88), Vector2.zero);
                var size = rect.gameObject.AddComponent<LayoutElement>();
                size.minWidth = size.preferredWidth = 640; size.minHeight = size.preferredHeight = 88;
                Image image = rect.gameObject.AddComponent<Image>();
                image.color = buttonColor; image.raycastTarget = true;
                Button button = rect.gameObject.AddComponent<Button>();
                button.targetGraphic = image;
                ColorBlock colors = button.colors;
                colors.highlightedColor = new Color(.8f, .9f, 1f); colors.pressedColor = new Color(.6f, .75f, .95f);
                button.colors = colors;
                Text("Label", rect, font, (string)labels[i], new Vector2(592, 72), Vector2.zero, 32, Color.white);
                buttons.Add(button);
            }
            for (int i = 0; i < buttons.Count; i++)
                buttons[i].navigation = new Navigation { mode = Navigation.Mode.Explicit,
                    selectOnUp = buttons[(i + buttons.Count - 1) % buttons.Count], selectOnDown = buttons[(i + 1) % buttons.Count] };
            root.SetActive(true);
            // Rebuild only owned hierarchy; no global ForceUpdateCanvases.
            LayoutRebuilder.ForceRebuildLayoutImmediate(group);
            foreach (TextMeshProUGUI text in root.GetComponentsInChildren<TextMeshProUGUI>())
            {
                Vector2 preferred = text.GetPreferredValues(text.text);
                if (preferred.x > text.rectTransform.rect.width + 1 || preferred.y > text.rectTransform.rect.height + 1)
                    throw new InvalidOperationException("文案超出固定模板容器：" + text.name + "；请缩短文案或等待后续模板扩展。");
            }
            return root;
            }
            catch
            {
                UnityEngine.Object.DestroyImmediate(root);
                throw;
            }
        }

        private static RectTransform Rect(string name, Transform parent, Vector2 size, Vector2 position)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            RectTransform rect = (RectTransform)go.transform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(.5f, .5f);
            rect.sizeDelta = size; rect.anchoredPosition = position;
            return rect;
        }

        private static void Stretch(RectTransform rect, float inset)
        {
            rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(inset, inset); rect.offsetMax = new Vector2(-inset, -inset);
        }

        private static void Text(string name, Transform parent, TMP_FontAsset font, string value, Vector2 size, Vector2 pos, float fontSize, Color color)
        {
            RectTransform rect = Rect(name, parent, size, pos);
            var text = rect.gameObject.AddComponent<TextMeshProUGUI>();
            text.font = font; text.text = value; text.fontSize = fontSize; text.color = color;
            text.alignment = TextAlignmentOptions.Center;
            text.enableAutoSizing = false; text.enableWordWrapping = false;
            text.richText = false; text.raycastTarget = false; text.overflowMode = TextOverflowModes.Truncate;
        }

        private static Type ResolveInputType()
        {
            var settings = new SerializedObject(Unsupported.GetSerializedAssetInterfaceSingleton("PlayerSettings"));
            SerializedProperty input = settings.FindProperty("activeInputHandler");
            if (input == null) return null;
            if (input.intValue == 0) return typeof(StandaloneInputModule);
            return AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("UnityEngine.InputSystem.UI.InputSystemUIInputModule")).FirstOrDefault(t => t != null);
        }

        private static IEnumerable<uint> CodePoints(string value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                if (char.IsHighSurrogate(value[i])) { yield return (uint)char.ConvertToUtf32(value[i], value[++i]); }
                else yield return value[i];
            }
        }

        private static bool HasGlyph(TMP_FontAsset font, uint value, HashSet<TMP_FontAsset> seen)
        {
            if (font == null || !seen.Add(font)) return false;
            if (font.characterLookupTable.ContainsKey(value)) return true;
            return font.fallbackFontAssetTable != null && font.fallbackFontAssetTable.Any(f => HasGlyph(f, value, seen));
        }

        private static void CollectFonts(TMP_FontAsset font, HashSet<TMP_FontAsset> seen)
        {
            if (font == null || !seen.Add(font)) return;
            if (font.fallbackFontAssetTable != null) foreach (TMP_FontAsset fallback in font.fallbackFontAssetTable) CollectFonts(fallback, seen);
        }

        private static void EnsureFolders(string path, List<KeyValuePair<string, string>> created)
        {
            string parent = "Assets";
            foreach (string part in path.Split('/').Skip(1))
            {
                string next = parent + "/" + part;
                if (!AssetDatabase.IsValidFolder(next))
                {
                    if (Directory.Exists(DiskPath(next)) || File.Exists(DiskPath(next)) || File.Exists(DiskPath(next + ".meta")))
                        throw new InvalidOperationException("父目录尚未被 Unity 导入，或有路径/元数据冲突：" + next);
                    string guid = AssetDatabase.CreateFolder(parent, part);
                    if (string.IsNullOrEmpty(guid) || AssetDatabase.GUIDToAssetPath(guid) != next)
                        throw new InvalidOperationException("不能按指定路径创建目录：" + next);
                    created.Add(new KeyValuePair<string, string>(next, guid));
                }
                parent = next;
            }
        }

        private static void CleanupObject(GameObject obj, Report report)
        {
            if (obj == null) return;
            try { Undo.ClearUndo(obj); UnityEngine.Object.DestroyImmediate(obj); }
            catch (Exception ex) { report.cleanupErrors.Add(ex.Message); }
        }

        private static void CleanupFolders(List<KeyValuePair<string, string>> folders, Report report)
        {
            foreach (var folder in folders.AsEnumerable().Reverse())
            {
                try
                {
                    if (AssetDatabase.AssetPathToGUID(folder.Key) != folder.Value || Directory.GetFileSystemEntries(DiskPath(folder.Key)).Length != 0 || !AssetDatabase.DeleteAsset(folder.Key))
                        report.cleanupErrors.Add("保留无法安全清理的目录：" + folder.Key);
                }
                catch (Exception ex) { report.cleanupErrors.Add(ex.Message); }
            }
        }

        private static void Add(Report report, string code, string field, string message)
        {
            report.errors.Add(new UiSpecValidator.ValidationError { code = code, field = field, message = message });
        }

        private static string DiskPath(string assetPath)
        {
            return Path.Combine(Path.GetDirectoryName(Application.dataPath), assetPath);
        }
    }
}
