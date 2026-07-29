namespace Loupedeck.ClaudeConsolePlugin
{
    using System;

    /// <summary>
    /// Placeholder client application. The plugin is universal (<c>HasNoApplication == true</c>),
    /// so nothing is matched here; the SDK still expects a ClientApplication type to be present.
    /// </summary>
    public class ClaudeConsolePluginApplication : ClientApplication
    {
        protected override String GetProcessName() => String.Empty;

        protected override String GetBundleName() => String.Empty;

        public override ClientApplicationStatus GetApplicationStatus() => ClientApplicationStatus.Unknown;
    }
}
