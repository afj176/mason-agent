namespace Mason;

/// <summary>Model-runtime code reads its data folder from Store; checks use a throwaway folder instead of the desktop store.</summary>
public static class Store
{
    public static string Root => Environment.GetEnvironmentVariable("MASON_DATA_HOME") ?? Path.Combine(Path.GetTempPath(), "mason-checks");
}
