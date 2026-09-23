using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace FFXIVPenumbraHanhua.Services;

/// <summary>
/// 词典服务：加载 词典目录 的全部资产（我的翻译 / 个性翻译 / wiki 术语分类 / 两类黑名单 / AI 知识库），
/// 语义与独立版完全一致——优先级：单词黑名单 &gt; 个性翻译 &gt; 我的翻译 &gt; wiki 术语。
/// </summary>
public sealed class DictionaryService
{
    private readonly AppLog _log;
        private readonly Dictionary<string, string> _terms = new();       // 大小写敏感词表
        private readonly Dictionary<string, string> _termsLower = new();   // 小写兜底词表
        private readonly Dictionary<string, string> _custom = new();       // 个性翻译：最高优先级覆盖层（英文 -> 译文）
        private readonly Dictionary<string, string> _customLower = new();  // 个性翻译小写兜底
        private readonly Dictionary<string, string> _mods = new();         // mods 层整条（relKey||Opt||原文 -> 译文）
    private readonly HashSet<string> _blacklist = new(StringComparer.OrdinalIgnoreCase);     // 单词黑名单
    private readonly HashSet<string> _wikiBlacklist = new(StringComparer.OrdinalIgnoreCase); // wiki 黑名单

    public string? DictionaryDir { get; private set; }
    public int MaxTermLen { get; private set; }
    public string Status { get; internal set; } = "未加载词典";

    // 各来源条数统计（UI 展示用）
    public int MyCount { get; private set; }
    public int CustomCount { get; private set; }
    public int WikiCount { get; private set; }
    public int AiCount { get; private set; }
    public int BlacklistCount => _blacklist.Count;

    public DictionaryService(AppLog log)
    {
        _log = log;
    }

    /// <summary> 加载指定目录的全部词典。返回是否成功。 </summary>
    public bool Load(string dictionaryDir)
    {
        _log.Info($"[词典] 开始加载：{dictionaryDir}");
        DictionaryDir = dictionaryDir;
        _terms.Clear();
        _termsLower.Clear();
        _custom.Clear();
        _customLower.Clear();
        _mods.Clear();
        _blacklist.Clear();
        _wikiBlacklist.Clear();
        MaxTermLen = 0;

        if (string.IsNullOrWhiteSpace(dictionaryDir) || !Directory.Exists(dictionaryDir))
        {
            Status = "词典目录不存在：" + dictionaryDir;
            _log.Error("[词典] " + Status);
            return false;
        }

        var dir = Path.Combine(dictionaryDir, "wiki_术语对照");
        _wikiBlacklist.UnionWith(TextListFile.Load(Path.Combine(dir, "wiki_术语对照_黑名单.json")));

        // 0) 单词黑名单（必须先加载，后续所有词表都要用它过滤）
        var blPath = Path.Combine(dictionaryDir, "单词黑名单.json");
        _blacklist.UnionWith(TextListFile.Load(blPath));

        // 1) 我的翻译.json（含 mods 双层 + terms 平铺；损坏时跳过并提示）
        var myPath = Path.Combine(dictionaryDir, "我的翻译.json");
        MyCount = LoadMergedDict(myPath, isWiki: false);

        // 2) 个性翻译.json（词级覆盖层）
        var customPath = Path.Combine(dictionaryDir, "个性翻译.json");
        CustomCount = LoadCustomOverlay(customPath);

        // 3) wiki 术语分类文件夹（合并全部 *.json，黑名单过滤，不覆盖已有词条）
        WikiCount = LoadWikiFolder(dir);

        // 5) AI 知识库（格式同 我的翻译，只读底料；优先级低于 我的翻译/个性翻译）
        var aiPath = Path.Combine(dictionaryDir, "AI知识库", "AI知识库.json");
        AiCount = LoadMergedDict(aiPath, isWiki: false, overlayOnly: true);

        Status = $"词典已加载：我的翻译 {MyCount} 条 / 个性翻译 {CustomCount} 条 / wiki {WikiCount} 条 / AI知识库 {AiCount} 条 / 黑名单 {_blacklist.Count} 词";
        _log.Info("[词典] " + Status);
        return true;
    }

