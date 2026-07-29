namespace Loupedeck.ClaudeConsolePlugin.Rendering
{
    using System;

    /// <summary>Colour set for one key face.</summary>
    internal readonly struct KeyPalette
    {
        public KeyPalette(BitmapColor background, BitmapColor accent, BitmapColor primary, BitmapColor secondary)
        {
            this.Background = background;
            this.Accent = accent;
            this.Primary = primary;
            this.Secondary = secondary;
        }

        public BitmapColor Background { get; }

        /// <summary>Drawn as a bar across the top of the key.</summary>
        public BitmapColor Accent { get; }

        /// <summary>Headline text.</summary>
        public BitmapColor Primary { get; }

        /// <summary>Sub-heading and footer text.</summary>
        public BitmapColor Secondary { get; }
    }

    /// <summary>
    /// All key drawing lives here so every action produces a visually consistent tile.
    ///
    /// Only APIs verified against the shipped PluginApi.dll are used:
    ///   BitmapBuilder(PluginImageSize) / .Width / .Height / Clear(BitmapColor)
    ///   FillRectangle(int,int,int,int,BitmapColor) / DrawRectangle(int,int,int,int,BitmapColor)
    ///   DrawText(string,int,int,int,int,BitmapColor?,int,int,int,string) / ToImage()
    /// Every optional parameter is passed explicitly so we never depend on SDK default values.
    /// </summary>
    internal static class KeyFaceRenderer
    {
        // ---- Shared palette ---------------------------------------------------------------------

        internal static readonly BitmapColor AttentionAmber = new BitmapColor(255, 158, 27);
        internal static readonly BitmapColor AttentionAmberDim = new BitmapColor(104, 62, 8);
        internal static readonly BitmapColor NearBlack = new BitmapColor(18, 14, 6);
        internal static readonly BitmapColor Ink = new BitmapColor(16, 18, 22);
        internal static readonly BitmapColor Paper = new BitmapColor(240, 242, 245);
        internal static readonly BitmapColor Muted = new BitmapColor(140, 149, 162);
        internal static readonly BitmapColor DeepMuted = new BitmapColor(84, 91, 102);
        internal static readonly BitmapColor BusyBlue = new BitmapColor(96, 140, 200);
        internal static readonly BitmapColor DoneGreen = new BitmapColor(88, 206, 138);
        internal static readonly BitmapColor DangerRed = new BitmapColor(224, 96, 84);

        /// <summary>True for the 60 px keys, where only a headline fits.</summary>
        internal static Boolean IsCompact(Int32 width) => width < 76;

        // ---- Public API -------------------------------------------------------------------------

        /// <summary>
        /// Renders a three-line tile: headline, sub-line, footer. Empty lines are skipped and the
        /// remaining lines expand to use the space.
        /// </summary>
        public static BitmapImage Render(
            PluginImageSize imageSize,
            KeyPalette palette,
            String headline,
            String subline,
            String footer,
            Boolean drawAccentBar = true,
            Boolean drawBorder = false)
        {
            using (var builder = new BitmapBuilder(imageSize))
            {
                var width = builder.Width;
                var height = builder.Height;

                builder.Clear(palette.Background);

                var barHeight = 0;
                if (drawAccentBar)
                {
                    barHeight = Math.Max(3, height / 18);
                    builder.FillRectangle(0, 0, width, barHeight, palette.Accent);
                }

                if (drawBorder)
                {
                    builder.DrawRectangle(0, 0, width - 1, height - 1, palette.Accent);
                }

                var padding = Math.Max(3, width / 16);
                var contentX = padding;
                var contentWidth = Math.Max(1, width - (padding * 2));
                var contentTop = barHeight + Math.Max(2, height / 22);
                var contentHeight = Math.Max(1, height - contentTop - Math.Max(2, height / 22));

                var compact = IsCompact(width);
                var hasSub = !compact && !String.IsNullOrWhiteSpace(subline);
                var hasFooter = !String.IsNullOrWhiteSpace(footer);

                if (compact)
                {
                    // 60 px keys: headline over an optional single small line.
                    if (hasFooter)
                    {
                        var headlineSize = ScaleFont(width, 0.21);
                        var footerSize = ScaleFont(width, 0.135);
                        var split = (Int32)(contentHeight * 0.62);
                        DrawLine(builder, headline, contentX, contentTop, contentWidth, split, palette.Primary, headlineSize);
                        DrawLine(builder, footer, contentX, contentTop + split, contentWidth, contentHeight - split, palette.Secondary, footerSize);
                    }
                    else
                    {
                        DrawLine(builder, headline, contentX, contentTop, contentWidth, contentHeight, palette.Primary, ScaleFont(width, 0.24));
                    }

                    return builder.ToImage();
                }

                if (hasSub && hasFooter)
                {
                    var h1 = (Int32)(contentHeight * 0.38);
                    var h2 = (Int32)(contentHeight * 0.34);
                    var h3 = contentHeight - h1 - h2;
                    DrawLine(builder, headline, contentX, contentTop, contentWidth, h1, palette.Primary, ScaleFont(width, 0.185));
                    DrawLine(builder, subline, contentX, contentTop + h1, contentWidth, h2, palette.Primary, ScaleFont(width, 0.155));
                    DrawLine(builder, footer, contentX, contentTop + h1 + h2, contentWidth, h3, palette.Secondary, ScaleFont(width, 0.115));
                }
                else if (hasSub)
                {
                    var h1 = (Int32)(contentHeight * 0.52);
                    DrawLine(builder, headline, contentX, contentTop, contentWidth, h1, palette.Primary, ScaleFont(width, 0.21));
                    DrawLine(builder, subline, contentX, contentTop + h1, contentWidth, contentHeight - h1, palette.Primary, ScaleFont(width, 0.165));
                }
                else if (hasFooter)
                {
                    var h1 = (Int32)(contentHeight * 0.64);
                    DrawLine(builder, headline, contentX, contentTop, contentWidth, h1, palette.Primary, ScaleFont(width, 0.225));
                    DrawLine(builder, footer, contentX, contentTop + h1, contentWidth, contentHeight - h1, palette.Secondary, ScaleFont(width, 0.125));
                }
                else
                {
                    DrawLine(builder, headline, contentX, contentTop, contentWidth, contentHeight, palette.Primary, ScaleFont(width, 0.24));
                }

                return builder.ToImage();
            }
        }

        // ---- Text helpers -----------------------------------------------------------------------

        /// <summary>Draws one already-fitted line of text inside a rectangle.</summary>
        private static void DrawLine(
            BitmapBuilder builder,
            String text,
            Int32 x,
            Int32 y,
            Int32 width,
            Int32 height,
            BitmapColor color,
            Int32 fontSize)
        {
            if (String.IsNullOrWhiteSpace(text) || width <= 0 || height <= 0)
            {
                return;
            }

            var fitted = Truncate(text, width, fontSize);

            // All arguments are explicit: text, x, y, width, height, color, fontSize, lineHeight,
            // spaceHeight, fontName. fontName null == the SDK default face.
            builder.DrawText(fitted, x, y, width, height, color, fontSize, fontSize + 2, Math.Max(1, fontSize / 2), null);
        }

        private static Int32 ScaleFont(Int32 width, Double factor) => Math.Max(7, (Int32)Math.Round(width * factor));

        /// <summary>
        /// Trims text to what will plausibly fit on one line. The SDK gives us no text-measuring API,
        /// so this uses an average glyph advance of ~0.55 em, which is right for the default sans face.
        /// </summary>
        internal static String Truncate(String text, Int32 pixelWidth, Int32 fontSize)
        {
            if (String.IsNullOrEmpty(text) || fontSize <= 0)
            {
                return text;
            }

            var maxChars = (Int32)Math.Floor(pixelWidth / (fontSize * 0.55));
            if (maxChars <= 0)
            {
                return String.Empty;
            }

            if (text.Length <= maxChars)
            {
                return text;
            }

            if (maxChars <= 1)
            {
                return text.Substring(0, 1);
            }

            return text.Substring(0, maxChars - 1) + "…";
        }

        /// <summary>Formats a duration compactly for the status tile: 4s, 2m10s, 1h04m.</summary>
        internal static String FormatDuration(TimeSpan span)
        {
            if (span < TimeSpan.Zero)
            {
                span = TimeSpan.Zero;
            }

            if (span.TotalHours >= 1)
            {
                return $"{(Int32)span.TotalHours}h{span.Minutes:00}m";
            }

            if (span.TotalMinutes >= 1)
            {
                return $"{span.Minutes}m{span.Seconds:00}s";
            }

            return $"{span.Seconds}s";
        }
    }
}
