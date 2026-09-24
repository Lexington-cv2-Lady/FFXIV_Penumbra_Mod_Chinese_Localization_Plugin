using System;
using System.Collections.Generic;
using System.IO;

namespace FFXIVPenumbraHanhua.Services;

/// <summary> 已翻译标记：模组目录下创建无后缀「已翻译」文件，提取/翻译时自动跳过该模组。 </summary>
public sealed class MarkService
{
    private readonly Func<string> _modRootGetter;

    public MarkService(Func<string> modRootGetter)
    {
        _modRootGetter = modRootGetter;
    }

    public const string MarkName = "已翻译";

    private string Root => _modRootGetter() ?? "";

    /// <summary> 模组目录的完整路径（兼容 mod.Directory 为相对或绝对路径）。 </summary>
    private static string ModFullPath(string root, string modDirectory)
    {
        if (string.IsNullOrEmpty(root)) return modDirectory;
        return Path.IsPathRooted(modDirectory) ? modDirectory : Path.Combine(root, modDirectory);
    }

    /// <summary> 模组目录是否已有「已翻译」标记。 </summary>
    public bool HasMark(string modDirectory)
    {
        if (string.IsNullOrEmpty(Root)) return false;
        return File.Exists(Path.Combine(ModFullPath(Root, modDirectory), MarkName));
    }

    /// <summary> 创建标记。返回是否成功。 </summary>
    public bool Create(string modDirectory)
    {
        try
        {
            var dir = ModFullPath(Root, modDirectory);
            if (!Directory.Exists(dir)) return false;
            var p = Path.Combine(dir, MarkName);
            if (!File.Exists(p)) File.WriteAllText(p, "此模组已完成汉化，提取英文时将自动跳过。如需重新提取，请删除本文件。\n");
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary> 删除标记。返回是否成功。 </summary>
    public bool Remove(string modDirectory)
    {
        try
        {
            var p = Path.Combine(ModFullPath(Root, modDirectory), MarkName);
            if (File.Exists(p))
            {
                File.Delete(p);
                return true;
            }
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary> 模组当前文件内容是否含中文（meta.json 顶层 Groups + 根目录 group_*.json）。 </summary>
    public bool ModHasChinese(string modDirectory)
    {
        try
        {
            var dir = ModFullPath(Root, modDirectory);
            if (!Directory.Exists(dir)) return false;

            var meta = Path.Combine(dir, "meta.json");
            if (File.Exists(meta) && EnglishSnapshotService.ContainsChinese(File.ReadAllText(meta))) return true;

            foreach (var f in Directory.EnumerateFiles(dir, "group_*.json"))
                if (EnglishSnapshotService.ContainsChinese(File.ReadAllText(f))) return true;

            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 标记是否真失效：有「已翻译」标记，但模组内容已「以英文为主」地还原——
    /// 即「仍是英文且词典可译」的字段数 ≥ 「已中文」字段数，且至少有一处中文（证明该模组曾被译过）。
    /// 这样才不会把「绝大部分已译好、仅余零星英文」的正常模组误判为失效。
    /// 无选项模组、选项全是黑名单专名的模组不会被判失效，标记保留。
    /// </summary>
    public bool IsStale(string modDirectory, DictionaryService dict, ModFileService files)
    {
        if (!HasMark(modDirectory)) return false;
        try
        {
            var dir = ModFullPath(Root, modDirectory);
            if (!Directory.Exists(dir)) return false;

            var zh = 0; // 已中文（译好）的字段数
            var en = 0; // 仍是英文且词典可译（疑似还原）的字段数
            foreach (var file in files.ReadModFiles(dir))
            {
                var fileName = file.FileName;
                foreach (var g in file.Groups)
                {
                    TallyField(dict, fileName, "Name", g.Name, ref zh, ref en);
                    foreach (var o in g.Options)
                    {
                        TallyField(dict, fileName, "Opt", o.Name, ref zh, ref en);
                        TallyField(dict, fileName, "Description", o.Description, ref zh, ref en);
                    }
                }
            }
            // 失效：曾译过（zh>0）且英文可译字段不比中文少（en>=zh）——内容已「以英文为主」地还原。
            // 绝大部分已译好的模组（中文远多于英文）不会被误清。
            return zh > 0 && en >= zh;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 统计单个字段：已中文 → zh++；英文且词典可译（疑似还原）→ en++；
    /// 其余（英文但词典暂不可译 / 黑名单专名 / 空）不计入任一方，避免误判。
    /// </summary>
    private static void TallyField(DictionaryService dict, string fileName, string field, string text, ref int zh, ref int en)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (dict.ContainsChinese(text)) { zh++; return; }
        if (AwaitsTranslation(dict, fileName, field, text)) en++;
    }

    /// <summary>
    /// 清理失效标记：对真失效的模组删除「已翻译」标记，使其重新进入提取与自动汉化。
    /// 返回被清理的模组目录名列表。
    /// </summary>
    public List<string> PruneStaleMarks(IReadOnlyList<ModEntry> mods, DictionaryService dict, ModFileService files)
    {
        var pruned = new List<string>();
        if (string.IsNullOrEmpty(Root)) return pruned;
        foreach (var m in mods)
        {
            if (IsStale(m.Directory, dict, files) && Remove(m.Directory))
                pruned.Add(m.Directory);
        }
        return pruned;
    }

    /// <summary>
    /// 条目是否「该是中文却仍是英文」：非空、不含中文、非黑名单、含 ASCII 字母，
    /// 且词典（模组级优先，其次全局术语）能查到非空中文译文。
    /// </summary>
    private static bool AwaitsTranslation(DictionaryService dict, string fileName, string field, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (dict.ContainsChinese(text)) return false;
        if (dict.IsBlacklisted(text)) return false;
        if (!HasAsciiLetter(text)) return false;

        // 个性翻译（最高覆盖层）优先于 我的翻译
        var zh = dict.LookupCustom(text);
        if (string.IsNullOrWhiteSpace(zh)) zh = dict.LookupMod($"{fileName}||{field}||{text}");
        if (string.IsNullOrWhiteSpace(zh)) zh = dict.LookupTerm(text);
        return !string.IsNullOrWhiteSpace(zh);
    }

    private static bool HasAsciiLetter(string s)
    {
        foreach (var c in s)
            if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')) return true;
        return false;
    }
}
