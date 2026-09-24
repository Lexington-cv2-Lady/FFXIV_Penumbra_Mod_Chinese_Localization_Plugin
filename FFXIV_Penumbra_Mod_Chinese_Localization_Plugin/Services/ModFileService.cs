using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FFXIVPenumbraHanhua.Services;

/// <summary> 模组文件（meta.json 新格式 / group_*.json 旧格式）解析、备份、写回。 </summary>
public sealed class ModFileService
{
    /// <summary> 备份轮转保留份数（可配置）。 </summary>
    public int MaxBackups { get; set; } = 5;

    /// <summary> 可选日志出口：删除 / 轮转 / 清理失败时记一行（为 null 则静默跳过）。 </summary>
    public AppLog? Log { get; set; }

    /// <summary> 扫描模组目录，返回全部可汉化文件（meta.json + group_*.json）。跳过乱码文件名的副本。 </summary>
    public List<ModFileInfo> ReadModFiles(string modDirPath)
    {
        var result = new List<ModFileInfo>();
        if (!Directory.Exists(modDirPath)) return result;

        var meta = Path.Combine(modDirPath, "meta.json");
        var hasFv4Meta = false;
        if (File.Exists(meta))
        {
            var info = ParseMeta(meta);
            if (info != null)
            {
                result.Add(info);
                // FV4（顶层 Groups）模组：meta.json 已是完整且权威的单一数据源，
                // 旧格式遗留的 group_*.json 在 Penumbra FV4 下被忽略，纯属冗余。
                // 跳过它们可避免重复翻译、并杜绝延续历史遗留的「同名不同分隔符」重复文件。
                if (!info.MetaWrapped) hasFv4Meta = true;
            }
        }

        // 仅当模组不是 FV4（无顶层 Groups 的 meta，或完全没有 meta）时，
        // 才处理旧格式 group_*.json（它们是这类模组的唯一/主要翻译来源）。
        if (!hasFv4Meta)
        {
            foreach (var f in Directory.GetFiles(modDirPath, "group_*.json").OrderBy(x => x, StringComparer.Ordinal))
            {
                // 跳过乱码文件名（替换符/代理区）：这类是同一中文名的编码损坏副本，读写/备份都会造成污染
                var name = Path.GetFileName(f);
                if (name.IndexOf('\uFFFD') >= 0) continue;
                var hasSurrogate = false;
                foreach (var c in name)
                {
                    if (char.IsSurrogate(c)) { hasSurrogate = true; break; }
                }
                if (hasSurrogate) continue;

                var info = ParseGroup(f);
                if (info != null) result.Add(info);
            }
        }

        return result;
    }

