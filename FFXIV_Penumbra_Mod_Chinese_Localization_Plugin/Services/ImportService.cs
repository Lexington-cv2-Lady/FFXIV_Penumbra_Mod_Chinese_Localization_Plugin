using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace FFXIVPenumbraHanhua.Services;

/// <summary> 翻译写入MOD：按已加载的词典（我的翻译/个性翻译/wiki/AI知识库）直接写回模组文件。 </summary>
public sealed class ImportService
{
    private readonly ModFileService _files;
    private readonly PenumbraService _penumbra;
    private readonly AppLog _log;
    private readonly MarkService _mark;
    private readonly EnglishSnapshotService _snapshot;

    public string LastResult { get; private set; } = "";

    public ImportService(ModFileService files, PenumbraService penumbra, AppLog log, MarkService mark,
        EnglishSnapshotService snapshot)
    {
        _files = files;
        _penumbra = penumbra;
        _log = log;
        _mark = mark;
        _snapshot = snapshot;
    }

    /// <summary>
    /// 词典直写回（翻译写入MOD）：直接读取已加载的词典译文（我的翻译/个性翻译/wiki/AI知识库），
    /// 应用到指定模组的组名 / 选项名 / 描述，写回模组文件并重载。
    /// overwrite=false：已含中文的条目跳过；overwrite=true：已中文条目按英文快照重查词典，可覆写旧译法。
    /// </summary>
    public int ApplyDictionary(string modRoot, DictionaryService dict, IReadOnlyList<ModEntry> mods, bool overwrite = false)
    {
        if (string.IsNullOrEmpty(modRoot) || !Directory.Exists(modRoot))
        {
            LastResult = "无法获取 Penumbra 模组根目录";
            return -1;
        }

        var totalWritten = 0;
        var totalBackups = 0;
        var errors = new List<string>();
        var reloaded = new Dictionary<string, string>();  // 目录 -> 显示名（ReloadMod 需按二元组匹配）
        var completed = new Dictionary<string, string>(); // 本次有写入或已无待翻译内容的模组（用于建「已翻译」标记）
        var modBackedUp = new HashSet<string>();

        foreach (var mod in mods)
        {
            var modDirPath = Path.Combine(modRoot, mod.Directory);
            if (!Directory.Exists(modDirPath))
            {
                errors.Add(mod.Directory + "：目录不存在");
                continue;
            }

            var files = _files.ReadModFiles(modDirPath);
            if (files.Count == 0) continue; // 无 meta.json / group_*.json 的模组（本身没有选项）

            var wroteAny = false;
            var anyContent = false;              // 是否存在可翻译条目（避免空 group 文件被误判为已完成）
            var allDone = true;                  // 该模组是否已无待翻译内容（全中文 / 黑名单专名 / 词典可覆盖）
            foreach (var fileInfo in files)
            {
                var groupNames = new Dictionary<int, string>();
                var optionNames = new Dictionary<(int, int), string>();
                var optionDescs = new Dictionary<(int, int), string>();
                var fileName = Path.GetFileName(fileInfo.Path);
                var snapInfo = overwrite ? _snapshot.GetEnglish(mod.Directory, fileInfo.FileName) : null;

                foreach (var g in fileInfo.Groups)
                {
                    if (g.Name.Length > 0) anyContent = true;
                    if (!IsDone(dict, fileName, "Name", g.Name)) allDone = false;
                    var gName = ApplyLookup(dict, fileName, "Name", g.Name);
                    if (gName != null) groupNames[g.Index] = gName;

                    // 覆写旧译法：已中文组名按英文快照重查词典
                    if (overwrite && snapInfo != null && dict.ContainsChinese(g.Name))
                    {
                        var enG = snapInfo.Groups.FirstOrDefault(x => x.Index == g.Index)?.Name;
                        if (!string.IsNullOrWhiteSpace(enG) && !dict.ContainsChinese(enG))
                        {
                            var tG = Translator.Translate(enG, $"{fileName}||Name||{enG}", dict);
                            if (!string.IsNullOrWhiteSpace(tG) && tG != g.Name)
                                groupNames[g.Index] = tG;
                        }
                    }

                    foreach (var o in g.Options)
                    {
                        if (o.Name.Length > 0 || o.Description.Length > 0) anyContent = true;
                        if (!IsDone(dict, fileName, "Opt", o.Name)) allDone = false;
                        if (!IsDone(dict, fileName, "Description", o.Description)) allDone = false;
                        var oName = ApplyLookup(dict, fileName, "Opt", o.Name);
                        if (oName != null) optionNames[(g.Index, o.Index)] = oName;
                        var oDesc = ApplyLookup(dict, fileName, "Description", o.Description);
                        if (oDesc != null) optionDescs[(g.Index, o.Index)] = oDesc;

                        // 覆写旧译法：已中文选项名/描述按英文快照重查词典
                        if (overwrite && snapInfo != null && dict.ContainsChinese(o.Name))
                        {
                            var en = FindEn(snapInfo, g.Index, o.Index);
                            if (!string.IsNullOrWhiteSpace(en) && !dict.ContainsChinese(en))
                            {
                                var t = Translator.Translate(en, $"{fileName}||Opt||{en}", dict);
                                if (!string.IsNullOrWhiteSpace(t) && t != o.Name)
                                    optionNames[(g.Index, o.Index)] = t;
                            }
                        }
                        if (overwrite && snapInfo != null && !string.IsNullOrWhiteSpace(o.Description) && dict.ContainsChinese(o.Description))
                        {
                            var enD = snapInfo.Groups.FirstOrDefault(x => x.Index == g.Index)?
                                .Options.FirstOrDefault(x => x.Index == o.Index)?.Description;
                            if (!string.IsNullOrWhiteSpace(enD) && !dict.ContainsChinese(enD))
                            {
                                var tD = Translator.Translate(enD, $"{fileName}||Description||{enD}", dict);
                                if (!string.IsNullOrWhiteSpace(tD) && tD != o.Description)
                                    optionDescs[(g.Index, o.Index)] = tD;
                            }
                        }
                    }
                }

                if (groupNames.Count == 0 && optionNames.Count == 0 && optionDescs.Count == 0)
                    continue;

                // 整个模组打 zip 备份一次（同一模组多文件只备一次）
                if (!modBackedUp.Contains(mod.Directory))
                {
                    var zip = _files.CreateModZip(modDirPath, _files.MaxBackups);
                    if (zip == null)
                    {
                        errors.Add(mod.Directory + "：备份失败，跳过写回");
                        continue;
                    }
                    modBackedUp.Add(mod.Directory);
                    totalBackups++;
                }

                if (_files.WriteTranslation(fileInfo.Path, fileInfo, groupNames, optionNames, optionDescs))
                {
                    totalWritten += groupNames.Count + optionNames.Count + optionDescs.Count;
                    wroteAny = true;
                }
                else
                {
                    errors.Add(fileName + "：写入失败");
                }
            }
            if (wroteAny) reloaded[mod.Directory] = mod.Name;
            // 有写入 或（有内容且已全部译好）-> 视为已完成汉化，建标记
            if (wroteAny || (anyContent && allDone)) completed[mod.Directory] = mod.Name;
        }

        foreach (var kv in reloaded)
        {
            _penumbra.Reload(kv.Key, kv.Value);
        }

        // 为已完成汉化的模组创建无后缀「已翻译」标记：
        // 既覆盖「本次新写入」的模组，也覆盖「本次无写入但已全中文」的模组（避免重复点 ⑤ 时漏建）。
        // 效果：① 提取英文自动跳过该模组；查漏补缺不受影响；恢复备份自动删除标记。
        var marked = 0;
        foreach (var modDir in completed.Keys)
        {
            // Create 对已存在的标记也返回 true：先判断，仅统计本次新建的
            if (!_mark.HasMark(modDir) && _mark.Create(modDir)) marked++;
        }

        var sb = new StringBuilder();
        sb.Append($"翻译写入完成：写入 {totalWritten} 项 / 备份 {totalBackups} 个模组（zip）/ 重载 {reloaded.Count} 个模组");
        if (marked > 0) sb.Append($"/ 创建「已翻译」标记 {marked} 个");
        if (errors.Count > 0)
            sb.Append("；问题：" + string.Join("；", errors.Take(3)) + (errors.Count > 3 ? $" 等 {errors.Count} 条" : ""));
        LastResult = sb.ToString();
        _log.Info(LastResult);
        if (marked > 0)
            _log.Info($"[标记] 已为 {marked} 个模组创建「已翻译」标记（① 提取英文自动跳过；恢复备份或手动删除后恢复提取）");
        return totalWritten;
    }

