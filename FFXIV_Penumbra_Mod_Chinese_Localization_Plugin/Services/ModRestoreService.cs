using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace FFXIVPenumbraHanhua.Services;

/// <summary>
/// 模组还原服务：把被翻译改坏的组/选项文本恢复为原始状态。
/// ① HS 模组（有 heliosphere.json）：走 Heliosphere GraphQL 拿英文选项树还原；
/// ② 手动安装模组：从 手动安装 目录匹配原始 PMP，还原其自带文本。
/// [!] HS 下载协议依赖服务端，若其改版需按官方源码（git.sharlayan.cloud/heliosphere/plugin，queries/ 目录）更新查询。
/// </summary>
public sealed class ModRestoreService
{
    private const string ApiBase = "https://heliosphere.app/api";

    /// <summary> 选项树查询（imc 补了 originalIndex 用于对位）。 </summary>
    private const string VariantQuery = """
        query DownloadTask($versionId: UUID!) {
            getVersion(id: $versionId) {
                version
                groups {
                    standard { name description penumbraId originalIndex options { name description isDefault } }
                    imc { name description penumbraId originalIndex options { name description } }
                    combining { name description penumbraId originalIndex options { name description } }
                }
            }
        }
        """;

    /// <summary> Heliosphere 查询用的 HttpClient：设 30 秒超时——服务端挂起时尽快报错，
    /// 而非用默认 100 秒让用户在「还原中…」界面干等。 </summary>
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public string LastResult { get; private set; } = "";

    public static bool HasHsMeta(string modDir)
        => File.Exists(Path.Combine(modDir, "heliosphere.json"));

    /// <summary>
    /// 从 Heliosphere 还原：按 heliosphere.json 的 VersionId 查询英文选项树，
    /// 对位重写 meta.json 的 Groups（只改 Name/Description，选择状态等字段原样保留）
    /// 及 group_&lt;penumbraId&gt;.json。数据文件不受翻译影响，不下载。
    /// beforeWrite：全部查询/解析成功后、写第一个文件前回调（用于备份；抛异常即中止还原）。
    /// 返回结果描述。
    /// </summary>
    public async Task<string> RestoreFromHeliosphereAsync(string modDir, Action? beforeWrite = null)
    {
        var hsPath = Path.Combine(modDir, "heliosphere.json");
        if (!File.Exists(hsPath)) return "该模组无 heliosphere.json（非 HS 模组）";
        var hs = JsonNode.Parse(File.ReadAllText(hsPath, Encoding.UTF8));
        var versionId = hs?["VersionId"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(versionId)) return "heliosphere.json 缺少 VersionId";

        var body = new JsonObject
        {
            ["query"] = VariantQuery,
            ["variables"] = new JsonObject
            {
                ["versionId"] = versionId,
            },
        };
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await Http.PostAsync(ApiBase + "/graphql", content);
        var text = await resp.Content.ReadAsStringAsync();
        var json = JsonNode.Parse(text) ?? throw new InvalidOperationException("GraphQL 响应解析失败");
        if (json["errors"] is JsonArray errs && errs.Count > 0)
            return "查询失败：" + errs[0]?["message"]?.GetValue<string>();
        var gv = json["data"]?["getVersion"];
        var version = gv?["version"]?.GetValue<string>() ?? "?";
        var groups = gv?["groups"];
        if (groups == null) return "HS 未返回组数据";

        var metaPath = Path.Combine(modDir, "meta.json");
        var meta = JsonNode.Parse(File.ReadAllText(metaPath, Encoding.UTF8)) as JsonObject;
        if (meta == null) return "meta.json 解析失败";
        var mgroups = meta["Groups"] as JsonArray
                      ?? (meta["Mod"] as JsonObject)?["Groups"] as JsonArray;
        if (mgroups == null) return "meta.json 无 Groups（该模组可能没有选项）";

        // 查询与解析全部成功，写文件前备份（查询失败不建备份，避免重复产生备份包）
        beforeWrite?.Invoke();

        int pg = 0, po = 0;
        foreach (var key in new[] { "standard", "imc", "combining" })
        {
            if (groups[key] is not JsonArray arr) continue;
            foreach (var hgNode in arr)
            {
                if (hgNode is not JsonObject hg) continue;
                var idx = hg["originalIndex"]?.GetValue<int>() ?? -1;
                if (idx < 0 || idx >= mgroups.Count) continue;
                if (mgroups[idx] is not JsonObject mg) continue;
                var name = hg["name"]?.GetValue<string>();
                if (name != null) { mg["Name"] = name; pg++; }
                var desc = hg["description"]?.GetValue<string>();
                if (desc != null) mg["Description"] = desc;

                if (mg["Options"] is not JsonArray mopts) continue;
                if (hg["options"] is not JsonArray hopts) continue;
                for (int j = 0; j < hopts.Count && j < mopts.Count; j++)
                {
                    if (mopts[j] is not JsonObject mo || hopts[j] is not JsonObject ho) continue;
                    var on = ho["name"]?.GetValue<string>();
                    if (on != null) { mo["Name"] = on; po++; }
                    var od = ho["description"]?.GetValue<string>();
                    if (od != null) mo["Description"] = od;
                }
            }
        }

        // 原子写：这是「用户的模组文件」，File.WriteAllText 先截断再写，中途被打断会让 Penumbra 无法解析该模组
        JsonFile.WriteAtomic(metaPath, meta.ToJsonString(JsonFile.Indented));

        // 标准组的独立 group json（按 penumbraId 对应文件名）
        int pf = 0;
        if (groups["standard"] is JsonArray std)
        {
            foreach (var hgNode in std)
            {
                if (hgNode is not JsonObject hg) continue;
                var pid = hg["penumbraId"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(pid)) continue;
                var gp = Path.Combine(modDir, $"group_{pid}.json");
                if (!File.Exists(gp)) continue;
                if (JsonNode.Parse(File.ReadAllText(gp, Encoding.UTF8)) is not JsonObject gj) continue;
                var n = hg["name"]?.GetValue<string>();
                if (n != null) gj["Name"] = n;
                var dd = hg["description"]?.GetValue<string>();
                if (dd != null) gj["Description"] = dd;
                if (gj["Options"] is JsonArray mopts && hg["options"] is JsonArray hopts)
                    for (int j = 0; j < hopts.Count && j < mopts.Count; j++)
                    {
                        if (mopts[j] is not JsonObject mo || hopts[j] is not JsonObject ho) continue;
                        var on = ho["name"]?.GetValue<string>();
                        if (on != null) mo["Name"] = on;
                        var od = ho["description"]?.GetValue<string>();
                        if (od != null) mo["Description"] = od;
                    }
                JsonFile.WriteAtomic(gp, gj.ToJsonString(JsonFile.Indented));
                pf++;
            }
        }

        return $"已从 Heliosphere 还原（v{version}）：组 {pg} / 选项 {po} / group 文件 {pf}（数据文件未受翻译影响，未下载）";
    }