    /// <summary> mods 层整条查询（key 形如 group_xxx.json||Opt||Large）。 </summary>
    public string? LookupMod(string key)
        => _mods.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) ? v : null;

    /// <summary> terms 层整条查询（大小写敏感优先，小写兜底）。 </summary>
    public string? LookupTerm(string text)
    {
        if (_terms.TryGetValue(text, out var v)) return v;
        return _termsLower.TryGetValue(text.ToLowerInvariant(), out var v2) ? v2 : null;
    }

    /// <summary> 个性翻译：最高优先级覆盖层查询（大小写敏感优先，小写兜底）。 </summary>
    public string? LookupCustom(string text)
    {
        if (_custom.TryGetValue(text, out var v)) return v;
        return _customLower.TryGetValue(text.ToLowerInvariant(), out var v2) ? v2 : null;
    }

    public bool IsBlacklisted(string word) => _blacklist.Contains(word);

    public bool ContainsChinese(string s)
    {
        foreach (var c in s)
            if (c >= 0x4E00 && c <= 0x9FFF) return true;
        return false;
    }

    private int LoadMergedDict(string path, bool isWiki, bool overlayOnly = false)
    {
        if (!File.Exists(path)) return 0;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return 0;
            var count = 0;

            // terms 层：兼容两种格式
            //   A) 对象 { 英文: 中文 }（wiki 分类词典）
            //   B) 数组 [{ 原文, 译文 }]（我的翻译 / AI 知识库）
            if (root.TryGetProperty("terms", out var terms))
            {
                if (terms.ValueKind == JsonValueKind.Object)
                    count += MergeTerms(terms, isWiki, overlayOnly);
                else if (terms.ValueKind == JsonValueKind.Array)
                    count += MergeTermsArray(terms, isWiki, overlayOnly);
            }

            // mods 双层（仅 我的翻译/AI知识库 有）
            if (!isWiki && root.TryGetProperty("mods", out var mods) && mods.ValueKind == JsonValueKind.Object)
            {
                foreach (var mod in mods.EnumerateObject())
                {
                    if (mod.Value.ValueKind != JsonValueKind.Object) continue;
                    foreach (var grp in mod.Value.EnumerateObject())
                    {
                        if (grp.Value.ValueKind != JsonValueKind.Object) continue;
                        foreach (var field in grp.Value.EnumerateObject())
                        {
                            var fieldName = field.Name; // Name / Opt / Description ...
                            if (field.Value.ValueKind != JsonValueKind.Array) continue;
                            foreach (var entry in field.Value.EnumerateArray())
                            {
                                if (entry.ValueKind != JsonValueKind.Object) continue;
                                if (!entry.TryGetProperty("原文", out var en) || !entry.TryGetProperty("译文", out var zh)) continue;
                                if (en.ValueKind != JsonValueKind.String || zh.ValueKind != JsonValueKind.String) continue;
                                var enS = en.GetString() ?? "";
                                var zhS = zh.GetString() ?? "";
                                if (enS.Length == 0) continue;
                                // AI 知识库（overlayOnly）只兜底，不覆盖 我的翻译 的 mods 精确词条
                                var key = $"{grp.Name}||{fieldName}||{enS}";
                                if (overlayOnly && _mods.ContainsKey(key)) continue;
                                _mods[key] = zhS;
                                count++;
                            }
                        }
                    }
                }
            }
            return count;
        }
        catch (Exception ex)
        {
            _log.Error($"[词典] 文件解析失败（已跳过）：{path}：{ex.Message}");
            return 0;
        }
    }

    private int LoadCustomOverlay(string path)
    {
        if (!File.Exists(path)) return 0;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return 0;
            var count = 0;
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                if (!entry.TryGetProperty("原文", out var en) || !entry.TryGetProperty("译文", out var zh)) continue;
                if (en.ValueKind != JsonValueKind.String || zh.ValueKind != JsonValueKind.String) continue;
                var enS = en.GetString() ?? "";
                var zhS = zh.GetString() ?? "";
                if (enS.Length == 0 || IsBlacklisted(enS)) continue;
                _custom[enS] = zhS;
                _customLower[enS.ToLowerInvariant()] = zhS;
                if (enS.Length > MaxTermLen) MaxTermLen = Math.Min(enS.Length, 200);
                count++;
            }
            return count;
        }
        catch (Exception ex)
        {
            _log.Error($"[词典] 文件解析失败（已跳过）：{path}：{ex.Message}");
            return 0;
        }
    }

    private int LoadWikiFolder(string dir)
    {
        if (!Directory.Exists(dir)) return 0;
        var count = 0;
        foreach (var file in Directory.GetFiles(dir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(file);
            if (name == "wiki_术语对照_黑名单.json") continue;
            count += LoadMergedDict(file, isWiki: true);
        }
        return count;
    }

    private int MergeTerms(JsonElement terms, bool isWiki, bool overlayOnly)
    {
        var count = 0;
        foreach (var kv in terms.EnumerateObject())
        {
            var en = kv.Name;
            if (kv.Value.ValueKind != JsonValueKind.String) continue;
            var zh = kv.Value.GetString() ?? "";
            if (en.Length == 0) continue;

            // wiki 黑名单过滤（wiki 词条整体剔除）
            if (isWiki && _wikiBlacklist.Contains(en)) continue;
            // 单词黑名单词条一律不进词表（防止黑名单词被译成中文）
            if (IsBlacklisted(en)) continue;
            // 个性翻译/我的翻译优先：wiki 兜底不覆盖已有词条
            if (isWiki && _terms.ContainsKey(en)) continue;
            // AI 知识库只兜底，不覆盖 我的翻译/个性翻译
            if (overlayOnly && _terms.ContainsKey(en)) continue;

            if (zh.Length == 0) continue; // 空译文占位不生效
            _terms[en] = zh;
            var low = en.ToLowerInvariant();
            if (low != en) _termsLower[low] = zh;
            if (en.Length > MaxTermLen) MaxTermLen = Math.Min(en.Length, 200);
            count++;
        }
        return count;
    }

    /// <summary> 合并数组格式 terms：[{ 原文, 译文 }]。 </summary>
    private int MergeTermsArray(JsonElement arr, bool isWiki, bool overlayOnly)
    {
        var count = 0;
        foreach (var entry in arr.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            if (!entry.TryGetProperty("原文", out var en) || !entry.TryGetProperty("译文", out var zh)) continue;
            if (en.ValueKind != JsonValueKind.String || zh.ValueKind != JsonValueKind.String) continue;
            var enS = en.GetString() ?? "";
            var zhS = zh.GetString() ?? "";
            if (enS.Length == 0) continue;

            if (isWiki && _wikiBlacklist.Contains(enS)) continue;
            if (IsBlacklisted(enS)) continue;
            if (isWiki && _terms.ContainsKey(enS)) continue;
            if (overlayOnly && _terms.ContainsKey(enS)) continue;
            if (zhS.Length == 0) continue;

            _terms[enS] = zhS;
            var low = enS.ToLowerInvariant();
            if (low != enS) _termsLower[low] = zhS;
            if (enS.Length > MaxTermLen) MaxTermLen = Math.Min(enS.Length, 200);
            count++;
        }
        return count;
    }
}
