namespace Loupedeck.ClaudeConsolePlugin.Actions
{
    using System;

    using Loupedeck.ClaudeConsolePlugin.Helpers;
    using Loupedeck.ClaudeConsolePlugin.Rendering;

    /// <summary>
    /// The flashing key. Alternates between a bright amber "CLAUDE / NEEDS YOU" face and a dimmed
    /// variant twice a second while Claude Code is waiting on the user; shows a quiet status face
    /// otherwise. Pressing it brings the Claude Code window to the front.
    /// </summary>
    public class ClaudeAttentionCommand : ClaudeCommandBase
    {
        public ClaudeAttentionCommand()
            : base("Claude Attention", "Flashes when Claude Code is waiting for your input; press to jump to the session")
        {
        }

        /// <summary>This is the one key that must repaint on every blink flip.</summary>
        internal override RedrawPolicy RedrawWhen => RedrawPolicy.OnEveryNotification;

        protected override void RunCommand(String actionParameter)
        {
            PluginLog.Info("Claude Attention pressed: focusing the Claude Code window.");
            FocusClaude();
        }

        /// <summary>The image carries all the text, so suppress the SDK's own label.</summary>
        protected override String GetCommandDisplayName(String actionParameter, PluginImageSize imageSize) => String.Empty;

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize)
        {
            try
            {
                var snapshot = Snapshot;
                var project = snapshot.ProjectLabel;

                switch (snapshot.State)
                {
                    case ClaudeState.NeedsInput:
                        return Service.BlinkOn
                            ? KeyFaceRenderer.Render(
                                imageSize,
                                new KeyPalette(
                                    KeyFaceRenderer.AttentionAmber,
                                    KeyFaceRenderer.NearBlack,
                                    KeyFaceRenderer.NearBlack,
                                    KeyFaceRenderer.NearBlack),
                                "CLAUDE",
                                "NEEDS YOU",
                                project,
                                drawAccentBar: true)
                            : KeyFaceRenderer.Render(
                                imageSize,
                                new KeyPalette(
                                    KeyFaceRenderer.AttentionAmberDim,
                                    KeyFaceRenderer.AttentionAmber,
                                    KeyFaceRenderer.AttentionAmber,
                                    KeyFaceRenderer.AttentionAmber),
                                "CLAUDE",
                                "NEEDS YOU",
                                project,
                                drawAccentBar: true);

                    case ClaudeState.Busy:
                        return KeyFaceRenderer.Render(
                            imageSize,
                            new KeyPalette(
                                KeyFaceRenderer.Ink,
                                KeyFaceRenderer.BusyBlue,
                                KeyFaceRenderer.Muted,
                                KeyFaceRenderer.DeepMuted),
                            "CLAUDE",
                            "working…",
                            project,
                            drawAccentBar: true);

                    case ClaudeState.Done:
                        return KeyFaceRenderer.Render(
                            imageSize,
                            new KeyPalette(
                                new BitmapColor(14, 34, 24),
                                KeyFaceRenderer.DoneGreen,
                                KeyFaceRenderer.DoneGreen,
                                KeyFaceRenderer.Muted),
                            "CLAUDE",
                            "done",
                            project,
                            drawAccentBar: true);

                    default:
                        return KeyFaceRenderer.Render(
                            imageSize,
                            new KeyPalette(
                                new BitmapColor(14, 15, 18),
                                new BitmapColor(38, 41, 47),
                                KeyFaceRenderer.DeepMuted,
                                KeyFaceRenderer.DeepMuted),
                            "CLAUDE",
                            null,
                            snapshot.IsUnreadable ? "no state" : "idle",
                            drawAccentBar: true);
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, "ClaudeAttentionCommand.GetCommandImage failed");
                return null;
            }
        }
    }
}
