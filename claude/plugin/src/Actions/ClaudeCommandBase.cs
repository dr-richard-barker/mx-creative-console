namespace Loupedeck.ClaudeConsolePlugin.Actions
{
    using System;

    using Loupedeck.ClaudeConsolePlugin.Helpers;

    /// <summary>When a key should be re-rendered.</summary>
    internal enum RedrawPolicy
    {
        /// <summary>Only when the parsed Claude state actually changes. Cheapest — no USB traffic while idle.</summary>
        OnStateChange,

        /// <summary>On every notification, including blink flips and the 1 Hz heartbeat.</summary>
        OnEveryNotification,
    }

    /// <summary>
    /// Common wiring for every Claude Console key: subscribe to the shared state service, redraw
    /// according to policy, and expose the current snapshot plus a focus helper to subclasses.
    /// </summary>
    public abstract class ClaudeCommandBase : PluginDynamicCommand
    {
        /// <summary>The action group all Claude Console keys appear under in Logi Options+.</summary>
        protected const String ClaudeGroupName = "Claude Code";

        private Int64 _lastSeenRevision = -1;

        protected ClaudeCommandBase(String displayName, String description)
            : base(displayName, description, ClaudeGroupName)
        {
        }

        /// <summary>Parameterless variant for actions that register their own parameters via AddParameter.</summary>
        protected ClaudeCommandBase()
            : base()
        {
            this.GroupName = ClaudeGroupName;
        }

        internal virtual RedrawPolicy RedrawWhen => RedrawPolicy.OnStateChange;

        protected static ClaudeStateService Service => ClaudeStateService.Instance;

        protected static ClaudeSnapshot Snapshot => ClaudeStateService.Instance.Current;

        protected override Boolean OnLoad()
        {
            // Start() is idempotent; calling it here as well as from Plugin.Load() removes any
            // dependence on the SDK's action/plugin load ordering.
            Service.Start();
            Service.StateChanged += this.OnClaudeStateChanged;
            return base.OnLoad();
        }

        protected override Boolean OnUnload()
        {
            Service.StateChanged -= this.OnClaudeStateChanged;
            return base.OnUnload();
        }

        private void OnClaudeStateChanged(Object sender, EventArgs e)
        {
            try
            {
                if (this.RedrawWhen == RedrawPolicy.OnStateChange)
                {
                    var revision = Service.StateRevision;
                    if (revision == this._lastSeenRevision)
                    {
                        return;
                    }

                    this._lastSeenRevision = revision;
                }

                // null redraws every parameter of this action.
                this.ActionImageChanged(null);
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, $"{this.GetType().Name} failed to request a redraw");
            }
        }

        /// <summary>Brings the Claude Code window forward using the current snapshot's app hints.</summary>
        protected static void FocusClaude()
        {
            var snapshot = Snapshot;
            PlatformShell.FocusClaude(snapshot.App, snapshot.BundleId);
        }

        /// <summary>Focuses Claude, waits for the window to front, then runs <paramref name="then"/>.</summary>
        protected static void FocusClaudeThen(Action then, String what)
        {
            var snapshot = Snapshot;
            PlatformShell.FocusClaudeThen(snapshot.App, snapshot.BundleId, then, what);
        }
    }
}
