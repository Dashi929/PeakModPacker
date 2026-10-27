using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace PeakModPacker
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            System.IO.File.WriteAllText(CrashLog,
                DateTime.Now + " starting v" + System.Reflection.Assembly.GetExecutingAssembly().GetName().Version + Environment.NewLine);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => WriteCrash("unhandled", e.ExceptionObject as Exception);
            Application.ThreadException += (s, e) => WriteCrash("thread", e.Exception);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

            if (args != null && args.Length > 0 && args[0] == "--selftest")
            {
                var reportPath = SelfTest.Run();
                System.Console.WriteLine(reportPath);
                return;
            }
            if (args != null && args.Length > 0 && args[0] == "--layoutcheck")
            {
                LayoutCheck();
                return;
            }
            if (args != null && args.Length > 0 && args[0] == "--pack")
            {
                PackAll();
                return;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                Application.Run(new MainForm(args != null && args.Length > 0 ? args[0] : null));
            }
            catch (Exception e)
            {
                WriteCrash("run", e);
                throw;
            }
        }

        public static string CrashLog => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log");

        /// <summary>Offscreen layout verification: renders the form and dumps every control's
        /// final rectangle, so layout regressions are provable without screen capture.</summary>
        static void LayoutCheck()
        {
            try
            {
                using (var form = new MainForm(null))
                {
                    form.Show();
                    Application.DoEvents();
                    var bounds = new System.Text.StringBuilder();
                    Action<Control, int> dump = null;
                    dump = (c, depth) =>
                    {
                        bounds.AppendLine(new string(' ', depth * 2) + c.GetType().Name + " '" + Text(c) + "' " +
                            c.Bounds.X + "," + c.Bounds.Y + " " + c.Bounds.Width + "x" + c.Bounds.Height +
                            (c.Visible ? "" : " [hidden]"));
                        foreach (Control child in c.Controls) dump(child, depth + 1);
                    };
                    dump(form, 0);
                    using (var bmp = new Bitmap(form.Width, form.Height))
                    {
                        form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
                        bmp.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "layout-preview.png"));
                    }
                    File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "layout-report.txt"), bounds.ToString());
                    form.Close();
                }
                System.Console.WriteLine("layout check written");
            }
            catch (Exception e)
            {
                WriteCrash("layoutcheck", e);
            }
        }

        /// <summary>CLI pack: all mods (+ deps, loose files, configs) to the desktop.</summary>
        static void PackAll()
        {
            try
            {
                var root = GameTools.LoadSavedGameRoot();
                if (root == null || !GameTools.IsValidGameRoot(root))
                {
                    File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "pack-result.txt"), "未找到游戏目录");
                    return;
                }
                var mods = GameTools.ScanMods(root);
                var result = GameTools.Pack(root, mods, mods, true,
                    s => { }, (d, t2) => { });
                var report = result.Ok
                    ? "OK\n" + result.ZipPath + "\nmods=" + result.ModCount + " files=" + result.FileCount
                    : "FAILED\n" + result.Error;
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "pack-result.txt"), report);
            }
            catch (Exception e)
            {
                WriteCrash("pack", e);
            }
        }

        static string Text(Control c)
        {
            var t = c.Text ?? "";
            return t.Length > 20 ? t.Substring(0, 20) : t.Replace('\n', ' ');
        }

        public static void WriteCrash(string kind, Exception e)
        {
            try
            {
                File.AppendAllText(CrashLog, "[" + kind + "] " + (e?.ToString() ?? "?") + Environment.NewLine);
            }
            catch { }
        }
    }
}