    /// <summary> 该条目是否无需再翻译：空 / 已含中文 / 黑名单专名 / 不含英文字母 / 词典已有对应译文。 </summary>
    private static bool IsDone(DictionaryService dict, string fileName, string field, string english)
    {
        if (string.IsNullOrWhiteSpace(english)) return true;
        if (dict.ContainsChinese(english)) return true;
        if (dict.IsBlacklisted(english)) return true;
        if (!HasAsciiLetter(english)) return true;
        var zh = dict.LookupMod($"{fileName}||{field}||{english}") ?? dict.LookupTerm(english);
        return !string.IsNullOrWhiteSpace(zh);
    }

    private static bool HasAsciiLetter(string s)
    {
        foreach (var c in s)
            if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')) return true;
        return false;
    }

    /// <summary> 从英文快照取选项英文原文。 </summary>
    private static string? FindEn(ModFileInfo? snap, int gIndex, int oIndex)
    {
        var g = snap?.Groups.FirstOrDefault(x => x.Index == gIndex);
        return g?.Options.FirstOrDefault(x => x.Index == oIndex)?.Name;
    }

    /// <summary> 查词典译文：原文为空 / 已含中文（不重复覆盖）/ 黑名单 -> 不写回。mods 层精确键优先，再 terms 层。 </summary>
    private static string? ApplyLookup(DictionaryService dict, string fileName, string field, string english)
    {
        if (string.IsNullOrWhiteSpace(english)) return null;
        if (dict.ContainsChinese(english)) return null;
        if (dict.IsBlacklisted(english)) return null;

        var zh = dict.LookupMod($"{fileName}||{field}||{english}");
        if (zh == null) zh = dict.LookupTerm(english);
        if (string.IsNullOrWhiteSpace(zh)) return null;
        return zh == english ? null : zh;
    }
}
