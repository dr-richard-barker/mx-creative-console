namespace Loupedeck.ClaudeConsolePlugin.Actions
{
    using System;

    using Loupedeck.ClaudeConsolePlugin.Helpers;
    using Loupedeck.ClaudeConsolePlugin.Rendering;

    /// <summary>
    /// A status tile: current state plus how long we have been in it. Pressing it opens the session's
    /// working directory in Finder / Explorer, which is a harmless, useful side effect for a key
    /// people will inevitably prod.
    /// </summary>
    public class ClaudeStatusCommand : ClaudeCommandBase
    {
        public ClaudeStatusCommand()
            : base("Claude Status", "Shows the Claude Code session state and elapsed time; press to open the project folder")
        {
            // Renders as an always-on information tile rather than a momentary button.
            this.IsWidget = true;
        }

        /// <summary>The elapsed-time readout needs the service's 1 Hz heartbeat.</summary>
        internal override RedrawPolicy RedrawWhen => RedrawPolicy.OnEveryNotification;

        protected override void RunCommand(String actionParameter)
        {
            var cwd = Snapshot.Cwd;
            if (String.IsNullOrWhiteSpace(cwd))
            {
                PluginLog.Info("Claude Status pressed but no cwd is known; nothing to open.");
                return;
            }

            PluginLog.Info($"Claude Status pressed: opening {cwd}");
            PlatformShell.OpenPath(cwd);
        }

        protected override String GetCommandDisplayName(String actionParameter, PluginImageSize imageSize) => String.Empty;

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize)
        {
            try
            {
                var snapshot = Snapshot;
                var elapsed = KeyFaceRenderer.FormatDuration(Service.TimeInState);
                var project = snapshot.ProjectLabel;

                String headline;
                KeyPalette palette;

                switch (snapshot.State)
                {
                    case ClaudeState.NeedsInput:
                        headline = "WAITING";
                        palette = new KeyPalette(
                            new BitmapColor(38, 28, 8),
                            KeyFaceRenderer.AttentionAmber,
                            KeyFaceRenderer.AttentionAmber,
                            new BitmapColor(178, 134, 66));
                        break;

                    case ClaudeState.Busy:
                        headline = "WORKING";
                        palette = new KeyPalette(
                            KeyFaceRenderer.Ink,
                            KeyFaceRenderer.BusyBlue,
                            KeyFaceRenderer.BusyBlue,
                            KeyFaceRenderer.DeepMuted);
                        break;

                    case ClaudeState.Done:
                        headline = "DONE";
                        palette = new KeyPalette(
                            new BitmapColor(14, 34, 24),
                            KeyFaceRenderer.DoneGreen,
                            KeyFaceRenderer.DoneGreen,
                            KeyFaceRenderer.DeepMuted);
                        break;

                    default:
                        headline = "IDLE";
                        palette = new KeyPalette(
                            new BitmapColor(14, 15, 18),
                            new BitmapColor(38, 41, 47),
                            KeyFaceRenderer.DeepMuted,
                            KeyFaceRenderer.DeepMuted);
                        break;
                }

                // Idle sessions have no meaningful elapsed time to report.
                var subline = snapshot.State == ClaudeState.Idle ? null : elapsed;
                var footer = project ?? (snapshot.State == ClaudeState.Idle ? "no session" : null);

                return KeyFaceRenderer.Render(imageSize, palette, headline, subline, footer, drawAccentBar: true);
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, "ClaudeStatusCommand.GetCommandImage failed");
                return null;
            }
        }
    }
}
