using UnityEngine;
using UnityEditor;
using UnityEditor.ShortcutManagement;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.IO;
using System.Linq;

#nullable enable

namespace UnityBazel {
	[InitializeOnLoadAttribute]
	public static class UnityBazelRunner {
		const string initialCopyStatekey = "BazelInitialCopyPackagesRan";

		static UnityBazelRunner() {
			if(!SessionState.GetBool(initialCopyStatekey, false)) {
				EditorApplication.delayCall += async () => await CopyPackages();
			}

			UnityBazelSettings.refresh += () => {
				EditorApplication.delayCall += async () => await CopyPackages();
			};
		}

		[Shortcut("Bazel/Copy Packages")]
		private static async void CopyPackagesShortcut() {
			await CopyPackages();
		}

		public static async Task CopyPackages() {
			var settings = UnityBazelSettings.GetSettings();
			if(settings == null || settings.copiedPackages == null) return;
			if(!settings.copiedPackages.Any()) {
				settings.lastCopiedFiles = null;
				return;
			}

			settings.lastCopiedFiles = new List<string>();

			var asyncInfos = Bazel.GetInfo(new[]{
				"execution_root",
				"bazel-bin",
				"workspace"
			});

			string packagesStr = "";

			foreach(var copyPkg in settings.copiedPackages) {
				if(String.IsNullOrEmpty(copyPkg.bazelPackage)) continue;
				packagesStr += copyPkg.bazelPackage! + " ";
			}

			var buildResult = Bazel.Build(packagesStr);

			var infos = await asyncInfos;

			var tasks = new List<Task<List<string>>>();

			foreach(var copyPkg in settings.copiedPackages) {
				if(String.IsNullOrEmpty(copyPkg.bazelPackage)) continue;

				if(Application.isEditor) {
					if(copyPkg.mode == UnityBazelPackageCopyMode.StandaloneOnly) {
						continue;
					}
				} else {
					if(copyPkg.mode == UnityBazelPackageCopyMode.EditorOnly) {
						continue;
					}
				}

				tasks.Add(Task.Run(() => PackageQueryComplete(
					Bazel.QueryOutputs(copyPkg.bazelPackage!),
					outputPathPattern: (String.IsNullOrEmpty(copyPkg.outputPath)
						? settings.defaultOutputPath
						: copyPkg.outputPath!),
					executionRoot: infos["execution_root"],
					bazelBin: infos["bazel-bin"],
					workspace: infos["workspace"]
				)));
			}

			foreach(var task in tasks) {
				var outputPaths = await task;
				foreach(var outputPath in outputPaths) {
					settings.lastCopiedFiles.Add(outputPath);
				}
			}

			await buildResult;
			AssetDatabase.Refresh();
			SessionState.SetBool(initialCopyStatekey, true);
		}

		private static async Task<List<string>> PackageQueryComplete
			( Task<List<string>>  outputPathsTask
			, string              outputPathPattern
			, string              executionRoot
			, string              bazelBin
			, string              workspace
			)
		{
			executionRoot = executionRoot.Replace('\\', '/');
			bazelBin = bazelBin.Replace('\\', '/');
			workspace = workspace.Replace('\\', '/');

			var outputPaths = await outputPathsTask;
			var results = new List<string>();

			foreach(var itemOutputPath in outputPaths) {
				if(String.IsNullOrEmpty(itemOutputPath)) continue;

				var fullOutputPath =
					Path.Combine(executionRoot, itemOutputPath).Replace('\\', '/');

				string filePath = "";
				if(fullOutputPath.StartsWith(bazelBin)) {
					filePath = fullOutputPath.Substring(bazelBin.Length + 1);
				} else {
					filePath = itemOutputPath;
					fullOutputPath =
						Path.Combine(executionRoot, itemOutputPath).Replace('\\', '/');
				}

				string userOutputPath = outputPathPattern;
				userOutputPath = userOutputPath.Replace("{FILEPATH}", filePath);
				userOutputPath = userOutputPath.Replace(
					"{FILENAME}",
					Path.GetFileName(fullOutputPath)
				);
				userOutputPath = userOutputPath.Replace(
					"{EXTNAME}",
					Path.GetExtension(fullOutputPath)
				);

				if(File.Exists(userOutputPath)) {
					File.SetAttributes(userOutputPath, FileAttributes.Normal);
				} else {
					Directory.CreateDirectory(Path.GetDirectoryName(userOutputPath));
				}

				File.Copy(fullOutputPath, userOutputPath, true);
				File.SetAttributes(userOutputPath, FileAttributes.Normal);

				results.Add(userOutputPath);
			}

			return results;
		}
	}
}
