using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using Microsoft.Win32;

namespace NutstoreAutoPatch
{
    public enum StartupStatus { Disabled, Enabled, OtherInstallation }

    public static class StartupSetting
    {
        private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "Nutstore";

        private static string Root(string root)
        {
            string full = Path.GetFullPath(root);
            if (!Directory.Exists(full)) throw new DirectoryNotFoundException("坚果云安装目录不存在。");
            return full.Length == Path.GetPathRoot(full).Length ? full : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static string Record(string root) { return Path.Combine(Root(root), ".nutstore-autopatch", "startup.xml"); }
        private static object Current(RegistryKey key)
        {
            return key == null ? null : key.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        }
        private static bool AbsoluteExecutable(string path)
        {
            string normalized = path.Replace('/', '\\');
            if (normalized.Length >= 3 && ((normalized[0] >= 'A' && normalized[0] <= 'Z') || (normalized[0] >= 'a' && normalized[0] <= 'z')) &&
                normalized[1] == ':' && normalized[2] == '\\') return true;
            if (!normalized.StartsWith(@"\\", StringComparison.Ordinal)) return false;
            string[] components = normalized.Substring(2).Split('\\');
            return components.Length >= 3 && components[0].Length != 0 && components[1].Length != 0 &&
                components[0] != "." && components[0] != ".." && components[0] != "?" &&
                components[1] != "." && components[1] != "..";
        }
        private static bool Owned(string root, object value)
        {
            var command = value as string;
            if (string.IsNullOrWhiteSpace(command)) return false;
            command = Environment.ExpandEnvironmentVariables(command).Trim();
            string executable;
            if (command.StartsWith("\"", StringComparison.Ordinal))
            {
                int end = command.IndexOf('"', 1);
                if (end < 0) return false;
                if (end + 1 < command.Length && !char.IsWhiteSpace(command[end + 1])) return false;
                executable = command.Substring(1, end - 1);
            }
            else
            {
                int end = command.IndexOfAny(new[] { ' ', '\t', '\r', '\n' });
                executable = end < 0 ? command : command.Substring(0, end);
            }
            if (!AbsoluteExecutable(executable)) return false;
            try { return Path.GetFullPath(executable).Equals(Path.Combine(Root(root), "Nutstore.exe"), StringComparison.OrdinalIgnoreCase); }
            catch (ArgumentException) { return false; }
            catch (NotSupportedException) { return false; }
        }

        public static StartupStatus GetStatus(string root)
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunPath, false)) return GetStatus(root, key);
        }
        public static StartupStatus GetStatus(string root, RegistryKey key)
        {
            Root(root);
            object value = Current(key);
            return value == null ? StartupStatus.Disabled : Owned(root, value) ? StartupStatus.Enabled : StartupStatus.OtherInstallation;
        }
        public static bool IsDisabled(string root) { return GetStatus(root) == StartupStatus.Disabled; }
        public static void Validate(string root)
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunPath, false)) Validate(root, key);
        }
        public static void Validate(string root, RegistryKey key)
        {
            if (GetStatus(root, key) == StartupStatus.OtherInstallation)
                throw new InvalidOperationException("Nutstore 开机启动项属于其他安装或无法识别，未修改。");
        }

        private static string Digest(string command, RegistryValueKind kind)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(((int)kind).ToString(CultureInfo.InvariantCulture) + "\n" + command))).Replace("-", "").ToLowerInvariant();
        }
        private static void Save(string root, string command, RegistryValueKind kind)
        {
            string record = Record(root);
            if (File.Exists(record))
            {
                RegistryValueKind previousKind;
                if (Read(root, out previousKind) != command || previousKind != kind)
                    throw new InvalidOperationException("已有不同的开机启动备份，未覆盖原备份或修改启动项。");
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(record));
            string temp = Path.Combine(Path.GetDirectoryName(record), ".startup-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var writer = XmlWriter.Create(temp, new XmlWriterSettings { Encoding = new UTF8Encoding(false) }))
                {
                    writer.WriteStartElement("startup");
                    writer.WriteAttributeString("root", Root(root));
                    writer.WriteAttributeString("kind", ((int)kind).ToString(CultureInfo.InvariantCulture));
                    writer.WriteAttributeString("hash", Digest(command, kind));
                    writer.WriteString(Convert.ToBase64String(Encoding.UTF8.GetBytes(command)));
                    writer.WriteEndElement();
                }
                File.Move(temp, record);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        private static string Read(string root, out RegistryValueKind kind)
        {
            if (!File.Exists(Record(root))) throw new InvalidOperationException("没有开机启动备份，无法自动恢复。");
            try
            {
                var document = new XmlDocument { XmlResolver = null };
                using (var reader = XmlReader.Create(Record(root), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null })) document.Load(reader);
                var node = document.DocumentElement;
                int number;
                if (node == null || node.Name != "startup" || !Root(root).Equals(node.GetAttribute("root"), StringComparison.OrdinalIgnoreCase) ||
                    !int.TryParse(node.GetAttribute("kind"), NumberStyles.None, CultureInfo.InvariantCulture, out number) || (number != 1 && number != 2))
                    throw new InvalidOperationException("开机启动恢复记录无效。");
                kind = (RegistryValueKind)number;
                string command = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(node.InnerText));
                if (!Owned(root, command) || node.GetAttribute("hash") != Digest(command, kind))
                    throw new InvalidOperationException("开机启动备份内容不匹配，拒绝恢复。");
                return command;
            }
            catch (XmlException) { throw new InvalidOperationException("开机启动恢复记录不是受支持的安全 XML。"); }
            catch (FormatException) { throw new InvalidOperationException("开机启动恢复记录无效。"); }
            catch (DecoderFallbackException) { throw new InvalidOperationException("开机启动恢复记录编码无效。"); }
        }

        public static bool CanRestore(string root)
        {
            if (!File.Exists(Record(root))) return false;
            RegistryValueKind kind;
            Read(root, out kind);
            return true;
        }
        public static bool Disable(string root, bool createBackup)
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunPath, true)) return Disable(root, createBackup, key);
        }
        public static bool Disable(string root, bool createBackup, RegistryKey key)
        {
            Validate(root, key);
            object value = Current(key);
            if (value == null) return false;
            string command = (string)value;
            if (!Owned(root, command)) throw new InvalidOperationException("开机启动项已变化，未修改。");
            RegistryValueKind kind = key.GetValueKind(ValueName);
            if (kind != RegistryValueKind.String && kind != RegistryValueKind.ExpandString)
                throw new InvalidOperationException("开机启动值的类型不受支持。");
            if (createBackup) Save(root, command, kind);
            if (!object.Equals(Current(key), command) || key.GetValueKind(ValueName) != kind)
                throw new InvalidOperationException("开机启动项已变化，未删除。");
            key.DeleteValue(ValueName, false);
            return true;
        }
        public static void Restore(string root)
        {
            RegistryValueKind kind;
            string command = Read(root, out kind);
            using (var key = Registry.CurrentUser.CreateSubKey(RunPath)) RestoreValue(root, key, command, kind);
        }
        public static void Restore(string root, RegistryKey key)
        {
            RegistryValueKind kind;
            string command = Read(root, out kind);
            RestoreValue(root, key, command, kind);
        }
        public static void ValidateRestore(string root)
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunPath, false)) ValidateRestore(root, key);
        }
        public static void ValidateRestore(string root, RegistryKey key)
        {
            RegistryValueKind kind;
            string command = Read(root, out kind);
            EnsureRestorable(key, command, kind);
        }
        private static void EnsureRestorable(RegistryKey key, string command, RegistryValueKind kind)
        {
            object current = Current(key);
            if (current != null && (!object.Equals(current, command) || key.GetValueKind(ValueName) != kind))
                throw new InvalidOperationException("开机启动项已有新内容，未覆盖。");
        }
        private static void RestoreValue(string root, RegistryKey key, string command, RegistryValueKind kind)
        {
            EnsureRestorable(key, command, kind);
            object current = Current(key);
            if (current == null) key.SetValue(ValueName, command, kind);
        }
    }
}
