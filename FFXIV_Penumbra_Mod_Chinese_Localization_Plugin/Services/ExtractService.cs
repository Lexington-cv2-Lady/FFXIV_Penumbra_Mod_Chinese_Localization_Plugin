using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FFXIVPenumbraHanhua.Services;

/// <summary> 提取英文：扫描模组选项/描述 → 生成翻译目录\全部模组_未翻译.json（供外部 AI 翻译）。 </summary>
public sealed class ExtractService
{
    private readonly DictionaryService _dict;
    private readonly ModFileService _files;
    private readonly AppLog _log;

    public string LastResult { get; private set; } = "";

    /// <summary> 最近一次提取实际写出的文件路径（供一键流程的预翻译/AI 翻译按文件续跑）。 </summary>
    public IReadOnlyList<string> LastOutputPaths => _lastOutputPaths;
    private readonly List<string> _lastOutputPaths = new();

    public ExtractService(DictionaryService dict, ModFileService files, AppLog log)
    {
        _dict = dict;
        _files = files;
        _log = log;
    }

    /// <summary> 构建「翻译规则」段（纯中文模式 + 黑名单专名注入）。 </summary>
    public static JsonObject BuildTranslationRules(string? dictionaryDir)
    {
        var r = new JsonObject
        {
            ["说明"] = "将英文翻译为简体中文（纯中文模式：译文不带英文对照）。请保持 JSON 结构，仅填写 _options 和 _descriptions 的翻译；文件内所有 JSON 键（含 || 分隔与字段名）原样保留、禁止改动。",
            ["1. 翻译范围"] = "无论是 _descriptions（描述）还是 _options（选项），都必须翻译，一视同仁。",
            ["2. 译文格式"] = "按「纯中文」模式交付：短名称（选项/组名）与长描述（Description）一律直接输出简体中文，禁止输出\"中文（英文）\"等任何带英文对照或括号夹英文的形式；专名保留按第 7 条执行。",
            ["3. 同组一致性"] = "同一文件内的选项通常属于同一维度（体型/尺寸/颜色/材质等），译文应统一语义、用词一致。",
            ["4. 无歧义固定词"] = "身体部位等没有歧义（多义词）的固定名词必须按通用中文直译，不得保留英文：Feet=脚部、Legs=腿部、Hands=手部、Chest=胸部、Belly=腹部、Thighs=大腿、Back=背部、Arms=手臂、Shoulders=肩部。例如 Feet 只译作「脚部」，不要译成其它生僻说法。",
            ["5. 文件命名"] = "翻译完成后，将文件名中的\"_未翻译\"改为\"_已翻译\"。",
            ["6. 交付方式"] = "每次修改后，请直接提供完整的 JSON 文件内容。"
        };

        // 7. 专名保留：注入单词黑名单
        var blPath = Path.Combine(dictionaryDir ?? "", "单词黑名单.json");
        var words = TextListFile.Load(blPath);
        if (words.Count > 0)
        {
            r["7. 专名保留"] = "以下为必须原样保留的英文专名/标识（来自用户词表，禁止译成中文或改动），待翻译文本中出现这些词时保持原样：" +
                string.Join("、", words) + "。";
        }

        r["8. 键与规则段"] = "文件内所有键（含 || 分隔与 _options/_descriptions 字段名）必须原样保留、一行不许改；文件顶部的『翻译规则』字段是给你的参考，不要翻译、不要修改、不要删除。";
        return r;
    }

    /// <summary> 提取英文（汇总模式）：所有模组合并写入 翻译目录\全部模组_未翻译.json。 </summary>
    public int Extract(IReadOnlyList<ModEntry> mods, bool skipMarked, string translationDir, string modRoot)
        => ExtractCore(mods, skipMarked, translationDir, modRoot, perMod: false);

    /// <summary> 提取英文（按模组模式）：每个模组单独写入 翻译目录\&lt;模组名&gt;_未翻译.json（文件名用简洁模组名，键内仍带完整目录，写回不受影响）。 </summary>
    public int ExtractPerMod(IReadOnlyList<ModEntry> mods, bool skipMarked, string translationDir, string modRoot)
        => ExtractCore(mods, skipMarked, translationDir, modRoot, perMod: true);

