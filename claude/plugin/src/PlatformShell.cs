namespace Loupedeck.ClaudeConsolePlugin
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Runtime.InteropServices;
    using System.Threading.Tasks;

    using Loupedeck.ClaudeConsolePlugin.Helpers;

    /// <summary>
    /// Fire-and-forget process helpers. Everything here is asynchronous and swallowing:
    /// a key press must never block the plugin service thread and must never throw into the SDK.
    /// </summary>
    internal static class PlatformShell
    {
        public static Boolean IsMac { get; } = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

        public static Boolean IsWindows { get; } = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        /// <summary>How long to wait after focusing before typing into the newly-fronted window.</summary>
        public const Int32 FocusSettleMs = 250;

        // ---- Focus -----------------------------------------------------------------------------

        /// <summary>
        /// Brings the Claude Code window to the front. Prefers <c>~/.claude/mx-console/focus-claude.sh</c>
        /// (which the user can tailor to their terminal / multiplexer), otherwise activates
        /// <paramref name="appName"/> via AppleScript.
        /// </summary>
        public static void FocusClaude(String appName, String bundleId) =>
            RunDetached(() => FocusClaudeCore(appName, bundleId), nameof(FocusClaude));

        /// <summary>Focus Claude, wait for the window to actually come forward, then run <paramref name="then"/>.</summary>
        public static void FocusClaudeThen(String appName, String bundleId, Action then, String what) =>
            RunDetachedAsync(
                async () =>
                {
                    FocusClaudeCore(appName, bundleId);
                    await Task.Delay(FocusSettleMs).ConfigureAwait(false);
                    then();
                },
                what);

        private static void FocusClaudeCore(String appName, String bundleId)
        {
            var script = ClaudeStateService.FocusScriptPath;
            if (File.Exists(script))
            {
                PluginLog.Verbose($"Focusing Claude via helper script {script}");
                Exec("/bin/sh", new[] { script }, waitForExitMs: 4000);
                return;
            }

            if (IsMac)
            {
                if (!String.IsNullOrWhiteSpace(bundleId))
                {
                    // `open -b` is more reliable than `activate` when several apps share a display name.
                    Exec("/usr/bin/open", new[] { "-b", bundleId }, waitForExitMs: 4000);
                    return;
                }

                var target = String.IsNullOrWhiteSpace(appName) ? "Terminal" : appName.Trim();
                RunOsaScript($"tell application \"{EscapeAppleScript(target)}\" to activate");
                return;
            }

            if (IsWindows)
            {
                var target = String.IsNullOrWhiteSpace(appName) ? "wt" : appName.Trim();
                // AppActivate matches on window title or process name.
                RunPowerShell(
                    "Add-Type -AssemblyName Microsoft.VisualBasic; " +
                    $"[Microsoft.VisualBasic.Interaction]::AppActivate('{EscapePowerShell(target)}')");
                return;
            }

            PluginLog.Info($"FocusClaude is not implemented on this OS ({RuntimeInformation.OSDescription}); no-op.");
        }

        // ---- Keystrokes ------------------------------------------------------------------------

        /// <summary>Sends Return to the frontmost application.</summary>
        public static void SendReturn()
        {
            if (IsMac)
            {
                RunOsaScript("tell application \"System Events\" to keystroke return");
                return;
            }

            if (IsWindows)
            {
                SendKeysWindows("{ENTER}");
                return;
            }

            PluginLog.Info("SendReturn is not implemented on this OS; no-op.");
        }

        /// <summary>Types a single literal character (e.g. "1") into the frontmost application.</summary>
        public static void SendCharacter(String character)
        {
            if (String.IsNullOrEmpty(character))
            {
                return;
            }

            if (IsMac)
            {
                RunOsaScript($"tell application \"System Events\" to keystroke \"{EscapeAppleScript(character)}\"");
                return;
            }

            if (IsWindows)
            {
                SendKeysWindows(EscapeSendKeys(character));
                return;
            }

            PluginLog.Info("SendCharacter is not implemented on this OS; no-op.");
        }

        /// <summary>
        /// Sends a raw macOS virtual key code (AppleScript <c>key code</c>). Needed for keys that
        /// <c>keystroke</c> cannot express, notably Escape (53).
        /// </summary>
        public static void SendKeyCode(Int32 macKeyCode, String windowsSendKeys)
        {
            if (IsMac)
            {
                RunOsaScript($"tell application \"System Events\" to key code {macKeyCode}");
                return;
            }

            if (IsWindows)
            {
                if (!String.IsNullOrEmpty(windowsSendKeys))
                {
                    SendKeysWindows(windowsSendKeys);
                }

                return;
            }

            PluginLog.Info("SendKeyCode is not implemented on this OS; no-op.");
        }

        // ---- Opening things --------------------------------------------------------------------

        /// <summary>Opens a directory or file in the platform file browser.</summary>
        public static void OpenPath(String path)
        {
            if (String.IsNullOrWhiteSpace(path))
            {
                return;
            }

            RunDetached(
                () =>
                {
                    if (IsMac)
                    {
                        Exec("/usr/bin/open", new[] { path }, waitForExitMs: 4000);
                    }
                    else if (IsWindows)
                    {
                        Exec("explorer.exe", new[] { path }, waitForExitMs: 4000);
                    }
                    else
                    {
                        PluginLog.Info("OpenPath is not implemented on this OS; no-op.");
                    }
                },
                $"OpenPath({path})");
        }

        // ---- Primitives ------------------------------------------------------------------------

        /// <summary>Runs an AppleScript one-liner via <c>osascript -e</c>. macOS only.</summary>
        public static void RunOsaScript(String script)
        {
            if (!IsMac || String.IsNullOrWhiteSpace(script))
            {
                return;
            }

            PluginLog.Verbose($"osascript -e {script}");
            Exec("/usr/bin/osascript", new[] { "-e", script }, waitForExitMs: 6000);
        }

        /// <summary>Runs a PowerShell one-liner. Windows only.</summary>
        public static void RunPowerShell(String script)
        {
            if (!IsWindows || String.IsNullOrWhiteSpace(script))
            {
                return;
            }

            PluginLog.Verbose($"powershell -Command {script}");
            Exec(
                "powershell.exe",
                new[] { "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-Command", script },
                waitForExitMs: 8000);
        }

        private static void SendKeysWindows(String keys) =>
            RunPowerShell(
                "Add-Type -AssemblyName System.Windows.Forms; " +
                $"[System.Windows.Forms.SendKeys]::SendWait('{EscapePowerShell(keys)}')");

        /// <summary>
        /// Launches a process without a shell (arguments are passed as a real argv, so no quoting bugs)
        /// and waits a bounded amount of time so we can log a non-zero exit. Always called off the UI thread.
        /// </summary>
        private static void Exec(String fileName, IReadOnlyList<String> arguments, Int32 waitForExitMs)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = fileName,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };

                foreach (var argument in arguments)
                {
                    psi.ArgumentList.Add(argument);
                }

                using (var process = Process.Start(psi))
                {
                    if (process == null)
                    {
                        PluginLog.Warning($"Process.Start returned null for {fileName}");
                        return;
                    }

                    // WaitForExit first, then drain: reading to end before waiting would block
                    // forever on a child that never exits. These children emit only a line or two
                    // of diagnostics, comfortably inside the OS pipe buffer, so nothing deadlocks.
                    if (!process.WaitForExit(waitForExitMs))
                    {
                        PluginLog.Warning($"{fileName} did not exit within {waitForExitMs} ms; leaving it running.");
                        return;
                    }

                    if (process.ExitCode != 0)
                    {
                        String stderr = null;
                        try
                        {
                            stderr = process.StandardError.ReadToEnd();
                        }
                        catch (Exception readEx)
                        {
                            PluginLog.Verbose($"Could not read stderr of {fileName}: {readEx.Message}");
                        }

                        PluginLog.Warning($"{fileName} exited with {process.ExitCode}: {stderr?.Trim()}");
                    }
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, $"Failed to run {fileName}");
            }
        }

        /// <summary>Runs work off the calling (SDK) thread. Distinctly named from the async variant
        /// so an async lambda can never be silently bound as async void.</summary>
        private static void RunDetached(Action body, String what)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    body();
                }
                catch (Exception ex)
                {
                    PluginLog.Warning(ex, $"{what} failed");
                }
            });
        }

        private static void RunDetachedAsync(Func<Task> body, String what)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await body().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    PluginLog.Warning(ex, $"{what} failed");
                }
            });
        }

        // ---- Escaping --------------------------------------------------------------------------

        /// <summary>Escapes a value for interpolation inside an AppleScript double-quoted string literal.</summary>
        internal static String EscapeAppleScript(String value) =>
            value == null ? String.Empty : value.Replace("\\", "\\\\").Replace("\"", "\\\"");

        /// <summary>Escapes a value for interpolation inside a PowerShell single-quoted string literal.</summary>
        internal static String EscapePowerShell(String value) =>
            value == null ? String.Empty : value.Replace("'", "''");

        /// <summary>Escapes SendKeys metacharacters so a literal character is typed.</summary>
        internal static String EscapeSendKeys(String value)
        {
            if (String.IsNullOrEmpty(value))
            {
                return String.Empty;
            }

            var builder = new System.Text.StringBuilder(value.Length * 3);
            foreach (var c in value)
            {
                if ("+^%~(){}[]".IndexOf(c) >= 0)
                {
                    builder.Append('{').Append(c).Append('}');
                }
                else
                {
                    builder.Append(c);
                }
            }

            return builder.ToString();
        }
    }
}
