using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace NutstoreAutoPatch
{
    public static class OperationRunner
    {
        public static string DefaultPath()
        {
            string directory = AppDomain.CurrentDomain.BaseDirectory;
            if (LooksLikeInstallation(directory)) return directory.TrimEnd(Path.DirectorySeparatorChar);
            var parent = Directory.GetParent(directory.TrimEnd(Path.DirectorySeparatorChar));
            return parent != null && LooksLikeInstallation(parent.FullName) ? parent.FullName : string.Empty;
        }

        private static bool LooksLikeInstallation(string directory)
        {
            return File.Exists(Path.Combine(directory, "Nutstore.exe")) ||
                File.Exists(Path.Combine(directory, "NutstoreLib.dll")) ||
                Directory.GetDirectories(directory, "bin-*").Any(d => File.Exists(Path.Combine(d, "NutstoreLib.dll")));
        }

        public static string[] Find(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) throw new InvalidOperationException("请先选择坚果云目录或 NutstoreLib.dll。");
            string path = Path.GetFullPath(input.Trim().Trim('"'));
            if (File.Exists(path))
            {
                if (!Path.GetFileName(path).Equals("NutstoreLib.dll", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("请指定 NutstoreLib.dll 或坚果云目录。");
                return new[] { path };
            }
            if (!Directory.Exists(path)) throw new DirectoryNotFoundException("目录不存在：" + path);
            string direct = Path.Combine(path, "NutstoreLib.dll");
            if (File.Exists(direct)) return new[] { direct };
            var files = Directory.GetDirectories(path, "bin-*")
                .Select(d => Path.Combine(d, "NutstoreLib.dll")).Where(File.Exists)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
            if (files.Length == 0) throw new InvalidOperationException("目录内未找到 bin-*\\NutstoreLib.dll。");
            return files;
        }

        public static bool IsTargetProcess(string executable, string[] files)
        {
            if (string.IsNullOrEmpty(executable)) return false;
            string fullPath = Path.GetFullPath(executable);
            foreach (string file in files)
            {
                string directory = Path.GetDirectoryName(Path.GetFullPath(file));
                if (fullPath.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return true;
                var parent = Directory.GetParent(directory);
                if (parent != null && fullPath.Equals(Path.Combine(parent.FullName, "Nutstore.exe"), StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        public static void PrepareProcesses(string[] files, bool closeProcesses, Action<string> log)
        {
            int ownId;
            using (var own = Process.GetCurrentProcess()) ownId = own.Id;
            foreach (var process in Process.GetProcesses())
            using (process)
            {
                string name;
                try { name = process.ProcessName; }
                catch (InvalidOperationException) { continue; }
                if (process.Id == ownId || !name.StartsWith("Nutstore", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("NutstoreDriverSvc", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("NutstoreAutoPatch", StringComparison.OrdinalIgnoreCase)) continue;
                string executable;
                try { executable = process.MainModule.FileName; }
                catch (System.ComponentModel.Win32Exception)
                {
                    throw new InvalidOperationException("无法确认进程路径，未结束此进程。请手动退出后重试：" + name);
                }
                catch (InvalidOperationException) { continue; }
                if (!IsTargetProcess(executable, files)) continue;
                if (!closeProcesses)
                    throw new InvalidOperationException("请先退出坚果云组件：" + name + " (PID " + process.Id + ")，或启用自动关闭。");
                log("正在关闭 " + name + " (PID " + process.Id + ")…");
                try
                {
                    if (process.HasExited) continue;
                    bool requested = process.CloseMainWindow();
                    if (requested && process.WaitForExit(3000)) { log("已正常关闭 " + name + "。"); continue; }
                    if (process.HasExited) continue;
                    log("组件仍在运行，正在结束 " + name + "…");
                    process.Kill();
                    if (!process.WaitForExit(3000)) throw new InvalidOperationException("进程未能结束：" + name);
                    log("已结束 " + name + "。");
                }
                catch (InvalidOperationException)
                {
                    if (!process.HasExited) throw;
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    throw new InvalidOperationException("无法关闭组件，请退出该组件或使用适当权限重试：" + name);
                }
            }
        }

        public static int Run(string action, string input, bool closeProcesses, Action<string> log)
        {
            return Run(action, input, closeProcesses, true, log);
        }

        public static int Run(string action, string input, bool closeProcesses, bool createBackup, Action<string> log)
        {
            if (log == null) throw new ArgumentNullException("log");
            if (action != "patch" && action != "restore" && action != "status")
                throw new InvalidOperationException("未知操作，请选择 status、patch 或 restore。");
            if (!createBackup && action != "patch")
                throw new InvalidOperationException("--no-backup 只适用于 patch 操作。");
            string[] files = Find(input);
            if (action != "status")
            {
                PrepareProcesses(files, closeProcesses, log);
                PrepareProcesses(files, false, log);
            }
            int failures = 0;
            foreach (string file in files)
            {
                log(file);
                try
                {
                    if (action == "patch") log(Patcher.Patch(file, createBackup)
                        ? (createBackup ? "补丁完成，原 DLL 已备份。" : "补丁完成；本次未创建备份或恢复记录。")
                        : "已是相对路径补丁，无需重复修改。");
                    else if (action == "restore") { Patcher.Restore(file); log("已恢复原 DLL；用户资料未改动。"); }
                    else log(Patcher.IsPatched(file) ? "已启用相对路径 UserData。" : "尚未启用相对路径 UserData。");
                    if (action != "restore")
                    {
                        var parent = Directory.GetParent(Path.GetDirectoryName(file));
                        if (parent != null) log("补丁后的资料位置：" + Path.Combine(parent.FullName, "UserData"));
                    }
                }
                catch (Exception error) { failures++; log("失败：" + error.Message); }
            }
            return failures == 0 ? 0 : 1;
        }
    }
}