    private int ExtractCore(IReadOnlyList<ModEntry> mods, bool skipMarked, string translationDir, string modRoot, bool perMod)
    {
        if (mods.Count == 0)
        {
            LastResult = "未选择任何模组";
            return -1;
        }
        if (string.IsNullOrEmpty(modRoot) || !Directory.Exists(modRoot))
        {
            LastResult = "无法获取 Penumbra 模组根目录";
            return -1;
        }

        try
        {
            if (!Directory.Exists(translationDir)) Directory.CreateDirectory(translationDir);
        }
        catch (Exception ex)
        {
            LastResult = "翻译目录不可用：" + ex.Message;
            return -1;
        }

        var skippedMarked = 0;
        var noFiles = 0;
        var total = 0;
        _lastOutputPaths.Clear();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 汇总模式：全部模组收集到一个文件
        var sumOptions = new JsonObject();
        var sumDescriptions = new JsonObject();

        foreach (var mod in mods)
        {
            if (skipMarked && File.Exists(Path.Combine(modRoot, mod.Directory, MarkService.MarkName)))
            {
                skippedMarked++;
                continue;
            }

            var fileInfos = _files.ReadModFiles(Path.Combine(modRoot, mod.Directory));
            if (fileInfos.Count == 0)
            {
                noFiles++;
                continue;
            }

            var options = new JsonObject();
            var descriptions = new JsonObject();
            var modCount = 0;

            foreach (var file in fileInfos)
            {
                var relKey = $"{mod.Directory}/{file.FileName}";
                foreach (var g in file.Groups)
                {
                    // 组名
                    if (!_dict.ContainsChinese(g.Name) && g.Name.Trim().Length > 0)
                    {
                        var key = $"{relKey}||Name||{g.Name}";
                        options[key] = "";
                        total++;
                        modCount++;
                    }
                    foreach (var o in g.Options)
                    {
                        if (!_dict.ContainsChinese(o.Name) && o.Name.Trim().Length > 0)
                        {
                            var key = $"{relKey}||Opt||{o.Name}";
                            options[key] = "";
                            total++;
                            modCount++;
                        }
                        if (!string.IsNullOrWhiteSpace(o.Description) && !_dict.ContainsChinese(o.Description))
                        {
                            var key = $"{relKey}||Description||{o.Description}";
                            descriptions[key] = "";
                            total++;
                            modCount++;
                        }
                    }
                }
            }

            if (perMod)
            {
                // 按模组模式：本模组独立成文件（文件名用简洁模组名；同名冲突时追加序号防覆盖）
                if (modCount == 0) continue;
                var baseName = SanitizeFileName(mod.Name);
                var fileName = baseName + "_未翻译.json";
                var n = 2;
                while (!usedNames.Add(fileName))
                {
                    fileName = $"{baseName}_{n}_未翻译.json";
                    n++;
                }
                var perRoot = new JsonObject
                {
                    ["翻译规则"] = BuildTranslationRules(_dict.DictionaryDir),
                    ["_options"] = options,
                    ["_descriptions"] = descriptions
                };
                var perPath = Path.Combine(translationDir, fileName);
                File.WriteAllText(perPath, perRoot.ToJsonString(JsonFile.Indented), Encoding.UTF8);
                _lastOutputPaths.Add(perPath);
            }
            else
            {
                // 汇总合并：必须深拷贝（JsonNode 不能同时挂在两个父节点下）
                foreach (var kv in options) sumOptions[kv.Key] = kv.Value?.DeepClone();
                foreach (var kv in descriptions) sumDescriptions[kv.Key] = kv.Value?.DeepClone();
            }
        }

        if (perMod)
        {
            var sb = new StringBuilder();
            sb.Append($"提取完成：{total} 项（{mods.Count - skippedMarked - noFiles} 个模组，每个模组一个 _未翻译.json 文件）");
            if (skippedMarked > 0) sb.Append($"；跳过 {skippedMarked} 个已标记「已翻译」的模组");
            if (noFiles > 0) sb.Append($"；{noFiles} 个模组无 group 文件");
            LastResult = sb.ToString();
            _log.Info(LastResult);
            return total;
        }

        var root = new JsonObject
        {
            ["翻译规则"] = BuildTranslationRules(_dict.DictionaryDir),
            ["_options"] = sumOptions,
            ["_descriptions"] = sumDescriptions
        };

        var outPath = Path.Combine(translationDir, "全部模组_未翻译.json");
        File.WriteAllText(outPath, root.ToJsonString(JsonFile.Indented), Encoding.UTF8);
        _lastOutputPaths.Add(outPath);

        var sb2 = new StringBuilder();
        sb2.Append($"提取完成：{total} 项（{mods.Count - skippedMarked - noFiles} 个模组）→ {outPath}");
        if (skippedMarked > 0) sb2.Append($"；跳过 {skippedMarked} 个已标记「已翻译」的模组");
        if (noFiles > 0) sb2.Append($"；{noFiles} 个模组无 group 文件");
        LastResult = sb2.ToString();
        _log.Info(LastResult);
        return total;
    }

