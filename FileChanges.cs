using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace NutstoreAutoPatch
{
    public static class FileChanges
    {
        public static string HashBytes(byte[] bytes)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        private static void ValidateFeature(string feature)
        {
            if (string.IsNullOrEmpty(feature) || feature.Length > 24 ||
                feature.Any(c => !(c >= 'a' && c <= 'z') && !(c >= '0' && c <= '9') && c != '-'))
                throw new ArgumentException("无效的功能标识。", "feature");
        }

        private static string RecordPath(string path, string feature, string modifiedHash)
        {
            string key = HashBytes(Encoding.UTF8.GetBytes(feature + "\n" + Path.GetFileName(path) + "\n" + modifiedHash));
            return Path.Combine(Path.GetDirectoryName(path), ".nsopt-" + key + ".state");
        }

        private static string TemporaryPath(string path)
        {
            return Path.Combine(Path.GetDirectoryName(path), ".nsopt-" + Guid.NewGuid().ToString("N") + ".tmp");
        }

        private static bool IsHash(string value)
        {
            return value.Length == 64 && value.All(c => "0123456789abcdef".Contains(c));
        }

        private static void SaveRecord(string path, string contents)
        {
            if (File.Exists(path))
            {
                if (File.ReadAllText(path) != contents) throw new InvalidOperationException("已有恢复记录不一致，未覆盖。");
                return;
            }
            string temp = TemporaryPath(path);
            try { File.WriteAllText(temp, contents); File.Move(temp, path); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        public static bool Apply(string path, byte[] replacement, bool createBackup, string feature)
        {
            return Apply(path, replacement, createBackup, feature, null);
        }

        public static bool Apply(string path, byte[] replacement, bool createBackup, string feature, string expectedOriginalHash)
        {
            if (replacement == null) throw new ArgumentNullException("replacement");
            ValidateFeature(feature);
            path = Path.GetFullPath(path);
            using (var operationLock = new FileStream(path + ".portable.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                string originalHash = Patcher.Hash(path);
                if (expectedOriginalHash != null && originalHash != expectedOriginalHash)
                    throw new InvalidOperationException("文件在读取后已变化，停止应用。");
                string modifiedHash = HashBytes(replacement);
                if (originalHash == modifiedHash) return false;
                string temp = TemporaryPath(path);
                try
                {
                    File.WriteAllBytes(temp, replacement);
                    if (Patcher.Hash(temp) != modifiedHash) throw new InvalidOperationException("临时文件校验失败。");
                    if (createBackup)
                    {
                        string backup = path + ".original." + originalHash + ".bak";
                        if (!File.Exists(backup)) File.Copy(path, backup, false);
                        if (Patcher.Hash(backup) != originalHash) throw new InvalidOperationException("原件备份校验失败，未修改目标文件。");
                        SaveRecord(RecordPath(path, feature, modifiedHash), Path.GetFileName(backup) + "\n" + originalHash + "\n" + modifiedHash + "\n");
                    }
                    if (Patcher.Hash(path) != originalHash) throw new InvalidOperationException("文件在应用期间已变化，停止替换。");
                    File.Replace(temp, path, null);
                    return true;
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
        }

        private static string ValidatedBackup(string path, string feature, string modifiedHash)
        {
            string record = RecordPath(path, feature, modifiedHash);
            if (!File.Exists(record)) return null;
            string[] state = File.ReadAllLines(record);
            if (state.Length != 3 || !IsHash(state[1]) || state[2] != modifiedHash ||
                state[0] != Path.GetFileName(path) + ".original." + state[1] + ".bak")
                throw new InvalidOperationException("恢复记录无效。");
            string backup = Path.Combine(Path.GetDirectoryName(path), state[0]);
            if (!File.Exists(backup) || Patcher.Hash(backup) != state[1])
                throw new InvalidOperationException("原件备份缺失或校验失败。");
            return backup;
        }

        public static bool CanRestore(string path, string feature)
        {
            ValidateFeature(feature);
            path = Path.GetFullPath(path);
            return ValidatedBackup(path, feature, Patcher.Hash(path)) != null;
        }

        public static void Restore(string path, string feature)
        {
            ValidateFeature(feature);
            path = Path.GetFullPath(path);
            using (var operationLock = new FileStream(path + ".portable.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                string modifiedHash = Patcher.Hash(path);
                string backup = ValidatedBackup(path, feature, modifiedHash);
                if (backup == null) throw new InvalidOperationException("当前文件没有本功能的有效备份；可能未勾选备份或文件已更新。");
                string originalHash = Patcher.Hash(backup);
                string temp = TemporaryPath(path);
                try
                {
                    File.Copy(backup, temp, false);
                    if (Patcher.Hash(temp) != originalHash || Patcher.Hash(path) != modifiedHash)
                        throw new InvalidOperationException("恢复期间文件已变化，停止替换。");
                    File.Replace(temp, path, null);
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
        }
    }
}