    /// <summary> 解析 meta.json：兼容新格式（FileVersion 4，顶层 Groups[]）与旧格式（FileVersion 3，Mod.Groups 包装）。 </summary>
    internal static ModFileInfo? ParseMeta(string path)
    {
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            if (node == null) return null;
            // 优先顶层 Groups（新版）；否则找 Mod.Groups 包装（旧版）
            JsonArray? groups = node["Groups"] as JsonArray;
            var wrapped = false;
            if (groups == null && node["Mod"] is JsonObject mod)
            {
                groups = mod["Groups"] as JsonArray;
                wrapped = true;
            }
            if (groups == null) return null;

            var info = new ModFileInfo { Path = path, IsMeta = true, MetaWrapped = wrapped };
            var gi = 0;
            foreach (var g in groups.OfType<JsonObject>())
            {
                var groupName = g["Name"]?.GetValue<string>() ?? "";
                var groupDesc = g["Description"]?.GetValue<string>() ?? "";
                var group = new ModGroup { Index = gi++, Name = groupName, Description = groupDesc };
                if (g["Options"] is JsonArray opts)
                {
                    var oi = 0;
                    foreach (var o in opts.OfType<JsonObject>())
                    {
                        group.Options.Add(new ModOption
                        {
                            Index = oi++,
                            Name = o["Name"]?.GetValue<string>() ?? "",
                            Description = o["Description"]?.GetValue<string>() ?? ""
                        });
                    }
                }
                info.Groups.Add(group);
            }
            return info;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary> 解析旧格式 group_*.json（Name + Options[]）。 </summary>
    internal static ModFileInfo? ParseGroup(string path)
    {
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            if (node == null) return null;

            var info = new ModFileInfo { Path = path, IsMeta = false };
            var group = new ModGroup
            {
                Index = 0,
                Name = node["Name"]?.GetValue<string>() ?? "",
                Description = node["Description"]?.GetValue<string>() ?? ""
            };
            if (node["Options"] is JsonArray opts)
            {
                var oi = 0;
                foreach (var o in opts.OfType<JsonObject>())
                {
                    group.Options.Add(new ModOption
                    {
                        Index = oi++,
                        Name = o["Name"]?.GetValue<string>() ?? "",
                        Description = o["Description"]?.GetValue<string>() ?? ""
                    });
                }
            }
            info.Groups.Add(group);
            return info;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 启动清理：删除旧格式 .json.bak / .json.bak2（独立版遗留的完整备份），
    /// 时间戳格式 *.json.bak_YYYYMMDD_HHMMSS 按前缀分组、每组只保留最新 maxBackups 份。
    /// penumbra 根目录递归，翻译/词典目录只扫顶层。失败的文件跳过（不中断），但会通过 warn 记一行日志。
    /// </summary>
    public void CleanupLegacyBak(string? penumbraRoot, string? translationDir, string? dictionaryDir, int maxBackups)
    {
        var dirs = new[] { penumbraRoot, translationDir, dictionaryDir };
        for (var i = 0; i < dirs.Length; i++)
        {
            var dir = dirs[i];
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) continue;
            var search = i == 0 ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

            // 1) 旧格式完整备份 .json.bak / .json.bak2：直接删除
            foreach (var f in Directory.GetFiles(dir, "*.json.bak", search))
            {
                try { File.Delete(f); }
                catch (Exception ex) { Log?.Warn($"[清理] 删除旧格式备份失败（已跳过）：{f} - {ex.Message}"); }
            }
            foreach (var f in Directory.GetFiles(dir, "*.json.bak2", search))
            {
                try { File.Delete(f); }
                catch (Exception ex) { Log?.Warn($"[清理] 删除旧格式备份失败（已跳过）：{f} - {ex.Message}"); }
            }

            // 2) 时间戳格式 .json.bak_*：按前缀分组，每组保留最新 maxBackups 份
            try
            {
                foreach (var g in Directory.GetFiles(dir, "*.json.bak_*", search)
                             .GroupBy(f => Path.GetFileName(f).Split(".json.bak_", StringSplitOptions.None)[0], StringComparer.Ordinal))
                {
                    foreach (var old in g.OrderByDescending(x => x, StringComparer.Ordinal).Skip(maxBackups))
                    {
                        try { File.Delete(old); }
                        catch (Exception ex) { Log?.Warn($"[清理] 轮转删除旧备份失败（已跳过）：{old} - {ex.Message}"); }
                    }
                }
            }
            catch (Exception ex)
            {
                Log?.Warn($"[清理] 枚举旧备份失败（跳过该目录）：{dir} - {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 创建模组 zip 备份（独立版格式：yyyy-MM-dd_HH-mm-ss备份.zip，内含 meta.json + group_*.json）。
    /// 轮转：同目录 *备份.zip 只保留最新 maxBackups 份。返回 zip 路径，失败返回 null。
    /// </summary>
    public string? CreateModZip(string modDirPath, int maxBackups)
    {
        try
        {
            if (string.IsNullOrEmpty(modDirPath) || !Directory.Exists(modDirPath)) return null;
            var files = ReadModFiles(modDirPath);
            if (files.Count == 0) return null;

            var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            var zipPath = Path.Combine(modDirPath, $"{stamp}备份.zip");
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                foreach (var f in files)
                {
                    zip.CreateEntryFromFile(f.Path, Path.GetFileName(f.Path));
                }
            }

            // 轮转：只保留最新 maxBackups 份 zip（文件名时间格式按字典序即时间序）
            var all = Directory.GetFiles(modDirPath, "*备份.zip")
                .OrderByDescending(x => x, StringComparer.Ordinal)
                .ToList();
            foreach (var old in all.Skip(maxBackups))
            {
                try { File.Delete(old); }
                catch (Exception ex) { Log?.Warn($"[备份] 轮转删除旧备份失败（已跳过）：{old} - {ex.Message}"); }
            }
            return zipPath;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary> 写回翻译：把组的 Name/Description、选项的 Name/Description 改为中文。 </summary>
    public bool WriteTranslation(string filePath, ModFileInfo info, Dictionary<int, string> groupNames,
        Dictionary<(int, int), string> optionNames, Dictionary<(int, int), string> optionDescs)
    {
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(filePath)) as JsonObject;
            if (node == null) return false;

            if (info.IsMeta)
            {
                // 定位 Groups：与解析时同规则（顶层或 Mod.Groups 包装）
                JsonArray? groups = node["Groups"] as JsonArray;
                if (groups == null && node["Mod"] is JsonObject mod)
                    groups = mod["Groups"] as JsonArray;
                if (groups != null)
                {
                    var gi = 0;
                    foreach (var g in groups.OfType<JsonObject>())
                    {
                        if (groupNames.TryGetValue(gi, out var gn) && gn.Length > 0)
                            g["Name"] = gn;
                        if (g["Options"] is JsonArray opts)
                        {
                            var oi = 0;
                            foreach (var o in opts.OfType<JsonObject>())
                            {
                                if (optionNames.TryGetValue((gi, oi), out var on) && on.Length > 0)
                                    o["Name"] = on;
                                if (optionDescs.TryGetValue((gi, oi), out var od) && od.Length > 0)
                                    o["Description"] = od;
                                oi++;
                            }
                        }
                        gi++;
                    }
                }
            }
            else
            {
                if (groupNames.TryGetValue(0, out var gn) && gn.Length > 0)
                    node["Name"] = gn;
                if (node["Options"] is JsonArray opts)
                {
                    var oi = 0;
                    foreach (var o in opts.OfType<JsonObject>())
                    {
                        if (optionNames.TryGetValue((0, oi), out var on) && on.Length > 0)
                            o["Name"] = on;
                        if (optionDescs.TryGetValue((0, oi), out var od) && od.Length > 0)
                            o["Description"] = od;
                        oi++;
                    }
                }
            }

            var options = JsonFile.Indented;
            JsonFile.WriteAtomic(filePath, node.ToJsonString(options));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary> 一个可汉化文件（meta.json 或 group_*.json）及其选项树。 </summary>
public sealed class ModFileInfo
{
    public string Path { get; init; } = "";
    public bool IsMeta { get; init; }

    /// <summary> meta.json 是否为 Mod.Groups 包装结构（旧版 FileVersion 3）。 </summary>
    public bool MetaWrapped { get; init; }

    public List<ModGroup> Groups { get; } = new();
    public string FileName => System.IO.Path.GetFileName(Path);
}

/// <summary> 一个选项组。 </summary>
public sealed class ModGroup
{
    public int Index { get; init; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<ModOption> Options { get; } = new();
}

/// <summary> 一个选项。 </summary>
public sealed class ModOption
{
    public int Index { get; init; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
}
