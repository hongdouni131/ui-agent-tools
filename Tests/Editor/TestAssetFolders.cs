using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace UIAgent.Tools.Editor.Tests
{
    // Each fixture creates its own prerequisites; no dependency on test ordering
    // or on the development Sandbox's pre-existing directory tree.
    internal static class TestAssetFolders
    {
        private static readonly HashSet<string> CreatedFolders = new HashSet<string>(StringComparer.Ordinal);

        internal static void Ensure(string path)
        {
            if (path != "Assets" && !path.StartsWith("Assets/", StringComparison.Ordinal))
                throw new ArgumentException("Expected an Assets-relative test folder.", nameof(path));
            string current = "Assets";
            foreach (string segment in path.Split('/'))
            {
                if (segment == "Assets") continue;
                if (string.IsNullOrEmpty(segment) || segment == "." || segment == ".." || segment.Contains("\\"))
                    throw new ArgumentException("Invalid test folder segment.", nameof(path));
                string next = current + "/" + segment;
                if (!AssetDatabase.IsValidFolder(next))
                {
                    Assert.That(AssetDatabase.CreateFolder(current, segment), Is.Not.Empty, "Could not create " + next);
                    CreatedFolders.Add(next);
                }
                current = next;
            }
        }

        internal static void CleanupCreatedEmptyFolders()
        {
            var remaining = new List<string>();
            foreach (string path in CreatedFolders.OrderByDescending(folder => folder.Count(character => character == '/')))
            {
                if (!AssetDatabase.IsValidFolder(path)) continue;

                string projectRoot = Path.GetDirectoryName(Application.dataPath);
                string absolutePath = Path.GetFullPath(Path.Combine(projectRoot, path));
                if (Directory.Exists(absolutePath) && Directory.GetFileSystemEntries(absolutePath).Length != 0)
                {
                    remaining.Add(path);
                    continue;
                }

                if (!AssetDatabase.DeleteAsset(path) && AssetDatabase.IsValidFolder(path))
                    remaining.Add(path);
            }

            CreatedFolders.Clear();
            foreach (string path in remaining) CreatedFolders.Add(path);
        }
    }
}
