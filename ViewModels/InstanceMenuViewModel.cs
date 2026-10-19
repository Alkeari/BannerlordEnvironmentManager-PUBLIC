using BannerlordEnvironmentManager.Core.Instances;
using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.ViewModels
{
    // What the Versions row menu offers for whichever row is selected. Core decides which items can
    // run; this only says so, because an item that is off without a reason is a control the user
    // cannot act on. Rebuilt whole on every selection change rather than made observable per item,
    // so the page binds one object and every item follows it.
    public sealed class InstanceMenuViewModel(InstanceMenuState state)
    {
        public static InstanceMenuViewModel Nothing { get; } = new(InstanceMenu.Nothing);

        public bool CanRename => state.Rename == InstanceActionState.Allowed;

#if DEV_BEM
        public bool CanSetPurpose => state.SetPurpose == InstanceActionState.Allowed;
#endif

        public bool CanOpenFolder => state.OpenFolder == InstanceActionState.Allowed;

        public bool CanCopyPath => state.CopyPath == InstanceActionState.Allowed;

        public bool CanSetActive => state.SetActive == InstanceActionState.Allowed;

        public bool CanSetResting => state.SetResting == InstanceActionState.Allowed;

        public bool CanRemove => state.Remove == InstanceActionState.Allowed;

        public bool CanShowDetails => state.ShowDetails == InstanceActionState.Allowed;

        public bool CanInstallAnotherCopy => state.InstallAnotherCopy == InstanceActionState.Allowed;

        public string RenameTooltip => Tooltip(state.Rename, "Versions.Menu.Rename.Tooltip");

#if DEV_BEM
        // Three items, one enablement: what stops any of them stops all three, so the reason is
        // read off the same state and only the sentence describing the item itself differs.
        public string PurposeTestingTooltip => Tooltip(state.SetPurpose, "Versions.Menu.Purpose.Testing.Tooltip");

        public string PurposePlayingTooltip => Tooltip(state.SetPurpose, "Versions.Menu.Purpose.Playing.Tooltip");

        public string PurposeUndeclaredTooltip => Tooltip(state.SetPurpose, "Versions.Menu.Purpose.Undeclared.Tooltip");
#endif

        public string OpenFolderTooltip => Tooltip(state.OpenFolder, "Versions.Menu.OpenFolder.Tooltip");

        public string CopyPathTooltip => Tooltip(state.CopyPath, "Versions.Menu.CopyPath.Tooltip");

        public string SetActiveTooltip => Tooltip(state.SetActive, "Versions.Menu.SetActive.Tooltip");

        public string SetRestingTooltip => Tooltip(state.SetResting, "Versions.SetRestingButton.Tooltip");

        public string RemoveTooltip => Tooltip(state.Remove, "Versions.RemoveButton.Tooltip");

        public string ShowDetailsTooltip => Tooltip(state.ShowDetails, "Versions.Menu.ShowDetails.Tooltip");

        public string InstallAnotherCopyTooltip =>
            Tooltip(state.InstallAnotherCopy, "Versions.Menu.InstallAnotherCopy.Tooltip");

        // An item that can run says what it does; one that cannot says why, in the same place, so
        // there is one thing to read either way.
        private static string Tooltip(InstanceActionState actionState, string allowedKey) => actionState switch
        {
            InstanceActionState.Allowed => Strings.Current[allowedKey],
            InstanceActionState.NoInstance => Strings.Current["Versions.Menu.Blocked.NoInstance"],
            InstanceActionState.NoPath => Strings.Current["Versions.Menu.Blocked.NoPath"],
            InstanceActionState.FolderMissing => Strings.Current["Versions.Menu.Blocked.FolderMissing"],
            InstanceActionState.AlreadyActive => Strings.Current["Versions.Menu.Blocked.AlreadyActive"],
            InstanceActionState.AlreadyResting => Strings.Current["Versions.Menu.Blocked.AlreadyResting"],
            InstanceActionState.IsResting => Strings.Current["Versions.Menu.Blocked.IsResting"],
            InstanceActionState.NotOffered => Strings.Current["Versions.Menu.Blocked.NotOffered"],
            InstanceActionState.CatalogNotRead => Strings.Current["Versions.Menu.Blocked.CatalogNotRead"],
            InstanceActionState.RestingIsForPlaying => Strings.Current["Versions.Menu.Blocked.RestingIsForPlaying"],
            _ => Strings.Current["Versions.Menu.Blocked.NoInstance"]
        };
    }
}
