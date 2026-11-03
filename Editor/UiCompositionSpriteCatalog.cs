using System;
using System.Collections.Generic;
using System.Globalization;
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
    /// <summary>Bounded, read-only discovery of Sprite identities supported by the v2 contract.</summary>
    public static class UiCompositionSpriteCatalog
    {
        public const int MaxSources = 1000, MaxSprites = 10000;

        public sealed class Report
        {
            public int schemaVersion = 1;
            public string reportType = "ui_composition_sprite_catalog";
            public string status = "blocked";
            // complete describes the scan, not whether this response contains every page.
            public bool complete, hasMore;
            public string sourcePath, nameContains, catalogFingerprint;
            public int offset, limit, sourceCount, scannedSources, totalMatches;
            public int? nextOffset;
            public List<JObject> sprites = new List<JObject>();
            public List<JObject> errors = new List<JObject>();
            public string[] limitations = {
                "Imported Assets only; no imports, slicing, reimports, writes, repairs or selection changes.",
                "Results describe source Sprites, not packed SpriteAtlas UVs or runtime overrides.",
                "Preview metadata gives source texture path and bottom-left pixel Rect; no thumbnail is created.",
                "A paged query must reuse the fingerprint. Incomplete scans do not prove absence.",
                "Candidate references do not choose visual intent, imageType, licensing or suitable border values."
            };
        }

        public static string QueryJson(string sourcePath, string nameContains = "", int offset = 0,
            int limit = 50, string catalogFingerprint = null) =>
            JsonConvert.SerializeObject(Query(sourcePath, nameContains, offset, limit, catalogFingerprint));

        public static Report Query(string sourcePath, string nameContains = "", int offset = 0,
            int limit = 50, string catalogFingerprint = null)
        {
            var report = new Report { sourcePath = sourcePath, nameContains = nameContains, offset = offset, limit = limit };
            if (!ValidPath(sourcePath) || nameContains == null || nameContains.Length > 64 || nameContains.Any(char.IsControl) ||
                offset < 0 || offset > MaxSprites || limit < 1 || limit > 100 ||
                (catalogFingerprint != null && !Regex.IsMatch(catalogFingerprint, "\\A[0-9a-fA-F]{64}\\z")) ||
                (offset > 0 && catalogFingerprint == null))
                return Fail(report, "INVALID_ARGUMENTS", "Use an Assets file/folder, a short name filter, offset 0..10000, limit 1..100; subsequent pages require catalogFingerprint.");
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode)
                return Fail(report, "EDITOR_NOT_READY", "Sprite discovery requires idle Edit Mode.");
            bool folder = AssetDatabase.IsValidFolder(sourcePath);
            string[] paths;
            if (folder)
                paths = AssetDatabase.FindAssets("t:Sprite", new[] { sourcePath }).Select(AssetDatabase.GUIDToAssetPath)
                    .Distinct().OrderBy(p => p, StringComparer.Ordinal).ToArray();
            else if (!string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(sourcePath))) paths = new[] { sourcePath };
            else return Fail(report, "SOURCE_NOT_FOUND", "No imported asset/folder at sourcePath.");
            report.sourceCount = paths.Length;
            if (paths.Length > MaxSources)
                return Fail(report, "SCAN_LIMIT", "More than 1000 source files. Narrow sourcePath; no partial candidates are returned.");
            var candidates = new List<JObject>();
            var dependencies = new JArray();
            try
            {
                foreach (string path in paths)
                {
                    var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                    string mode;
                    if (importer != null && importer.textureType == TextureImporterType.Sprite &&
                        (importer.spriteImportMode == SpriteImportMode.Single || importer.spriteImportMode == SpriteImportMode.Multiple))
                        mode = importer.spriteImportMode == SpriteImportMode.Multiple ? "multiple" : "single";
                    else if (path.EndsWith(".asset", StringComparison.Ordinal) && AssetDatabase.LoadMainAssetAtPath(path) is Sprite)
                        mode = "native";
                    else
                    {
                        if (!folder) return Fail(report, "SPRITE_SOURCE_REQUIRED", "Source is not a supported Single/Multiple/native Sprite.");
                        continue;
                    }
                    dependencies.Add(new JObject { ["path"] = path, ["hash"] = AssetDatabase.GetAssetDependencyHash(path).ToString() });
                    Sprite[] sprites = mode == "multiple" ? AssetDatabase.LoadAllAssetsAtPath(path).OfType<Sprite>().ToArray() :
                        new[] { AssetDatabase.LoadAssetAtPath<Sprite>(path) };
                    report.scannedSources++;
                    foreach (Sprite sprite in sprites)
                    {
                        if (sprite == null || !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(sprite, out string guid, out long id) || id == 0)
                            return Fail(report, "SPRITE_IDENTITY_UNAVAILABLE", "A source Sprite could not be read with a persistent GUID/local ID.");
                        if (candidates.Count >= MaxSprites)
                            return Fail(report, "SCAN_LIMIT", "More than 10000 Sprites. Narrow sourcePath; no partial candidates are returned.");
                        JObject item = UiCompositionSpriteResolver.Describe(sprite);
                        item["importMode"] = mode;
                        item["pixelsPerUnit"] = sprite.pixelsPerUnit;
                        Vector4 padding = UnityEngine.Sprites.DataUtility.GetPadding(sprite);
                        item["padding"] = new JArray(padding.x, padding.y, padding.z, padding.w);
                        var reference = new JObject { ["spritePath"] = path, ["spriteGuid"] = guid };
                        if (mode == "multiple")
                        {
                            reference["spriteName"] = sprite.name;
                            reference["spriteLocalFileId"] = id.ToString(CultureInfo.InvariantCulture);
                        }
                        item["specReference"] = reference;
                        var border = sprite.border;
                        item["slicedBorderUsable"] = border != Vector4.zero && border.x >= 0 && border.y >= 0 && border.z >= 0 && border.w >= 0 &&
                            border.x + border.z <= sprite.rect.width && border.y + border.w <= sprite.rect.height;
                        var texture = sprite.texture;
                        item["preview"] = new JObject { ["texturePath"] = texture != null ? AssetDatabase.GetAssetPath(texture) : null,
                            ["textureWidth"] = texture != null ? texture.width : 0, ["textureHeight"] = texture != null ? texture.height : 0,
                            ["rect"] = item["rect"].DeepClone(), ["origin"] = "bottom_left", ["units"] = "source_pixels" };
                        item["importSettings"] = importer == null ? null : new JObject {
                            ["filterMode"] = importer.filterMode.ToString(), ["compression"] = importer.textureCompression.ToString(),
                            ["mipmapEnabled"] = importer.mipmapEnabled, ["pixelsPerUnit"] = importer.spritePixelsPerUnit };
                        item["pixelsPerUnit"] = sprite.pixelsPerUnit;
                        candidates.Add(item);
                    }
                }
                candidates = candidates.OrderBy(s => (string)s["path"], StringComparer.Ordinal)
                    .ThenBy(s => (string)s["name"], StringComparer.Ordinal).ThenBy(s => (string)s["localFileId"], StringComparer.Ordinal).ToList();
                var snapshot = new JObject { ["sourcePath"] = sourcePath, ["nameContains"] = nameContains,
                    ["dependencies"] = dependencies, ["sprites"] = new JArray(candidates) };
                using (var sha = SHA256.Create())
                    report.catalogFingerprint = string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(snapshot.ToString(Formatting.None))).Select(b => b.ToString("x2")));
                if (catalogFingerprint != null && !string.Equals(catalogFingerprint, report.catalogFingerprint, StringComparison.OrdinalIgnoreCase))
                    return Fail(report, "CATALOG_CHANGED", "Source assets/filter changed between pages; restart at offset 0 and reselect references.");
                var matches = candidates.Where(s => ((string)s["name"]).IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                report.totalMatches = matches.Count;
                if (offset > matches.Count) return Fail(report, "INVALID_OFFSET", "offset exceeds totalMatches.");
                report.sprites = matches.Skip(offset).Take(limit).ToList();
                report.hasMore = offset + report.sprites.Count < matches.Count;
                report.nextOffset = report.hasMore ? (int?)(offset + report.sprites.Count) : null;
                report.complete = true;
                report.status = "complete";
            }
            catch (Exception e) { return Fail(report, "CATALOG_READ_FAILED", e.Message); }
            return report;
        }

        private static bool ValidPath(string path) => !string.IsNullOrEmpty(path) && path.Length <= 240 &&
            (path == "Assets" || path.StartsWith("Assets/", StringComparison.Ordinal)) &&
            !path.Any(c => char.IsControl(c) || "\\:*?\"<>|".IndexOf(c) >= 0) &&
            path.Split('/').All(p => p.Length != 0 && p != "." && p != ".." && !p.EndsWith(" ") && !p.EndsWith("."));

        private static Report Fail(Report report, string code, string message)
        {
            report.complete = false;
            report.status = "blocked";
            report.sprites.Clear();
            report.nextOffset = null;
            report.hasMore = false;
            report.errors.Add(new JObject { ["code"] = code, ["message"] = message });
            return report;
        }
    }
}
