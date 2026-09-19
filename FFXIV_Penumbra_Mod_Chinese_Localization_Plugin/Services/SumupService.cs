using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FFXIVPenumbraHanhua.Services;

/// <summary> 汇总已翻译内容：把「_已翻译.json」或「模组文件里手改的中文」沉淀进 我的翻译.json（已有译文不覆盖）。 </summary>
public sealed class SumupService
{
    private readonly AppLog _log;
    private readonly ModFileService _files;
    private readonly EnglishSnapshotService _snapshot;

    public string LastResult { get; private set; } = "";

    public SumupService(AppLog log, ModFileService files, EnglishSnapshotService snapshot)
    {
        _log = log;
        _files = files;
        _snapshot = snapshot;
    }

    /// <summary> 汇总外部 AI 的 _已翻译.json。返回写入（新增）条数。 </summary>
    public int Sumup(string inputPath, string dictionaryDir)
    {
        if (!File.Exists(inputPath))
        {
            LastResult = "未找到已翻译文件：" + inputPath;
            return -1;
        }
        if (string.IsNullOrWhiteSpace(dictionaryDir))
        {
            LastResult = "词典目录未配置";
            return -1;
        }

        JsonObject root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(inputPath, Encoding.UTF8)) as JsonObject ?? new JsonObject();
        }
        catch (Exception ex)
        {
            LastResult = "已翻译文件解析失败：" + ex.Message;
            return -1;
        }

        var dict = LoadDict(dictionaryDir);
        var written = 0;

        foreach (var sec in new[] { "_options", "_descriptions" })
        {
            if (root[sec] is not JsonObject obj) continue;
            foreach (var kv in obj)
            {
                var chinese = kv.Value?.ToString() ?? "";
                if (chinese.Length == 0) continue;
                var parts = kv.Key.Split(new[] { "||" }, StringSplitOptions.None);
                if (parts.Length != 3) continue;

                var fileRel = parts[0];
                var field = parts[1] switch
                {
                    "Name" => "Name",
                    "Opt" => "Opt",
                    _ => "Description"
                };
                var english = parts[2];
                if (english.Length == 0 || ContainsChinese(english)) continue;

                var slash = fileRel.IndexOf('/');
                if (slash <= 0) continue;
                var modDir = fileRel[..slash];
                var fileName = fileRel[(slash + 1)..];

                AddToDict(dict, modDir, fileName, field, english, chinese, ref written);
            }
        }

        var saveErr = SaveDict(Path.Combine(dictionaryDir, "我的翻译.json"), dict, dictionaryDir);
        if (saveErr != null)
        {
            LastResult = "写入 我的翻译.json 失败：" + saveErr;
            return -1;
        }

        LastResult = $"汇总已翻译内容完成：新增 {written} 条 -> 我的翻译.json（已有译文未覆盖）";
        _log.Info(LastResult);
        return written;
    }

    private static JsonObject LoadDict(string dictionaryDir)
    {
        var dictPath = Path.Combine(dictionaryDir, "我的翻译.json");
        if (File.Exists(dictPath))
        {
            try
            {
                return JsonNode.Parse(File.ReadAllText(dictPath, Encoding.UTF8)) as JsonObject ?? NewDict();
            }
            catch (Exception)
            {
                return NewDict();
            }
        }
        return NewDict();
    }

    /// <summary>
    /// 保存修改时的自动沉淀：把（英文原文已知且新值为中文的）条目写入 我的翻译.json。返回新增条数。
    /// </summary>
    public int Sediment(IEnumerable<(string ModDir, string FileName, string Field, string En, string Zh)> entries,
        string dictionaryDir)
    {
        if (entries == null) return 0;
        if (string.IsNullOrWhiteSpace(dictionaryDir))
        {
            LastResult = "词典目录未配置";
            return -1;
        }
        var dict = LoadDict(dictionaryDir);
        var written = 0;
        foreach (var e in entries)
            AddToDict(dict, e.ModDir, e.FileName, e.Field, e.En, e.Zh, ref written);
        if (written == 0)
        {
            LastResult = "无新增（相同原文不覆盖）";
            return 0;
        }
        var saveErr = SaveDict(Path.Combine(dictionaryDir, "我的翻译.json"), dict, dictionaryDir);
        if (saveErr != null)
        {
            LastResult = "写入 我的翻译.json 失败：" + saveErr;
            return -1;
        }
        LastResult = $"已自动沉淀 {written} 条 -> 我的翻译.json";
        _log.Info(LastResult);
        return written;
    }

    /// <summary> 写入词典文件。返回错误信息，成功返回 null。 </summary>
    private static string? SaveDict(string dictPath, JsonObject dict, string dictionaryDir)
    {
        try
        {
            if (!Directory.Exists(dictionaryDir)) Directory.CreateDirectory(dictionaryDir);
            File.WriteAllText(dictPath, dict.ToJsonString(JsonFile.Indented), Encoding.UTF8);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary> 往 mods[modDir][fileName][field] 数组加条目；已有相同原文不覆盖。 </summary>
    private static void AddToDict(JsonObject dict, string modDir, string fileName, string field,
        string english, string chinese, ref int written)
    {
        var mods = dict["mods"] as JsonObject ?? new JsonObject();
        var modObj = mods[modDir] as JsonObject ?? new JsonObject();
        var fileObj = modObj[fileName] as JsonObject ?? new JsonObject();
        var arr = fileObj[field] as JsonArray ?? new JsonArray();
        fileObj[field] = arr;
        modObj[fileName] = fileObj;
        mods[modDir] = modObj;
        dict["mods"] = mods;

        var exists = arr.Any(x => x is JsonObject eo && eo["原文"]?.ToString() == english);
        if (!exists)
        {
            arr.Add(new JsonObject { ["原文"] = english, ["译文"] = chinese });
            written++;
        }
    }

    private static JsonObject NewDict()
    {
        return new JsonObject { ["mods"] = new JsonObject() };
    }

    private static bool ContainsChinese(string s)
    {
        foreach (var c in s)
            if (c >= 0x4E00 && c <= 0x9FFF) return true;
        return false;
    }
}
