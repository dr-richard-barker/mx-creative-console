namespace Loupedeck.ClaudeConsolePlugin
{
    using System;
    using System.IO;

    using Loupedeck.ClaudeConsolePlugin.Helpers;

    /// <summary>
    /// "Claude Console" — makes an MX Creative Console keypad key flash when Claude Code is waiting
    /// on you, and gives you one-press keys to jump back to the session and answer it.
    ///
    /// The plugin is a pure consumer of <c>~/.claude/mx-console/state.json</c>, which Claude Code
    /// hooks are expected to write. See README.md for the contract.
    /// </summary>
    public class ClaudeConsolePlugin : Plugin
    {
        /// <summary>This plugin talks to no client application API.</summary>
        public override Boolean UsesApplicationApiOnly => true;

        /// <summary>Universal plugin: its actions are available regardless of the focused application.</summary>
        public override Boolean HasNoApplication => true;

        public ClaudeConsolePlugin() => PluginLog.Init(this.Log);

        public override void Load()
        {
            try
            {
                // The hooks create this directory, but creating it here means the very first run of
                // the plugin does not log a stat failure every 250 ms before the hooks ever fire.
                Directory.CreateDirectory(ClaudeStateService.StateDirectory);
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, $"Could not create {ClaudeStateService.StateDirectory}");
            }

            ClaudeStateService.Instance.Start();
            PluginLog.Info("Claude Console plugin loaded.");
        }

        public override void Unload()
        {
            ClaudeStateService.Instance.Stop();
            PluginLog.Info("Claude Console plugin unloaded.");
        }
    }
}
