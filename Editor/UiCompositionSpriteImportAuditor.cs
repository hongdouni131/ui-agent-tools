using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace UIAgent.Tools.Editor
{
    /// <summary>Read-only report of texture import readiness for the v2 Sprite contract.</summary>
    public static class UiCompositionSpriteImportAuditor
    {
        public const int MaxSources = 1000, MaxSprites = 10000;

        public sealed class Report
        {
            public int schemaVersion = 1;
            public string reportType = "ui_composition_sprite_import_audit";
            public string status = "blocked";
            public bool complete;
            public string sourcePath, catalogFingerprint;
            public int sourceCount, scannedSources, readySourceCount, requiresManualActionCount, excludedNonSpriteSourceCount;
            public int spriteCount, simpleUsableCount, slicedUsableCount, slicedBorderReviewCount;
            public List<JObject> sources = new List<JObject>();
            public List<JObject> errors = new List<JObject>();
            public string[] limitations = {
                "Read-only: does not import, reimport, slice, edit, or save assets.",
                "Simple/sliced readiness checks the current UI Agent Sprite contract, not visual suitability or licensing.",
                "Pixels Per Unit, filter mode, compression, and mipmaps are reported as facts; project policy is not defined here.",
                "A zero or unusable Border is acceptable for Simple images; it requires manual Sprite Editor work only if Sliced use is intended."
            };
        }

        public static string AuditJson(string sourcePath) => JsonConvert.SerializeObject(Audit(sourcePath));

        public static Report Audit(string sourcePath)
        {
            var report = new Report { sourcePath = sourcePath };
            if (!ValidPath(sourcePath)) return Fail(report, "INVALID_ARGUMENTS", "Use a normalized Assets-relative file or folder path.");
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode)
                return Fail(report, "EDITOR_NOT_READY", "Sprite import audit requires idle Edit Mode.");

            bool isFolder = AssetDatabase.IsValidFolder(sourcePath);
            string[] paths;
            if (isFolder)
            {
                paths = AssetDatabase.FindAssets("t:Texture2D", new[] { sourcePath })
                    .Concat(AssetDatabase.FindAssets("t:Sprite", new[] { sourcePath }))
                    .Select(AssetDatabase.GUIDToAssetPath).Distinct().OrderBy(p => p, StringComparer.Ordinal).ToArray();
            }
            else if (!string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(sourcePath))) paths = new[] { sourcePath };
            else return Fail(report, "SOURCE_NOT_FOUND", "No imported asset or folder exists at sourcePath.");

            report.sourceCount = paths.Length;
            if (paths.Length > MaxSources)
                return Fail(report, "SCAN_LIMIT", "More than 1000 texture/Sprite source files; narrow sourcePath. No partial report is returned.");

            try
            {
                foreach (string path in paths)
                {
                    var source = AuditSource(path, out string errorCode, out string errorMessage);
                    if (source == null)
                    {
                        if (errorCode != null) return Fail(report, errorCode, errorMessage);
                        continue;
                    }
                    report.scannedSources++;
                    string readiness = (string)source["readiness"];
                    if (readiness == "ready_for_composition") report.readySourceCount++;
                    if (readiness == "requires_manual_action" || readiness == "not_configured_as_sprite")
                        report.requiresManualActionCount++;
                    if (readiness == "not_configured_as_sprite" || readiness == "not_applicable")
                        report.excludedNonSpriteSourceCount++;
                    foreach (JObject sprite in (JArray)source["sprites"])
                    {
                        report.spriteCount++;
                        if (report.spriteCount > MaxSprites)
                            return Fail(report, "SCAN_LIMIT", "More than 10000 Sprites; narrow sourcePath. No partial report is returned.");
                        if ((bool)sprite["simpleUsable"]) report.simpleUsableCount++;
                        if ((bool)sprite["slicedUsable"]) report.slicedUsableCount++;
                        else if ((string)sprite["slicedStatus"] == "manual_border_required") report.slicedBorderReviewCount++;
                    }
                    report.sources.Add(source);
                }
                using (var sha = SHA256.Create())
                    report.catalogFingerprint = string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(new JObject {
                        ["sourcePath"] = sourcePath, ["sources"] = JArray.FromObject(report.sources)
                    }.ToString(Formatting.None))).Select(b => b.ToString("x2")));
                report.complete = true;
                report.status = "complete";
            }
            catch (Exception e) { return Fail(report, "AUDIT_READ_FAILED", e.Message); }
            return report;
        }

        private static JObject AuditSource(string path, out string errorCode, out string errorMessage)
        {
            errorCode = null; errorMessage = null;
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            string mode;
            Sprite[] sprites;
            JObject importSettings = null;
            if (importer != null)
            {
                mode = importer.textureType == TextureImporterType.Sprite
                    ? importer.spriteImportMode.ToString().ToLowerInvariant()
                    : "not_sprite";
                importSettings = new JObject {
                    ["textureType"] = importer.textureType.ToString(),
                    ["spriteImportMode"] = importer.spriteImportMode.ToString(),
                    ["pixelsPerUnit"] = importer.spritePixelsPerUnit,
                    ["filterMode"] = importer.filterMode.ToString(),
                    ["compression"] = importer.textureCompression.ToString(),
                    ["mipmapEnabled"] = importer.mipmapEnabled
                };
                sprites = importer.textureType == TextureImporterType.Sprite &&
                    (importer.spriteImportMode == SpriteImportMode.Single || importer.spriteImportMode == SpriteImportMode.Multiple)
                    ? (importer.spriteImportMode == SpriteImportMode.Multiple
                        ? AssetDatabase.LoadAllAssetsAtPath(path).OfType<Sprite>().ToArray()
                        : new[] { AssetDatabase.LoadAssetAtPath<Sprite>(path) })
                    : Array.Empty<Sprite>();
            }
            else
            {
                var main = AssetDatabase.LoadMainAssetAtPath(path) as Sprite;
                if (main == null || !path.EndsWith(".asset", StringComparison.Ordinal))
                    return new JObject {
                        ["path"] = path, ["guid"] = AssetDatabase.AssetPathToGUID(path),
                        ["dependencyHash"] = AssetDatabase.GetAssetDependencyHash(path).ToString(),
                        ["importMode"] = "not_applicable", ["readiness"] = "not_applicable",
                        ["manualReasons"] = new JArray("Not a texture importer or supported native main Sprite .asset."),
                        ["sprites"] = new JArray()
                    };
                mode = "native";
                sprites = new[] { main };
            }

            string guid = AssetDatabase.AssetPathToGUID(path);
            var spriteReports = new JArray();
            var manualReasons = new JArray();
            if (importer == null && sprites.Length == 0)
                manualReasons.Add("This asset is not an imported Sprite or native Sprite asset.");
            else if (importer != null && importer.textureType != TextureImporterType.Sprite)
                manualReasons.Add("Texture Type is not Sprite; this source is excluded from UI Sprite candidates until a developer selects it for conversion.");
            else if (importer != null && importer.spriteImportMode != SpriteImportMode.Single && importer.spriteImportMode != SpriteImportMode.Multiple)
                manualReasons.Add("This Sprite import mode is not supported by the current composition generator; choose Single or Multiple manually.");
            else if (sprites.Length == 0)
                manualReasons.Add("No Sprite subasset was found; configure the Sprite slices manually if intended.");

            if (sprites.Length > MaxSprites)
            {
                errorCode = "SCAN_LIMIT"; errorMessage = "More than 10000 Sprites; narrow sourcePath.";
                return null;
            }
            foreach (Sprite sprite in sprites.OrderBy(s => s != null ? s.name : "", StringComparer.Ordinal)
                .ThenBy(s => s != null && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(s, out string g, out long id) ? id : 0))
            {
                if (sprite == null || !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(sprite, out string spriteGuid, out long localId) || localId == 0)
                {
                    errorCode = "SPRITE_IDENTITY_UNAVAILABLE";
                    errorMessage = "A Sprite source could not be read with a persistent GUID/local file ID; no partial report is returned.";
                    return null;
                }
                JObject item = UiCompositionSpriteResolver.Describe(sprite);
                Vector4 border = sprite.border;
                bool validBounds = border.x >= 0 && border.y >= 0 && border.z >= 0 && border.w >= 0 &&
                    border.x + border.z <= sprite.rect.width && border.y + border.w <= sprite.rect.height;
                bool hasBorder = border != Vector4.zero;
                bool supportedBorder = Enumerable.Range(0, 4).All(i => !float.IsNaN(border[i]) && !float.IsInfinity(border[i]) &&
                    border[i] >= 0 && border[i] <= 4096 && border[i] == Mathf.Floor(border[i]));
                bool importerBorderMatches = importer == null || mode == "multiple" || importer.spriteBorder == border;
                bool slicedUsable = hasBorder && validBounds && supportedBorder && importerBorderMatches;
                item["importMode"] = mode;
                item["simpleUsable"] = true;
                item["slicedUsable"] = slicedUsable;
                item["slicedStatus"] = slicedUsable ? "ready" : !hasBorder ? "manual_border_required" :
                    !validBounds ? "invalid_border_bounds" : !supportedBorder ? "unsupported_border_values" : "importer_border_mismatch";
                item["slicedManualAction"] = slicedUsable ? null : !hasBorder
                    ? "Set a non-zero Border in the Sprite Editor only if Sliced use is intended."
                    : "Review Border: v2 requires integer 0..4096 values, bounds within Rect, and matching Single importer/Sprite Border.";
                item["pixelsPerUnit"] = sprite.pixelsPerUnit;
                item["preview"] = new JObject {
                    ["texturePath"] = sprite.texture != null ? AssetDatabase.GetAssetPath(sprite.texture) : null,
                    ["textureWidth"] = sprite.texture != null ? sprite.texture.width : 0,
                    ["textureHeight"] = sprite.texture != null ? sprite.texture.height : 0,
                    ["rect"] = item["rect"].DeepClone(), ["origin"] = "bottom_left", ["units"] = "source_pixels"
                };
                var reference = new JObject { ["spritePath"] = path, ["spriteGuid"] = spriteGuid };
                if (mode == "multiple")
                {
                    reference["spriteName"] = sprite.name;
                    reference["spriteLocalFileId"] = localId.ToString(CultureInfo.InvariantCulture);
                }
                item["specReference"] = reference;
                spriteReports.Add(item);
            }

            string readiness = manualReasons.Count == 0 ? "ready_for_composition" :
                importer != null && importer.textureType != TextureImporterType.Sprite ? "not_configured_as_sprite" : "requires_manual_action";
            return new JObject {
                ["path"] = path, ["guid"] = guid, ["dependencyHash"] = AssetDatabase.GetAssetDependencyHash(path).ToString(), ["importMode"] = mode,
                ["readiness"] = readiness, ["manualReasons"] = manualReasons,
                ["importSettings"] = importSettings, ["sprites"] = spriteReports
            };
        }

        private static bool ValidPath(string path) => !string.IsNullOrEmpty(path) && path.Length <= 240 &&
            (path == "Assets" || path.StartsWith("Assets/", StringComparison.Ordinal)) &&
            !path.Any(c => char.IsControl(c) || "\\:*?\"<>|".IndexOf(c) >= 0) &&
            path.Split('/').All(p => p.Length != 0 && p != "." && p != ".." && !p.EndsWith(" ") && !p.EndsWith("."));

        private static Report Fail(Report report, string code, string message)
        {
            report.complete = false; report.status = "blocked"; report.sources.Clear();
            report.catalogFingerprint = null;
            report.scannedSources = report.readySourceCount = report.requiresManualActionCount = report.excludedNonSpriteSourceCount = 0;
            report.spriteCount = report.simpleUsableCount = report.slicedUsableCount = report.slicedBorderReviewCount = 0;
            report.errors.Add(new JObject { ["code"] = code, ["message"] = message });
            return report;
        }
    }
}
