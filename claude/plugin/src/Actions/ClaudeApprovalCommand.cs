namespace Loupedeck.ClaudeConsolePlugin.Actions
{
    using System;
    using System.Collections.Generic;

    using Loupedeck.ClaudeConsolePlugin.Helpers;
    using Loupedeck.ClaudeConsolePlugin.Rendering;

    /// <summary>
    /// One action that registers three selectable variants — Allow once / Always allow / Deny —
    /// each of which focuses the Claude Code window and answers its permission prompt with the
    /// corresponding keystroke.
    /// </summary>
    public class ClaudeApprovalCommand : ClaudeCommandBase
    {
        // ==========================================================================================
        //  PERMISSION DIALOG KEY MAP
        //
        //  These are the keys Claude Code's permission prompt listens for. They are the single most
        //  likely thing in this plugin to go stale: if Claude Code reorders or renames its permission
        //  options, change ONLY the four constants below.
        //
        //  As of the version this plugin was written against, the prompt reads:
        //      1. Yes                       -> "1"
        //      2. Yes, and don't ask again  -> "2"
        //      3. No, and tell Claude ...   -> Escape also dismisses the prompt
        //
        //  macOS note: AppleScript's `keystroke` can type printable characters but not Escape, so
        //  Deny is sent as a raw virtual key code instead. 53 is Escape.
        // ==========================================================================================

        private const String AllowOnceCharacter = "1";
        private const String AlwaysAllowCharacter = "2";
        private const Int32 DenyMacKeyCode = 53;              // kVK_Escape
        private const String DenyWindowsSendKeys = "{ESC}";

        // Windows equivalents for the two printable answers are just the characters themselves.
        private const String AllowOnceWindowsSendKeys = AllowOnceCharacter;
        private const String AlwaysAllowWindowsSendKeys = AlwaysAllowCharacter;

        // ==========================================================================================

        private const String ParamAllowOnce = "allow-once";
        private const String ParamAlwaysAllow = "always-allow";
        private const String ParamDeny = "deny";

        /// <summary>One selectable variant of this action.</summary>
        private sealed class ApprovalVariant
        {
            public String Parameter { get; init; }

            public String DisplayName { get; init; }

            public String Headline { get; init; }

            public String Subline { get; init; }

            /// <summary>Printable character to type on macOS, or null when <see cref="MacKeyCode"/> is used.</summary>
            public String MacCharacter { get; init; }

            /// <summary>macOS virtual key code, or -1 when <see cref="MacCharacter"/> is used.</summary>
            public Int32 MacKeyCode { get; init; } = -1;

            public String WindowsSendKeys { get; init; }

            public BitmapColor Accent { get; init; }
        }

        private static readonly Dictionary<String, ApprovalVariant> Variants = new Dictionary<String, ApprovalVariant>(StringComparer.Ordinal)
        {
            [ParamAllowOnce] = new ApprovalVariant
            {
                Parameter = ParamAllowOnce,
                DisplayName = "Allow once",
                Headline = "ALLOW",
                Subline = "once",
                MacCharacter = AllowOnceCharacter,
                WindowsSendKeys = AllowOnceWindowsSendKeys,
                Accent = KeyFaceRenderer.DoneGreen,
            },
            [ParamAlwaysAllow] = new ApprovalVariant
            {
                Parameter = ParamAlwaysAllow,
                DisplayName = "Always allow",
                Headline = "ALLOW",
                Subline = "always",
                MacCharacter = AlwaysAllowCharacter,
                WindowsSendKeys = AlwaysAllowWindowsSendKeys,
                Accent = new BitmapColor(120, 190, 235),
            },
            [ParamDeny] = new ApprovalVariant
            {
                Parameter = ParamDeny,
                DisplayName = "Deny",
                Headline = "DENY",
                Subline = "esc",
                MacCharacter = null,
                MacKeyCode = DenyMacKeyCode,
                WindowsSendKeys = DenyWindowsSendKeys,
                Accent = KeyFaceRenderer.DangerRed,
            },
        };

        /// <summary>Declaration order controls the order the variants appear in Logi Options+.</summary>
        private static readonly String[] VariantOrder = { ParamAllowOnce, ParamAlwaysAllow, ParamDeny };

        public ClaudeApprovalCommand()
        {
            foreach (var parameter in VariantOrder)
            {
                var variant = Variants[parameter];
                this.AddParameter(variant.Parameter, variant.DisplayName, ClaudeGroupName);
            }
        }

        protected override void RunCommand(String actionParameter)
        {
            if (!TryGetVariant(actionParameter, out var variant))
            {
                PluginLog.Warning($"ClaudeApprovalCommand invoked with unknown parameter '{actionParameter}'");
                return;
            }

            PluginLog.Info($"Claude Approval '{variant.DisplayName}' pressed.");

            FocusClaudeThen(
                () =>
                {
                    if (variant.MacCharacter != null)
                    {
                        PlatformShell.SendCharacter(variant.MacCharacter);
                    }
                    else
                    {
                        PlatformShell.SendKeyCode(variant.MacKeyCode, variant.WindowsSendKeys);
                    }
                },
                $"Claude Approval ({variant.DisplayName})");
        }

        protected override String GetCommandDisplayName(String actionParameter, PluginImageSize imageSize) => String.Empty;

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize)
        {
            try
            {
                if (!TryGetVariant(actionParameter, out var variant))
                {
                    return null;
                }

                var waiting = Snapshot.State == ClaudeState.NeedsInput;

                var palette = waiting
                    ? new KeyPalette(
                        new BitmapColor(30, 32, 36),
                        variant.Accent,
                        variant.Accent,
                        KeyFaceRenderer.Muted)
                    : new KeyPalette(
                        new BitmapColor(16, 17, 20),
                        new BitmapColor(variant.Accent, 90),
                        new BitmapColor(variant.Accent, 130),
                        KeyFaceRenderer.DeepMuted);

                return KeyFaceRenderer.Render(
                    imageSize,
                    palette,
                    variant.Headline,
                    variant.Subline,
                    null,
                    drawAccentBar: true);
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, "ClaudeApprovalCommand.GetCommandImage failed");
                return null;
            }
        }

        private static Boolean TryGetVariant(String actionParameter, out ApprovalVariant variant)
        {
            variant = null;
            return !String.IsNullOrEmpty(actionParameter) && Variants.TryGetValue(actionParameter, out variant);
        }
    }
}
