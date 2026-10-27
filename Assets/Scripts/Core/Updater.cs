using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
using Debug = UnityEngine.Debug;

namespace Turnaround.Core
{
    // Checks the public GitHub repo's latest release on launch, downloads its zip if it's newer, and on restart
    // hands over to a small PowerShell script that waits for the game to close, copies the new files in and relaunches.
    public class Updater : MonoBehaviour
    {
        public const string Repo = "notsata/turnaround"; // public: friends download releases without logging in
        const string AssetName = "TURNAROUND.zip";

        public enum State { Idle, Checking, UpToDate, Downloading, Ready, Failed }

        public static State Status { get; private set; } = State.Idle;
        public static string LatestVersion { get; private set; }
        public static float Progress { get; private set; }
        public static string Error { get; private set; }

        static string WorkDir => Path.Combine(Path.GetTempPath(), "turnaround_update");
        static string ZipPath => Path.Combine(WorkDir, AssetName);

        [Serializable] class Release { public string tag_name; public Asset[] assets; }
        [Serializable] class Asset { public string name; public string browser_download_url; }

        // Only real players update themselves: not the Editor, not headless test runs.
        public static bool Enabled => !Application.isEditor && !Application.isBatchMode && !Repo.StartsWith("OWNER/");

        IEnumerator Start()
        {
            if (!Enabled || Status != State.Idle) yield break; // menus reload on disconnect; check once per launch
            Status = State.Checking;

            using var check = UnityWebRequest.Get($"https://api.github.com/repos/{Repo}/releases/latest");
            check.SetRequestHeader("User-Agent", "TURNAROUND-updater");
            check.SetRequestHeader("Accept", "application/vnd.github+json");
            check.timeout = 10;
            yield return check.SendWebRequest();
            if (check.result != UnityWebRequest.Result.Success)
            {
                Fail($"Update check failed: {check.error}");
                yield break;
            }

            var release = JsonUtility.FromJson<Release>(check.downloadHandler.text);
            LatestVersion = release.tag_name?.TrimStart('v');
            if (!IsNewer(LatestVersion, Application.version))
            {
                Status = State.UpToDate;
                yield break;
            }

            var asset = Array.Find(release.assets ?? Array.Empty<Asset>(), a => a.name == AssetName);
            if (asset == null)
            {
                Fail($"Release v{LatestVersion} has no {AssetName}");
                yield break;
            }

            Status = State.Downloading;
            Directory.CreateDirectory(WorkDir);
            using var download = new UnityWebRequest(asset.browser_download_url, UnityWebRequest.kHttpVerbGET)
            {
                downloadHandler = new DownloadHandlerFile(ZipPath) { removeFileOnAbort = true },
            };
            download.SetRequestHeader("User-Agent", "TURNAROUND-updater");
            var op = download.SendWebRequest();
            while (!op.isDone)
            {
                Progress = download.downloadProgress;
                yield return null;
            }
            if (download.result != UnityWebRequest.Result.Success)
            {
                Fail($"Update download failed: {download.error}");
                yield break;
            }
            Progress = 1f;
            Status = State.Ready;
            Debug.Log($"[UPDATE] v{LatestVersion} downloaded ({new FileInfo(ZipPath).Length / 1024 / 1024} MB)");
        }

        static void Fail(string message)
        {
            Error = message;
            Status = State.Failed;
            Debug.LogWarning("[UPDATE] " + message);
        }

        public static bool IsNewer(string latest, string current) =>
            Version.TryParse(latest, out var l) && Version.TryParse(current, out var c) && l > c;

        // The game can't overwrite its own files while running, so a script does it after we quit.
        public static void ApplyAndRestart()
        {
            if (Status != State.Ready) return;
            string gameDir = Path.GetDirectoryName(Application.dataPath); // ...\TURNAROUND_Data -> install folder
            string exe = Path.Combine(gameDir, "TURNAROUND.exe");
            string script = Path.Combine(WorkDir, "apply_update.ps1");
            static string Q(string s) => s.Replace("'", "''"); // single-quoted PowerShell strings
            File.WriteAllText(script, $@"
$ErrorActionPreference = 'Stop'
Wait-Process -Id {Process.GetCurrentProcess().Id} -ErrorAction SilentlyContinue
$extract = '{Q(WorkDir)}\extracted'
if (Test-Path $extract) {{ Remove-Item $extract -Recurse -Force }}
Expand-Archive -Path '{Q(ZipPath)}' -DestinationPath $extract -Force
Copy-Item -Path ""$extract\TURNAROUND\*"" -Destination '{Q(gameDir)}' -Recurse -Force
Start-Process '{Q(exe)}'
");
            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            Application.Quit();
        }
    }
}
