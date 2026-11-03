using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UIAgent.Tools.Editor.Tests
{
    public class UiCompositionSpriteCatalogTests
    {
        private string folder, path;

        [SetUp]
        public void SetUp()
        {
            folder = "Assets/AgentDemos/UIAgent/TestCases/CatalogTest_" + Guid.NewGuid().ToString("N");
            TestAssetFolders.Ensure(folder);
            path = folder + "/Sheet.png";
            var texture = new Texture2D(64, 32, TextureFormat.RGBA32, false);
            try
            {
                texture.SetPixels(Enumerable.Repeat(Color.white, 64 * 32).ToArray()); texture.Apply();
                File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(Application.dataPath), path), texture.EncodeToPNG());
            }
            finally { UnityEngine.Object.DestroyImmediate(texture); }
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Multiple;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
#pragma warning disable 618
            importer.spritesheet = new[] {
                new SpriteMetaData { name = "Alpha", rect = new Rect(0, 0, 32, 32), pivot = new Vector2(.5f, .5f), border = new Vector4(4, 4, 4, 4) },
                new SpriteMetaData { name = "Beta", rect = new Rect(32, 0, 32, 32), pivot = new Vector2(.5f, .5f) }
            };
#pragma warning restore 618
            importer.SaveAndReimport();
        }

        [TearDown]
        public void TearDown()
        {
            if (AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
            TestAssetFolders.CleanupCreatedEmptyFolders();
        }

        [Test]
        public void MultipleCandidatesRoundTripExactStringIdentityAndReference()
        {
            var report = UiCompositionSpriteCatalog.Query(path);
            Assert.That(report.complete, Is.True, JToken.FromObject(report).ToString());
            Assert.That(report.sprites.Count, Is.EqualTo(2));
            foreach (var item in report.sprites)
            {
                Assert.That(item["localFileId"].Type, Is.EqualTo(JTokenType.String));
                var reference = (JObject)item["specReference"];
                Assert.That(reference["spriteLocalFileId"].Type, Is.EqualTo(JTokenType.String));
                var issues = new System.Collections.Generic.List<UiCompositionSpriteResolver.Issue>();
                var sprite = UiCompositionSpriteResolver.Resolve(reference, issues);
                Assert.That(sprite, Is.Not.Null); Assert.That(issues, Is.Empty);
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(sprite, out string guid, out long localId);
                Assert.That((string)item["guid"], Is.EqualTo(guid));
                Assert.That((string)item["localFileId"], Is.EqualTo(localId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                Assert.That((string)item["preview"]["origin"], Is.EqualTo("bottom_left"));
                Assert.That((string)item["preview"]["texturePath"], Is.EqualTo(path));
            }
            Assert.That((bool)report.sprites[0]["slicedBorderUsable"], Is.True);
            Assert.That((bool)report.sprites[1]["slicedBorderUsable"], Is.False);
        }

        [Test]
        public void PaginationAndCaseInsensitiveFilterAreDeterministic()
        {
            var first = UiCompositionSpriteCatalog.Query(folder, limit: 1);
            Assert.That(first.complete && first.hasMore, Is.True);
            Assert.That(first.nextOffset, Is.EqualTo(1));
            var second = UiCompositionSpriteCatalog.Query(folder, offset: 1, limit: 1, catalogFingerprint: first.catalogFingerprint);
            Assert.That(second.complete, Is.True); Assert.That(second.hasMore, Is.False);
            Assert.That((string)first.sprites[0]["name"], Is.EqualTo("Alpha"));
            Assert.That((string)second.sprites[0]["name"], Is.EqualTo("Beta"));
            Assert.That(second.catalogFingerprint, Is.EqualTo(first.catalogFingerprint));
            Assert.That(UiCompositionSpriteCatalog.Query(folder, "ALP").totalMatches, Is.EqualTo(1));
            var empty = UiCompositionSpriteCatalog.Query(folder, "unknown");
            Assert.That(empty.complete, Is.True); Assert.That(empty.totalMatches, Is.Zero);
        }

        [Test]
        public void ChangedSourceOrFilterRejectsContinuation()
        {
            var first = UiCompositionSpriteCatalog.Query(path, limit: 1);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.filterMode = importer.filterMode == FilterMode.Point ? FilterMode.Bilinear : FilterMode.Point;
            importer.SaveAndReimport();
            var changed = UiCompositionSpriteCatalog.Query(path, offset: 1, catalogFingerprint: first.catalogFingerprint);
            Assert.That(changed.complete, Is.False); Assert.That(changed.sprites, Is.Empty);
            Assert.That((string)changed.errors[0]["code"], Is.EqualTo("CATALOG_CHANGED"));
            var changedFilter = UiCompositionSpriteCatalog.Query(path, "Alpha", catalogFingerprint: first.catalogFingerprint);
            Assert.That(changedFilter.complete, Is.False);
        }

        [Test]
        public void SingleReferenceKeepsLegacyShapeAndQueryDoesNotDirtySceneOrImporter()
        {
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.spriteBorder = new Vector4(4, 4, 4, 4);
            importer.SaveAndReimport();
            Scene scene = SceneManager.GetActiveScene();
            bool dirty = scene.isDirty; int roots = scene.rootCount; var selection = Selection.instanceIDs;
            var hash = AssetDatabase.GetAssetDependencyHash(path);
            var report = UiCompositionSpriteCatalog.Query(path);
            Assert.That(report.complete, Is.True);
            var reference = (JObject)report.sprites.Single()["specReference"];
            Assert.That(reference["spriteName"], Is.Null); Assert.That(reference["spriteLocalFileId"], Is.Null);
            Assert.That((string)report.sprites[0]["importMode"], Is.EqualTo("single"));
            Assert.That(SceneManager.GetActiveScene(), Is.EqualTo(scene));
            Assert.That(scene.isDirty, Is.EqualTo(dirty)); Assert.That(scene.rootCount, Is.EqualTo(roots));
            Assert.That(Selection.instanceIDs, Is.EqualTo(selection));
            Assert.That(AssetDatabase.GetAssetDependencyHash(path), Is.EqualTo(hash));
            Assert.That(EditorUtility.IsDirty(importer), Is.False);
        }

        [TestCase("Assets/../a")]
        [TestCase("Assets//a")]
        [TestCase("Assets/a/")]
        [TestCase("Packages/a")]
        [TestCase("C:/a")]
        [TestCase("Assets/a\\b")]
        [TestCase("Assets/a.")]
        public void UnsafePathsAreBlocked(string input)
        {
            var report = UiCompositionSpriteCatalog.Query(input);
            Assert.That(report.complete, Is.False);
            Assert.That((string)report.errors[0]["code"], Is.EqualTo("INVALID_ARGUMENTS"));
        }

        [Test]
        public void NativeSpriteMainAssetIsDiscoverableWithoutMultipleSelector()
        {
            string nativePath = folder + "/Native.asset";
            var texture = new Texture2D(16, 16);
            AssetDatabase.CreateAsset(texture, nativePath);
            var sprite = Sprite.Create(texture, new Rect(0, 0, 16, 16), new Vector2(.5f, .5f));
            sprite.name = "Native";
            AssetDatabase.AddObjectToAsset(sprite, nativePath);
            AssetDatabase.SetMainObject(sprite, nativePath);
            AssetDatabase.SaveAssetIfDirty(texture); AssetDatabase.SaveAssetIfDirty(sprite);
            AssetDatabase.ImportAsset(nativePath, ImportAssetOptions.ForceSynchronousImport);
            var report = UiCompositionSpriteCatalog.Query(nativePath);
            Assert.That(report.complete, Is.True, JToken.FromObject(report).ToString());
            Assert.That((string)report.sprites.Single()["importMode"], Is.EqualTo("native"));
            Assert.That(report.sprites.Single()["specReference"]["spriteLocalFileId"], Is.Null);
        }

        [Test]
        public void ImportAuditDistinguishesSimpleAndSlicedReadinessWithoutMutatingProjectState()
        {
            Scene scene = SceneManager.GetActiveScene();
            bool dirty = scene.isDirty; int roots = scene.rootCount; var selection = Selection.instanceIDs;
            var hash = AssetDatabase.GetAssetDependencyHash(path);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            bool importerDirty = EditorUtility.IsDirty(importer);

            var report = UiCompositionSpriteImportAuditor.Audit(path);

            Assert.That(report.complete, Is.True, JToken.FromObject(report).ToString());
            Assert.That(report.status, Is.EqualTo("complete"));
            Assert.That(report.catalogFingerprint, Does.Match("^[0-9a-f]{64}$"));
            Assert.That(report.readySourceCount, Is.EqualTo(1));
            Assert.That(report.requiresManualActionCount, Is.Zero);
            Assert.That(report.simpleUsableCount, Is.EqualTo(2));
            Assert.That(report.slicedUsableCount, Is.EqualTo(1));
            Assert.That(report.slicedBorderReviewCount, Is.EqualTo(1));
            var alpha = report.sources.Single()["sprites"].Single(x => (string)x["name"] == "Alpha");
            var beta = report.sources.Single()["sprites"].Single(x => (string)x["name"] == "Beta");
            Assert.That((string)alpha["slicedStatus"], Is.EqualTo("ready"));
            Assert.That((string)beta["slicedStatus"], Is.EqualTo("manual_border_required"));
            Assert.That((string)alpha["specReference"]["spriteLocalFileId"], Is.Not.Empty);
            Assert.That((string)alpha["specReference"]["spriteName"], Is.EqualTo("Alpha"));
            Assert.That((string)alpha["preview"]["texturePath"], Is.EqualTo(path));
            Assert.That((string)report.sources[0]["importSettings"]["textureType"], Is.EqualTo("Sprite"));
            Assert.That((string)report.sources[0]["importSettings"]["spriteImportMode"], Is.EqualTo("Multiple"));
            Assert.That(SceneManager.GetActiveScene(), Is.EqualTo(scene));
            Assert.That(scene.isDirty, Is.EqualTo(dirty)); Assert.That(scene.rootCount, Is.EqualTo(roots));
            Assert.That(Selection.instanceIDs, Is.EqualTo(selection));
            Assert.That(AssetDatabase.GetAssetDependencyHash(path), Is.EqualTo(hash));
            Assert.That(EditorUtility.IsDirty(importer), Is.EqualTo(importerDirty));
        }

        [Test]
        public void ImportAuditReportsNonSpriteTextureAsManualActionAndDoesNotAutoConvertIt()
        {
            string defaultPath = folder + "/Background.png";
            var texture = new Texture2D(16, 16, TextureFormat.RGBA32, false);
            try
            {
                texture.SetPixels(Enumerable.Repeat(Color.white, 16 * 16).ToArray()); texture.Apply();
                File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(Application.dataPath), defaultPath), texture.EncodeToPNG());
            }
            finally { UnityEngine.Object.DestroyImmediate(texture); }
            AssetDatabase.ImportAsset(defaultPath, ImportAssetOptions.ForceSynchronousImport);
            var importer = (TextureImporter)AssetImporter.GetAtPath(defaultPath);
            importer.textureType = TextureImporterType.Default;
            importer.SaveAndReimport();
            var dependencyHash = AssetDatabase.GetAssetDependencyHash(defaultPath);

            var report = UiCompositionSpriteImportAuditor.Audit(folder);

            Assert.That(report.complete, Is.True, JToken.FromObject(report).ToString());
            var background = report.sources.Single(x => (string)x["path"] == defaultPath);
            Assert.That((string)background["readiness"], Is.EqualTo("not_configured_as_sprite"));
            Assert.That((string)background["manualReasons"][0], Does.Contain("Texture Type is not Sprite"));
            Assert.That(report.requiresManualActionCount, Is.EqualTo(1));
            Assert.That(report.excludedNonSpriteSourceCount, Is.EqualTo(1));
            Assert.That(report.catalogFingerprint, Does.Match("^[0-9a-f]{64}$"));
            Assert.That(AssetDatabase.GetAssetDependencyHash(defaultPath), Is.EqualTo(dependencyHash));
            Assert.That(((TextureImporter)AssetImporter.GetAtPath(defaultPath)).textureType, Is.EqualTo(TextureImporterType.Default));
        }

        [Test]
        public void ImportAuditExcludesUnrelatedNativeTextureWithoutRequestingManualAction()
        {
            string nativePath = folder + "/UnrelatedTexture.asset";
            var texture = new Texture2D(8, 8);
            AssetDatabase.CreateAsset(texture, nativePath);
            AssetDatabase.SaveAssetIfDirty(texture);
            var hash = AssetDatabase.GetAssetDependencyHash(nativePath);

            var report = UiCompositionSpriteImportAuditor.Audit(nativePath);

            Assert.That(report.complete, Is.True, JToken.FromObject(report).ToString());
            Assert.That((string)report.sources.Single()["readiness"], Is.EqualTo("not_applicable"));
            Assert.That(report.scannedSources, Is.EqualTo(1));
            Assert.That(report.requiresManualActionCount, Is.Zero);
            Assert.That(report.excludedNonSpriteSourceCount, Is.EqualTo(1));
            Assert.That(report.spriteCount, Is.Zero);
            Assert.That(AssetDatabase.GetAssetDependencyHash(nativePath), Is.EqualTo(hash));
            Assert.That(EditorUtility.IsDirty(texture), Is.False);
        }

        [TestCase("Assets/../a")]
        [TestCase("Assets//a")]
        [TestCase("Packages/a")]
        [TestCase("C:/a")]
        public void ImportAuditRejectsNonNormalizedOrOutOfScopePaths(string input)
        {
            var report = UiCompositionSpriteImportAuditor.Audit(input);
            Assert.That(report.complete, Is.False);
            Assert.That((string)report.errors[0]["code"], Is.EqualTo("INVALID_ARGUMENTS"));
        }

        [Test]
        public void MissingSourceInvalidPagingAndUnsupportedSourceHaveDistinctErrors()
        {
            Assert.That((string)UiCompositionSpriteCatalog.Query(folder + "/Missing.png").errors[0]["code"], Is.EqualTo("SOURCE_NOT_FOUND"));
            Assert.That((string)UiCompositionSpriteCatalog.Query(path, offset: 1).errors[0]["code"], Is.EqualTo("INVALID_ARGUMENTS"));
            Assert.That((string)UiCompositionSpriteCatalog.Query(path, limit: 101).errors[0]["code"], Is.EqualTo("INVALID_ARGUMENTS"));
            var first = UiCompositionSpriteCatalog.Query(path);
            Assert.That((string)UiCompositionSpriteCatalog.Query(path, offset: 3, catalogFingerprint: first.catalogFingerprint).errors[0]["code"], Is.EqualTo("INVALID_OFFSET"));
            string notSprite = folder + "/Data.asset";
            var data = new Texture2D(8, 8);
            AssetDatabase.CreateAsset(data, notSprite); AssetDatabase.SaveAssetIfDirty(data);
            Assert.That((string)UiCompositionSpriteCatalog.Query(notSprite).errors[0]["code"], Is.EqualTo("SPRITE_SOURCE_REQUIRED"));
        }
    }

}
