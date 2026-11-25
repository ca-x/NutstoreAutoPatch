using System;
using System.Collections.Generic;
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
            return Run(action, input, closeProcesses, createBackup, new PortableOptions(), log);
        }

        public static int Run(string action, string input, bool closeProcesses, bool createBackup, PortableOptions options, Action<string> log)
        {
            if (log == null) throw new ArgumentNullException("log");
            if (options == null) throw new ArgumentNullException("options");
            if (action != "patch" && action != "restore" && action != "status")
                throw new InvalidOperationException("未知操作，请选择 status、patch 或 restore。");
            if (!createBackup && action != "patch")
                throw new InvalidOperationException("--no-backup 只适用于 patch 操作。");
            if (action != "status" && !options.HasSelection)
                throw new InvalidOperationException("请至少勾选一项要应用或恢复的功能。");
            string[] files = Find(input);
            if (action == "status") return ShowStatus(files, log);
            var operations = Plan(files, action == "restore", createBackup, options);
            if (options.ApplyUserData || options.DisableBackgroundUpdates || options.PortableLogs)
            {
                PrepareProcesses(files, closeProcesses, log);
                PrepareProcesses(files, false, log);
            }
            int failures = 0;
            foreach (var operation in operations)
            {
                log(operation.Name + "：" + operation.Target);
                try
                {
                    if (action == "restore")
                    {
                        operation.Restore();
                        log("已恢复此项；用户资料未移动或合并。");
                    }
                    else if (!operation.Apply()) log("此项已是目标状态，无需重复修改。");
                    else log(createBackup ? "应用完成，修改前状态已备份。" : "应用完成；本次未创建备份或恢复记录。");
                }
                catch (Exception error) { failures++; log("失败：" + error.Message); }
            }
            if (failures != 0) log("部分项目未完成；其他项目可能已成功，请根据以上记录检查。");
            return failures == 0 ? 0 : 1;
        }

        private static int ShowStatus(string[] files, Action<string> log)
        {
            int failures = 0;
            foreach (string file in files)
            {
                string bin = Path.GetDirectoryName(file);
                log(file);
                try { log(Patcher.IsPatched(file) ? "资料目录：已启用相对路径 UserData。" : "资料目录：尚未启用相对路径 UserData。"); }
                catch (Exception error) { failures++; log("资料目录不支持：" + error.Message); }
                log("补丁后的资料位置：" + Path.Combine(Root(file), "UserData"));
                string legacy = Path.Combine(bin, "NutstoreClient.Legacy.dll");
                if (!File.Exists(legacy)) log("后台更新检查：不支持，未找到对应组件。");
                else ShowFeature("后台更新检查", () => UpdatePatcher.IsDisabled(legacy) ? "已关闭" : "保持程序原状", log);
                ShowFeature("组件日志", () => {
                    var configs = PortableLogConfig.FindConfigs(bin);
                    if (configs.Length == 0) return "未发现可识别的组件日志配置";
                    int portable = configs.Count(PortableLogConfig.IsPortable);
                    return portable + "/" + configs.Length + " 项已使用相对 UserData\\logs";
                }, log);
            }
            foreach (string root in files.Select(Root).Distinct(StringComparer.OrdinalIgnoreCase))
                ShowFeature("开机启动（" + root + "）", () => {
                    var status = StartupSetting.GetStatus(root);
                    return status == StartupStatus.Disabled ? "未启用" : status == StartupStatus.Enabled ? "已启用" : "启动项属于其他安装，保持不变";
                }, log);
            return failures == 0 ? 0 : 1;
        }

        private static void ShowFeature(string label, Func<string> status, Action<string> log)
        {
            try { log(label + "：" + status() + "。"); }
            catch (Exception error) { log(label + "：不支持或无法确认，" + error.Message); }
        }

        private static string Root(string file)
        {
            return Directory.GetParent(Path.GetDirectoryName(file)).FullName;
        }

        private static List<PortableOperation> Plan(string[] files, bool restoring, bool backup, PortableOptions options)
        {
            var result = new List<PortableOperation>();
            foreach (string file in files)
            {
                if (options.ApplyUserData)
                {
                    if (restoring)
                    {
                        if (!Patcher.CanRestore(file)) throw new InvalidOperationException("资料目录没有与当前 DLL 匹配的恢复记录：" + file);
                    }
                    else Patcher.Validate(file);
                    result.Add(new PortableOperation("资料相对路径", file, () => Patcher.Patch(file, backup), () => Patcher.Restore(file)));
                }
                string bin = Path.GetDirectoryName(file);
                if (options.DisableBackgroundUpdates)
                {
                    string legacy = Path.Combine(bin, "NutstoreClient.Legacy.dll");
                    if (!File.Exists(legacy)) throw new InvalidOperationException("此版本不支持后台检查选项，未找到：" + legacy);
                    UpdatePatcher.IsDisabled(legacy);
                    if (restoring && !UpdatePatcher.CanRestore(legacy)) throw new InvalidOperationException("后台更新检查没有可用的恢复记录：" + legacy);
                    result.Add(new PortableOperation("关闭后台更新检查", legacy, () => UpdatePatcher.Disable(legacy, backup), () => UpdatePatcher.Restore(legacy)));
                }
                if (options.PortableLogs)
                {
                    var configs = PortableLogConfig.FindConfigs(bin);
                    if (configs.Length == 0) throw new InvalidOperationException("未找到支持的组件日志配置：" + bin);
                    foreach (string config in configs)
                    {
                        PortableLogConfig.Validate(config);
                        if (restoring && !PortableLogConfig.CanRestore(config)) throw new InvalidOperationException("组件日志没有匹配当前文件的恢复记录：" + config);
                        result.Add(new PortableOperation("组件日志便携化", config, () => PortableLogConfig.Apply(config, backup), () => PortableLogConfig.Restore(config)));
                    }
                }
            }
            if (options.DisableStartup)
                foreach (string root in files.Select(Root).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (restoring) StartupSetting.ValidateRestore(root);
                    else StartupSetting.Validate(root);
                    result.Add(new PortableOperation("关闭开机启动", root, () => StartupSetting.Disable(root, backup), () => StartupSetting.Restore(root)));
                }
            return result;
        }
    }
}
