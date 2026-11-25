using System;
using System.IO;
using System.Reflection;

internal static class PathProbe
{
    public static int Main(string[] args)
    {
        string file = Path.GetFullPath(args[0]);
        AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs e)
        {
            string name = new AssemblyName(e.Name).Name + ".dll";
            foreach (string directory in new[] { Path.GetDirectoryName(file), args.Length > 1 ? args[1] : Path.GetDirectoryName(file) })
            {
                string candidate = Path.Combine(directory, name);
                if (File.Exists(candidate)) return Assembly.LoadFrom(candidate);
            }
            return null;
        };
        var type = Assembly.LoadFrom(file).GetType("NutstoreLib.Utils.DirectoryUtils", true);
        string actual = (string)type.GetMethod("get_APPDATA_NUTSTORE_DIR", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Invoke(null, null);
        string expected = Path.Combine(Directory.GetParent(Path.GetDirectoryName(file)).FullName, "UserData");
        Console.WriteLine(actual);
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new Exception("Relative path mismatch. Expected: " + expected);
        return 0;
    }
}