    /// <summary>
    /// 在 手动安装 目录（含子目录）里按名称相似度匹配原始 PMP。
    /// 返回最佳匹配路径；无足够相似度返回 null。
    /// </summary>
    public static string? FindOriginalPmp(string installedDirName, string modDisplayName, string manualDir)
    {
        if (!Directory.Exists(manualDir)) return null;
        var tokens = Tokenize(installedDirName + " " + modDisplayName);
        string? best = null;
        var bestScore = 0;
        foreach (var p in Directory.EnumerateFiles(manualDir, "*.pmp", SearchOption.AllDirectories))
        {
            var fn = Path.GetFileNameWithoutExtension(p);
            var score = 0;
            foreach (var t in Tokenize(fn))
            {
                if (tokens.Any(x => string.Equals(x, t, StringComparison.OrdinalIgnoreCase)))
                    score += t.Length;
            }
            if (score > bestScore) { bestScore = score; best = p; }
        }
        var min = Math.Max(4, tokens.Max(t => t?.Length ?? 0) / 2);
        return bestScore >= min ? best : null;
    }

    private static IEnumerable<string> Tokenize(string s)
        => Regex.Split(s, "[^0-9A-Za-z\u4e00-\u9fff]+").Where(t => t.Length > 1);

    /// <summary>
    /// 从原始 PMP 还原手动安装的模组：覆盖根级 json（meta 除外）为 PMP 原始内容，
    /// 并对位修补 meta 内嵌 Groups 的 Name/Description（其余字段保留）。
    /// beforeWrite：解析全部成功后、写第一个文件前回调（用于备份；抛异常即中止还原）。
    /// 返回结果描述。
    /// </summary>
    public string RestoreFromPmp(string modDir, string pmpPath, Action? beforeWrite = null)
    {
        using var z = new ZipArchive(File.OpenRead(pmpPath));

        var metaEntry = z.Entries.FirstOrDefault(x =>
            x.Name.Equals("meta.json", StringComparison.OrdinalIgnoreCase));
        if (metaEntry == null) return "PMP 内无 meta.json";
        string pmpMetaText;
        using (var s = metaEntry.Open())
        using (var sr = new StreamReader(s))
        { pmpMetaText = sr.ReadToEnd(); }
        var pmpMeta = JsonNode.Parse(pmpMetaText) as JsonObject;

        // 根级 json（原始文本）：meta 之外全部按 PMP 原样还原
        var rootJsons = z.Entries.Where(x =>
            x.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            && !x.Name.Equals("meta.json", StringComparison.OrdinalIgnoreCase)
            && !x.FullName.Contains('/')).ToList();

        // meta：内嵌 Groups 的 Name/Description 对位修补
        var metaPath = Path.Combine(modDir, "meta.json");
        var meta = JsonNode.Parse(File.ReadAllText(metaPath, Encoding.UTF8)) as JsonObject;
        if (meta == null) return "meta.json 解析失败";
        var mgroups = meta["Groups"] as JsonArray
                      ?? (meta["Mod"] as JsonObject)?["Groups"] as JsonArray;
        if (mgroups == null) return "meta.json 无 Groups（该模组可能没有选项）";

        var pmpGroups = pmpMeta?["Groups"] as JsonArray;
        int pg = 0, po = 0;
        if (pmpGroups != null && pmpGroups.Count > 0)
        {
            // PMP meta 自带 Groups（v4 式）：直接对位还原
            for (int i = 0; i < pmpGroups.Count && i < mgroups.Count; i++)
            {
                if (pmpGroups[i] is not JsonObject src) continue;
                if (mgroups[i] is not JsonObject dst) continue;
                var n = src["Name"]?.GetValue<string>();
                if (n != null) dst["Name"] = n;
                var dd = src["Description"]?.GetValue<string>();
                if (dd != null) dst["Description"] = dd;
                if (src["Options"] is not JsonArray so) continue;
                if (dst["Options"] is not JsonArray dso) continue;
                for (int j = 0; j < so.Count && j < dso.Count; j++)
                {
                    if (so[j] is not JsonObject soo || dso[j] is not JsonObject dsoo) continue;
                    var on = soo["Name"]?.GetValue<string>();
                    if (on != null) dsoo["Name"] = on;
                    var od = soo["Description"]?.GetValue<string>();
                    if (od != null) dsoo["Description"] = od;
                }
            }
        }
        else
        {
            // v5 式（组定义在根级 json）：按文件对位修补内嵌 Groups 的名称
            foreach (var e in rootJsons)
            {
                string ptext;
                using (var es = e.Open())
                using (var sr = new StreamReader(es))
                { ptext = sr.ReadToEnd(); }
                var pj = JsonNode.Parse(ptext) as JsonObject;
                var pid = Path.GetFileNameWithoutExtension(e.Name);
                var idx = -1;
                for (int k = 0; k < mgroups.Count; k++)
                {
                    if (mgroups[k] is JsonObject g && string.Equals(g["Name"]?.GetValue<string>(), pid, StringComparison.OrdinalIgnoreCase))
                    { idx = k; break; }
                }
                if (idx < 0) continue;
                if (mgroups[idx] is not JsonObject mg) continue;
                var n = pj?["Name"]?.GetValue<string>();
                if (n != null) mg["Name"] = n;
                var dd = pj?["Description"]?.GetValue<string>();
                if (dd != null) mg["Description"] = dd;
            }
        }

        // PMP 与当前 meta 解析全部成功，写文件前备份（匹配/解析失败不建备份）
        beforeWrite?.Invoke();

        // 根级 json 整文件还原（含 default_mod.json）
        // 同类对齐：File.Create 会先截断目标，拷贝中途被打断就留下半截 json；
        // 改为「先写 .tmp 再替换」，与上面 meta 的原子写口径一致。
        foreach (var e in rootJsons)
        {
            using var src = e.Open();
            var target = Path.Combine(modDir, Path.GetFileName(e.Name));
            var tmp = target + ".tmp";
            using (var dst = File.Create(tmp)) src.CopyTo(dst);
            File.Move(tmp, target, overwrite: true);
        }
        // 原子写：这是「用户的模组文件」，File.WriteAllText 先截断再写，中途被打断会让 Penumbra 无法解析该模组
        JsonFile.WriteAtomic(metaPath, meta.ToJsonString(JsonFile.Indented));

        return $"已从原始 PMP 还原：组 {pg} / 选项 {po} / 根 json {rootJsons.Count} 个";
    }

    private static ZipArchive zipfile(string path) => new(File.OpenRead(path));
}
