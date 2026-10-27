using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using Mono.Cecil;

namespace PeakModPacker
{
    public class ModEntry
    {
        public string Id;
        public string Name;
        public string Version = "";
        public string FolderName;
        public List<FileEntry> Files = new List<FileEntry>();
        public List<FileEntry> ConfigFiles = new List<FileEntry>();
        /// <summary>GUIDs declared via [BepInDependency] in the mod's DLL(s).</summary>
        public List<string> Dependencies = new List<string>();
        public long TotalBytes { get { long t = 0; foreach (var f in Files) t += f.Size; return t; } }
    }

    public class FileEntry
    {
        public string Path;    // BepInEx-relative, e.g. "plugins/Foo/a.dll"
        public long Size;
        public string Sha256;
    }

    /// <summary>All game-facing work: locate the game, inventory mods, pack to desktop, install zips.</summary>
    public static class GameTools
    {
        public const string PackManifestName = "peak-modpack.json";

        // ---------------------------------------------------------------- game root

        public static string SettingsPath =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "peakmodpacker-settings.json");

        public static string LoadSavedGameRoot()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var j = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(SettingsPath));
                    object v;
                    if (j != null && j.TryGetValue("GameRoot", out v) && IsValidGameRoot(v as string)) return v as string;
                }
            }
            catch { }
            return FindGameRoot();
        }

        public static void SaveGameRoot(string root)
        {
            try
            {
                File.WriteAllText(SettingsPath, new JavaScriptSerializer().Serialize(new Dictionary<string, object> { { "GameRoot", root } }));
            }
            catch { }
        }

        public static bool IsValidGameRoot(string root)
        {
            try { return !string.IsNullOrEmpty(root) && File.Exists(Path.Combine(root, "PEAK.exe")); }
            catch { return false; }
        }

        /// <summary>Registry + libraryfolders.vdf scan, then the well-known default.</summary>
        public static string FindGameRoot()
        {
            var candidates = new List<string>();
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    var steamPath = key?.GetValue("SteamPath") as string;
                    if (!string.IsNullOrEmpty(steamPath)) candidates.Add(steamPath);
                }
            }
            catch { }
            candidates.Add(@"F:\SteamLibrary\steamapps\common\PEAK");
            var libs = new List<string>();
            foreach (var steam in candidates.Where(c => !c.EndsWith("common\\PEAK", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
                    if (File.Exists(vdf))
                        foreach (var m in System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                            libs.Add(((System.Text.RegularExpressions.Match)m).Groups[1].Value.Replace("\\\\", "\\"));
                }
                catch { }
                libs.Add(steam);
            }
            foreach (var lib in libs.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var p = Path.Combine(lib, "steamapps", "common", "PEAK");
                if (IsValidGameRoot(p)) return p;
                if (IsValidGameRoot(lib)) return lib; // direct path given
            }
            return null;
        }

        public static string BepInEx(string root) => Path.Combine(root, "BepInEx");
        public static string Plugins(string root) => Path.Combine(BepInEx(root), "plugins");
        public static string Config(string root) => Path.Combine(BepInEx(root), "config");

        public static string GameVersion(string root)
        {
            try { return File.ReadAllText(Path.Combine(root, "version.txt")).Trim(); } catch { return ""; }
        }

        // ---------------------------------------------------------------- inventory

        /// <summary>Scans BepInEx/plugins and groups files into mods (folder = mod; loose DLL = mod by GUID).</summary>
        public static List<ModEntry> ScanMods(string root)
        {
            var mods = new List<ModEntry>();
            var plugins = Plugins(root);
            if (!Directory.Exists(plugins)) return mods;
            var rootGroup = new ModEntry { Id = "__root__", Name = "根目录散装文件" };

            foreach (var dir in Directory.GetDirectories(plugins))
            {
                var mod = BuildFolderMod(root, dir);
                if (mod != null) mods.Add(mod);
            }
            foreach (var file in Directory.GetFiles(plugins))
            {
                var fe = new FileEntry { Path = "plugins/" + Path.GetFileName(file), Size = new FileInfo(file).Length };
                ModEntry mod;
                if (file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                    TryReadBepInPlugin(file, out var guid, out var name, out var ver))
                {
                    mod = new ModEntry { Id = guid, Name = name, Version = ver, FolderName = Path.GetFileName(file) };
                }
                else mod = rootGroup;
                mod.Files.Add(fe);
                if (mod != rootGroup) mods.Add(mod);
            }
            if (rootGroup.Files.Count > 0) mods.Add(rootGroup);

            foreach (var mod in mods)
            {
                mod.ConfigFiles = FindConfigs(root, mod);
                mod.Dependencies = ReadDependencies(mod, root);
            }
            return mods;
        }

        static ModEntry BuildFolderMod(string root, string dir)
        {
            var id = Path.GetFileName(dir);
            var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
            if (files.Length == 0) return null;
            var mod = new ModEntry { Id = id, Name = id, FolderName = id };
            foreach (var file in files)
            {
                mod.Files.Add(new FileEntry
                {
                    Path = "plugins/" + file.Substring(Plugins(root).Length + 1).Replace('\\', '/'),
                    Size = new FileInfo(file).Length,
                });
                if (mod.Version == "" && file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                    TryReadBepInPlugin(file, out var guid, out var name, out var ver))
                {
                    mod.Name = string.IsNullOrEmpty(name) ? mod.Name : name;
                    mod.Version = ver;
                    mod.Id = guid;
                }
            }
            if (mod.Version == "")
            {
                var ts = Path.Combine(dir, "manifest.json");
                if (File.Exists(ts))
                {
                    try
                    {
                        var j = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(ts));
                        if (j.ContainsKey("name") && j["name"] != null) mod.Name = j["name"].ToString();
                        if (j.ContainsKey("version_number") && j["version_number"] != null) mod.Version = j["version_number"].ToString();
                    }
                    catch { }
                }
            }
            return mod;
        }

        /// <summary>Config files in BepInEx/config belonging to a mod (&lt;guid&gt;.cfg, &lt;folder&gt;*.cfg).</summary>
        public static List<FileEntry> FindConfigs(string root, ModEntry mod)
        {
            var result = new List<FileEntry>();
            var cfgDir = Config(root);
            if (!Directory.Exists(cfgDir)) return result;
            var keys = new List<string>();
            if (!string.IsNullOrEmpty(mod.Id) && mod.Id != "__root__") keys.Add(mod.Id.ToLowerInvariant());
            if (!string.IsNullOrEmpty(mod.FolderName)) keys.Add(mod.FolderName.ToLowerInvariant());
            foreach (var file in Directory.GetFiles(cfgDir))
            {
                var n = Path.GetFileName(file).ToLowerInvariant();
                var hit = keys.Any(k => n == k + ".cfg" || (n.StartsWith(k + ".", StringComparison.Ordinal) && n.EndsWith(".cfg")));
                if (!hit) continue;
                result.Add(new FileEntry { Path = "config/" + Path.GetFileName(file), Size = new FileInfo(file).Length });
            }
            return result;
        }

        static bool TryReadBepInPlugin(string dll, out string guid, out string name, out string version)
        {
            return TryReadAssemblyInfo(dll, out guid, out name, out version, null);
        }

        /// <summary>Reads [BepInPlugin] and every [BepInDependency] from a managed DLL.</summary>
        static bool TryReadAssemblyInfo(string dll, out string guid, out string name, out string version, List<string> dependencies)
        {
            guid = name = version = null;
            try
            {
                using (var asm = AssemblyDefinition.ReadAssembly(dll, new ReaderParameters { ReadSymbols = false, ReadingMode = ReadingMode.Deferred }))
                {
                    foreach (var attr in asm.MainModule.Assembly.CustomAttributes)
                    {
                        if (attr.AttributeType.FullName == "BepInEx.BepInDependency" && dependencies != null && attr.ConstructorArguments.Count > 0)
                        {
                            var dep = attr.ConstructorArguments[0].Value as string;
                            if (!string.IsNullOrEmpty(dep) && !dependencies.Contains(dep)) dependencies.Add(dep);
                        }
                        if (attr.AttributeType.FullName != "BepInEx.BepInPlugin" || attr.ConstructorArguments.Count < 3) continue;
                        guid = attr.ConstructorArguments[0].Value as string;
                        name = attr.ConstructorArguments[1].Value as string;
                        version = attr.ConstructorArguments[2].Value as string;
                    }
                    return guid != null;
                }
            }
            catch { }
            return false;
        }

        /// <summary>Collects [BepInDependency] GUIDs from every DLL of a mod (folder or loose file).</summary>
        static List<string> ReadDependencies(ModEntry mod, string root)
        {
            var deps = new List<string>();
            foreach (var f in mod.Files)
            {
                if (!f.Path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                var abs = Path.Combine(BepInEx(root), f.Path.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(abs)) continue;
                TryReadAssemblyInfo(abs, out _, out _, out _, deps);
            }
            return deps;
        }

        // ---------------------------------------------------------------- pack

        public class PackResult
        {
            public bool Ok;
            public string Error;
            public string ZipPath;
            public int ModCount;
            public int FileCount;
            public List<string> DependencyNames = new List<string>();
        }

        /// <summary>Packs the selected mods (plus optional configs) into a zip on the desktop.
        /// Dependencies declared via [BepInDependency] and loose files in the plugins root are
        /// included automatically, resolved against the full installed-mod list.</summary>
        public static PackResult Pack(string root, List<ModEntry> allMods, List<ModEntry> selected, bool includeConfigs,
            Action<string> status, Action<int, int> progress)
        {
            var result = new PackResult();
            try
            {
                // Expand the selection with every locally-installed dependency declared via
                // [BepInDependency], transitively, so the receiver gets a runnable mod.
                var byId = new Dictionary<string, ModEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (var m in allMods)
                {
                    if (!byId.ContainsKey(m.Id)) byId[m.Id] = m;
                    if (!string.IsNullOrEmpty(m.FolderName) && !byId.ContainsKey(m.FolderName)) byId[m.FolderName] = m;
                }
                var closure = new List<ModEntry>(selected);
                var queue = new List<ModEntry>(selected);
                // Loose files at the plugins root (fonts/bundles/text tables that mods load by
                // path) always ride along — they cannot be attributed to a single mod.
                ModEntry rootGroup;
                if (byId.TryGetValue("__root__", out rootGroup) && !closure.Contains(rootGroup) && rootGroup.Files.Count > 0)
                {
                    closure.Add(rootGroup);
                    queue.Add(rootGroup);
                }
                var addedDeps = new List<string>();
                while (queue.Count > 0)
                {
                    var current = queue[0];
                    queue.RemoveAt(0);
                    foreach (var depGuid in current.Dependencies)
                    {
                        foreach (var key in new[] { depGuid })
                        {
                            ModEntry depMod;
                            if (!byId.TryGetValue(key, out depMod)) continue;
                            if (closure.Contains(depMod)) continue;
                            closure.Add(depMod);
                            queue.Add(depMod);
                            addedDeps.Add(depMod.Name);
                        }
                    }
                }
                status(string.Format("依赖解析：自动附带 {0} 个依赖", addedDeps.Count));

                var plan = new List<ModEntry>();
                foreach (var m in closure)
                {
                    var copy = new ModEntry { Id = m.Id, Name = m.Name, Version = m.Version, FolderName = m.FolderName };
                    copy.Files.AddRange(m.Files.Where(f => File.Exists(Abs(root, f.Path))));
                    if (includeConfigs) copy.ConfigFiles.AddRange(m.ConfigFiles.Where(f => File.Exists(Abs(root, f.Path))));
                    if (copy.Files.Count + copy.ConfigFiles.Count > 0) plan.Add(copy);
                }
                if (plan.Count == 0) { result.Error = "没有可打包的文件（选择至少一个 mod）"; return result; }

                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                string zip = Path.Combine(desktop, "PEAK-mods-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip");
                int n = 0;
                while (File.Exists(zip)) zip = Path.Combine(desktop, "PEAK-mods-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + (++n) + ".zip");

                int totalFiles = plan.Sum(m => m.Files.Count + m.ConfigFiles.Count);
                int done = 0;
                using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
                {
                    foreach (var mod in plan)
                    {
                        status("打包: " + mod.Name);
                        var all = mod.Files.Concat(mod.ConfigFiles).ToList();
                        foreach (var f in all)
                        {
                            var abs = Abs(root, f.Path);
                            var entry = archive.CreateEntry("BepInEx/" + f.Path, CompressionLevel.Optimal);
                            f.Sha256 = null;
                            using (var src = File.OpenRead(abs))
                            using (var dst = entry.Open())
                            {
                                var hash = CopyAndHash(src, dst);
                                f.Sha256 = hash;
                            }
                            progress(++done, totalFiles);
                        }
                    }
                    var manifest = new Dictionary<string, object>
                    {
                        { "format", "peakmodpack" },
                        { "version", 1 },
                        { "created", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") },
                        { "gameVersion", GameVersion(root) },
                        { "mods", plan.Select(m => (object)new Dictionary<string, object>
                            {
                                { "id", m.Id }, { "name", m.Name }, { "version", m.Version },
                                { "files", m.Files.Concat(m.ConfigFiles).Select(f => (object)new Dictionary<string, object>
                                    { { "path", f.Path }, { "size", f.Size }, { "sha256", f.Sha256 } }).ToList() },
                            }).ToList() },
                    };
                    var me = archive.CreateEntry(PackManifestName);
                    using (var w = new StreamWriter(me.Open()))
                        w.Write(new JavaScriptSerializer().Serialize(manifest));
                }
                result.Ok = true;
                result.ZipPath = zip;
                result.ModCount = plan.Count;
                result.FileCount = totalFiles;
                result.DependencyNames = addedDeps;
                return result;
            }
            catch (Exception e)
            {
                result.Error = e.Message;
                return result;
            }
        }

        static string Abs(string root, string bepinexRel) =>
            Path.Combine(BepInEx(root), bepinexRel.Replace('/', Path.DirectorySeparatorChar));

        /// <summary>Packs the ENTIRE BepInEx folder (plugins, config, patchers, core — minus
        /// cache/logs) so the receiver ends up with a byte-identical environment. This is the
        /// safest mode: fonts, loose text tables and library dependencies all ship.</summary>
        public static PackResult PackFull(string root, Action<string> status, Action<int, int> progress)
        {
            var result = new PackResult();
            try
            {
                var bepinex = BepInEx(root);
                if (!Directory.Exists(bepinex)) { result.Error = "找不到 BepInEx 目录"; return result; }
                status("扫描 BepInEx…");
                var files = Directory.GetFiles(bepinex, "*", SearchOption.AllDirectories)
                    .Select(f => new { Abs = f, Rel = f.Substring(bepinex.Length + 1).Replace('\\', '/') })
                    .Where(f => !f.Rel.StartsWith("cache/", StringComparison.OrdinalIgnoreCase)
                             && !f.Rel.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (files.Count == 0) { result.Error = "BepInEx 目录为空"; return result; }

                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                string zip = Path.Combine(desktop, "PEAK-BepInEx完整包-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip");
                int n = 0;
                while (File.Exists(zip)) zip = Path.Combine(desktop, "PEAK-BepInEx完整包-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + (++n) + ".zip");

                var entries = new List<FileEntry>();
                using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
                {
                    int done = 0;
                    foreach (var f in files)
                    {
                        var entry = archive.CreateEntry("BepInEx/" + f.Rel, CompressionLevel.Optimal);
                        using (var src = File.OpenRead(f.Abs))
                        using (var dst = entry.Open())
                        {
                            var hash = CopyAndHash(src, dst);
                            entries.Add(new FileEntry { Path = f.Rel, Size = src.Length, Sha256 = hash });
                        }
                        if ((++done & 15) == 0) progress(done, files.Count);
                    }
                    var manifest = new Dictionary<string, object>
                    {
                        { "format", "peakmodpack" },
                        { "version", 1 },
                        { "created", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") },
                        { "gameVersion", GameVersion(root) },
                        { "full", true },
                        { "mods", new List<object>
                            {
                                new Dictionary<string, object>
                                {
                                    { "id", "__full__" },
                                    { "name", "BepInEx 完整环境" },
                                    { "version", "" },
                                    { "files", entries.Select(f => (object)new Dictionary<string, object>
                                        { { "path", f.Path }, { "size", f.Size }, { "sha256", f.Sha256 } }).ToList() },
                                },
                            } },
                    };
                    var me = archive.CreateEntry(PackManifestName);
                    using (var w = new StreamWriter(me.Open()))
                        w.Write(new JavaScriptSerializer().Serialize(manifest));
                }
                result.Ok = true;
                result.ZipPath = zip;
                result.ModCount = 1;
                result.FileCount = entries.Count;
                return result;
            }
            catch (Exception e)
            {
                result.Error = e.Message;
                return result;
            }
        }

        static string CopyAndHash(Stream src, Stream dst)
        {
            using (var sha = SHA256.Create())
            {
                var buffer = new byte[64 * 1024];
                int read;
                while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
                {
                    dst.Write(buffer, 0, read);
                    sha.TransformBlock(buffer, 0, read, buffer, 0);
                }
                sha.TransformFinalBlock(buffer, 0, 0);
                return BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
            }
        }

        // ---------------------------------------------------------------- unpack

        public enum ZipKind { PeakPack, Thunderstore, BepInExRooted, BepInExRelative, Unknown }

        public class InstallPlan
        {
            public ZipKind Kind;
            public string ZipPath;
            public List<KeyValuePair<string, string>> Map = new List<KeyValuePair<string, string>>(); // zip entry -> BepInEx rel
            public List<string> Skipped = new List<string>();
            public List<string> ModNames = new List<string>();
            public long TotalBytes;
            public int Overwrites;
            public Dictionary<string, string> Hashes = new Dictionary<string, string>(); // rel -> sha256
            public string Describe;
        }

        /// <summary>Figures out where a zip's files should land. Never touches the disk.</summary>
        public static InstallPlan AnalyzeZip(string root, string zipPath)
        {
            var plan = new InstallPlan { ZipPath = zipPath };
            using (var archive = ZipFile.OpenRead(zipPath))
            {
                var entries = archive.Entries.Where(e => e.Name != "" || e.FullName.EndsWith("/")).ToList();
                var names = archive.Entries.Select(e => e.FullName).ToList();
                bool hasPackManifest = names.Any(n => n == PackManifestName);
                bool hasTsManifest = names.Any(n => n == "manifest.json");

                plan.Kind = hasPackManifest ? ZipKind.PeakPack
                    : hasTsManifest ? ZipKind.Thunderstore
                    : names.Any(n => n.StartsWith("BepInEx/", StringComparison.OrdinalIgnoreCase)) ? ZipKind.BepInExRooted
                    : ZipKind.Unknown;

                // For unknown, check if every top folder is a known BepInEx folder → relative layout.
                if (plan.Kind == ZipKind.Unknown && names.Count > 0)
                {
                    var tops = names.Where(n => n != "").Select(n => n.Split('/')[0]).Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    if (tops.Count > 0 && tops.All(t => new[] { "plugins", "config", "patchers", "core", "monomod" }.Contains(t, StringComparer.OrdinalIgnoreCase)))
                        plan.Kind = ZipKind.BepInExRelative;
                }

                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue; // directory stub
                    var full = entry.FullName.Replace('\\', '/');
                    string rel = null;
                    if (plan.Kind == ZipKind.PeakPack || plan.Kind == ZipKind.BepInExRooted)
                    {
                        if (full == PackManifestName) continue;
                        if (full.StartsWith("BepInEx/", StringComparison.OrdinalIgnoreCase))
                            rel = full.Substring("BepInEx/".Length);
                    }
                    else if (plan.Kind == ZipKind.Thunderstore)
                    {
                        var low = full.ToLowerInvariant();
                        if (full == "manifest.json" || low.StartsWith("readme") || low == "icon.png" || low == "icon.webp" || low.StartsWith("images/")) { plan.Skipped.Add(full); continue; }
                        rel = full; // zip root maps to BepInEx/
                    }
                    else if (plan.Kind == ZipKind.BepInExRelative)
                    {
                        rel = full;
                    }
                    else
                    {
                        plan.Skipped.Add(full);
                        continue;
                    }
                    if (!SanitizeRel(rel)) { plan.Skipped.Add(full + "（不安全路径）"); continue; }
                    plan.Map.Add(new KeyValuePair<string, string>(entry.FullName, rel));
                    plan.TotalBytes += entry.Length;
                }

                // Preview: mod names + overwrite count.
                if (plan.Kind == ZipKind.PeakPack)
                {
                    try
                    {
                        var me = archive.GetEntry(PackManifestName);
                        using (var r = new StreamReader(me.Open()))
                        {
                            var j = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(r.ReadToEnd());
                            foreach (var m in (System.Collections.ArrayList)j["mods"])
                            {
                                var md = (Dictionary<string, object>)m;
                                plan.ModNames.Add(md["name"] + (md.ContainsKey("version") && md["version"] != null ? " v" + md["version"] : ""));
                                foreach (var f in (System.Collections.ArrayList)md["files"])
                                {
                                    var fd = (Dictionary<string, object>)f;
                                    if (fd.ContainsKey("sha256") && fd["sha256"] != null)
                                        plan.Hashes[fd["path"].ToString()] = fd["sha256"].ToString();
                                }
                            }
                        }
                    }
                    catch { }
                }
                else
                {
                    plan.ModNames = plan.Map.Select(m => m.Value.Split('/')[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                }
                foreach (var kv in plan.Map)
                    if (File.Exists(Abs(root, kv.Value))) plan.Overwrites++;
            }
            var kindText = plan.Kind == ZipKind.PeakPack ? "PeakModPacker 包"
                : plan.Kind == ZipKind.Thunderstore ? "Thunderstore 包（映射到 BepInEx/）"
                : plan.Kind == ZipKind.BepInExRooted ? "含 BepInEx/ 目录结构"
                : plan.Kind == ZipKind.BepInExRelative ? "BepInEx 相对结构"
                : "无法识别";
            plan.Describe = string.Format("{0} · {1} 个文件 · {2} · 覆盖已有文件 {3} 个",
                kindText, plan.Map.Count, FmtBytes(plan.TotalBytes), plan.Overwrites);
            return plan;
        }

        static readonly string[] AllowedRoots = { "plugins", "config", "patchers", "monomod", "core" };

        static bool SanitizeRel(string rel)
        {
            if (string.IsNullOrWhiteSpace(rel)) return false;
            rel = rel.Replace('\\', '/').TrimStart('/');
            if (rel.Contains("..")) return false;
            var segs = rel.Split('/');
            if (segs.Length == 0) return false;
            if (!AllowedRoots.Contains(segs[0], StringComparer.OrdinalIgnoreCase)) return false;
            foreach (var s in segs)
                if (s.Length == 0 || s.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
            return true;
        }

        public class InstallResult
        {
            public bool Ok;
            public string Error;
            public int InstalledFiles;
            public int WipedFiles;
            /// <summary>Files locked by the running game — replaced in place, old copy parked
            /// as "*.modsyncdel" and swept on the next tool start.</summary>
            public int DeferredFiles;
            public List<string> HashMismatches = new List<string>();
        }

        /// <summary>Extracts to a temp dir, verifies hashes (when present), then moves files
        /// into BepInEx. wipePlugins clears BepInEx/plugins first (no backup in either mode —
        /// the zip itself is the source of truth).</summary>
        public static InstallResult Install(string root, InstallPlan plan, bool wipePlugins,
            Action<string> status, Action<int, int> progress)
        {
            var result = new InstallResult();
            var staging = Path.Combine(Path.GetTempPath(), "PeakModPacker-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var locked = new List<string>();
            try
            {
                Directory.CreateDirectory(staging);
                // 1. extract to staging
                using (var archive = ZipFile.OpenRead(plan.ZipPath))
                {
                    int done = 0;
                    foreach (var kv in plan.Map)
                    {
                        var entry = archive.GetEntry(kv.Key);
                        if (entry == null) continue;
                        var target = Path.Combine(staging, kv.Value.Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        entry.ExtractToFile(target, true);
                        progress(++done, plan.Map.Count * 2);
                    }
                }
                // 2. verify hashes
                int i = 0;
                foreach (var kv in plan.Map)
                {
                    string expected;
                    if (plan.Hashes.TryGetValue(kv.Value, out expected))
                    {
                        var staged = Path.Combine(staging, kv.Value.Replace('/', Path.DirectorySeparatorChar));
                        if (File.Exists(staged))
                        {
                            string sha;
                            using (var fs = File.OpenRead(staged))
                            using (var h = SHA256.Create())
                                sha = BitConverter.ToString(h.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
                            if (!string.Equals(sha, expected, StringComparison.OrdinalIgnoreCase))
                                result.HashMismatches.Add(kv.Value);
                        }
                    }
                    progress(plan.Map.Count + ++i, plan.Map.Count * 2);
                }
                if (result.HashMismatches.Count > 0)
                {
                    result.Error = "校验失败: " + string.Join("; ", result.HashMismatches.Take(5));
                    return result;
                }
                // 3. optional wipe: remove every recognized mod's related files (the same
                // grouping the packer uses: mod files + matching configs), then clean up
                // empty folders. Unattributed leftovers outside those groups are kept.
                if (wipePlugins)
                {
                    status("移除现有 mod…");
                    var targets = new List<string>();
                    foreach (var mod in ScanMods(root))
                    {
                        foreach (var f in mod.Files.Concat(mod.ConfigFiles))
                            targets.Add(Abs(root, f.Path));
                    }
                    foreach (var abs in targets.Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        if (!File.Exists(abs)) continue;
                        try { File.Delete(abs); result.WipedFiles++; }
                        catch
                        {
                            try { Park(abs); result.WipedFiles++; result.DeferredFiles++; }
                            catch { locked.Add(abs); }
                        }
                    }
                    if (locked.Count > 0)
                    {
                        result.Error = "有 " + locked.Count + " 个文件无法删除（游戏可能正在运行）。请完全退出游戏后重试。";
                        return result;
                    }
                    // drop mod folders that are now empty
                    if (Directory.Exists(Plugins(root)))
                    {
                        foreach (var dir in Directory.GetDirectories(Plugins(root)))
                        {
                            try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { }
                        }
                    }
                }
                // 4. move into place
                status("安装文件…");
                foreach (var kv in plan.Map)
                {
                    var staged = Path.Combine(staging, kv.Value.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(staged)) continue;
                    var target = Abs(root, kv.Value);
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    if (File.Exists(target))
                    {
                        try { File.Delete(target); }
                        catch
                        {
                            // Loaded by the running game: park the old copy (rename is allowed
                            // even when locked) so the new file can take its place immediately.
                            Park(target);
                            result.DeferredFiles++;
                        }
                    }
                    File.Move(staged, target);
                    result.InstalledFiles++;
                }
                result.Ok = true;
                return result;
            }
            catch (Exception e)
            {
                result.Error = e.Message;
                return result;
            }
            finally
            {
                try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
            }
        }

        const string DeferredSuffix = ".modsyncdel";

        static void Park(string file)
        {
            string parked = file + DeferredSuffix;
            int n = 0;
            while (File.Exists(parked)) parked = file + "." + (++n) + DeferredSuffix;
            File.Move(file, parked);
        }

        /// <summary>Deletes "*.modsyncdel" leftovers (old copies that were locked by the
        /// running game when they got replaced). Nothing loads them; the sweep frees the disk.</summary>
        public static void SweepDeferred(string root)
        {
            try
            {
                var bepinex = BepInEx(root);
                if (!Directory.Exists(bepinex)) return;
                int swept = 0;
                foreach (var file in Directory.GetFiles(bepinex, "*" + DeferredSuffix, SearchOption.AllDirectories))
                {
                    try { File.Delete(file); swept++; } catch { }
                }
                if (swept > 0) Log2("swept " + swept + " replaced-while-running leftovers");
            }
            catch { }
        }

        static void Log2(string msg)
        {
            try { System.IO.File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log"), DateTime.Now + " " + msg + Environment.NewLine); } catch { }
        }

        public static string FmtBytes(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.0") + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024).ToString("0.0") + " MB";
            return (bytes / 1024.0 / 1024 / 1024).ToString("0.00") + " GB";
        }
    }
}
