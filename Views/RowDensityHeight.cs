namespace BannerlordEnvironmentManager.Views
{
    // The three densities the Settings selector offers, as the row heights they mean. A static
    // function keeps the mapping in one place for the x:Bind in the module row template.
    public static class RowDensityHeight
    {
        public static double For(RowDensity density) => density switch
        {
            RowDensity.Compact => 32,
            RowDensity.Comfortable => 48,
            _ => 40,
        };
    }
}
