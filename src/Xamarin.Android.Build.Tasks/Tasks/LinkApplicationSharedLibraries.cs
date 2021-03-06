using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

using Xamarin.Android.Tools;
using Xamarin.Build;

namespace Xamarin.Android.Tasks
{
	public class LinkApplicationSharedLibraries : AndroidAsyncTask
	{
		public override string TaskPrefix => "LAS";

		sealed class Config
		{
			public string LinkerPath;
			public string LinkerOptions;
			public string OutputSharedLibrary;
		}

		sealed class InputFiles
		{
			public List<string> ObjectFiles;
			public string OutputSharedLibrary;
		}

		[Required]
		public ITaskItem[] ObjectFiles { get; set; }

		[Required]
		public ITaskItem[] ApplicationSharedLibraries { get; set; }

		[Required]
		public bool DebugBuild { get; set; }

		[Required]
		public string AndroidBinUtilsDirectory { get; set; }

		public override System.Threading.Tasks.Task RunTaskAsync ()
		{
			return this.WhenAll (GetLinkerConfigs (), RunLinker);
		}

		void RunLinker (Config config)
		{
			var stdout_completed = new ManualResetEvent (false);
			var stderr_completed = new ManualResetEvent (false);
			var psi = new ProcessStartInfo () {
				FileName = config.LinkerPath,
				Arguments = config.LinkerOptions,
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true,
				WindowStyle = ProcessWindowStyle.Hidden,
			};

			string targetDir = Path.GetDirectoryName (config.OutputSharedLibrary);
			if (!Directory.Exists (targetDir))
				Directory.CreateDirectory (targetDir);

			string linkerName = Path.GetFileName (config.LinkerPath);
			LogDebugMessage ($"[Native Linker] {psi.FileName} {psi.Arguments}");
			using (var proc = new Process ()) {
				proc.OutputDataReceived += (s, e) => {
					if (e.Data != null)
						OnOutputData (linkerName, s, e);
					else
						stdout_completed.Set ();
				};

				proc.ErrorDataReceived += (s, e) => {
					if (e.Data != null)
						OnErrorData (linkerName, s, e);
					else
						stderr_completed.Set ();
				};

				proc.StartInfo = psi;
				proc.Start ();
				proc.BeginOutputReadLine ();
				proc.BeginErrorReadLine ();
				CancellationToken.Register (() => { try { proc.Kill (); } catch (Exception) { } });
				proc.WaitForExit ();

				if (psi.RedirectStandardError)
					stderr_completed.WaitOne (TimeSpan.FromSeconds (30));

				if (psi.RedirectStandardOutput)
					stdout_completed.WaitOne (TimeSpan.FromSeconds (30));

				if (proc.ExitCode != 0) {
					LogCodedError ("XA3001", $"Could not link native shared library: {Path.GetFileName (config.OutputSharedLibrary)}");
					Cancel ();
				}
			}
		}

		IEnumerable<Config> GetLinkerConfigs ()
		{
			var abis = new Dictionary <string, InputFiles> (StringComparer.Ordinal);
			ITaskItem[] dsos = ApplicationSharedLibraries;
			foreach (ITaskItem item in dsos) {
				string abi = item.GetMetadata ("abi");
				abis [abi] = GatherFilesForABI(item.ItemSpec, abi, ObjectFiles);
			}

			foreach (var kvp in abis) {
				string abi = kvp.Key;
				InputFiles inputs = kvp.Value;
				AndroidTargetArch arch;

				var linkerArgs = new List <string> {
					"--unresolved-symbols=ignore-in-shared-libs",
					"--export-dynamic",
					"-soname", "libxamarin-app.so",
					"-z", "relro",
					"-z", "noexecstack",
					"--enable-new-dtags",
					"--eh-frame-hdr",
					"-shared",
					"--build-id",
					"--warn-shared-textrel",
					"--fatal-warnings",
					"-o", QuoteFileName (inputs.OutputSharedLibrary),
				};

				string elf_arch;
				switch (abi) {
					case "armeabi-v7a":
						arch = AndroidTargetArch.Arm;
						linkerArgs.Add ("-X");
						elf_arch = "armelf_linux_eabi";
						break;

					case "arm64":
					case "arm64-v8a":
					case "aarch64":
						arch = AndroidTargetArch.Arm64;
						linkerArgs.Add ("--fix-cortex-a53-843419");
						elf_arch = "aarch64linux";
						break;

					case "x86":
						arch = AndroidTargetArch.X86;
						elf_arch = "elf_i386";
						break;

					case "x86_64":
						arch = AndroidTargetArch.X86_64;
						elf_arch = "elf_x86_64";
						break;

					default:
						throw new NotSupportedException ($"Unsupported Android target architecture ABI: {abi}");
				}

				linkerArgs.Add ("-m");
				linkerArgs.Add (elf_arch);

				foreach (string file in inputs.ObjectFiles) {
					linkerArgs.Add (QuoteFileName (file));
				}

				string ld = MonoAndroidHelper.GetExecutablePath (AndroidBinUtilsDirectory, $"{NdkUtil.GetNdkToolchainPrefix (arch, false)}ld");
				yield return new Config {
					LinkerPath = Path.Combine (AndroidBinUtilsDirectory, ld),
					LinkerOptions = String.Join (" ", linkerArgs),
					OutputSharedLibrary = inputs.OutputSharedLibrary,
				};
			}
		}

		InputFiles GatherFilesForABI (string runtimeSharedLibrary, string abi, ITaskItem[] objectFiles)
		{
			return new InputFiles {
				OutputSharedLibrary = runtimeSharedLibrary,
				ObjectFiles = GetItemsForABI (abi, objectFiles),
			};
		}

		List<string> GetItemsForABI (string abi, ITaskItem[] items)
		{
			var ret = new List <string> ();
			foreach (ITaskItem item in items) {
				if (String.Compare (abi, item.GetMetadata ("abi"), StringComparison.Ordinal) != 0)
					continue;
				ret.Add (item.ItemSpec);
			}

			return ret;
		}

		void OnOutputData (string linkerName, object sender, DataReceivedEventArgs e)
		{
			if (e.Data != null)
				LogMessage ($"[{linkerName} stdout] {e.Data}");
		}

		void OnErrorData (string linkerName, object sender, DataReceivedEventArgs e)
		{
			if (e.Data != null)
				LogMessage ($"[{linkerName} stderr] {e.Data}");
		}

		static string QuoteFileName (string fileName)
		{
			var builder = new CommandLineBuilder ();
			builder.AppendFileNameIfNotNull (fileName);
			return builder.ToString ();
		}
	}
}
