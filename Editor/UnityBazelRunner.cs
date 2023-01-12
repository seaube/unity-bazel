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
	public static class UnityBazelRunner {
		const string watchProgressIdSessionKey = "BazelWatchProgressId";
		private static bool copyingPackagesInProgress = false;

		[InitializeOnLoadMethod]
		static void Init() {
			UnityBazelSettings.refresh += () => {
				EditorApplication.delayCall += async () => await DelayedInit();
			};

			EditorApplication.playModeStateChanged += state => {
				if(state == PlayModeStateChange.ExitingEditMode) {
					if(copyingPackagesInProgress) {
						UnityEngine.Debug.LogError(
							"Bazel copy packages in progress. Wait until done or cancel " +
							"before entering play mode."
						);
						EditorApplication.isPlaying = false;
					}
				}
			};

			foreach(var entry in _watchers) {
				entry.Value.Dispose();
			}
			_watchers.Clear();

			EditorApplication.delayCall += async () => {
				await DelayedInit();
			};
		}

		private static async Task DelayedInit() {
			var settings = UnityBazelSettings.GetSettings();
			if(settings == null || settings.copiedPackages == null) return;

			if(settings.buildOnEditorStart) {
				await CopyPackages();
			}

			if(settings.watchOnEditorStart) {
				await WatchForChanges();
			} else {
				ClearExistingWatch();
			}
		}

		private static void ClearExistingWatch() {
			var watchProgressId = SessionState.GetInt(watchProgressIdSessionKey, -1);
			if(watchProgressId != -1) {
				Progress.Cancel(watchProgressId);
				SessionState.EraseInt(watchProgressIdSessionKey);
			}
		}

		[Shortcut("Bazel/Copy Packages")]
		private static async void CopyPackagesShortcut() {
			await CopyPackages();
		}

		private static Dictionary<string, FileSystemWatcher> _watchers = new();
		[Shortcut("Bazel/Start Output File Watcher")]
		private static async void WatchForChangesShortcut() {
			await WatchForChanges();
		}

		private static async Task WatchForChanges() {
			var settings = UnityBazelSettings.GetSettings();
			if(settings == null || settings.copiedPackages == null) return;

			ClearExistingWatch();

			List<string> pkgLabels = new();
			foreach(var entry in settings.copiedPackages) {
				if(entry.bazelPackage == null) continue;
				pkgLabels.Add(entry.bazelPackage);
			}

			if(pkgLabels.Count == 0) {
				return;
			}

			var outputPaths = await Bazel.QueryOutputs(pkgLabels);
			var watchProgressId = Progress.Start(
				"Watching for bazel output changes",
				null,
				Progress.Options.Indefinite
			);
			SessionState.SetInt(watchProgressIdSessionKey, watchProgressId);

			foreach(var outputPath in outputPaths) {
				var directory = Path.GetDirectoryName(outputPath);
				if(_watchers.ContainsKey(directory)) {
					continue;
				}

				var watcher = new FileSystemWatcher(directory);
				var childWatchProgress = Progress.Start(
					directory,
					null,
					Progress.Options.Indefinite,
					watchProgressId
				);

				watcher.Changed += (_, ev) => {
					var relPath = Path.GetRelativePath(directory, ev.FullPath);
					Progress.SetDescription(childWatchProgress, $"{relPath} changed");
				};

				watcher.EnableRaisingEvents = true;

				_watchers.Add(directory, watcher);
			}

			Progress.RegisterCancelCallback(watchProgressId, () => {
				foreach(var entry in _watchers) {
					entry.Value.Dispose();
				}
				_watchers.Clear();
				Progress.Remove(watchProgressId);
				SessionState.EraseInt(watchProgressIdSessionKey);
				return true;
			});
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

		private static async Task CopyPackages
			( string rootDirectory
			)
		{
			if(copyingPackagesInProgress) {
				UnityEngine.Debug.LogWarning(
					"Unity bazel runner in progress. Try again once finished"
				);
				return;
			}

			copyingPackagesInProgress = true;
			try {
				await _CopyPackages(rootDirectory);
			} finally {
				copyingPackagesInProgress = false;
			}
		}

		private static async Task _CopyPackages
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
					Bazel.QueryOutputs(new List<string>{copyPkg.bazelPackage!}),
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

		static void TryImportAssets
			( List<string> assets
			)
		{
			EditorApplication.delayCall += () => {
				if(!Progress.running) {
					try {
						AssetDatabase.StartAssetEditing();
						foreach(var asset in assets) {
							AssetDatabase.ImportAsset(asset);
						}
					} finally {
						AssetDatabase.StopAssetEditing();
					}
				} else {
					TryImportAssets(assets);
				}
			};
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

			TryImportAssets(results);

			return results;
		}
	}
}
