using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using Microsoft.Win32;
using NutstoreAutoPatch;

internal static class ConfigOptionTests
{
    private static int count;
    private static void Check(bool value, string label)
    {
        if (!value) throw new Exception("FAIL: " + label);
        count++;
        Console.WriteLine("PASS: " + label);
    }
    private static void Reject(Action action, string label)
    {
        bool rejected = false;
        try { action(); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, label);
    }
    private static string Config(string directory, string name, string path)
    {
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, name + ".exe.config");
        File.WriteAllText(file, "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<configuration><!--keep--><appSettings><add key=\"other\" value=\"original\" /></appSettings><log4net><appender name=\"file\"><file value=\"" + path + "\" /></appender></log4net></configuration>", new UTF8Encoding(false));
        return file;
    }
    public static int Main(string[] args)
    {
        string root = Path.GetFullPath(args[0]);
        string bin = Path.Combine(root, "logs", "bin-1.0");
        string config = Config(bin, "NutstoreClient", "${APPDATA}\\Nutstore\\logs\\client.log");
        string original = Patcher.Hash(config);
        Check(!PortableLogConfig.IsPortable(config), "original component logs are not portable");
        Check(PortableLogConfig.FindConfigs(bin).Single() == config, "only eligible component configs selected");
        Check(PortableLogConfig.Apply(config, true), "log configuration becomes portable");
        Check(PortableLogConfig.IsPortable(config), "portable log configuration recognized");
        var document = new XmlDocument();
        document.Load(config);
        Check(document.SelectSingleNode("/configuration/log4net/appender/file").Attributes["value"].Value == "..\\UserData\\logs\\client.log", "log path is relative to program bin");
        Check(document.SelectSingleNode("/configuration/appSettings/add").Attributes["value"].Value == "original" && File.ReadAllText(config).Contains("<!--keep-->"), "unrelated settings and comments preserved");
        string portableHash = Patcher.Hash(config);
        Check(!PortableLogConfig.Apply(config, true) && Patcher.Hash(config) == portableHash, "repeated log conversion leaves bytes unchanged");
        Check(PortableLogConfig.CanRestore(config), "log original has a valid restore record");
        PortableLogConfig.Restore(config);
        Check(Patcher.Hash(config) == original, "log restore reproduces exact bytes");
        string disabled = Config(Path.Combine(root, "no-backup"), "Updater", "${ALLUSERSPROFILE}\\Application Data\\Nutstore\\logs\\updater.log");
        Check(PortableLogConfig.Apply(disabled, false), "log backup can be disabled");
        Check(!Directory.GetFiles(Path.GetDirectoryName(disabled), "*.bak").Any() && !Directory.GetFiles(Path.GetDirectoryName(disabled), "*.state").Any(), "no log backup or restore record when disabled");
        Reject(delegate { PortableLogConfig.Restore(disabled); }, "log restore without backup rejected");
        string unknown = Config(Path.Combine(root, "unknown"), "Unknown", "C:\\elsewhere\\keep.log");
        string unknownHash = Patcher.Hash(unknown);
        Check(PortableLogConfig.FindConfigs(Path.GetDirectoryName(unknown)).Length == 0, "unknown log destinations excluded");
        Reject(delegate { PortableLogConfig.Apply(unknown, true); }, "unknown log destinations are not rewritten");
        Check(Patcher.Hash(unknown) == unknownHash, "unsupported config remains unchanged");
        string traversal = Config(Path.Combine(root, "traversal"), "Unsafe", "${APPDATA}\\Nutstore\\logs\\..\\escape.log");
        Reject(delegate { PortableLogConfig.Apply(traversal, true); }, "traversal log destination rejected");
        string dtd = Path.Combine(root, "dtd.exe.config");
        File.WriteAllText(dtd, "<!DOCTYPE configuration [<!ENTITY x SYSTEM 'file:///does-not-exist'>]><configuration><log4net>&x;</log4net></configuration>");
        Reject(delegate { PortableLogConfig.Apply(dtd, true); }, "external entities are rejected without expansion");
        PortableLogConfig.Apply(config, true);
        File.AppendAllText(config, "<!--external-change-->");
        string changed = Patcher.Hash(config);
        Reject(delegate { PortableLogConfig.Restore(config); }, "externally changed config cannot restore stale bytes");
        Check(Patcher.Hash(config) == changed, "failed config restore keeps external changes");

        string keyPath = @"Software\NutstoreAutoPatch.Tests\" + Guid.NewGuid().ToString("N");
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(keyPath))
            {
                string install = Path.Combine(root, "startup");
                Directory.CreateDirectory(install);
                string command = "\"" + Path.Combine(install, "Nutstore.exe") + "\" --autostart";
                Check(StartupSetting.GetStatus(install, key) == StartupStatus.Disabled, "missing Run value is disabled");
                Check(!Directory.Exists(Path.Combine(install, ".nutstore-autopatch")), "status creates no metadata");
                string previousDirectory = Environment.CurrentDirectory;
                try
                {
                    Environment.CurrentDirectory = install;
                    string[] relativeCommands = { "Nutstore.exe", @".\Nutstore.exe", Path.GetPathRoot(install).Substring(0, 2) + "Nutstore.exe", Path.Combine(install, "Nutstore.exe").Substring(2) };
                    foreach (string executable in relativeCommands)
                    {
                        string relativeCommand = "\"" + executable + "\" --autostart";
                        key.SetValue("Nutstore", relativeCommand, RegistryValueKind.String);
                        Check(StartupSetting.GetStatus(install, key) == StartupStatus.OtherInstallation, "relative startup executable is not owned: " + executable);
                        Reject(delegate { StartupSetting.Disable(install, false, key); }, "relative startup value is not deleted");
                        Check((string)key.GetValue("Nutstore") == relativeCommand, "relative startup remains exact");
                    }
                }
                finally { Environment.CurrentDirectory = previousDirectory; }
                string quotedSuffix = "\"" + Path.Combine(install, "Nutstore.exe") + "\"suffix.exe --autostart";
                key.SetValue("Nutstore", quotedSuffix, RegistryValueKind.String);
                Check(StartupSetting.GetStatus(install, key) == StartupStatus.OtherInstallation, "text appended after the executable quote is not owned");
                Reject(delegate { StartupSetting.Disable(install, false, key); }, "quoted executable suffix is not deleted");
                Check((string)key.GetValue("Nutstore") == quotedSuffix, "quoted executable suffix remains exact");
                key.SetValue("Nutstore", command, RegistryValueKind.String);
                key.SetValue("OtherApp", "keep", RegistryValueKind.String);
                Check(StartupSetting.GetStatus(install, key) == StartupStatus.Enabled, "target installation startup recognized");
                Check(StartupSetting.Disable(install, true, key), "target startup disabled");
                Check(key.GetValue("Nutstore") == null && (string)key.GetValue("OtherApp") == "keep", "only target Run value removed");
                Check(!StartupSetting.Disable(install, true, key), "repeated startup disable is idempotent");
                Check(StartupSetting.CanRestore(install), "startup backup can restore");
                StartupSetting.Restore(install, key);
                Check((string)key.GetValue("Nutstore") == command && key.GetValueKind("Nutstore") == RegistryValueKind.String, "startup command and value type restored");
                key.SetValue("Nutstore", command + " --changed", RegistryValueKind.String);
                Reject(delegate { StartupSetting.ValidateRestore(install, key); }, "restore preflight rejects externally changed arguments");
                Reject(delegate { StartupSetting.Restore(install, key); }, "restore preserves same-installation command changes");
                string foreign = "\"" + Path.Combine(root, "other-install", "Nutstore.exe") + "\" --autostart";
                key.SetValue("Nutstore", foreign, RegistryValueKind.String);
                Check(StartupSetting.GetStatus(install, key) == StartupStatus.OtherInstallation, "another installation recognized");
                Reject(delegate { StartupSetting.Disable(install, true, key); }, "another installation startup is not removed");
                Reject(delegate { StartupSetting.Restore(install, key); }, "restore never overwrites another installation");
                Check((string)key.GetValue("Nutstore") == foreign, "foreign startup remains exact");
                string without = Path.Combine(root, "startup-no-backup");
                Directory.CreateDirectory(without);
                key.SetValue("Nutstore", "\"" + Path.Combine(without, "Nutstore.exe") + "\" --autostart", RegistryValueKind.String);
                Check(StartupSetting.Disable(without, false, key), "startup disable can skip backup");
                Check(!Directory.Exists(Path.Combine(without, ".nutstore-autopatch")), "no startup metadata when backup disabled");
                Reject(delegate { StartupSetting.Restore(without, key); }, "startup restore without backup rejected");
                key.SetValue("Nutstore", 42, RegistryValueKind.DWord);
                Reject(delegate { StartupSetting.Disable(install, false, key); }, "non-string startup is not deleted");
                string expanded = Path.Combine(root, "startup-expanded");
                Directory.CreateDirectory(expanded);
                Environment.SetEnvironmentVariable("NUTSTORE_OPTION_TEST_ROOT", expanded, EnvironmentVariableTarget.Process);
                try
                {
                    string expandedCommand = "\"%NUTSTORE_OPTION_TEST_ROOT%\\Nutstore.exe\" --autostart";
                    key.SetValue("Nutstore", expandedCommand, RegistryValueKind.ExpandString);
                    Check(StartupSetting.GetStatus(expanded, key) == StartupStatus.Enabled, "expanded startup executable is matched to target installation");
                    StartupSetting.Disable(expanded, true, key);
                    StartupSetting.Restore(expanded, key);
                    Check((string)key.GetValue("Nutstore", null, RegistryValueOptions.DoNotExpandEnvironmentNames) == expandedCommand && key.GetValueKind("Nutstore") == RegistryValueKind.ExpandString, "expanded startup command restores raw bytes and value type");
                    StartupSetting.Disable(expanded, true, key);
                    File.AppendAllText(Path.Combine(expanded, ".nutstore-autopatch", "startup.xml"), "broken");
                    Reject(delegate { StartupSetting.Restore(expanded, key); }, "corrupt startup backup is rejected");
                    Check(key.GetValue("Nutstore") == null, "corrupt backup does not recreate startup value");
                }
                finally { Environment.SetEnvironmentVariable("NUTSTORE_OPTION_TEST_ROOT", null, EnvironmentVariableTarget.Process); }
            }
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(keyPath, false); }
        Check(Registry.CurrentUser.OpenSubKey(keyPath) == null, "isolated registry fixture cleaned");
        Console.WriteLine("CONFIG_TOTAL: " + count);
        return 0;
    }
}
