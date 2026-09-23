using System;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using FFXIVPenumbraHanhua.Services;
using FFXIVPenumbraHanhua.Windows;

namespace FFXIVPenumbraHanhua;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private const string CommandName = "/pmh";

    /// <summary> 旧版中文插件 ID（InternalName）；用于把旧配置文件迁移到新的英文 ID。 </summary>
    private const string LegacyInternalName = "FFXIV_penumbra的模组汉化插件";

    private string _initialTranslationPath;
    private string _initialDictionaryPath;

    // 全自动汉化触发调度（D2/D3：启动/新模组自动，默认关）
    private DateTime _startupHanuaDeadline;   // 启动自动汉化触发时刻（插件加载后 10 秒，等 Penumbra/IPC/词典稳定）
    private bool _startupHanuaFired;          // 启动触发是否已执行过（一生只跑一次）
    private DateTime _newModArrivalTime = DateTime.MinValue; // 新模组到达时刻（防抖基准）
    private bool _newModArmed;                // 新模组自动汉化是否已挂起待触发

    public Configuration Configuration { get; init; }
    public PenumbraService Penumbra { get; init; }
    public DictionaryService Dict { get; init; }
    public ModFileService ModFiles { get; init; }
    public HanhuaService Hanhua { get; init; }
    public AppLog AppLog { get; init; }
    public MarkService Mark { get; init; }
    public EnglishSnapshotService Snapshot { get; init; }
    public ExtractService Extract { get; init; }
    public AiTranslateService AiTranslate { get; init; }
    public ImportService Import { get; init; }
    public BackupManager Backup { get; init; }
    public SumupService Sumup { get; init; }
    public WikiExportService Wiki { get; init; }
    public ModRestoreService ModRestore { get; init; }

    public readonly WindowSystem WindowSystem = new("FFXIVPenumbraHanhua");
    public MainWindow MainWindow { get; init; }
    public DictionaryWindow DictionaryWindow { get; init; }
    public TranslatePipelineWindow PipelineWindow { get; init; }
    public BackupWindow BackupWindow { get; init; }
    public AiSettingsWindow AiSettingsWindow { get; init; }
    public AiConfigWindow AiConfigWindow { get; init; }
    public WikiExportWindow WikiExportWindow { get; init; }
    public LogWindow LogWindow { get; init; }
    public FileListWindow FileListWindow { get; init; }
    public OptionEditWindow OptionEditWindow { get; init; }
    public DevWindow DevWindow { get; init; }

    public Plugin()
    {
        MigrateLegacyConfig(); // 插件 ID 由中文改为英文：先迁移旧配置（含 API Key），再读取
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        EnsureDefaultDataDirectories(); // 新用户开箱即用：词典/翻译目录为空时默认建在插件安装目录
        // 旧版单一 API Key 字段一次性迁移：落到「当前服务商」名下（之后按服务商分存，切换/删除不再串 Key）
        Configuration.AiApiKeys ??= new();
        var hasAnyKey = false;
        foreach (var kvp in Configuration.AiApiKeys)
            if (!string.IsNullOrWhiteSpace(kvp.Value)) { hasAnyKey = true; break; }
        if (!hasAnyKey && !string.IsNullOrWhiteSpace(Configuration.AiApiKey))
        {
            var legacyName = AiTranslateService.CurrentProviderName(Configuration);
            Configuration.AiApiKeys[legacyName] = Configuration.AiApiKey;
            Configuration.AiApiKey = "";
            Configuration.Save();
            Log.Information($"已把旧版单一 API Key 迁移到「{legacyName}」（按服务商分存）");
        }
        AppLog = new AppLog(Path.Combine(PluginInterface.GetPluginConfigDirectory(), "汉化日志.log"));
        Penumbra = new PenumbraService(PluginInterface, AppLog);
        Dict = new DictionaryService(AppLog);
        ModFiles = new ModFileService { MaxBackups = Configuration.BackupCount };
        Snapshot = new EnglishSnapshotService(() => Configuration.DictionaryPath);
        Hanhua = new HanhuaService(Penumbra, Dict, ModFiles, Snapshot, AppLog);
        Mark = new MarkService(() => Penumbra.GetModRoot() ?? "");
        Extract = new ExtractService(Dict, ModFiles, AppLog);
        AiTranslate = new AiTranslateService(AppLog);
        AiTranslateService.ApplyProxyConfig(Configuration); // 启动即按配置应用代理（海外服务商需要）
        Import = new ImportService(ModFiles, Penumbra, AppLog, Mark, Snapshot);
        Backup = new BackupManager(ModFiles, Penumbra, AppLog, Snapshot, Mark);
        Sumup = new SumupService(AppLog, ModFiles, Snapshot);
        Wiki = new WikiExportService(AppLog);
        ModRestore = new ModRestoreService();

        MainWindow = new MainWindow(this, Penumbra, Dict, Hanhua);
        DictionaryWindow = new DictionaryWindow(this);
        PipelineWindow = new TranslatePipelineWindow(this);
        BackupWindow = new BackupWindow(this);
        AiSettingsWindow = new AiSettingsWindow(this);
        AiConfigWindow = new AiConfigWindow(this);
        WikiExportWindow = new WikiExportWindow(this);
        DevWindow = new DevWindow(this);

        // 开发功能：恢复备份后自动重跑未翻译模组汉化
        Backup.RestoreCompleted += () =>
        {
            if (Configuration.AutoHanhuaAfterRestore)
            {
                AppLog.Info("[开发功能] 恢复备份完成，自动重跑未翻译模组汉化");
                MainWindow.StartAutoHanhua();
            }
        };
        LogWindow = new LogWindow(this);
        FileListWindow = new FileListWindow(this);
        OptionEditWindow = new OptionEditWindow(this);

        WindowSystem.AddWindow(MainWindow);
        WindowSystem.AddWindow(DictionaryWindow);
        WindowSystem.AddWindow(PipelineWindow);
        WindowSystem.AddWindow(BackupWindow);
        WindowSystem.AddWindow(AiSettingsWindow);
        WindowSystem.AddWindow(AiConfigWindow);
        WindowSystem.AddWindow(WikiExportWindow);
        WindowSystem.AddWindow(LogWindow);
        WindowSystem.AddWindow(FileListWindow);
        WindowSystem.AddWindow(OptionEditWindow);
        WindowSystem.AddWindow(DevWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "打开 FFXIV Penumbra 模组汉化插件主窗口"
        });

        _initialTranslationPath = Configuration.TranslationPath;
        _initialDictionaryPath = Configuration.DictionaryPath;

        PluginInterface.UiBuilder.Draw += DrawAll;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleDictionaryUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;

        // 插件加载即尝试连接 Penumbra 并加载词典；确保词典目录下的 .英文快照 目录存在
        Penumbra.Refresh();

        // 自动备份：启动为无备份模组补备份；新增模组事件即时备份
        try { Backup.BackupMissing(Penumbra.Mods, Penumbra.GetModRoot() ?? "", Configuration.BackupCount); } catch { }
        Penumbra.ModAddedEvent += d => { try { Backup.BackupNew(d, Penumbra.GetModRoot() ?? "", Configuration.BackupCount); } catch { } };
        // 新模组自动汉化：只挂起计时，真正触发在 DrawAll 里延迟 8 秒（等批量导入稳定、Penumbra 列表刷完）
        Penumbra.ModAddedEvent += _ =>
        {
            if (!Configuration.AutoHanhuaOnNewMod) return;
            _newModArrivalTime = DateTime.Now;
            _newModArmed = true;
        };
        _startupHanuaDeadline = DateTime.Now.AddSeconds(10);
        ReloadDictionary();
        Snapshot.EnsureRoot();
        // 启动自愈失效标记：内容被 Penumbra 升级 / 重下 / 手动替换还原成英文的模组，清除残留标记、回到未翻译列表
        try
        {
            var healed = Mark.PruneStaleMarks(Penumbra.Mods, Dict, ModFiles);
            if (healed.Count > 0)
                AppLog.Info($"[启动] {healed.Count} 个模组内容已还原成英文，已清除失效标记：{string.Join("、", healed.Take(5))}{(healed.Count > 5 ? " 等" : "")}");
        }
        catch { /* 自愈失败不阻断启动 */ }
        // 启动清理：删除独立版遗留的旧 .json.bak 垃圾备份（时间戳格式按份数轮转保留）
        ModFileService.CleanupLegacyBak(Penumbra.GetModRoot(), Configuration.TranslationPath, Configuration.DictionaryPath,
            Math.Max(1, Configuration.BackupCount));
        Log.Information("FFXIV_penumbra的模组汉化插件 已加载");
    }

    /// <summary> 带边框的结果/日志显示区（全插件统一风格：边框 Child + 自动换行，高 56px）。 </summary>
    internal static void ResultBox(string id, string text, string? placeholder = null, float height = 56f)
    {
        if (ImGui.BeginChild(id, new Vector2(-1f, height), true))
        {
            if (!string.IsNullOrEmpty(text))
            {
                ImGui.TextWrapped(text);
            }
            else if (!string.IsNullOrEmpty(placeholder))
            {
                ImGui.TextDisabled(placeholder);
            }
        }
        ImGui.EndChild();
    }

    /// <summary> 统一绘制：给所有窗口加明显边框（窗口边框 + 内部 Child 边框统一），再绘制窗口系统。 </summary>
    private void DrawAll()
    {
        AutoHanhuaTick(); // 全自动汉化触发调度（启动延迟 / 新模组防抖）
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 2.5f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, 2f); // Child 边框与窗口边框统一
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 6f);
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0.42f, 0.72f, 1f, 0.9f));
        try
        {
            WindowSystem.Draw();
        }
        finally
        {
            ImGui.PopStyleColor();
            ImGui.PopStyleVar();
            ImGui.PopStyleVar();
            ImGui.PopStyleVar();
            ImGui.PopStyleVar();
        }
    }

    /// <summary> 按当前配置路径重载词典。 </summary>
    public void ReloadDictionary()
    {
        Dict.Load(Configuration.DictionaryPath);
    }

    /// <summary>
    /// 全自动汉化触发调度（每帧 Draw 调用，UI 线程）：
    /// ① 启动自动：加载 10 秒后触发一次（一生一次，等 Penumbra/IPC/词典稳定）；
    /// ② 新模组自动：ModAddedEvent 挂起后延迟 8 秒触发（批量导入防抖；已有任务在跑时 StartAutoHanhua 内部自动跳过）。
    /// 开关默认关，未配 Key / 无未翻译模组时 StartAutoHanhua 内部静默跳过。
    /// </summary>
    private void AutoHanhuaTick()
    {
        try
        {
            if (!_startupHanuaFired && Configuration.AutoHanhuaOnStart &&
                DateTime.Now >= _startupHanuaDeadline)
            {
                _startupHanuaFired = true;
                AppLog.Info("[全自动] 启动延迟触发");
                MainWindow.StartAutoHanhua();
            }
            if (_newModArmed && Configuration.AutoHanhuaOnNewMod &&
                DateTime.Now >= _newModArrivalTime.AddSeconds(8))
            {
                _newModArmed = false;
                AppLog.Info("[全自动] 新模组到达延迟触发");
                MainWindow.StartAutoHanhua();
            }
        }
        catch (Exception ex)
        {
            AppLog.Error($"[全自动] 触发调度失败：{ex.Message}");
        }
    }

    /// <summary> 目录变更后自动迁移：翻译目录 -> 翻译 json；词典目录 -> .英文快照 / wiki / AI知识库 文件夹与词典 json。目标已存在不覆盖。 </summary>
    public void MigrateDirectories()
    {
        // 词典目录变更：迁移 .英文快照、wiki/AI知识库 文件夹与词典 json
        if (_initialDictionaryPath != Configuration.DictionaryPath)
        {
            var oldD = _initialDictionaryPath;
            var newD = Configuration.DictionaryPath;
            _initialDictionaryPath = newD;
            if (!string.IsNullOrEmpty(oldD) && !string.IsNullOrEmpty(newD) && oldD != newD)
            {
                var moved = 0;
                if (TryMoveDir(oldD, newD, "wiki_术语对照")) moved++;
                if (TryMoveDir(oldD, newD, "AI知识库")) moved++;
                if (TryMoveDir(oldD, newD, ".英文快照")) moved++;
                if (TryMoveFile(oldD, newD, "我的翻译.json")) moved++;
                if (TryMoveFile(oldD, newD, "个性翻译.json")) moved++;
                if (TryMoveFile(oldD, newD, "内置wiki_术语对照.json")) moved++;
                AppLog.Info($"[配置] 词典目录变更：迁移 {moved} 项 -> {newD}");
            }
        }

        // 翻译目录变更：把旧位置遗留的 .英文快照 迁往词典目录（v2.6.7 起快照归属词典目录）
        if (_initialTranslationPath != Configuration.TranslationPath)
        {
            var oldT = _initialTranslationPath;
            _initialTranslationPath = Configuration.TranslationPath;
            var legacy = Path.Combine(oldT, ".英文快照");
            if (Directory.Exists(legacy) && Configuration.DictionaryPath.Length > 0)
            {
                var dst = Path.Combine(Configuration.DictionaryPath, ".英文快照");
                try
                {
                    if (!Directory.Exists(dst))
                    {
                        Directory.Move(legacy, dst);
                        AppLog.Info($"[配置] 翻译目录变更：旧 .英文快照 已迁往词典目录 -> {dst}");
                    }
                    else
                    {
                        AppLog.Info($"[配置] 翻译目录变更：旧 .英文快照 仍留在 {oldT}（词典目录已有 .英文快照，不覆盖）");
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Info($"[配置] 翻译目录变更：旧 .英文快照 迁移失败（{ex.Message}），保留在 {oldT}");
                }
            }
        }
    }

    private static bool TryMoveDir(string oldRoot, string newRoot, string name)
    {
        var src = Path.Combine(oldRoot, name);
        var dst = Path.Combine(newRoot, name);
        if (!Directory.Exists(src) || Directory.Exists(dst)) return false;
        try
        {
            Directory.Move(src, dst);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryMoveFile(string oldRoot, string newRoot, string name)
    {
        var src = Path.Combine(oldRoot, name);
        var dst = Path.Combine(newRoot, name);
        if (!File.Exists(src) || File.Exists(dst)) return false;
        try
        {
            File.Move(src, dst);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 插件 ID 从中文（FFXIV_penumbra的模组汉化插件）改为英文后，把旧配置文件复制到新 ID 目录，
    /// 避免升级后丢失目录设置与按服务商分存的 API Key。仅在新配置尚不存在时迁移。
    /// </summary>
    private void MigrateLegacyConfig()
    {
        try
        {
            var newFile = PluginInterface.ConfigFile; // ...\pluginConfigs\FFXIV_Penumbra_Mod_Chinese_Localization_Plugin.json
            if (newFile.Exists) return;
            var dir = newFile.DirectoryName;
            if (string.IsNullOrEmpty(dir)) return;
            var oldFile = Path.Combine(dir, LegacyInternalName + ".json");
            if (!File.Exists(oldFile)) return;

            var newDir = newFile.Directory; // ...\pluginConfigs\<新ID>\
            if (newDir is { Exists: false }) newDir.Create();

            // 1) 配置文件本体
            File.Copy(oldFile, newFile.FullName, overwrite: true);
            // 2) 旧配置目录（若存在：日志等）一并迁移，已存在的不覆盖
            var oldDir = Path.Combine(dir, LegacyInternalName);
            if (Directory.Exists(oldDir) && newDir != null)
            {
                CopyDirIfMissing(oldDir, newDir.FullName);
            }
            Log.Information($"已把旧插件配置（{LegacyInternalName}）迁移到新 ID（{PluginInterface.InternalName}）");
        }
        catch (Exception ex)
        {
            Log.Warning($"旧插件配置迁移失败（不影响新配置）：{ex.Message}");
        }
    }

    /// <summary>
    /// 新用户开箱即用：词典/翻译目录为空时，默认建在插件安装目录下（保留「目录和词典管理」里手动修改与自动迁移功能）。
    /// 安装版 dll 在 installedPlugins\&lt;ID&gt;\&lt;版本&gt;\ 下——数据放 &lt;ID&gt;\ 层（版本更新不丢）；
    /// dev 版 dll 在 devPlugins\&lt;ID&gt;\ 下——直接用该层。
    /// </summary>
    private void EnsureDefaultDataDirectories()
    {
        try
        {
            var changed = false;
            if (string.IsNullOrWhiteSpace(Configuration.DictionaryPath))
            {
                Configuration.DictionaryPath = DefaultDataDir("词典目录");
                changed = true;
            }
            if (string.IsNullOrWhiteSpace(Configuration.TranslationPath))
            {
                Configuration.TranslationPath = DefaultDataDir("翻译目录");
                changed = true;
            }
            if (changed)
            {
                Configuration.Save();
                Log.Information($"已按默认位置创建数据目录：词典 {Configuration.DictionaryPath} / 翻译 {Configuration.TranslationPath}");
            }
            Directory.CreateDirectory(Configuration.DictionaryPath);
            Directory.CreateDirectory(Configuration.TranslationPath);
        }
        catch (Exception ex)
        {
            Log.Warning($"默认数据目录创建失败（可在「目录和词典管理」手动设置）：{ex.Message}");
        }
    }

    /// <summary> 默认数据根：安装版取 installedPlugins\&lt;ID&gt;\，dev 版取 devPlugins\&lt;ID&gt;\。 </summary>
    private string DefaultDataDir(string name)
    {
        var dllDir = Path.GetDirectoryName(PluginInterface.AssemblyLocation?.FullName) ?? "";
        var root = dllDir;
        try
        {
            var parent = Directory.GetParent(dllDir);
            // 安装版：dll 的上级目录就是 <ID>（当前目录是版本号文件夹），数据放 <ID> 层
            if (parent != null && string.Equals(parent.Name, PluginInterface.InternalName, StringComparison.OrdinalIgnoreCase))
                root = parent.FullName;
        }
        catch
        {
            /* 取父目录失败则退回 dll 所在目录 */
        }
        return Path.Combine(root, name);
    }

    private static void CopyDirIfMissing(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src))
        {
            var t = Path.Combine(dst, Path.GetFileName(f));
            if (!File.Exists(t)) File.Copy(f, t);
        }
    }

    public void Dispose()
    {
        AppLog.Info("[插件] 卸载，会话结束");
        PluginInterface.UiBuilder.Draw -= DrawAll;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleDictionaryUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;

        WindowSystem.RemoveAllWindows();

        MainWindow.Dispose();
        DictionaryWindow.Dispose();
        PipelineWindow.Dispose();
        BackupWindow.Dispose();
        AiSettingsWindow.Dispose();
        AiConfigWindow.Dispose();
        WikiExportWindow.Dispose();
        LogWindow.Dispose();
        FileListWindow.Dispose();
        OptionEditWindow.Dispose();
        Penumbra.Dispose();

        CommandManager.RemoveHandler(CommandName);
    }

    private void OnCommand(string command, string args)
    {
        MainWindow.Toggle();
    }

    public void ToggleMainUi() => MainWindow.Toggle();
    public void ToggleDictionaryUi() => DictionaryWindow.Toggle();
    public void TogglePipelineUi() => PipelineWindow.Toggle();
    public void ToggleBackupUi() => BackupWindow.Toggle();
    public void ToggleAiSettingsUi() => AiSettingsWindow.Toggle();
    public void ToggleAiConfigUi() => AiConfigWindow.Toggle();
    public void ToggleWikiUi() => WikiExportWindow.Toggle();
    public void ToggleLogUi() => LogWindow.Toggle();
    public void ToggleDevUi() => DevWindow.Toggle();
    public void ToggleFileListUi() => FileListWindow.Toggle();
    public void ToggleOptionEditUi() => OptionEditWindow.Toggle();
}