    /// <summary>
    /// 生成「交给外部 AI」的完整提示词：格式要求 + 合并后的待翻译内容全文。
    /// 用户点一次即可整段粘贴到任意 AI 对话框（含支持知识库的对话式 AI），不必解释格式、不必上传文件；
    /// AI 回复的 JSON 可直接用「从剪贴板导入译文」写回 _已翻译.json。
    /// </summary>
    public string BuildExternalPrompt(IReadOnlyList<string> inputPaths)
    {
        var options = new JsonObject();
        var descriptions = new JsonObject();
        foreach (var p in inputPaths)
        {
            if (!File.Exists(p)) continue;
            if (JsonNode.Parse(File.ReadAllText(p, Encoding.UTF8)) is not JsonObject root) continue;
            if (root["_options"] is JsonObject o)
                foreach (var kv in o) options[kv.Key] = kv.Value?.DeepClone();
            if (root["_descriptions"] is JsonObject d)
                foreach (var kv in d) descriptions[kv.Key] = kv.Value?.DeepClone();
        }

        // 只把「仍为英文/空」的条目交给 AI（词典预填过的已命中项不重复送翻，省额度）
        var pendingOptions = new JsonObject();
        var pendingDescs = new JsonObject();
        foreach (var kv in options)
        {
            var v = kv.Value?.ToString() ?? "";
            if (v.Length == 0 || !_dict.ContainsChinese(v)) pendingOptions[kv.Key] = "";
        }
        foreach (var kv in descriptions)
        {
            var v = kv.Value?.ToString() ?? "";
            if (v.Length == 0 || !_dict.ContainsChinese(v)) pendingDescs[kv.Key] = "";
        }

        var payload = new JsonObject
        {
            ["翻译规则"] = BuildTranslationRules(_dict.DictionaryDir),
            ["_options"] = pendingOptions,
            ["_descriptions"] = pendingDescs
        };

        var sb = new StringBuilder();
        sb.AppendLine("请把下面 JSON 中 _options 与 _descriptions 的英文翻译为简体中文（纯中文，不带英文对照）。要求：");
        sb.AppendLine("1. 所有键（含 || 分隔与字段名）必须原样保留，不许改动、不许增删、不许合并；");
        sb.AppendLine("2. 只输出 JSON 本体，不要任何解释文字，不要用 ``` 代码块包裹；");
        sb.AppendLine("3. 翻译规则 字段仅作参考，原样保留、不要翻译；");
        sb.AppendLine("4. 专名（见 翻译规则 第 7 条）保留英文；同一文件内的选项用词要统一；");
        sb.AppendLine("5. 输出结构与输入完全一致（同一份 JSON，只把值换成中文）。");
        sb.AppendLine();
        sb.Append(payload.ToJsonString());
        return sb.ToString();
    }

    /// <summary> 文件名清洗：剔除 Windows 非法字符，空名兜底。 </summary>
    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        var s = sb.ToString().Trim();
        return s.Length == 0 ? "模组" : s;
    }

    /// <summary>
    /// 词典预填（一键流程/② 预翻译共用）：把文件中空值条目按当前词典填上译文后原路写回。
    /// 返回命中数；解析失败返回 -1。
    /// </summary>
    public int PrefillFile(string path)
    {
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)) as JsonObject;
            if (root == null)
            {
                _log.Error($"[预翻译] 解析失败（已跳过）：{path}");
                return -1;
            }
            var hit = 0;

            foreach (var sec in new[] { "_options", "_descriptions" })
            {
                if (root[sec] is not JsonObject obj) continue;
                foreach (var kv in obj.ToList())
                {
                    var text = kv.Value?.ToString() ?? "";
                    if (text.Length > 0) continue;
                    var parts = kv.Key.Split(new[] { "||" }, StringSplitOptions.None);
                    if (parts.Length != 3) continue;
                    // mods 层 key 是「文件名||字段||原文」（DictionaryService 生成时不含模组目录前缀），
                    // 而提取 key 的 parts[0] 是「模组目录/文件名」——须剥掉目录，否则整条精确查询恒不命中
                    // （只能落到 terms 层兜底，会用通用译法覆盖文件级专属译法）。
                    var modKey = $"{Path.GetFileName(parts[0])}||{parts[1]}||{parts[2]}";
                    var translated = Translator.Translate(parts[2], modKey, _dict);
                    if (translated.Length > 0 && _dict.ContainsChinese(translated))
                    {
                        obj[kv.Key] = translated;
                        hit++;
                    }
                }
            }

            File.WriteAllText(path, root.ToJsonString(JsonFile.Indented), Encoding.UTF8);
            _log.Info($"[预翻译] {Path.GetFileName(path)}：命中 {hit} 项");
            return hit;
        }
        catch (Exception ex)
        {
            _log.Error($"[预翻译] 文件处理失败（已跳过）：{path}：{ex.Message}");
            return -1;
        }
    }
}
