using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace FFXIVPenumbraHanhua.Services;

/// <summary> 汉化闭环：读模组文件 → 词典翻译 → 备份 → 写回 → Penumbra 重载。 </summary>
public sealed class HanhuaService
{
    private readonly PenumbraService _penumbra;
    private readonly DictionaryService _dict;
    private readonly ModFileService _files;
    private readonly EnglishSnapshotService _snapshot;
    private readonly AppLog _log;

    public string LastResult { get; private set; } = "尚未执行翻译";

    public HanhuaService(PenumbraService penumbra, DictionaryService dict, ModFileService files,
        EnglishSnapshotService snapshot, AppLog log)
    {
        _penumbra = penumbra;
        _dict = dict;
        _files = files;
        _snapshot = snapshot;
        _log = log;
    }

    /// <summary> 翻译并写入一个模组。返回翻译修改的条目数；失败返回 -1。 </summary>
    public int TranslateMod(string modDirectory, string modName)
    {
        var modRoot = _penumbra.GetModRoot();
        if (string.IsNullOrEmpty(modRoot))
        {
            LastResult = "无法获取 Penumbra 模组根目录";
            _log.Warn($"[词典翻译写入] {modDirectory}：{LastResult}");
            return -1;
        }

        var modPath = Path.Combine(modRoot, modDirectory);
        if (!Directory.Exists(modPath))
        {
            LastResult = $"模组目录不存在：{modPath}";
            _log.Warn($"[词典翻译写入] {LastResult}");
            return -1;
        }

        var fileInfos = _files.ReadModFiles(modPath);
        if (fileInfos.Count == 0)
        {
            LastResult = "未找到 meta.json / group_*.json（该模组可能没有选项）";
            _log.Info($"[词典翻译写入] {modDirectory}：{LastResult}");
            return 0;
        }

        // 翻译前：整个模组打 zip 备份（yyyy-MM-dd_HH-mm-ss备份.zip），失败则跳过写回
        var maxBackups = _files.MaxBackups;
        var zipBackup = _files.CreateModZip(modPath, maxBackups);
        if (zipBackup == null)
        {
            LastResult = "备份失败，已跳过写回（无 meta.json / group_*.json 或打包异常）";
            _log.Error($"[词典翻译写入] {modDirectory}：{LastResult}");
            return -1;
        }

        var totalChanged = 0;
        var totalFiles = 0;
        var errors = new List<string>();

        foreach (var file in fileInfos)
        {
            // 写回前：文件仍为纯英文时存英文快照（覆写时用于找回英文原文 key）
            try
            {
                _snapshot.SaveIfEnglish(modDirectory, file.FileName, File.ReadAllText(file.Path));
            }
            catch (Exception ex)
            {
                _log.Warn($"[词典翻译写入] {modDirectory}/{file.FileName}：英文快照保存失败（覆写功能可能受影响）：{ex.Message}");
            }
            var snapInfo = _snapshot.GetEnglish(modDirectory, file.FileName);

            var groupNames = new Dictionary<int, string>();
            var optionNames = new Dictionary<(int, int), string>();
            var optionDescs = new Dictionary<(int, int), string>();

            foreach (var g in file.Groups)
            {
                var gKey = $"{file.FileName}||Name||{g.Name}";
                var gZh = TranslateWithOverwrite(g.Name, gKey, file, modDirectory, g.Index, null,
                    TextKind.Group, snapInfo, _dict, _snapshot);
                if (gZh != g.Name && gZh.Length > 0)
                {
                    groupNames[g.Index] = gZh;
                    totalChanged++;
                }

                foreach (var o in g.Options)
                {
                    var oKey = $"{file.FileName}||Opt||{o.Name}";
                    var oZh = TranslateWithOverwrite(o.Name, oKey, file, modDirectory, g.Index, o.Index,
                        TextKind.Option, snapInfo, _dict, _snapshot);
                    if (oZh != o.Name && oZh.Length > 0)
                    {
                        optionNames[(g.Index, o.Index)] = oZh;
                        totalChanged++;
                    }

                    if (!string.IsNullOrWhiteSpace(o.Description))
                    {
                        var dKey = $"{file.FileName}||Description||{o.Description}";
                        var dZh = TranslateWithOverwrite(o.Description, dKey, file, modDirectory, g.Index, o.Index,
                            TextKind.Description, snapInfo, _dict, _snapshot);
                        if (dZh != o.Description && dZh.Length > 0)
                        {
                            optionDescs[(g.Index, o.Index)] = dZh;
                            totalChanged++;
                        }
                    }
                }
            }

            if (groupNames.Count == 0 && optionNames.Count == 0 && optionDescs.Count == 0)
            {
                // 按本文件判定（不能用跨文件累计的 totalChanged，否则后续空文件会被误报）
                errors.Add(file.Groups.Count == 0
                    ? $"{file.FileName}：无可翻译内容"
                    : $"{file.FileName}：词典未命中，无需修改");
                continue;
            }

            if (_files.WriteTranslation(file.Path, file, groupNames, optionNames, optionDescs))
            {
                totalFiles++;
            }
            else
            {
                errors.Add($"{file.FileName}：写入失败");
            }
        }

        // 触发 Penumbra 重载，游戏内立即生效
        var reload = _penumbra.Reload(modDirectory, modName);

        var sb = new StringBuilder();
        sb.Append($"已翻译 {totalChanged} 项 / {totalFiles} 个文件（已备份模组 {zipBackup}）");
        if (reload == Penumbra.Api.Enums.PenumbraApiEc.Success)
            sb.Append("，已触发重载生效");
        else
            sb.Append("，重载未成功（可手动在 Penumbra 刷新）");
        if (errors.Count > 0)
            sb.Append("；" + string.Join("；", errors.Take(3)) + (errors.Count > 3 ? $" 等 {errors.Count} 条提示" : ""));
        LastResult = sb.ToString();
        _log.Info($"[词典翻译写入] {modDirectory}：{LastResult}");
        return totalChanged;
    }

