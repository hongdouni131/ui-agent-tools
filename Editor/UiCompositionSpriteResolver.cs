using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace UIAgent.Tools.Editor
{
    // Shared, read-only asset resolution for v2 preflight and saved-Prefab audits.
    internal static class UiCompositionSpriteResolver
    {
        internal sealed class Issue { public string code, property, message; }

        internal static Sprite Resolve(JObject style, List<Issue> issues)
        {
            string path = (string)style["spritePath"], expectedGuid = (string)style["spriteGuid"];
            string guid = AssetDatabase.AssetPathToGUID(path);
            if (string.IsNullOrEmpty(guid))
            {
                Add(issues, "SPRITE_NOT_FOUND", "style.spritePath", "Declared path has no imported Unity asset.");
                return null;
            }
            if (guid != expectedGuid)
                Add(issues, "SPRITE_GUID_MISMATCH", "style.spriteGuid", "Path GUID differs from the Spec; select the asset again and prepare a new plan.");
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            bool multiple = importer != null && importer.textureType == TextureImporterType.Sprite && importer.spriteImportMode == SpriteImportMode.Multiple;
            bool selector = style["spriteLocalFileId"] != null || style["spriteName"] != null;
            Sprite sprite;
            if (multiple)
            {
                if (!selector)
                {
                    Add(issues, "SPRITE_SUBASSET_REQUIRED", "style.spriteLocalFileId", "Multiple requires spriteName and a string spriteLocalFileId; no first-slice or name-only fallback.");
                    return null;
                }
                if (!long.TryParse((string)style["spriteLocalFileId"], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long wanted))
                {
                    Add(issues, "SPRITE_SUBASSET_NOT_FOUND", "style.spriteLocalFileId", "Invalid local file ID.");
                    return null;
                }
                Sprite[] matches = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Sprite>().Where(s =>
                    AssetDatabase.TryGetGUIDAndLocalFileIdentifier(s, out string g, out long id) && g == guid && id == wanted).ToArray();
                if (matches.Length != 1)
                {
                    Add(issues, matches.Length == 0 ? "SPRITE_SUBASSET_NOT_FOUND" : "SPRITE_SUBASSET_AMBIGUOUS",
                        "style.spriteLocalFileId", "Cannot uniquely resolve the sub-Sprite by local file ID; read its identity again.");
                    return null;
                }
                sprite = matches[0];
                if (sprite.name != (string)style["spriteName"])
                    Add(issues, "SPRITE_NAME_MISMATCH", "style.spriteName", "Selected local file ID has a different name; no fallback by name.");
            }
            else
            {
                if (selector) Add(issues, "SPRITE_SUBASSET_UNEXPECTED", "style.spriteLocalFileId", "Sub-Sprite selectors are only supported for TextureImporter Multiple.");
                sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
                if (sprite == null)
                    Add(issues, "SPRITE_IMPORT_SETTINGS", "importer", "The path must load an imported Sprite.");
                if (importer != null ? importer.textureType != TextureImporterType.Sprite || importer.spriteImportMode != SpriteImportMode.Single :
                    !(path.EndsWith(".asset", StringComparison.Ordinal) && AssetDatabase.LoadMainAssetAtPath(path) is Sprite))
                    Add(issues, "SPRITE_IMPORT_SETTINGS", "importer", "Only Sprite/Single, explicit Sprite/Multiple, or a native main Sprite .asset is supported.");
            }
            if (sprite == null || (string)style["imageType"] != "sliced") return sprite;
            var b = (JArray)style["border"];
            var border = new Vector4((float)b[0], (float)b[1], (float)b[2], (float)b[3]);
            // Multiple has per-slice Border; the texture-wide importer border belongs to Single.
            if (sprite.border != border || (!multiple && importer != null && importer.spriteBorder != border))
                Add(issues, "SPRITE_BORDER_MISMATCH", "Sprite.border", "Spec and selected Sprite border differ (Single also checks importer.spriteBorder).");
            if (border.x + border.z > sprite.rect.width || border.y + border.w > sprite.rect.height ||
                sprite.border.x + sprite.border.z > sprite.rect.width || sprite.border.y + sprite.border.w > sprite.rect.height)
                Add(issues, "SPRITE_BORDER_EXCEEDS_RECT", "Sprite.border", "Border sums exceed the selected Sprite rectangle.");
            return sprite;
        }

        internal static JObject Describe(Sprite sprite)
        {
            if (sprite == null) return null;
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(sprite, out string guid, out long localId);
            return new JObject { ["path"] = AssetDatabase.GetAssetPath(sprite), ["guid"] = guid,
                ["name"] = sprite.name, ["localFileId"] = localId.ToString(CultureInfo.InvariantCulture),
                ["rect"] = new JArray(sprite.rect.x, sprite.rect.y, sprite.rect.width, sprite.rect.height),
                ["border"] = new JArray(sprite.border.x, sprite.border.y, sprite.border.z, sprite.border.w) };
        }

        private static void Add(List<Issue> issues, string code, string property, string message) =>
            issues.Add(new Issue { code = code, property = property, message = message });
    }
}
