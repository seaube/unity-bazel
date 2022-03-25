using UnityEngine;
using UnityEditor;
using UnityEngine.UIElements;
using UnityEditor.UIElements;
using Unity.Collections;
using System.IO;
using System.Collections.Generic;

#nullable enable

namespace UnityBazel {

	public enum UnityBazelPackageCopyMode {
		Default,
		EditorOnly,
		StandaloneOnly,
	}

	[System.Serializable]
	public struct UnityBazelPackageCopyOptions {
		public string? bazelPackage;
		public string? outputPath;
		public UnityBazelPackageCopyMode mode;
	}

	[System.Serializable]
	public class UnityBazelSettings : ScriptableObject {
		public const string assetPath = "Assets/Editor/UnityBazelSettings.asset";
		public const string path = "Project/BazelProjectSettings";
		public const SettingsScope scope = SettingsScope.Project;

		public delegate void RefreshEvent();
		public static event RefreshEvent? refresh;
		internal static bool onValidateCalled = false;

		public static UnityBazelSettings? GetSettings() {
			var settings = AssetDatabase.LoadAssetAtPath<UnityBazelSettings>(
				assetPath
			);
			return settings;
		}

		internal static UnityBazelSettings GetOrCreateSettings() {
			var settings = GetSettings();
			if(settings == null) {
				settings = ScriptableObject.CreateInstance<UnityBazelSettings>();
				AssetDatabase.CreateAsset(settings, assetPath);
				AssetDatabase.SaveAssetIfDirty(settings);
			}

			return settings;
		}

		internal static void InvokeRefresh() {
			refresh?.Invoke();
		}

		public string defaultOutputPath = "";
		public List<UnityBazelPackageCopyOptions>? copiedPackages;
		[ReadOnly]
		public List<string>? lastCopiedFiles;

		void OnValidate() {
			onValidateCalled = true;
		}
	}

	static class UnityBazelSettingsUIElementsRegister {
		[SettingsProvider]
		public static SettingsProvider CreateUnityBazelSettingsProvider() {
			return new SettingsProvider(
				UnityBazelSettings.path,
				UnityBazelSettings.scope
			) {
				label = "Bazel",
				deactivateHandler = () => {
					if(UnityBazelSettings.onValidateCalled) {
						UnityBazelSettings.InvokeRefresh();
					}
				},
				activateHandler = (searchContext, rootElement) => {
					UnityBazelSettings.onValidateCalled = false;
					var settingsObj = UnityBazelSettings.GetOrCreateSettings();
					var settings = new SerializedObject(settingsObj);

					var title = new Label("Bazel");
					title.style.fontSize = 20;
					title.style.paddingBottom = 8;
					title.style.paddingTop = 8;

					rootElement.Add(title);
					rootElement.style.paddingLeft = 20;

					var scrollView = new ScrollView();
					scrollView.style.paddingRight = 20;

					var properties = new VisualElement() {};
					scrollView.Add(properties);

					var defaultOutputPathProp = new PropertyField(
						settings.FindProperty("defaultOutputPath")
					);
					var copiedPackagesProp = new PropertyField(
						settings.FindProperty("copiedPackages")
					);

					var lastCopiedFilesProp = settings.FindProperty("lastCopiedFiles");
					var lastCopiedFilesPropField = new PropertyField(lastCopiedFilesProp);

					properties.Add(defaultOutputPathProp);
					properties.Add(copiedPackagesProp);

					var refreshButton = new Button() { text = "Refresh" };
					refreshButton.style.maxWidth = 200;
					refreshButton.style.paddingTop = 8;
					refreshButton.style.paddingBottom = 8;
					refreshButton.style.marginTop = 8;
					refreshButton.style.marginBottom = 8;
					refreshButton.clicked += () => {
						UnityBazelSettings.InvokeRefresh();
					};
					scrollView.Add(refreshButton);

					rootElement.Add(scrollView);
					rootElement.Bind(settings);
				},
				keywords = new HashSet<string>(new[] {"Bazel", "Query", "Copy"}),
			};
		}
	}

}
