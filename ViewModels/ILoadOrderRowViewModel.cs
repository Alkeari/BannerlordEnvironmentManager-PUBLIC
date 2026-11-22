namespace BannerlordEnvironmentManager.ViewModels
{
    // The two things the Play tab's list can show. A module row is the existing ModuleRowViewModel;
    // everything else in this codebase that operates on "the real load order" keeps using
    // EnvironmentViewModel.Modules (module rows only, unchanged) - this interface exists purely so
    // ModuleRowTemplateSelector can tell the two apart in the one list that shows both.
    public interface ILoadOrderRowViewModel
    {
        bool IsDivider { get; }
    }
}
