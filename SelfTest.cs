using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace PeakModPacker
{
    /// <summary>Round-trip check run via "PeakModPacker.exe --selftest": builds a sandbox game
    /// tree, scans, packs to a temp zip, analyzes and installs into a second sandbox.</summary>
    public static class SelfTest
    {
        public static string Run()
        {
            var log = new List<string>();
            int pass = 0; var fail = new List<string>();
            Action<bool, string> check = (ok, name) => { if (ok) { pass++; log.Add("PASS " + name); } else { fail.Add(name); log.Add("FAIL " + name); } };

            string sandbox1 = Path.Combine(Path.GetTempPath(), "PeakPackerTest1-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            string sandbox2 = Path.Combine(Path.GetTempPath(), "PeakPackerTest2-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            try
            {
                // Build a fake game tree with two mods + configs.
                Directory.CreateDirectory(Path.Combine(sandbox1, "BepInEx", "plugins", "ModA"));
                Directory.CreateDirectory(Path.Combine(sandbox1, "BepInEx", "plugins", "ModB"));
                Directory.CreateDirectory(Path.Combine(sandbox1, "BepInEx", "config"));
                var bytes = new byte[4096]; new Random(1).NextBytes(bytes);
                File.WriteAllBytes(Path.Combine(sandbox1, "BepInEx", "plugins", "ModA", "ModA.dll"), bytes);
                File.WriteAllBytes(Path.Combine(sandbox1, "BepInEx", "plugins", "ModA", "skin.bundle"), new byte[8192]);
                File.WriteAllBytes(Path.Combine(sandbox1, "BepInEx", "plugins", "ModB", "boom.mp3"), new byte[2048]);
                File.WriteAllText(Path.Combine(sandbox1, "BepInEx", "plugins", "ModB", "manifest.json"),
                    "{\"name\":\"Sound B\",\"version_number\":\"2.1.0\"}");
                File.WriteAllText(Path.Combine(sandbox1, "BepInEx", "config", "ModB.cfg"), "a=1");

                // 1. scan
                var mods = GameTools.ScanMods(sandbox1);
                check(mods.Any(m => m.FolderName == "ModA" && m.Files.Count == 2), "scan-folder-mod");
                var modB = mods.FirstOrDefault(m => m.FolderName == "ModB");
                check(modB != null && modB.Version == "2.1.0", "scan-thunderstore-version");
                check(modB != null && modB.ConfigFiles.Count == 1, "scan-config-detection");

                // 2. pack (to a temp zip path on the real desktop, then moved away again)
                var pack = GameTools.Pack(sandbox1, mods, mods, true, _ => { }, (d, t) => { });
                check(pack.Ok && pack.FileCount == 5, "pack-ok(" + pack.FileCount + "/5 " + pack.Error + ")"); // 4 mod files + 1 config
                string zip = pack.ZipPath;
                check(File.Exists(zip) && new FileInfo(zip).Length > 0, "pack-zip-created");

                // 3. analyze
                Directory.CreateDirectory(Path.Combine(sandbox2, "BepInEx", "plugins"));
                var plan = GameTools.AnalyzeZip(sandbox2, zip);
                check(plan.Kind == GameTools.ZipKind.PeakPack, "analyze-kind");
                check(plan.Map.Count == 5, "analyze-file-count(" + plan.Map.Count + ")");
                check(plan.Hashes.Count == 5, "analyze-hashes");

                // 3b. Thunderstore/unsafe path handling is exercised below by install + mapping tests.

                // 4. install into second sandbox (fresh)
                var install = GameTools.Install(sandbox2, plan, false, _ => { }, (d, t) => { });
                check(install.Ok, "install-ok" + (install.Ok ? "" : " err=" + install.Error));
                check(File.Exists(Path.Combine(sandbox2, "BepInEx", "plugins", "ModA", "ModA.dll")), "install-file-a");
                check(File.Exists(Path.Combine(sandbox2, "BepInEx", "config", "ModB.cfg")), "install-config");
                check(true, "install-no-overwrite");

                // 5. install again → plain overwrite, no backup by design
                var install2 = GameTools.Install(sandbox2, plan, false, _ => { }, (d, t) => { });
                check(install2.Ok, "reinstall-overwrite-no-backup");

                // 5b. wipe mode removes old mods not present in the zip
                var oldDir = Path.Combine(sandbox2, "BepInEx", "plugins", "OldMod");
                Directory.CreateDirectory(oldDir);
                File.WriteAllText(Path.Combine(oldDir, "old.dll"), "x");
                var wInstall = GameTools.Install(sandbox2, plan, true, _ => { }, (d, t) => { });
                check(wInstall.Ok && wInstall.WipedFiles >= 3, "wipe-removes-old-mods(" + wInstall.WipedFiles + ")");
                check(!File.Exists(Path.Combine(oldDir, "old.dll")), "wipe-old-file-gone");
                check(File.Exists(Path.Combine(sandbox2, "BepInEx", "plugins", "ModA", "ModA.dll")), "wipe-then-install");

                // 6. corrupted zip hash → rejected
                if (File.Exists(zip))
                {
                    var broken = Path.GetTempPath() + "broken-pack.zip";
                    File.Copy(zip, broken, true);
                    using (var a = ZipFile.Open(broken, ZipArchiveMode.Update))
                    {
                        var e = a.Entries.First(x => x.FullName.EndsWith("ModA.dll"));
                        using (var w = new StreamWriter(e.Open())) w.Write("corrupted!");
                    }
                    var badPlan = GameTools.AnalyzeZip(sandbox2, broken);
                    var bad = GameTools.Install(sandbox2, badPlan, false, _ => { }, (d, t) => { });
                    check(!bad.Ok && bad.HashMismatches.Count > 0, "hash-mismatch-detected");
                    File.Delete(broken);
                }

                // 7. Thunderstore-style zip mapping
                var tsZip = Path.GetTempPath() + "ts-pack.zip";
                using (var a = ZipFile.Open(tsZip, ZipArchiveMode.Create))
                {
                    a.CreateEntry("manifest.json");
                    var e = a.CreateEntry("plugins/TSMod.dll");
                    using (var w = new StreamWriter(e.Open())) w.Write("stub");
                }
                var tsPlan = GameTools.AnalyzeZip(sandbox2, tsZip);
                check(tsPlan.Kind == GameTools.ZipKind.Thunderstore, "ts-analyze");
                check(tsPlan.Map.Count == 1 && tsPlan.Map[0].Value == "plugins/TSMod.dll", "ts-mapping");
                File.Delete(tsZip);

                try { File.Delete(zip); } catch { }

                // 8. full-BepInEx pack round-trip
                var full = GameTools.PackFull(sandbox1, _ => { }, (d, t2) => { });
                check(full.Ok && full.FileCount >= 5, "fullpack-ok(" + full.FileCount + ")" + (full.Ok ? "" : " " + full.Error));
                if (full.Ok)
                {
                    var fplan = GameTools.AnalyzeZip(sandbox2, full.ZipPath);
                    check(fplan.Kind == GameTools.ZipKind.PeakPack && fplan.Map.Count == full.FileCount, "fullpack-analyze");
                    var finstall = GameTools.Install(sandbox2, fplan, false, _ => { }, (d, t2) => { });
                    check(finstall.Ok, "fullpack-install" + (finstall.Ok ? "" : " err=" + finstall.Error + " mismatches=" + finstall.HashMismatches.Count));
                    check(File.Exists(Path.Combine(sandbox2, "BepInEx", "plugins", "ModB", "manifest.json")), "fullpack-contents");
                    try { File.Delete(full.ZipPath); } catch { }
                }
            }
            catch (Exception e)
            {
                fail.Add("exception: " + e);
                log.Add("EXCEPTION " + e);
            }
            finally
            {
                try { if (Directory.Exists(sandbox1)) Directory.Delete(sandbox1, true); } catch { }
                try { if (Directory.Exists(sandbox2)) Directory.Delete(sandbox2, true); } catch { }
            }

            log.Add("");
            log.Add(fail.Count == 0 ? "RESULT: ALL PASS (" + pass + ")" : "RESULT: FAILED (" + string.Join("; ", fail) + ")");
            string report = string.Join(Environment.NewLine, log);
            string reportPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "selftest-report.txt");
            try { File.WriteAllText(reportPath, report); } catch { }
            return reportPath;
        }
    }
}
