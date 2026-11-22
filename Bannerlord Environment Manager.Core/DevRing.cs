namespace BannerlordEnvironmentManager.Core;

// Which of the three concentric tiers this assembly was compiled for.
//
// BEM < BEM with Advanced Mode < Dev-BEM. The first two are one product and one build: Advanced Mode
// is a runtime setting that deepens what a power user sees. The third ring is not a setting at all,
// because a setting ships the code and someone finds it: it is the DEV_BEM constant, which only
// -p:DevBem=true puts into a compilation, and everything inside it is absent from the exe that
// reaches Nexus.
//
// Read this rather than testing the constant directly anywhere a test needs to know which tier it is
// looking at. Code that is itself in the outer ring uses #if DEV_BEM, not this property: a compile
// time constant read through a property still compiles the gated code into the assembly.
public static class DevRing
{
    public static bool IsCompiledIn =>
#if DEV_BEM
        true;
#else
        false;
#endif
}
