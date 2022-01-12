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
		static UnityBazelRunner() {
			UnityBazelSettings.refresh += () => {
				EditorApplication.delayCall += async () => await CopyPackages();
			};
		}

		[Shortcut("Bazel/Copy Packages")]
		private static async void CopyPackagesShortcut() {
			await CopyPackages();
		}

		public static async Task CopyPackages() {
			await CopyPackages("");
		}

		private static string GetPackagePath
			( string rootDirectory
			, string packageName
			)
		{
			var projectPackagePath = Path.GetFullPath($"Packages/{packageName}");
			if(String.IsNullOrEmpty(rootDirectory)) {
				return projectPackagePath;
			}

			// https://docs.unity3d.com/ScriptReference/Application-dataPath.html
			// In Unity Editor the Application.dataPath is the editor assets folder
			var projectDir = Application.dataPath.Substring(
				0,
				Application.dataPath.Length - ("/Assets".Length)
			);

			return Path.Combine(
				rootDirectory,
				Path.GetRelativePath(projectDir, projectPackagePath)
			);
		}

		public static async Task CopyPackages
			( string rootDirectory
			)
		{
			var settings = UnityBazelSettings.GetSettings();
			if(settings == null || settings.copiedPackages == null) return;

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
				if(String.IsNullOrEmpty(copyPkg.bazelPackage)) {
					UnityEngine.Debug.LogWarning("Unset bazel package in bazel settings");
					continue;
				}

				if(Application.isEditor) {
					if(copyPkg.mode == UnityBazelPackageCopyMode.StandaloneOnly) {
						continue;
					}
				} else {
					if(copyPkg.mode == UnityBazelPackageCopyMode.EditorOnly) {
						continue;
					}
				}

				var outputPathPattern = (String.IsNullOrEmpty(copyPkg.outputPath)
					? settings.defaultOutputPath
					: copyPkg.outputPath!);

				tasks.Add(PackageQueryComplete(
					Bazel.QueryOutputs(copyPkg.bazelPackage!),
					outputPathPattern: outputPathPattern,
					executionRoot: infos["execution_root"],
					bazelBin: infos["bazel-bin"],
					workspace: infos["workspace"],
					rootDirectory: rootDirectory
				));
			}

			foreach(var task in tasks) {
				var outputPaths = await task;
				foreach(var outputPath in outputPaths) {
					settings.lastCopiedFiles.Add(outputPath);
				}
			}

			await buildResult;
		}

		private static async Task<List<string>> PackageQueryComplete
			( Task<List<string>>  outputPathsTask
			, string              outputPathPattern
			, string              executionRoot
			, string              bazelBin
			, string              workspace
			, string              rootDirectory
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

				if(userOutputPath.StartsWith("Packages/")) {
					var slashIdx = userOutputPath.IndexOf('/', "Packages".Length);
					var pkgNameEndSlashIdx = userOutputPath.IndexOf('/', slashIdx + 1);

					var pkgName = userOutputPath.Substring(
						slashIdx,
						pkgNameEndSlashIdx - slashIdx
					);

					if(String.IsNullOrEmpty(pkgName)) {
						UnityEngine.Debug.LogError(
							$"Couldn't find package name in output path. '{filePath}' will " +
							$"not be coped using '{outputPathPattern}' output pattern."
						);
						continue;
					}

					var packagePath = GetPackagePath(rootDirectory, pkgName);
					userOutputPath = packagePath + userOutputPath.Substring(
						pkgNameEndSlashIdx
					);
				} else if(!String.IsNullOrEmpty(rootDirectory)) {
					userOutputPath = Path.Combine(rootDirectory, userOutputPath);
				}

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
