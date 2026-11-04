namespace JacintoLauncher;

// The tests run without the interface, so text stays English.
internal static class L
{
    internal static string T(string english) => english;
    internal static string F(string english, params object[] args) => string.Format(System.Globalization.CultureInfo.InvariantCulture, english, args);
}
