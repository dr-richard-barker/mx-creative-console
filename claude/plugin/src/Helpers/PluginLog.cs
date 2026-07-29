namespace Loupedeck.ClaudeConsolePlugin.Helpers
{
    using System;

    /// <summary>
    /// Thin static wrapper around the SDK's per-plugin log file, so services and helpers that do not
    /// derive from a plugin type can still log. Initialised from the plugin constructor.
    /// Logs land in ~/Library/Application Support/Logi/LogiPluginService/Logs on macOS.
    /// </summary>
    internal static class PluginLog
    {
        private static PluginLogFile _log;

        public static void Init(PluginLogFile log) => PluginLog._log = log;

        public static void Verbose(String text) => Safe(() => PluginLog._log?.Verbose(text));

        public static void Info(String text) => Safe(() => PluginLog._log?.Info(text));

        public static void Warning(String text) => Safe(() => PluginLog._log?.Warning(text));

        public static void Warning(Exception ex, String text) => Safe(() => PluginLog._log?.Warning(ex, text));

        public static void Error(String text) => Safe(() => PluginLog._log?.Error(text));

        public static void Error(Exception ex, String text) => Safe(() => PluginLog._log?.Error(ex, text));

        /// <summary>Logging must never be the thing that takes the plugin down.</summary>
        private static void Safe(Action action)
        {
            try
            {
                action();
            }
            catch
            {
                // Intentionally ignored.
            }
        }
    }
}
