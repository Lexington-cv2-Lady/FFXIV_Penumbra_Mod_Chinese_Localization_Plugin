using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualBasic.FileIO;
using Penumbra.Api.Enums;

namespace FFXIVPenumbraHanhua.Services;

/// <summary> 备份管理：扫描模组备份（.json.bak_* 与独立版日期_时间备份.zip）、还原、删除（回收站）、手动备份。 </summary>
public sealed class BackupManager
{
    private readonly ModFileService _files;
    private readonly PenumbraService _penumbra;
    private readonly AppLog _log;
    private readonly EnglishSnapshotService _snapshot;
    private readonly MarkService _mark;

    public string LastResult { get; private set; } = "";

    /// <summary> 恢复备份成功后触发（用于开发功能：自动重跑未翻译模组汉化）。 </summary>
    public event Action? RestoreCompleted;

    public sealed record BackupInfo(string ModDir, string FileName, string BakPath, DateTime Time, bool IsZip);

    public BackupManager(ModFileService files, PenumbraService penumbra, AppLog log,
        EnglishSnapshotService snapshot, MarkService mark)
    {
        _files = files;
        _penumbra = penumbra;
        _log = log;
        _snapshot = snapshot;
        _mark = mark;
    }

    /// <summary> 扫描模组根目录下所有备份，按时间新→旧。识别：.json.bak_yyyyMMdd_HHmmss 与 yyyy-MM-dd_HH-mm-ss备份.zip。 </summary>
    public List<BackupInfo> ListBackups(string modRoot)
    {
        var list = new List<BackupInfo>();
        if (string.IsNullOrEmpty(modRoot) || !Directory.Exists(modRoot)) return list;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(modRoot))
            {
                var modDir = Path.GetFileName(dir);
                foreach (var f in Directory.EnumerateFiles(dir))
                {
                    var name = Path.GetFileName(f);
                    if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && name.Contains("备份"))
                    {
                        // 独立版 zip：2026-09-11_03-04-01备份.zip
                        var head = name.Replace("备份.zip", "", StringComparison.OrdinalIgnoreCase);
                        if (DateTime.TryParseExact(head, "yyyy-MM-dd_HH-mm-ss", null,
                                System.Globalization.DateTimeStyles.None, out var zt))
                        {
                            list.Add(new BackupInfo(modDir, name, f, zt, true));
                        }
                    }
                    else if (name.Contains(".json.bak_"))
                    {
                        var idx = name.IndexOf(".bak_", StringComparison.Ordinal);
                        if (DateTime.TryParseExact(name[(idx + 5)..], "yyyyMMdd_HHmmss", null,
                                System.Globalization.DateTimeStyles.None, out var t))
                        {
                            list.Add(new BackupInfo(modDir, name, f, t, false));
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log.Error("扫描备份失败：" + ex.Message);
        }
        return list.OrderByDescending(b => b.Time).ToList();
    }

    /// <summary> 还原备份：json.bak 覆盖回原名；zip 解压覆盖模组目录。还原后备份移至回收站，并触发 Penumbra 重载。 </summary>
    public bool Restore(string modRoot, BackupInfo b)
    {
        try
        {
            if (b.IsZip)
            {
                var targetDir = Path.Combine(modRoot, b.ModDir);
                var tmp = Path.Combine(Path.GetTempPath(), "pm_restore_" + Guid.NewGuid().ToString("N"));
                ZipFile.ExtractToDirectory(b.BakPath, tmp);

                // meta.json 选择性还原：只把备份的英文 Groups 合并进当前文件，
                // 保留当前文件的 Meta 节点（Penumbra 选项状态），避免「还原后模组设置也还原」
                var tmpMeta = Path.Combine(tmp, "meta.json");
                var curMeta = Path.Combine(targetDir, "meta.json");
                if (File.Exists(tmpMeta) && File.Exists(curMeta) && MergeMetaKeepState(tmpMeta, curMeta))
                {
                    File.Delete(tmpMeta); // 已合并，不再整体覆盖
                }

                CopyOverwrite(tmp, targetDir);
                try { Directory.Delete(tmp, true); } catch { /* 忽略清理失败 */ }
            }
            else
            {
                var bakName = Path.GetFileName(b.BakPath);
                var orig = bakName[..bakName.IndexOf(".bak_", StringComparison.Ordinal)] + ".json";
                var origPath = Path.Combine(modRoot, b.ModDir, orig);
                File.Copy(b.BakPath, origPath, true);
            }

            SendToRecycleBin(b.BakPath);

            // 还原后模组回退为备份时的内容（通常为英文）：按文档约定清除「已翻译」标记
            try { _mark.Remove(b.ModDir); } catch { /* 标记删除失败不影响还原 */ }

            // 还原的是英文原版：同步更新英文快照（保证后续可覆写）
            try
            {
                var targetDir = Path.Combine(modRoot, b.ModDir);
                foreach (var f in _files.ReadModFiles(targetDir))
                {
                    _snapshot.SaveIfEnglish(b.ModDir, f.FileName, File.ReadAllText(f.Path));
                }
            }
            catch (Exception)
            {
                /* 快照更新失败不影响还原 */
            }

            // ReloadMod 按（目录, 名称）匹配：只传目录可能重载不到，优先从已加载列表取显示名
            var entry = _penumbra.Mods.FirstOrDefault(m => m.Directory == b.ModDir);
            if (entry == null)
                _log.Warn($"[恢复备份] 未在 Penumbra 列表找到目录「{b.ModDir}」，重载未触发（游戏内可能需手动刷新）");
            var ec = _penumbra.Reload(b.ModDir, entry?.Name ?? "");
            if (ec != PenumbraApiEc.Success)
                _log.Warn($"[恢复备份] Penumbra 重载返回 {ec}（目录={b.ModDir}，名称={entry?.Name ?? "(空)"}），游戏内可能未刷新");
            else
                _log.Info($"[恢复备份] 已还原 {b.ModDir}（{b.FileName}，原备份已移至回收站，已清除「已翻译」标记，已重载 Penumbra）");
            RestoreCompleted?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            _log.Error($"[恢复备份] 还原失败 {b.BakPath}：{ex.Message}");
            return false;
        }
    }

    /// <summary> 删除备份（移至回收站）。 </summary>
    public bool Delete(string modRoot, BackupInfo b)
    {
        try
        {
            SendToRecycleBin(b.BakPath);
            _log.Info($"[备份] 已删除 {b.ModDir}\\{b.FileName}（移至回收站）");
            return true;
        }
        catch (Exception ex)
        {
            _log.Error($"[备份] 删除失败 {b.BakPath}：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 自动备份（启动扫描）：为尚无任何备份 zip 的模组各建一个。返回新建数。
    /// </summary>
    public int BackupMissing(IReadOnlyList<ModEntry> mods, string modRoot, int maxBackups)
    {
        var n = 0;
        var noContent = 0;
        if (string.IsNullOrEmpty(modRoot)) return 0;
        foreach (var m in mods)
        {
            try
            {
                var dir = Path.Combine(modRoot, m.Directory);
                if (!Directory.Exists(dir)) continue;
                if (Directory.GetFiles(dir, "*备份.zip").Length > 0) continue;
                if (CreateModZip(dir, maxBackups) != null) n++;
                else noContent++; // 无选项内容的模组：正常跳过（不记错误，仅汇总为一条提示）
            }
            catch
            {
                /* 单个失败不中断 */
            }
        }
        if (n > 0) _log.Info($"[自动备份] 已为 {n} 个尚无备份的模组创建备份");
        if (noContent > 0)
            _log.Info($"[自动备份] {noContent} 个模组无可备份的选项内容（纯文件替换类，如武器/动作替换），已跳过");
        return n;
    }

    /// <summary>
    /// 自动备份（新增模组事件）：模组尚无备份时立即建一个。
    /// </summary>
    public void BackupNew(string modDirectory, string modRoot, int maxBackups)
    {
        try
        {
            var dir = Path.Combine(modRoot, modDirectory);
            if (!Directory.Exists(dir)) return;
            if (Directory.GetFiles(dir, "*备份.zip").Length > 0) return;
            if (CreateModZip(dir, maxBackups) != null)
                _log.Info($"[自动备份] 新模组 {modDirectory} 已自动备份");
        }
        catch
        {
            /* 失败静默，不影响 Penumbra */
        }
    }

    /// <summary> 手动备份模组全部文件为 zip（yyyy-MM-dd_HH-mm-ss备份.zip），轮转保留 maxBackups 份。返回备份数（0/1）。 </summary>
    public int ManualBackup(string modDirPath, int maxBackups)
    {
        var zip = CreateModZip(modDirPath, maxBackups);
        return zip != null ? 1 : 0;
    }

    /// <summary> 创建模组 zip 备份（含日志）。 </summary>
    /// <remarks>
    /// 无 meta.json Groups / group_*.json 的模组（纯文件替换类，如武器/动作替换）**本来就没有可备份的选项内容**，
    /// 属正常情况：静默返回 null、不记错误——否则每次启动扫描都会重试并记一条错误，刷屏并误导用户以为出故障。
    /// 只有「有内容却打包失败」才记错误。
    /// </remarks>
    public string? CreateModZip(string modDirPath, int maxBackups)
    {
        if (_files.ReadModFiles(modDirPath).Count == 0) return null; // 无可备份内容：正常跳过，不记日志
        var zip = _files.CreateModZip(modDirPath, maxBackups);
        if (zip != null)
        {
            _log.Info($"[备份] 已创建 {Path.GetFileName(zip)}（{Path.GetFileName(modDirPath)}）");
        }
        else
        {
            _log.Error($"[备份] 创建备份失败：{modDirPath}（打包异常）");
        }
        return zip;
    }

    /// <summary>
    /// 把备份 meta.json 的英文 Groups（顶层 Groups 或 Mod.Groups）合并进当前 meta.json，
    /// 当前文件的其余节点（Meta 选项状态、Name 等）全部保留。成功返回 true。
    /// 返回 false 时调用方回退整体覆盖，保证还原可用。
    /// </summary>
    private static bool MergeMetaKeepState(string backupMetaPath, string currentMetaPath)
    {
        try
        {
            var bak = JsonNode.Parse(File.ReadAllText(backupMetaPath)) as JsonObject;
            var cur = JsonNode.Parse(File.ReadAllText(currentMetaPath)) as JsonObject;
            if (bak == null || cur == null) return false;

            var bakGroups = bak["Groups"] ?? (bak["Mod"] as JsonObject)?["Groups"];
            if (bakGroups == null) return false;

            if (cur["Groups"] != null)
            {
                cur["Groups"] = bakGroups.DeepClone();
            }
            else if (cur["Mod"] is JsonObject curMod && curMod["Groups"] != null)
            {
                curMod["Groups"] = bakGroups.DeepClone();
            }
            else
            {
                return false;
            }

            File.WriteAllText(currentMetaPath, cur.ToJsonString(JsonFile.Indented));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void CopyOverwrite(string srcDir, string dstDir)
    {
        if (!Directory.Exists(dstDir)) Directory.CreateDirectory(dstDir);
        foreach (var f in Directory.EnumerateFiles(srcDir))
        {
            var name = Path.GetFileName(f);
            File.Copy(f, Path.Combine(dstDir, name), true);
        }
        foreach (var sub in Directory.EnumerateDirectories(srcDir))
        {
            CopyOverwrite(sub, Path.Combine(dstDir, Path.GetFileName(sub)));
        }
    }

    private static void SendToRecycleBin(string path)
    {
        if (File.Exists(path))
        {
            FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        }
    }
}
