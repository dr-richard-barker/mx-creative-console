namespace Loupedeck.ClaudeConsolePlugin
{
    using System;
    using System.IO;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;

    using Loupedeck.ClaudeConsolePlugin.Helpers;

    /// <summary>The four states Claude Code hooks can write into the state file.</summary>
    public enum ClaudeState
    {
        /// <summary>No session, or the state file is missing / stale.</summary>
        Idle,

        /// <summary>Claude is waiting on the user (permission prompt or idle prompt). This is the state that flashes.</summary>
        NeedsInput,

        /// <summary>Claude is working.</summary>
        Busy,

        /// <summary>The turn finished.</summary>
        Done,
    }

    /// <summary>Immutable view of the contents of <c>~/.claude/mx-console/state.json</c>.</summary>
    public sealed class ClaudeSnapshot
    {
        public static readonly ClaudeSnapshot Empty = new ClaudeSnapshot();

        public ClaudeState State { get; init; } = ClaudeState.Idle;

        public String SessionId { get; init; }

        public String Cwd { get; init; }

        public String Title { get; init; }

        /// <summary>Human-readable macOS application name, e.g. "Terminal". Used by the osascript fallback.</summary>
        public String App { get; init; }

        public String BundleId { get; init; }

        /// <summary>Unix seconds the hook wrote the file, or <see cref="DateTimeOffset.MinValue"/> if absent.</summary>
        public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.MinValue;

        /// <summary>True when the file could not be read or parsed at all (as opposed to being legitimately idle).</summary>
        public Boolean IsUnreadable { get; init; }

        /// <summary>Best label to put on a key: the project title, falling back to the last path segment of cwd.</summary>
        public String ProjectLabel
        {
            get
            {
                if (!String.IsNullOrWhiteSpace(this.Title))
                {
                    return this.Title.Trim();
                }

                if (String.IsNullOrWhiteSpace(this.Cwd))
                {
                    return null;
                }

                var trimmed = this.Cwd.TrimEnd('/', '\\');
                var slash = trimmed.LastIndexOfAny(new[] { '/', '\\' });
                var leaf = slash >= 0 ? trimmed.Substring(slash + 1) : trimmed;
                return String.IsNullOrWhiteSpace(leaf) ? null : leaf;
            }
        }

        /// <summary>Value equality over everything an action can render, so we only redraw on real changes.</summary>
        public Boolean IsSameAs(ClaudeSnapshot other) =>
            other != null
            && this.State == other.State
            && this.SessionId == other.SessionId
            && this.Cwd == other.Cwd
            && this.Title == other.Title
            && this.App == other.App
            && this.BundleId == other.BundleId
            && this.IsUnreadable == other.IsUnreadable;
    }

    /// <summary>
    /// Shared singleton that polls the Claude Code state file and drives the blink phase.
    /// Actions subscribe to <see cref="StateChanged"/> and call <c>ActionImageChanged(null)</c>.
    /// </summary>
    public sealed class ClaudeStateService
    {
        // ---- Tuning ----------------------------------------------------------------------------
        private const Int32 PollIntervalMs = 250;
        private const Int32 BlinkIntervalMs = 500;
        private const Int32 TicksPerBlink = BlinkIntervalMs / PollIntervalMs;   // 2
        private const Int32 TicksPerSecond = 1000 / PollIntervalMs;             // 4

        /// <summary>A state file older than this is treated as idle (Claude Code probably died).</summary>
        private static readonly TimeSpan StaleAfter = TimeSpan.FromHours(2);

        /// <summary>Reading a file mid-write can yield truncated JSON; retry a couple of times before giving up.</summary>
        private const Int32 ReadRetries = 3;

        // ---- Paths -----------------------------------------------------------------------------

        /// <summary><c>~/.claude/mx-console</c></summary>
        public static String StateDirectory { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "mx-console");

        /// <summary><c>~/.claude/mx-console/state.json</c></summary>
        public static String StateFilePath { get; } = Path.Combine(StateDirectory, "state.json");

        /// <summary><c>~/.claude/mx-console/focus-claude.sh</c> — optional user-supplied focus helper.</summary>
        public static String FocusScriptPath { get; } = Path.Combine(StateDirectory, "focus-claude.sh");

        // ---- Singleton -------------------------------------------------------------------------

        public static ClaudeStateService Instance { get; } = new ClaudeStateService();

        private ClaudeStateService()
        {
        }

        // ---- Public surface --------------------------------------------------------------------

        /// <summary>Raised whenever the key faces need to be redrawn (state change, or a blink flip).</summary>
        public event EventHandler StateChanged;

        /// <summary>Latest parsed state. Never null.</summary>
        public ClaudeSnapshot Current { get; private set; } = ClaudeSnapshot.Empty;

        /// <summary>Blink phase. Only meaningful while <see cref="Current"/> is <see cref="ClaudeState.NeedsInput"/>.</summary>
        public Boolean BlinkOn { get; private set; } = true;

        /// <summary>UTC instant the current <see cref="ClaudeState"/> was first observed.</summary>
        public DateTime StateSinceUtc { get; private set; } = DateTime.UtcNow;

        /// <summary>How long we have been in the current state.</summary>
        public TimeSpan TimeInState
        {
            get
            {
                var elapsed = DateTime.UtcNow - this.StateSinceUtc;
                return elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
            }
        }

        // ---- Lifecycle -------------------------------------------------------------------------

        private readonly Object _lifecycleLock = new Object();
        private CancellationTokenSource _cts;
        private Task _loop;

        private DateTime _lastWriteUtc = DateTime.MinValue;
        private Int64 _lastLength = -1;
        private Int32 _blinkTicks;
        private Int32 _secondTicks;

        /// <summary>
        /// Bumped every time the parsed state (not the blink phase, not the clock) actually changes.
        /// Actions use this to skip redraws they do not need.
        /// </summary>
        public Int64 StateRevision { get; private set; }

        public void Start()
        {
            lock (this._lifecycleLock)
            {
                if (this._loop != null)
                {
                    return;
                }

                this._cts = new CancellationTokenSource();
                var token = this._cts.Token;
                this._loop = Task.Run(() => this.RunLoopAsync(token), token);
                PluginLog.Info($"ClaudeStateService started. Watching {StateFilePath}");
            }
        }

        public void Stop()
        {
            lock (this._lifecycleLock)
            {
                try
                {
                    this._cts?.Cancel();
                    this._cts?.Dispose();
                }
                catch (Exception ex)
                {
                    PluginLog.Warning(ex, "ClaudeStateService.Stop failed");
                }
                finally
                {
                    this._cts = null;
                    this._loop = null;
                    PluginLog.Info("ClaudeStateService stopped.");
                }
            }
        }

        // ---- Poll loop -------------------------------------------------------------------------

        private async Task RunLoopAsync(CancellationToken token)
        {
            // Prime the cache so the very first redraw is already correct.
            this.PollOnce();

            try
            {
                using (var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(PollIntervalMs)))
                {
                    while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                    {
                        this.PollOnce();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown.
            }
            catch (Exception ex)
            {
                // A poll loop that dies silently is the worst possible failure mode for this plugin.
                PluginLog.Error(ex, "ClaudeStateService poll loop terminated unexpectedly");
            }
        }

        /// <summary>One tick. Must never throw.</summary>
        private void PollOnce()
        {
            try
            {
                var changed = this.RefreshSnapshotIfFileChanged();
                var needsInput = this.Current.State == ClaudeState.NeedsInput;

                if (needsInput)
                {
                    this._blinkTicks++;
                    if (this._blinkTicks >= TicksPerBlink)
                    {
                        this._blinkTicks = 0;
                        this.BlinkOn = !this.BlinkOn;
                        changed = true;
                    }
                }
                else if (!this.BlinkOn)
                {
                    // Leave the blink phase in a known-bright state for the next attention burst.
                    this.BlinkOn = true;
                    this._blinkTicks = 0;
                }

                // Keep the elapsed-time readout on the status tile alive at 1 Hz while a session is
                // live. When there is no session at all we stay completely silent.
                this._secondTicks++;
                if (this._secondTicks >= TicksPerSecond)
                {
                    this._secondTicks = 0;
                    if (this.Current.State != ClaudeState.Idle)
                    {
                        changed = true;
                    }
                }

                // Only push pixels when something actually changed: the device redraw goes over USB.
                if (changed)
                {
                    this.RaiseStateChanged();
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "ClaudeStateService tick failed");
            }
        }

        /// <summary>Re-reads and re-parses the state file if its stamp moved. Returns true if the cached state changed.</summary>
        private Boolean RefreshSnapshotIfFileChanged()
        {
            FileInfo info;
            try
            {
                info = new FileInfo(StateFilePath);
                info.Refresh();
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "Could not stat the Claude state file");
                return this.Publish(new ClaudeSnapshot { IsUnreadable = true });
            }

            if (!info.Exists)
            {
                this._lastWriteUtc = DateTime.MinValue;
                this._lastLength = -1;
                return this.Publish(ClaudeSnapshot.Empty);
            }

            var writeUtc = info.LastWriteTimeUtc;
            var length = info.Length;
            var fileMoved = writeUtc != this._lastWriteUtc || length != this._lastLength;

            if (!fileMoved)
            {
                // Contents unchanged, but a previously fresh file may have just crossed the stale threshold.
                return this.ApplyStaleness(this.Current);
            }

            this._lastWriteUtc = writeUtc;
            this._lastLength = length;

            var parsed = ReadSnapshot(StateFilePath);
            if (parsed == null)
            {
                // Malformed or torn write. Keep showing the last good state rather than flickering to idle,
                // and deliberately do not latch _lastWriteUtc back, so the next tick retries the same file.
                this._lastWriteUtc = DateTime.MinValue;
                this._lastLength = -1;
                return false;
            }

            return this.ApplyStaleness(parsed);
        }

        /// <summary>Downgrades a snapshot whose timestamp is too old to idle, then publishes it.</summary>
        private Boolean ApplyStaleness(ClaudeSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return this.Publish(ClaudeSnapshot.Empty);
            }

            if (snapshot.State != ClaudeState.Idle
                && snapshot.Timestamp > DateTimeOffset.MinValue
                && (DateTimeOffset.UtcNow - snapshot.Timestamp) > StaleAfter)
            {
                snapshot = new ClaudeSnapshot
                {
                    State = ClaudeState.Idle,
                    SessionId = snapshot.SessionId,
                    Cwd = snapshot.Cwd,
                    Title = snapshot.Title,
                    App = snapshot.App,
                    BundleId = snapshot.BundleId,
                    Timestamp = snapshot.Timestamp,
                };
            }

            return this.Publish(snapshot);
        }

        private Boolean Publish(ClaudeSnapshot snapshot)
        {
            var previous = this.Current;
            if (snapshot.IsSameAs(previous))
            {
                return false;
            }

            if (previous == null || previous.State != snapshot.State)
            {
                this.StateSinceUtc = DateTime.UtcNow;
                this._blinkTicks = 0;
                this.BlinkOn = true;
            }

            this.Current = snapshot;
            this.StateRevision++;
            PluginLog.Verbose($"Claude state -> {snapshot.State} (project '{snapshot.ProjectLabel}', app '{snapshot.App}')");
            return true;
        }

        private void RaiseStateChanged()
        {
            try
            {
                this.StateChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "A StateChanged subscriber threw");
            }
        }

        // ---- Parsing ---------------------------------------------------------------------------

        /// <summary>Reads and parses the state file. Returns null on any failure (missing, locked, torn, malformed).</summary>
        private static ClaudeSnapshot ReadSnapshot(String path)
        {
            for (var attempt = 0; attempt < ReadRetries; attempt++)
            {
                String text;
                try
                {
                    // FileShare.ReadWrite|Delete so we never block the hook that is writing the file.
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                               FileShare.ReadWrite | FileShare.Delete))
                    using (var reader = new StreamReader(stream))
                    {
                        text = reader.ReadToEnd();
                    }
                }
                catch (FileNotFoundException)
                {
                    return null;
                }
                catch (DirectoryNotFoundException)
                {
                    return null;
                }
                catch (IOException)
                {
                    Thread.Sleep(20);
                    continue;
                }
                catch (Exception ex)
                {
                    PluginLog.Warning(ex, "Unexpected error reading the Claude state file");
                    return null;
                }

                if (String.IsNullOrWhiteSpace(text))
                {
                    Thread.Sleep(20);
                    continue;
                }

                var snapshot = TryParse(text);
                if (snapshot != null)
                {
                    return snapshot;
                }

                // Very likely a partial write; give the writer a moment and re-read.
                Thread.Sleep(20);
            }

            return null;
        }

        /// <summary>Tolerant JSON parse. Unknown fields are ignored; missing fields fall back to defaults.</summary>
        internal static ClaudeSnapshot TryParse(String json)
        {
            try
            {
                using (var document = JsonDocument.Parse(json))
                {
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object)
                    {
                        return null;
                    }

                    return new ClaudeSnapshot
                    {
                        State = ParseState(GetString(root, "state")),
                        SessionId = GetString(root, "session_id"),
                        Cwd = GetString(root, "cwd"),
                        Title = GetString(root, "title"),
                        App = GetString(root, "app"),
                        BundleId = GetString(root, "bundle_id"),
                        Timestamp = ParseTimestamp(root),
                    };
                }
            }
            catch (JsonException)
            {
                return null;
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "Unexpected error parsing the Claude state file");
                return null;
            }
        }

        private static String GetString(JsonElement root, String name)
        {
            if (!root.TryGetProperty(name, out var value))
            {
                return null;
            }

            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.ToString(),
                _ => null,
            };
        }

        private static ClaudeState ParseState(String raw)
        {
            if (String.IsNullOrWhiteSpace(raw))
            {
                return ClaudeState.Idle;
            }

            switch (raw.Trim().ToLowerInvariant())
            {
                case "needs_input":
                case "needsinput":
                case "waiting":
                    return ClaudeState.NeedsInput;

                case "busy":
                case "working":
                case "running":
                    return ClaudeState.Busy;

                case "done":
                case "finished":
                case "complete":
                    return ClaudeState.Done;

                case "idle":
                case "":
                    return ClaudeState.Idle;

                default:
                    PluginLog.Verbose($"Unrecognised Claude state '{raw}', treating as idle");
                    return ClaudeState.Idle;
            }
        }

        /// <summary>Accepts <c>ts</c> as unix seconds (number or numeric string). Milliseconds are auto-detected.</summary>
        private static DateTimeOffset ParseTimestamp(JsonElement root)
        {
            if (!root.TryGetProperty("ts", out var value))
            {
                return DateTimeOffset.MinValue;
            }

            Double seconds;
            if (value.ValueKind == JsonValueKind.Number)
            {
                if (!value.TryGetDouble(out seconds))
                {
                    return DateTimeOffset.MinValue;
                }
            }
            else if (value.ValueKind == JsonValueKind.String)
            {
                if (!Double.TryParse(value.GetString(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out seconds))
                {
                    return DateTimeOffset.MinValue;
                }
            }
            else
            {
                return DateTimeOffset.MinValue;
            }

            if (seconds <= 0)
            {
                return DateTimeOffset.MinValue;
            }

            // Anything past ~Nov 2286 in seconds is really milliseconds.
            if (seconds > 1e12)
            {
                seconds /= 1000.0;
            }

            try
            {
                return DateTimeOffset.FromUnixTimeMilliseconds((Int64)(seconds * 1000.0));
            }
            catch (ArgumentOutOfRangeException)
            {
                return DateTimeOffset.MinValue;
            }
        }
    }
}
