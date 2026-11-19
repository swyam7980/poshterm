namespace PoshTerm;

// Stand-in for the WPF App class, which the UI-free sources linked into this test runner log through.
internal static class App
{
    public static void LogError(Exception e) { }
}
