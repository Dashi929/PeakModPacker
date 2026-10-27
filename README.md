# PeakModPacker — PEAK Mod 一键打包 / 解压工具

一个 Windows 桌面小工具，用于给 [PEAK](https://store.steampowered.com/app/3527290/PEAK/) 玩家之间分发 BepInEx mod：

- **一键打包**：勾选 mod → 自动找出每个 mod 的全部相关文件 → 压缩成 zip 放到桌面
- **一键解压**：选一个 zip（支持拖拽）→ 自动识别格式 → 一键装回游戏

绿色免安装，无需 .NET 运行时（依赖系统自带的 .NET Framework 4.6.2+），高 DPI/4K 适配。

## 功能特性

**打包**
- 自动扫描 `BepInEx/plugins`，按 mod 分组（文件夹 mod / 散装 DLL 按 BepInPlugin GUID 识别 / 散装文件）
- 自动附带每个 mod 的全部相关文件：文件夹全部内容（DLL/AssetBundle/音效/字体）、对应的 `BepInEx/config` 配置
- **自动依赖解析**：读取 DLL 的 `[BepInDependency]` 声明，递归附带依赖的 mod（如 PEAKLib）
- plugins 根目录的散装文件（字体/文本表等）自动随包
- 包内含 `peak-modpack.json` 清单（每个文件的 SHA256）

**解压**
- 支持三种 zip 格式：本工具的包 / Thunderstore 包（自动映射到 BepInEx/）/ 普通 BepInEx 目录结构包
- 安装前在临时目录展开并**逐文件 SHA256 校验**，损坏/篡改直接拒绝
- 可选"清除旧 mod"：逐个识别现有 mod 后精准删除其文件再安装（非文件夹清空）
- 游戏运行时禁止安装（避免文件占用导致半安装状态）

**安全**
- 路径净化：只允许写入 `plugins / config / patchers / monomod / core`，拒绝路径穿越

## 使用

1. 解压任意目录，运行 `PeakModPacker.exe`（首次自动定位 Steam 库中的 PEAK，也可手动浏览）
2. 打包：勾选 mod → 点「📦 一键打包到桌面」→ zip 出现在桌面
3. 分发 zip 给朋友；对方用同款工具「📤 一键解压安装」即可

命令行：
- `PeakModPacker.exe --selftest` — 运行内置自测（结果写入 selftest-report.txt）
- `PeakModPacker.exe <xxx.zip>` — 启动时直接载入指定压缩包

## 构建

需要 .NET SDK（6+）：

```powershell
dotnet build -c Release
# 或部署到桌面/PeakModPacker：
powershell -ExecutionPolicy Bypass -File build.ps1
```

## 常见问题

- **装到别人电脑后 mod 文字显示为「口」**：让对方把游戏语言切换为简体中文（多数中文 mod 依赖游戏字体）。
- **解压时提示文件被占用**：先完全退出游戏（工具也会自动检测并拦截）。

## License

MIT
