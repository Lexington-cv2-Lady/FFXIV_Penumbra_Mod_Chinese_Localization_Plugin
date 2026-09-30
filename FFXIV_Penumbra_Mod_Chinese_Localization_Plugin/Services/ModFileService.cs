using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.VisualBasic.FileIO;

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
    /// 时间戳格式 *.json.bak_YYYYMMDD_HHMMSS 按前缀分组、每组只保留最新 maxBackups 份；
    /// 并清理冗余双后缀 *.json.json（去末尾 .json 后原文件存在者，移回收站）。
    /// penumbra 根目录递归，翻译/词典目录只扫顶层。失败的文件跳过（不中断），但会通过 warn 记一行日志。
    /// </summary>
    public void CleanupLegacyBak(string? penumbraRoot, string? translationDir, string? dictionaryDir, int maxBackups)
    {
        var dirs = new[] { penumbraRoot, translationDir, dictionaryDir };
        for (var i = 0; i < dirs.Length; i++)
        {
            var dir = dirs[i];
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) continue;
            var search = i == 0 ? System.IO.SearchOption.AllDirectories : System.IO.SearchOption.TopDirectoryOnly;

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

            // 3) 冗余双后缀 *.json.json（如 meta.json.json）：去掉最后一个 .json 后原文件存在 -> 判定为冗余副本，移回收站
            //    背景：这类文件是同一份内容（同 Identifier）的「转义版」副本，插件与 Penumbra 都不读取，纯占空间。
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*.json.json", search))
                {
                    if (!Path.GetFileName(f).EndsWith(".json.json", StringComparison.OrdinalIgnoreCase)) continue;
                    var orig = f[..^".json".Length];
                    if (!File.Exists(orig)) continue; // 原文件不在，保守保留，不误删
                    try
                    {
                        FileSystem.DeleteFile(f, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                        Log?.Info($"[清理] 已清理冗余双后缀文件（移至回收站）：{f}");
                    }
                    catch (Exception ex) { Log?.Warn($"[清理] 清理冗余双后缀失败（已跳过）：{f} - {ex.Message}"); }
                }
            }
            catch (Exception ex)
            {
                Log?.Warn($"[清理] 枚举冗余双后缀失败（跳过该目录）：{dir} - {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 创建模组 zip 备份，命名「{来源}_{yyyy-MM-dd_HH-mm-ss}.zip」（如「后台汉化备份_2026-09-30_12-00-00.zip」），
    /// 内含 meta.json + group_*.json，压缩等级取最高（SmallestSize，无损）。
    /// 轮转：同目录全部备份 zip（兼容旧命名「时间戳备份.zip」）按时间只保留最新 maxBackups 份。返回 zip 路径，失败返回 null。
    /// </summary>
    /// <param name="source">备份来源名（用于区分是哪个操作触发的备份，如「自动备份」「后台汉化备份」）。</param>
    public string? CreateModZip(string modDirPath, int maxBackups, string source = "备份")
    {
        try
        {
            if (string.IsNullOrEmpty(modDirPath) || !Directory.Exists(modDirPath)) return null;
            var files = ReadModFiles(modDirPath);
            if (files.Count == 0) return null;

            var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            var zipPath = Path.Combine(modDirPath, $"{SanitizeSource(source)}_{stamp}.zip");
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                foreach (var f in files)
                {
                    // SmallestSize：最高压缩等级、无损；对 json 文本收益明显
                    var entry = zip.CreateEntry(Path.GetFileName(f.Path), CompressionLevel.SmallestSize);
                    using var es = entry.Open();
                    using var fs = File.OpenRead(f.Path);
                    fs.CopyTo(es);
                }
            }

            // 轮转：只保留最新 maxBackups 份备份 zip（按解析出的时间排序；不能按文件名，因新命名来源名在前）
            var all = Directory.EnumerateFiles(modDirPath)
                .Where(x => IsBackupZip(Path.GetFileName(x)))
                .OrderByDescending(x => BackupTime(Path.GetFileName(x)) ?? DateTime.MinValue)
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

    /// <summary> 备份来源名净化：替换文件名非法字符，空则回退「备份」。 </summary>
    private static string SanitizeSource(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) return "备份";
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder();
        foreach (var c in source)
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        var s = sb.ToString().Trim();
        return s.Length == 0 ? "备份" : s;
    }

    // 新命名：{来源}_{时间戳}.zip；旧命名：{时间戳}备份.zip
    private static readonly Regex NewBakName = new(
        @"^(?<src>.+)_(?<ts>\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2})\.zip$", RegexOptions.Compiled);
    private static readonly Regex OldBakName = new(
        @"^(?<ts>\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2})备份\.zip$", RegexOptions.Compiled);

    /// <summary> 是否为备份 zip（兼容新命名「来源_时间戳.zip」与旧命名「时间戳备份.zip」）。 </summary>
    public static bool IsBackupZip(string fileName)
        => OldBakName.IsMatch(fileName) || NewBakName.IsMatch(fileName);

    /// <summary> 从备份文件名解析时间（解析失败返回 null）。 </summary>
    public static DateTime? BackupTime(string fileName)
    {
        var m = OldBakName.Match(fileName);
        if (!m.Success) m = NewBakName.Match(fileName);
        if (!m.Success) return null;
        return DateTime.TryParseExact(m.Groups["ts"].Value, "yyyy-MM-dd_HH-mm-ss",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var t) ? t : (DateTime?)null;
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
