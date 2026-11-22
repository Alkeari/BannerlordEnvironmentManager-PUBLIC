namespace BannerlordEnvironmentManager.Services
{
    // Every route that writes a mod id (Find Mod IDs, an install, a row's own Set or Forget) says so
    // here, and Play and Library redraw from it. Before this, an id saved anywhere but Play's own
    // update check left both lists showing "no id" until the owner pressed Check for Updates.
    internal static class NexusIdsChanged
    {
        public static event EventHandler? Raised;

        // Always delivered on the UI thread, because every listener redraws rows.
        public static void Raise(object? source)
        {
            var dispatcher = App.AppWindow?.DispatcherQueue;

            if (dispatcher is null || dispatcher.HasThreadAccess)
                Raised?.Invoke(source, EventArgs.Empty);
            else
                dispatcher.TryEnqueue(() => Raised?.Invoke(source, EventArgs.Empty));
        }
    }
}