    /// <summary> 文本种类（决定快照里取哪段英文原文）。 </summary>
    private enum TextKind { Group, Option, Description }

    /// <summary>
    /// 翻译单个条目（支持覆写）：
    /// - 已是中文：先用英文快照找回原文 → 以英文 key 重新查词典（词典更新后覆盖旧译法）；
    ///   快照缺失或未命中新译文时回退为现有剥壳处理。
    /// - 纯英文：走现有翻译管线。
    /// </summary>
    private static string TranslateWithOverwrite(string current, string modKey, ModFileInfo file,
        string modDir, int gIndex, int? oIndex, TextKind kind, ModFileInfo? snapInfo,
        DictionaryService dict, EnglishSnapshotService snapshot)
    {
        if (!dict.ContainsChinese(current))
            return Translator.Translate(current, modKey, dict);

        // 已是中文 → 尝试覆写
        var en = FindEnglish(snapInfo, gIndex, oIndex, kind);
        if (!string.IsNullOrEmpty(en) && en != current)
        {
            var enKey = $"{file.FileName}||{KindTag(kind)}||{en}";
            var newZh = Translator.Translate(en, enKey, dict);
            if (newZh.Length > 0 && newZh != en && newZh != current)
                return newZh; // 词典有新译文 → 覆盖
        }

        // 回退：现有剥壳处理（双语规范化 / 黑名单还原等）
        return Translator.Translate(current, modKey, dict);
    }

    /// <summary> 从英文快照中取对应组/选项的英文原文。 </summary>
    private static string? FindEnglish(ModFileInfo? snapInfo, int gIndex, int? oIndex, TextKind kind)
    {
        if (snapInfo == null) return null;
        var g = snapInfo.Groups.FirstOrDefault(x => x.Index == gIndex);
        if (g == null) return null;
        if (kind == TextKind.Group) return g.Name;
        if (oIndex == null) return null;
        var o = g.Options.FirstOrDefault(x => x.Index == oIndex);
        if (o == null) return null;
        return kind == TextKind.Description ? o.Description : o.Name;
    }

    private static string KindTag(TextKind kind) => kind switch
    {
        TextKind.Group => "Name",
        TextKind.Description => "Description",
        _ => "Opt"
    };
}
