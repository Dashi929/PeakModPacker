using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace PeakModPacker
{
    /// <summary>
    /// Hand-laid-out UI: one LayoutAll() computes every control's rectangle from the current
    /// window size (no Dock/TableLayoutPanel z-order surprises). The window is freely
    /// resizable; LayoutAll runs on init and every resize.
    /// </summary>
    public class MainForm : Form
    {
        readonly float ui = 1f;
        int S(int v) { return (int)Math.Round(v * ui); }

        readonly Label pathLabel = new Label { Text = "游戏目录:", AutoSize = true };
        readonly TextBox gamePathBox = new TextBox { ReadOnly = true };
        readonly Button browseBtn = new Button { Text = "浏览…", AutoSize = true };

        readonly TabControl tabs = new TabControl();
        readonly TabPage packTab = new TabPage("一键打包");
        readonly TabPage unpackTab = new TabPage("一键解压");

        readonly ListView modList = new ListView();
        readonly FlowLayoutPanel packOptions = new FlowLayoutPanel { WrapContents = false, AutoSize = false };
        readonly Button selectAllBtn = new Button { Text = "全选", AutoSize = true };
        readonly Button selectNoneBtn = new Button { Text = "全不选", AutoSize = true };
        readonly Button packBtn = new Button();
        readonly ProgressBar packBar = new ProgressBar();
        readonly Label packStatus = new Label { AutoEllipsis = true };

        readonly Button pickZipBtn = new Button { Text = "选择压缩包…", AutoSize = true };
        readonly CheckBox wipeCheck = new CheckBox { Text = "清除旧 mod（清空 plugins 后安装，不可恢复）", AutoSize = true, Checked = false };
        readonly Label zipName = new Label { AutoEllipsis = true };
        readonly Label zipDescribe = new Label { AutoEllipsis = true };
        readonly TextBox zipPreview = new TextBox();
        readonly Button unpackBtn = new Button();
        readonly ProgressBar unpackBar = new ProgressBar();
        readonly Label unpackStatus = new Label { AutoEllipsis = true };

        readonly Label bottomStatus = new Label { AutoEllipsis = true };

        string gameRoot;
        GameTools.InstallPlan plan;

        public MainForm(string zipArg)
        {
            using (var g = CreateGraphics())
            {
                ui = Math.Max(1f, g.DpiX / 96f);
            }
            AutoScaleMode = AutoScaleMode.None;

            Text = "PEAK Mod 打包工具 v1.2";
            Font = new Font("Microsoft YaHei UI", 10.5f * ui);
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(S(680), S(700));
            MinimumSize = new Size(S(560), S(520));
            AllowDrop = true;

            browseBtn.Click += (s, e) => BrowseGame();
            selectAllBtn.Click += (s, e) => SetAllChecks(true);
            selectNoneBtn.Click += (s, e) => SetAllChecks(false);
            packOptions.Controls.Add(selectAllBtn);
            packOptions.Controls.Add(selectNoneBtn);

            packBtn.Text = "📦  一键打包到桌面";
            packBtn.BackColor = Color.FromArgb(226, 118, 45);
            packBtn.ForeColor = Color.White;
            packBtn.FlatStyle = FlatStyle.Flat;
            packBtn.Font = new Font("Microsoft YaHei UI", 12f * ui, FontStyle.Bold);
            packBtn.Click += (s, e) => StartPack();
            packBar.Visible = false;
            packStatus.Visible = false;

            modList.View = View.Details;
            modList.CheckBoxes = true;
            modList.FullRowSelect = true;
            modList.HideSelection = false;
            modList.Columns.Add("Mod", S(280));
            modList.Columns.Add("版本", S(100));
            modList.Columns.Add("文件", S(64));
            modList.Columns.Add("大小", S(84));
            modList.Columns.Add("配置", S(56));

            pickZipBtn.Click += (s, e) => PickZip();
            zipName.ForeColor = Color.FromArgb(75, 56, 38);
            zipDescribe.ForeColor = Color.FromArgb(75, 56, 38);
            zipPreview.Multiline = true;
            zipPreview.ReadOnly = true;
            zipPreview.ScrollBars = ScrollBars.Both;
            unpackBtn.Text = "📤  一键解压安装";
            unpackBtn.BackColor = Color.FromArgb(94, 140, 58);
            unpackBtn.ForeColor = Color.White;
            unpackBtn.FlatStyle = FlatStyle.Flat;
            unpackBtn.Font = new Font("Microsoft YaHei UI", 12f * ui, FontStyle.Bold);
            unpackBtn.Enabled = false;
            unpackBtn.Click += (s, e) => StartUnpack();
            unpackBar.Visible = false;
            unpackStatus.Visible = false;

            packTab.Controls.Add(modList);
            packTab.Controls.Add(packOptions);
            packTab.Controls.Add(packBtn);
            packTab.Controls.Add(packBar);
            packTab.Controls.Add(packStatus);
            unpackTab.Controls.Add(wipeCheck);
            unpackTab.Controls.Add(pickZipBtn);
            unpackTab.Controls.Add(zipName);
            unpackTab.Controls.Add(zipDescribe);
            unpackTab.Controls.Add(zipPreview);
            unpackTab.Controls.Add(unpackBtn);
            unpackTab.Controls.Add(unpackBar);
            unpackTab.Controls.Add(unpackStatus);
            tabs.TabPages.Add(packTab);
            tabs.TabPages.Add(unpackTab);

            bottomStatus.ForeColor = Color.Gray;
            bottomStatus.Text = "打包 = 勾选的 mod → zip 放到桌面；解压 = 选 zip → 一键装回游戏（游戏运行时不可用）";

            Controls.Add(pathLabel);
            Controls.Add(gamePathBox);
            Controls.Add(browseBtn);
            Controls.Add(tabs);
            Controls.Add(bottomStatus);

            Resize += (s, e) => LayoutAll();
            Layout += (s, e) => LayoutAll();

            DragEnter += (s, e) =>
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
            };
            DragDrop += (s, e) =>
            {
                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files == null || files.Length == 0) return;
                if (string.Equals(Path.GetExtension(files[0]), ".zip", StringComparison.OrdinalIgnoreCase))
                    LoadZip(files[0]);
                else MessageBox.Show("请拖入 .zip 文件", "提示");
            };

            LoadGameRoot();
            LayoutAll();
            if (zipArg != null && File.Exists(zipArg)) LoadZip(zipArg);
        }

        /// <summary>Single source of truth for every control's rectangle.</summary>
        bool layouting;
        void LayoutAll()
        {
            if (layouting) return;
            layouting = true;
            try
            {
                int W = ClientSize.Width;
                int H = ClientSize.Height;
                int pad = S(10);
                int pad2 = pad * 2;

                // ---- path row ------------------------------------------------------------
                int pathH = Math.Max(S(44), Math.Max(gamePathBox.PreferredHeight, browseBtn.PreferredSize.Height) + S(12));
                pathLabel.Location = new Point(pad, pad + (pathH - pathLabel.PreferredSize.Height) / 2);
                gamePathBox.SetBounds(pathLabel.Right + S(8), pad + (pathH - gamePathBox.PreferredHeight) / 2,
                    Math.Max(S(120), W - pad2 - pathLabel.Width - S(8) - browseBtn.PreferredSize.Width - S(8)),
                    gamePathBox.PreferredHeight);
                browseBtn.Location = new Point(gamePathBox.Right + S(8), pad + (pathH - browseBtn.PreferredSize.Height) / 2);

                // ---- bottom bar ------------------------------------------------------------
                int bottomH = S(30);
                bottomStatus.SetBounds(pad, H - bottomH + S(4), W - pad2, S(22));

                // ---- tabs area ------------------------------------------------------------
                tabs.SetBounds(0, pathH, W, Math.Max(S(200), H - pathH - bottomH));
                // DisplayRectangle = the tab CONTENT area below the header (TabPage bounds
                // inside the TabControl). ClientSize would wrongly include the tab header.
                var dr = tabs.DisplayRectangle;
                int tx = tabPadLeft();
                int ty = tabPadTop();
                int tw = Math.Max(S(200), dr.Width - tx * 2);
                int th = Math.Max(S(120), dr.Height - S(4));

                // Bottom-anchored action block. The button sits tight at the page bottom; while
                // busy the progress bar + status occupy the button's rect (button hidden), so the
                // idle layout reserves no blank space.
                int optItemH = 0;
                foreach (Control c in packOptions.Controls) optItemH = Math.Max(optItemH, c.PreferredSize.Height);
                int optionsH = optItemH + S(8);
                int btnH = S(48);
                int barH = S(16);
                int yBtn = th - S(8) - btnH;
                int yOpts = yBtn - S(8) - optionsH;

                // tab 1: list on top, action area at the bottom
                modList.SetBounds(tx, ty, tw, Math.Max(S(80), yOpts - S(6) - ty));
                packOptions.SetBounds(tx, yOpts, tw, optionsH);
                packBtn.SetBounds(tx, yBtn, tw, btnH);
                packBar.SetBounds(tx, yBtn + S(8), tw, barH);
                packStatus.SetBounds(tx, yBtn + S(8) + barH + S(4), tw, S(20));

                // tab 2: pickers on top, preview fills the middle, action at the bottom
                pickZipBtn.Location = new Point(tx, ty + S(2));
                zipName.SetBounds(pickZipBtn.Right + S(8), ty + S(4),
                    Math.Max(S(100), tw - pickZipBtn.Width - S(16)), zipName.PreferredHeight);
                zipDescribe.SetBounds(tx, zipName.Bottom + S(6), tw, S(24));
                int wipeH = Math.Max(S(28), wipeCheck.PreferredSize.Height);
                wipeCheck.SetBounds(tx + S(2), zipDescribe.Bottom + S(2), tw - S(4), wipeH);
                zipPreview.SetBounds(tx, wipeCheck.Bottom + S(4), tw, Math.Max(S(60), yBtn - S(8) - wipeCheck.Bottom - S(4)));

                unpackBtn.SetBounds(tx, yBtn, tw, btnH);
                unpackBar.SetBounds(tx, yBtn + S(8), tw, barH);
                unpackStatus.SetBounds(tx, yBtn + S(8) + barH + S(4), tw, S(20));

                // stretch list's first column to fill
                int others = 0;
                for (int i = 1; i < modList.Columns.Count; i++) others += modList.Columns[i].Width;
                int usable = modList.ClientSize.Width - others - 4;
                if (usable > S(120)) modList.Columns[0].Width = usable;
            }
            catch (Exception e)
            {
                Program.WriteCrash("layout", e);
            }
            finally
            {
                layouting = false;
            }
        }

        int tabPadLeft() { return S(8); }
        int tabPadTop() { return S(6); }

        // ------------------------------------------------------------------ game root

        void LoadGameRoot()
        {
            gameRoot = GameTools.LoadSavedGameRoot();
            gamePathBox.Text = gameRoot ?? "（未找到 PEAK——请点浏览选择游戏目录）";
            if (GameTools.IsValidGameRoot(gameRoot)) GameTools.SweepDeferred(gameRoot);
            Rescan();
        }

        void BrowseGame()
        {
            using (var dlg = new FolderBrowserDialog { Description = "选择 PEAK 游戏目录（包含 PEAK.exe）" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                if (!GameTools.IsValidGameRoot(dlg.SelectedPath))
                {
                    if (MessageBox.Show("该目录下没有 PEAK.exe，仍然使用？", "提示", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
                }
                gameRoot = dlg.SelectedPath;
                GameTools.SaveGameRoot(gameRoot);
                gamePathBox.Text = gameRoot;
                Rescan();
            }
        }

        void Rescan()
        {
            modList.Items.Clear();
            if (gameRoot == null || !GameTools.IsValidGameRoot(gameRoot)) return;
            foreach (var mod in GameTools.ScanMods(gameRoot))
            {
                var item = new ListViewItem(mod.Name) { Tag = mod, Checked = true };
                item.SubItems.Add(mod.Version ?? "");
                item.SubItems.Add(mod.Files.Count.ToString());
                item.SubItems.Add(GameTools.FmtBytes(mod.TotalBytes));
                item.SubItems.Add(mod.ConfigFiles.Count > 0 ? mod.ConfigFiles.Count.ToString() : "-");
                modList.Items.Add(item);
            }
            bottomStatus.Text = gameRoot + " · 共 " + modList.Items.Count + " 个 mod";
        }

        void SetAllChecks(bool value)
        {
            foreach (ListViewItem item in modList.Items) item.Checked = value;
        }

        // ------------------------------------------------------------------ pack

        void StartPack()
        {
            if (gameRoot == null || !GameTools.IsValidGameRoot(gameRoot)) { MessageBox.Show("请先设置游戏目录"); return; }
            var selected = new System.Collections.Generic.List<ModEntry>();
            var all = new System.Collections.Generic.List<ModEntry>();
            foreach (ListViewItem item in modList.Items) all.Add((ModEntry)item.Tag);
            foreach (ListViewItem item in modList.CheckedItems) selected.Add((ModEntry)item.Tag);
            if (selected.Count == 0) { MessageBox.Show("请至少勾选一个 mod"); return; }
            SetBusy(packBtn, packBar, packStatus, true, "📦  一键打包到桌面");
            new Thread(() =>
            {
                var result = GameTools.Pack(gameRoot, all, selected, true,   // configs always included
                    msg => SafeRun(() => packStatus.Text = msg),
                    (done, total) => SafeRun(() => { packBar.Value = total > 0 ? Math.Min(100, done * 100 / total) : 0; }));
                SafeRun(() =>
                {
                    SetBusy(packBtn, packBar, packStatus, false, "📦  一键打包到桌面");
                    packBar.Value = 100;
                    if (result.Ok)
                    {
                        packStatus.Text = "";
                        if (MessageBox.Show(string.Format("打包完成：{0} 个 mod / {1} 个文件\n\n{2}\n\n打开桌面文件夹？",
                                result.ModCount, result.FileCount, result.ZipPath),
                                "完成", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                            System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + result.ZipPath + "\"");
                    }
                    else
                    {
                        packBar.Value = 0;
                        MessageBox.Show("打包失败: " + result.Error, "错误");
                    }
                });
            }).Start();
        }

        // ------------------------------------------------------------------ unpack

        void PickZip()
        {
            using (var dlg = new OpenFileDialog { Title = "选择 mod 压缩包", Filter = "zip 压缩包 (*.zip)|*.zip" })
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                if (Directory.Exists(desktop)) dlg.InitialDirectory = desktop;
                if (dlg.ShowDialog(this) == DialogResult.OK) LoadZip(dlg.FileName);
            }
        }

        void LoadZip(string path)
        {
            if (gameRoot == null || !GameTools.IsValidGameRoot(gameRoot))
            {
                MessageBox.Show("请先设置游戏目录", "提示");
                return;
            }
            tabs.SelectedIndex = 1;
            zipName.Text = Path.GetFileName(path);
            unpackStatus.Visible = false;
            try
            {
                plan = GameTools.AnalyzeZip(gameRoot, path);
            }
            catch (Exception e)
            {
                plan = null;
                zipDescribe.Text = "读取失败: " + e.Message;
                zipPreview.Text = "";
                unpackBtn.Enabled = false;
                return;
            }
            zipDescribe.Text = plan.Describe;
            zipPreview.Text = plan.ModNames.Count == 0 ? "（无内容）" : string.Join(Environment.NewLine, plan.ModNames);
            unpackBtn.Enabled = plan.Map.Count > 0;
            if (plan.Map.Count == 0) zipDescribe.Text += "  ——该包没有可安装的内容";
        }

        void StartUnpack()
        {
            if (plan == null || plan.Map.Count == 0) return;
            // Installing while the game runs would hit locked DLLs and a half-applied state.
            if (System.Diagnostics.Process.GetProcessesByName("PEAK").Length > 0)
            {
                MessageBox.Show("检测到 PEAK 正在运行。请先完全退出游戏，再执行解压安装。", "请先关闭游戏",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            bool wipe = wipeCheck.Checked;
            var confirm = MessageBox.Show(
                wipe
                    ? string.Format("将先清空 BepInEx/plugins（现有 mod 全部删除，不备份、不可恢复），\n再安装 {0} 个文件。\n\n确认继续？", plan.Map.Count)
                    : string.Format("将安装 {0} 个文件到 BepInEx（直接新增/覆盖，不备份）。\n\n确认继续？", plan.Map.Count),
                "一键解压安装", MessageBoxButtons.YesNo, wipe ? MessageBoxIcon.Warning : MessageBoxIcon.Question);
            if (confirm != DialogResult.Yes) return;
            SetBusy(unpackBtn, unpackBar, unpackStatus, true, "📤  一键解压安装");
            var currentPlan = plan;
            new Thread(() =>
            {
                var result = GameTools.Install(gameRoot, currentPlan, wipeCheck.Checked,
                    msg => SafeRun(() => unpackStatus.Text = msg),
                    (done, total) => SafeRun(() => { unpackBar.Value = total > 0 ? Math.Min(100, done * 100 / total) : 0; }));
                SafeRun(() =>
                {
                    SetBusy(unpackBtn, unpackBar, unpackStatus, false, "📤  一键解压安装");
                    unpackBar.Value = 100;
                    if (result.Ok)
                    {
                        unpackStatus.Text = "";
                        string note = result.WipedFiles > 0 ? "（已清除旧文件 " + result.WipedFiles + " 个）" : "";
                        if (result.DeferredFiles > 0) note += "\n提示：" + result.DeferredFiles + " 个文件当时正被游戏占用，旧版本已让位（下次打开本工具时自动清理；重启游戏后加载新版本）。";
                        MessageBox.Show(string.Format("安装完成：{0} 个文件。{1}", result.InstalledFiles, note),
                            "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        Rescan();
                        tabs.SelectedIndex = 0;
                    }
                    else
                    {
                        unpackBar.Value = 0;
                        MessageBox.Show("安装失败: " + result.Error, "错误");
                    }
                });
            }).Start();
        }

        // ------------------------------------------------------------------ helpers

        void SetBusy(Button btn, ProgressBar bar, Label status, bool busy, string idleText)
        {
            btn.Enabled = !busy;
            btn.Visible = !busy;   // bar + status take over the button's rect while working
            btn.Text = busy ? "处理中…" : idleText;
            bar.Visible = busy;
            status.Visible = busy;
            if (!busy) bar.Value = 0;
            UseWaitCursor = busy;
        }

        void SafeRun(System.Action action)
        {
            if (IsDisposed) return;
            try { BeginInvoke((MethodInvoker)delegate { action(); }); } catch { }
        }
    }
}
