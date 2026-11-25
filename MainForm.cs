using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace NutstoreAutoPatch
{
    public sealed class MainForm : Form
    {
        private readonly TextBox installPathBox = new TextBox { Name = "TargetPath", Dock = DockStyle.Fill };
        private readonly TextBox logBox = new TextBox { Name = "LogOutput", Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.White };
        private readonly CheckBox autoCloseCheckBox = new CheckBox { Name = "AutoCloseProcesses", Text = "操作前自动关闭坚果云相关进程", Checked = true, AutoSize = true };
        private readonly CheckBox createBackupCheckBox = new CheckBox { Name = "CreateBackup", Text = "备份原 DLL（用于恢复）", Checked = true, AutoSize = true };
        private readonly Button statusButton = new Button { Name = "CheckButton", Text = "检查状态", AutoSize = true, Padding = new Padding(14, 7, 14, 7) };
        private readonly Button patchButton = new Button { Name = "PatchButton", Text = "应用补丁", AutoSize = true, Padding = new Padding(14, 7, 14, 7) };
        private readonly Button restoreButton = new Button { Name = "RestoreButton", Text = "恢复原程序", AutoSize = true, Padding = new Padding(14, 7, 14, 7) };
        private readonly Label stateLabel = new Label { Name = "stateLabel", Text = "就绪", AutoSize = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        private readonly ProgressBar progressBar = new ProgressBar { Name = "progressBar", Style = ProgressBarStyle.Marquee, Dock = DockStyle.Fill, Visible = false };
        private readonly TableLayoutPanel inputPanel;
        private bool busy;

        public MainForm()
        {
            Name = "mainForm";
            Text = "坚果云相对路径补丁 · NutstoreAutoPatch";
            Font = new Font("Microsoft YaHei UI", 10F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(830, 570);
            MinimumSize = new Size(720, 560);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(247, 249, 252);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 8 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int row = 0; row < 8; row++) layout.RowStyles.Add(new RowStyle(row == 6 ? SizeType.Percent : SizeType.AutoSize, row == 6 ? 100 : 0));
            Controls.Add(layout);
            layout.Controls.Add(new Label { Text = "让资料跟随程序目录", AutoSize = true, Font = new Font(Font.FontFamily, 19F, FontStyle.Bold), Margin = new Padding(0, 0, 0, 8) }, 0, 0);
            layout.Controls.Add(new Label { Text = "选择坚果云目录，将用户资料位置设置为程序目录旁的 UserData。", AutoSize = true, Margin = new Padding(0, 0, 0, 22), ForeColor = Color.FromArgb(80, 91, 110) }, 0, 1);

            inputPanel = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, RowCount = 2, Margin = new Padding(0, 0, 0, 14) };
            inputPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            inputPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            inputPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var pathLabel = new Label { Text = "坚果云目录或 NutstoreLib.dll", AutoSize = true, Margin = new Padding(0, 0, 0, 6) };
            inputPanel.Controls.Add(pathLabel, 0, 0);
            inputPanel.SetColumnSpan(pathLabel, 3);
            installPathBox.Margin = new Padding(0, 4, 8, 0);
            installPathBox.AccessibleName = "坚果云目录或 DLL 路径";
            inputPanel.Controls.Add(installPathBox, 0, 1);
            var folderButton = new Button { Name = "BrowseFolder", Text = "选择目录", AutoSize = true, Margin = new Padding(0, 0, 8, 0), Padding = new Padding(6, 3, 6, 3) };
            var dllButton = new Button { Name = "BrowseDll", Text = "选择 DLL", AutoSize = true, Margin = new Padding(0), Padding = new Padding(6, 3, 6, 3) };
            folderButton.Click += delegate { BrowseFolder(); };
            dllButton.Click += delegate { BrowseDll(); };
            inputPanel.Controls.Add(folderButton, 1, 1);
            inputPanel.Controls.Add(dllButton, 2, 1);
            layout.Controls.Add(inputPanel, 0, 2);

            var optionsPanel = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0, 0, 0, 14), WrapContents = false, FlowDirection = FlowDirection.TopDown };
            optionsPanel.Controls.Add(autoCloseCheckBox);
            optionsPanel.Controls.Add(createBackupCheckBox);
            var backupHint = new Label { Name = "BackupHint", Text = "不备份将无法依靠本次操作自动恢复。", AutoSize = true, Visible = false, ForeColor = Color.FromArgb(130, 84, 30), Margin = new Padding(3, 3, 0, 0) };
            createBackupCheckBox.CheckedChanged += delegate { backupHint.Visible = !createBackupCheckBox.Checked; };
            optionsPanel.Controls.Add(backupHint);
            layout.Controls.Add(optionsPanel, 0, 3);
            var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0, 0, 0, 16), WrapContents = false };
            patchButton.BackColor = Color.FromArgb(36, 92, 201);
            patchButton.ForeColor = Color.White;
            patchButton.FlatStyle = FlatStyle.Flat;
            patchButton.FlatAppearance.BorderSize = 0;
            actions.Controls.Add(statusButton);
            actions.Controls.Add(patchButton);
            actions.Controls.Add(restoreButton);
            statusButton.Click += async delegate { await Run("status", "检查状态"); };
            patchButton.Click += async delegate { await Run("patch", "应用补丁"); };
            restoreButton.Click += async delegate { await Run("restore", "恢复原程序"); };
            layout.Controls.Add(actions, 0, 4);
            layout.Controls.Add(new Label { Text = "操作记录 · 已有用户资料不会被移动或合并。", AutoSize = true, Margin = new Padding(0, 0, 0, 8), ForeColor = Color.FromArgb(80, 91, 110) }, 0, 5);
            logBox.Margin = new Padding(0, 0, 0, 12);
            logBox.Font = new Font("Microsoft YaHei UI", 9F);
            layout.Controls.Add(logBox, 0, 6);
            var footer = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Bottom, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 70));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
            footer.Controls.Add(stateLabel, 0, 0);
            footer.Controls.Add(progressBar, 1, 0);
            layout.Controls.Add(footer, 0, 7);
            installPathBox.Text = OperationRunner.DefaultPath();
            AppendLog("请选择目录后检查状态。检查不会关闭任何进程。");
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (!busy) return;
                e.Cancel = true;
                MessageBox.Show(this, "操作正在进行，请等待完成后关闭窗口。", "正在处理", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
        }

        private void BrowseFolder()
        {
            using (var dialog = new FolderBrowserDialog { Description = "选择坚果云根目录或 bin-* 目录", ShowNewFolderButton = false })
            {
                if (Directory.Exists(installPathBox.Text)) dialog.SelectedPath = installPathBox.Text;
                if (dialog.ShowDialog(this) == DialogResult.OK) installPathBox.Text = dialog.SelectedPath;
            }
        }

        private void BrowseDll()
        {
            using (var dialog = new OpenFileDialog { Title = "选择 NutstoreLib.dll", Filter = "坚果云程序库 (NutstoreLib.dll)|NutstoreLib.dll", CheckFileExists = true })
            {
                if (Directory.Exists(installPathBox.Text)) dialog.InitialDirectory = installPathBox.Text;
                if (dialog.ShowDialog(this) == DialogResult.OK) installPathBox.Text = dialog.FileName;
            }
        }

        private void AppendLog(string message)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string>(AppendLog), message); return; }
            logBox.AppendText(message + Environment.NewLine);
        }

        private async Task Run(string action, string label)
        {
            if (busy) return;
            string input = installPathBox.Text;
            bool closeProcesses = autoCloseCheckBox.Checked;
            bool createBackup = action != "patch" || createBackupCheckBox.Checked;
            busy = true;
            inputPanel.Enabled = autoCloseCheckBox.Enabled = createBackupCheckBox.Enabled = statusButton.Enabled = patchButton.Enabled = restoreButton.Enabled = false;
            progressBar.Visible = true;
            stateLabel.Text = "正在" + label + "…";
            AppendLog(Environment.NewLine + "── " + label + " ──");
            try
            {
                int result = await Task.Run(() => OperationRunner.Run(action, input, closeProcesses, createBackup, AppendLog));
                stateLabel.Text = result == 0 ? label + "完成" : "有操作失败，请查看记录";
            }
            catch (Exception error) { AppendLog("失败：" + error.Message); stateLabel.Text = "操作未完成，请查看记录"; }
            finally
            {
                busy = false;
                inputPanel.Enabled = autoCloseCheckBox.Enabled = createBackupCheckBox.Enabled = statusButton.Enabled = patchButton.Enabled = restoreButton.Enabled = true;
                progressBar.Visible = false;
            }
        }
    }
}
