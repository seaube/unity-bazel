using UnityEditor;
using System.Diagnostics;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Linq;
using System;
using System.Text.RegularExpressions;

namespace UnityBazel {
	public static class Bazel {
		private static Regex progressMessageRegex;

		static Bazel() {
			progressMessageRegex = new Regex(
				@"\s*\[\s*([0-9,]+)\s*\/\s*([0-9,]+)\s*\](.*)",
				RegexOptions.Compiled
			);
		}

		public static Task<Dictionary<string, string>> GetInfo() {
			return GetInfo(Enumerable.Empty<string>());
		}

		public static Task<Dictionary<string, string>> GetInfo
			( IEnumerable<string> infos
			)
		{
			var infosCount = infos.Count();
			var infosStr = String.Join(" ", infos);
			var progressId = Progress.Start($"Bazel Info", infosStr);
			var tcs = new TaskCompletionSource<Dictionary<string, string>>();

			var process = new Process{
				StartInfo = {
					FileName = "bazel",
					Arguments = $"info {infosStr}",
					RedirectStandardOutput = true,
					UseShellExecute = false,
					CreateNoWindow = true,
				},
				EnableRaisingEvents = true,
			};

			process.Exited += (sender, args) => {
				if(process.ExitCode == 0) {
					Progress.Remove(progressId);
				} else {
					Progress.Finish(progressId, Progress.Status.Failed);
					Progress.ShowDetails(false);
				}

				var result = new Dictionary<string, string>();
				if(infosCount == 1) {
					result.Add(infos.First(), process.StandardOutput.ReadLine());
				} else {
					string line = process.StandardOutput.ReadLine();
					while(line != null) {
						var infoComponents = line.Split(new char[]{':'}, 2);
						result.Add(infoComponents[0].Trim(), infoComponents[1].Trim());
						line = process.StandardOutput.ReadLine();
					}
				}

				tcs.SetResult(result);
				process.Dispose();
			};

			process.Start();

			return tcs.Task;
		}

		public static Task<string> GetInfo
			( string info
			)
		{
			var progressId = Progress.Start($"Bazel Info", info);
			var tcs = new TaskCompletionSource<string>();

			var process = new Process{
				StartInfo = {
					FileName = "bazel",
					Arguments = $"info {info}",
					RedirectStandardOutput = true,
					UseShellExecute = false,
					CreateNoWindow = true,
				},
				EnableRaisingEvents = true,
			};

			process.Exited += (sender, args) => {
				if(process.ExitCode == 0) {
					Progress.Remove(progressId);
				} else {
					Progress.Finish(progressId, Progress.Status.Failed);
				}
				tcs.SetResult(process.StandardOutput.ReadLine());
				process.Dispose();
			};

			process.Start();

			return tcs.Task;
		}

		public static Task<List<string>> QueryOutputs
			( string package
			)
		{
			var progressId = Progress.Start(
				name: $"Bazel Query Outputs {package}",
				options: Progress.Options.Indefinite
			);
			var tcs = new TaskCompletionSource<List<string>>();

			var process = new Process{
				StartInfo = {
					FileName = "bazel",
					Arguments =
						$"cquery {package} --output=starlark " +
						"--starlark:expr=\"'\\n'.join(" +
						"[f.path for f in target.files.to_list()]" +
						")\"",
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					UseShellExecute = false,
					CreateNoWindow = true,
				},
				EnableRaisingEvents = true,
			};

			var outputPaths = new List<string>();

			process.ErrorDataReceived += (_, ev) => {
				var line = ev.Data;
				if(!String.IsNullOrEmpty(line)) {
					Progress.SetDescription(progressId, line);
				}
			};

			process.OutputDataReceived += (_, ev) => {
				var line = ev.Data;
				if(!String.IsNullOrEmpty(line)) {
					outputPaths.Add(line);
					Progress.SetDescription(progressId, line);
				}
			};

			process.Exited += (sender, args) => {
				if(process.ExitCode == 0) {
					Progress.Remove(progressId);
				} else {
					Progress.Finish(progressId, Progress.Status.Failed);
				}
				tcs.SetResult(outputPaths);
				process.Dispose();
			};

			process.Start();
			process.BeginOutputReadLine();
			process.BeginErrorReadLine();

			return tcs.Task;
		}

		public static Task<bool> Build
			( string package
			)
		{
			var progressId = Progress.Start($"Bazel Build {package}");
			Progress.SetStepLabel(progressId, "Targets");
			var tcs = new TaskCompletionSource<bool>();

			var process = new Process{
				StartInfo = {
					FileName = "bazel",
					Arguments = $"build {package}",
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					UseShellExecute = false,
					CreateNoWindow = true,
				},
				EnableRaisingEvents = true,
			};

			Progress.RegisterCancelCallback(progressId, () => {
				process.Kill();
				return true;
			});

			process.ErrorDataReceived += (_, ev) => {
				var line = ev.Data;
				if(!String.IsNullOrEmpty(line)) {
					Progress.SetDescription(progressId, line);
					var match = progressMessageRegex.Match(line);
					if(match.Groups != null && match.Groups.Count > 0) {
						var itemCountStr = match.Groups[1].Value;
						var itemTotalStr = match.Groups[2].Value;
						var itemMessage = match.Groups[3].Value;

						int itemCount = 0;
						int itemTotal = 0;
						if(int.TryParse(itemCountStr.Replace(",", ""), out itemCount)) {
							if(int.TryParse(itemTotalStr.Replace(",", ""), out itemTotal)) {
								Progress.Report(
									progressId,
									itemCount,
									itemTotal,
									itemMessage
								);

								return;
							}
						}
					}
				}
			};

			process.Exited += (sender, args) => {
				if(process.ExitCode == 0) {
					Progress.Remove(progressId);
					tcs.SetResult(true);
				} else {
					Progress.Finish(progressId, Progress.Status.Failed);
					tcs.SetResult(false);
				}
				process.Dispose();
			};

			process.Start();
			process.BeginOutputReadLine();
			process.BeginErrorReadLine();

			return tcs.Task;
		}
	}
}