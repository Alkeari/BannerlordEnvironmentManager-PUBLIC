using BannerlordEnvironmentManager.Core.Diagnostics;
using BannerlordEnvironmentManager.Views;
using Microsoft.UI.Xaml.Controls;

namespace BannerlordEnvironmentManager.ViewModels
{
    // Accept the risk for everything that is not a FindingRow: a status bar, a banner, an amber line.
    // The notice's own words are its identity, the same way a finding's headline is, so the same
    // warning stays hidden and a different one is shown. Records go to the active version's
    // accepted-findings.json, which Settings lists to forgive, so nothing accepted here is out of reach.
    internal static class AcceptedNotices
    {
        private static AcceptedFindingStore Store =>
            new(AcceptedFindingStore.DefaultPath(ShellViewModels.Instance.Environment.ActiveDataRoot));

        public static string Identity(string title, string message) =>
            title.Length == 0 ? message : message.Length == 0 ? title : title + ": " + message;

        public static bool IsWarning(InfoBarSeverity severity) =>
            severity is InfoBarSeverity.Warning or InfoBarSeverity.Error;

        public static bool IsAccepted(string category, string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            try
            {
                return Store.LoadKeys().Contains(AcceptedFindingStore.KeyFor(category, text));
            }
            catch (Exception ex) when (ex is InvalidOperationException or NullReferenceException)
            {
                // Before the shell is up there is no active version to read from.
                return false;
            }
        }

        public static bool IsAccepted(string category, InfoBarSeverity severity, string title, string message) =>
            IsWarning(severity) && IsAccepted(category, Identity(title, message));

        public static string Unless(string category, string text) => IsAccepted(category, text) ? string.Empty : text;

        public static string Accept(string category, string text)
        {
            var record = Store.Accept([new AcceptedFinding(category, text, DateTimeOffset.UtcNow)]);
            ShellViewModels.Instance.Diagnostics.RefreshAcceptedFindingRows();
            return record.Describe();
        }
    }
}
