using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace EQ2Lexicon.ACTPlugin
{
    /// <summary>
    /// Outcome of <see cref="UpdateInstaller.StageUpdate"/>. UI uses
    /// the message for the SettingsPanel status label.
    /// </summary>
    public class StageResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = "";
    }

    /// <summary>
    /// On-disk staging + on-exit swap for the self-update flow.
    /// Lives in the UI assembly because it touches the loaded
    /// assembly's file path and spawns a child process — neither of
    /// which belong in Core.
    ///
    /// ── Why we can't just overwrite the DLL ────────────────────────────
    ///
    /// Windows holds an exclusive lock on a loaded .NET DLL for the
    /// process lifetime. The plugin can't overwrite its own DLL from
    /// inside ACT. Workaround:
    ///
    ///   1. Write the new DLL to <c>&lt;path&gt;.new</c> next to the
    ///      loaded one (creation is allowed even though overwrite of
    ///      the loaded file isn't).
    ///   2. Spawn a hidden PowerShell background job that does
    ///      <c>Wait-Process -Id &lt;act_pid&gt;</c> — sleeps until the
    ///      current ACT process exits, then atomic-renames the .new
    ///      file over the locked one.
    ///   3. Drop a <c>READ_ME_IF_STUCK.txt</c> next to the .new file
    ///      so a user whose PowerShell helper got killed still has a
    ///      manual recovery path.
    ///   4. Tell the user "close + reopen ACT to apply".
    ///
    /// On the user's next ACT start, the file in the Plugins folder
    /// IS the new DLL — no further action required.
    /// </summary>
    internal static class UpdateInstaller
    {
        /// <summary>
        /// Path to the currently-loaded plugin DLL on disk. Resolved
        /// via <see cref="Assembly.Location"/> on the running plugin's
        /// own assembly — handles users who install ACT or the plugin
        /// in non-default locations.
        /// </summary>
        public static string ResolveRunningDllPath()
        {
            // typeof(UpdateInstaller).Assembly is the (ILRepacked) UI
            // DLL itself — the one Windows is holding open.
            return typeof(UpdateInstaller).Assembly.Location ?? "";
        }

        /// <summary>
        /// Write <paramref name="newBytes"/> to <c>runningDllPath + ".new"</c>
        /// and spawn the background swap helper. Returns a
        /// <see cref="StageResult"/> describing what happened — UI
        /// surfaces the message.
        /// </summary>
        public static StageResult StageUpdate(byte[] newBytes, string runningDllPath)
        {
            if (newBytes == null || newBytes.Length == 0)
                return new StageResult { Message = "No bytes to stage." };
            if (string.IsNullOrWhiteSpace(runningDllPath))
                return new StageResult { Message = "Couldn't resolve the currently-loaded DLL path." };

            var newPath = runningDllPath + ".new";
            var readmePath = Path.Combine(
                Path.GetDirectoryName(runningDllPath) ?? "",
                "EQ2Lexicon.ACTPlugin.UPDATE_READ_ME.txt");

            // 1. Write the new bytes. If this fails (permission, disk
            //    full, AV quarantine) we bail with a clear message and
            //    no partial state on disk.
            try
            {
                File.WriteAllBytes(newPath, newBytes);
            }
            catch (Exception ex)
            {
                // Try to clean up if a partial file got written.
                try { if (File.Exists(newPath)) File.Delete(newPath); } catch { /* swallow */ }
                return new StageResult { Message = "Couldn't write update file: " + ex.Message };
            }

            // 2. Drop a recovery readme. Best-effort — if this fails
            //    we still proceed; the .new file + helper script are
            //    the load-bearing parts.
            try
            {
                File.WriteAllText(readmePath, BuildReadmeText(newPath, runningDllPath));
            }
            catch
            {
                // Swallow — the readme is a backup recovery path,
                // not the primary mechanism.
            }

            // 3. Spawn the on-exit swap helper. If THIS fails (no
            //    powershell, restricted execution policy, AV
            //    blocking child processes), keep the .new file
            //    around and tell the user to do it manually.
            var spawned = TrySpawnSwapHelper(newPath, runningDllPath);
            if (!spawned)
            {
                return new StageResult
                {
                    Success = false,
                    Message = "Downloaded but couldn't schedule the auto-swap. See " +
                              Path.GetFileName(readmePath) + " in the Plugins folder for manual steps.",
                };
            }

            return new StageResult
            {
                Success = true,
                Message = "Update staged. Close and reopen ACT to apply — the swap happens automatically.",
            };
        }

        /// <summary>
        /// Spawn a hidden PowerShell that waits for the current ACT
        /// process to exit, then atomic-renames the .new file. Returns
        /// false on any failure to start the process — caller falls
        /// back to telling the user to swap manually.
        ///
        /// ── PowerShell quoting and path-handling caveats ──────────────────
        ///
        ///   * The paths are single-quoted in the script — that's the
        ///     ONLY quoting style that doesn't expand $vars / backticks /
        ///     etc. Within '...' the only escape required is the
        ///     apostrophe itself, encoded as ''. We pre-escape via
        ///     EscapePowerShellSingleQuoted so users whose Windows
        ///     username contains an apostrophe (O'Brien, D'Souza,
        ///     N'Diaye — all legal on NTFS and in Windows account names)
        ///     don't get a broken swap script that silently fails to
        ///     close its `if` block.
        ///
        ///   * We use -LiteralPath everywhere instead of plain positional
        ///     args. Test-Path / Move-Item run their input through
        ///     wildcard expansion by default, so a path containing
        ///     `[`, `]`, or `*` (also legal in Windows filenames) would
        ///     either match the wrong file or no file at all.
        ///
        ///   * The Move-Item is wrapped in a retry loop. The expected
        ///     failure mode it handles: the user has TWO ACT instances
        ///     running and both stage updates. When instance A exits,
        ///     the target .dll is still locked by instance B; without
        ///     the retry the swap fails and the .new file disappears
        ///     before instance B closes. With the retry, helper A
        ///     waits up to ~20s for instance B to release the lock.
        /// </summary>
        private static bool TrySpawnSwapHelper(string newPath, string targetPath)
        {
            try
            {
                var actPid = Process.GetCurrentProcess()
                    .Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var newQuoted = EscapePowerShellSingleQuoted(newPath);
                var tgtQuoted = EscapePowerShellSingleQuoted(targetPath);

                // The retry loop handles the multi-ACT case: target .dll
                // is locked while another instance is still alive.
                var script =
                    $"Wait-Process -Id {actPid} -ErrorAction SilentlyContinue; " +
                    $"if (Test-Path -LiteralPath '{newQuoted}') {{ " +
                    $"$ok = $false; " +
                    $"for ($i = 0; $i -lt 10 -and -not $ok; $i++) {{ " +
                    $"try {{ Move-Item -LiteralPath '{newQuoted}' -Destination '{tgtQuoted}' -Force -ErrorAction Stop; $ok = $true }} " +
                    $"catch {{ Start-Sleep -Seconds 2 }} " +
                    $"}} " +
                    $"}}";

                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -WindowStyle Hidden -Command \"{script}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                };
                using (var p = Process.Start(psi))
                {
                    return p != null;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Double up any single quotes for safe embedding in a
        /// PowerShell single-quoted string. Within '...' a literal
        /// apostrophe is the ONLY thing that needs escaping, and the
        /// escape sequence is '' (two single quotes). Pre-v0.1.13 this
        /// helper didn't exist and a user with an apostrophe in their
        /// AppData path (e.g. C:\Users\O'Brien\...) got a malformed
        /// helper script that silently failed to swap the DLL.
        /// </summary>
        internal static string EscapePowerShellSingleQuoted(string s)
        {
            return (s ?? "").Replace("'", "''");
        }

        private static string BuildReadmeText(string newPath, string targetPath)
        {
            return
                "EQ2 Lexicon ACT Plugin — update staged\r\n" +
                "=======================================\r\n\r\n" +
                "A new plugin version has been downloaded and verified, and is\r\n" +
                "waiting to be applied. Normally this happens automatically the\r\n" +
                "next time you close ACT — a background helper script swaps the\r\n" +
                "files for you. If for any reason that didn't happen (helper\r\n" +
                "killed by antivirus, PowerShell restricted, etc.) you can do\r\n" +
                "it yourself:\r\n\r\n" +
                "  1. Close ACT.\r\n" +
                "  2. In the same folder as this file:\r\n" +
                $"       {Path.GetFileName(newPath)}  →  {Path.GetFileName(targetPath)}\r\n" +
                "     Rename (or move-and-replace) the .new file over the\r\n" +
                "     existing .dll file.\r\n" +
                "  3. Reopen ACT. The new version will load on startup.\r\n\r\n" +
                "If the .new file isn't there anymore, the swap already\r\n" +
                "completed — you can delete this readme.\r\n";
        }
    }
}
