namespace Loupedeck.ClaudeConsolePlugin.Actions
{
    using System;

    using Loupedeck.ClaudeConsolePlugin.Helpers;
    using Loupedeck.ClaudeConsolePlugin.Rendering;

    /// <summary>
    /// The "go on" key that sits next to the attention key. Focuses the Claude Code window and then
    /// presses Return in it, which accepts the default answer of whatever prompt is on screen.
    /// </summary>
    public class ClaudeContinueCommand : ClaudeCommandBase
    {
        public ClaudeContinueCommand()
            : base("Claude Continue", "Focus the Claude Code window and press Return")
        {
        }

        protected override void RunCommand(String actionParameter)
        {
            PluginLog.Info("Claude Continue pressed: focusing then sending Return.");

            // The delay lives inside FocusClaudeThen: without it the keystroke lands in whatever
            // window was frontmost before the activate call took effect.
            FocusClaudeThen(PlatformShell.SendReturn, "Claude Continue");
        }

        protected override String GetCommandDisplayName(String actionParameter, PluginImageSize imageSize) => String.Empty;

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize)
        {
            try
            {
                var waiting = Snapshot.State == ClaudeState.NeedsInput;

                // Subtly highlighted while Claude is actually waiting, so the pair of keys reads as
                // "something needs you" + "here is the answer".
                var palette = waiting
                    ? new KeyPalette(
                        new BitmapColor(46, 34, 10),
                        KeyFaceRenderer.AttentionAmber,
                        KeyFaceRenderer.AttentionAmber,
                        new BitmapColor(186, 140, 70))
                    : new KeyPalette(
                        new BitmapColor(20, 22, 26),
                        new BitmapColor(52, 57, 66),
                        KeyFaceRenderer.Paper,
                        KeyFaceRenderer.DeepMuted);

                return KeyFaceRenderer.Render(
                    imageSize,
                    palette,
                    "Continue",
                    "⏎",
                    waiting ? "waiting" : null,
                    drawAccentBar: true);
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, "ClaudeContinueCommand.GetCommandImage failed");
                return null;
            }
        }
    }
}
