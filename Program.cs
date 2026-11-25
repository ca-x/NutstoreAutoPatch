using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace NutstoreAutoPatch
{
    internal static class Program
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(uint processId);
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetStdHandle(int handle);
        [DllImport("kernel32.dll")]
        private static extern uint GetFileType(IntPtr handle);

        private static void ConnectConsole()
        {
            IntPtr output = GetStdHandle(-11);
            if (output == IntPtr.Zero || output == new IntPtr(-1) || GetFileType(output) == 0)
                AttachConsole(uint.MaxValue);
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true });
        }

        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Length == 0)
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
                return 0;
            }
            ConnectConsole();
            try
            {
                if (args.Length < 2)
                    throw new InvalidOperationException("用法：NutstoreAutoPatch.exe status|patch|restore \"坚果云目录或 DLL 路径\" [--close] [--no-backup] [--no-userdata] [--disable-background-updates] [--portable-logs] [--disable-startup]");
                bool closeProcesses = false;
                bool createBackup = true;
                var options = new PortableOptions();
                var flags = new HashSet<string>(StringComparer.Ordinal);
                var positional = new List<string>();
                foreach (string argument in args)
                {
                    if (!argument.StartsWith("--", StringComparison.Ordinal)) { positional.Add(argument); continue; }
                    if (!flags.Add(argument)) throw new InvalidOperationException("重复的选项：" + argument);
                    switch (argument)
                    {
                        case "--close": closeProcesses = true; break;
                        case "--no-backup": createBackup = false; break;
                        case "--no-userdata": options.ApplyUserData = false; break;
                        case "--disable-background-updates": options.DisableBackgroundUpdates = true; break;
                        case "--portable-logs": options.PortableLogs = true; break;
                        case "--disable-startup": options.DisableStartup = true; break;
                        default: throw new InvalidOperationException("未知的选项：" + argument);
                    }
                }
                if (positional.Count != 2) throw new InvalidOperationException("请指定操作和坚果云目录或 DLL 路径。");
                return OperationRunner.Run(positional[0].ToLowerInvariant(), positional[1], closeProcesses, createBackup, options, Console.WriteLine);
            }
            catch (Exception error) { Console.Error.WriteLine("失败：" + error.Message); return 1; }
        }
    }
}
