using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace FFXIVPenumbraHanhua.Services;

/// <summary> AI 翻译：OpenAI 兼容 /chat/completions 接口，翻译「_未翻译.json」→「_已翻译.json」。 </summary>
public sealed class AiTranslateService
{
    private readonly AppLog _log;
    /// <summary>
    /// HttpClient 单例。走代理时需重建实例（HttpClient 创建后无法更换 Handler 的代理），
    /// 由 <see cref="ApplyProxyConfig"/> 在启动与配置变更时刷新；旧的延迟释放（避免在飞请求被中断）。
    /// </summary>
    private static HttpClient Http = new();
    private static readonly object HttpLock = new();

    /// <summary> 按配置应用代理（未启用或地址为空 → 恢复系统默认代理行为）。 </summary>
    public static void ApplyProxyConfig(Configuration cfg)
    {
        var useProxy = cfg.UseProxy && !string.IsNullOrWhiteSpace(cfg.ProxyAddress);
        try
        {
            HttpClientHandler handler;
            if (useProxy)
            {
                var addr = cfg.ProxyAddress.Trim();
                if (!addr.Contains("://")) addr = "http://" + addr; // 容忍只填 ip:端口
                handler = new HttpClientHandler
                {
                    Proxy = new System.Net.WebProxy(addr),
                    UseProxy = true
                };
            }
            else
            {
                handler = new HttpClientHandler(); // 默认：使用系统代理设置
            }

            var fresh = new HttpClient(handler);
            lock (HttpLock)
            {
                var stale = Http;
                Http = fresh;
                // 延迟释放旧实例：可能仍有在飞请求持用它（立即 Dispose 会中断那些请求）
                _ = Task.Delay(TimeSpan.FromMinutes(2)).ContinueWith(t => stale.Dispose());
            }
        }
        catch (Exception)
        {
            /* 代理地址非法：保留原 HttpClient，不影响直连场景 */
        }
    }

    public string LastResult { get; private set; } = "";

    /// <summary> 预置供应商（国内可直连的优先置顶，海外在后；自定义模式见 Combo 首项）。
    /// 默认模型取**免费档**（成本优先，免费档足以满足本插件的翻译需求）：实测 glm-4-flash-250414 免费用、1.3 秒/批、JSON 稳定。 </summary>
    public static readonly (string Name, string Model, string BaseUrl, string Note)[] Providers =
    {
        ("智谱 GLM", "glm-4-flash-250414", "https://open.bigmodel.cn/api/paas/v4", "★推荐·免费：glm-4-flash-250414（实测 1.3 秒/批，JSON 输出稳定、128K 长上下文）"),
        ("通义千问", "qwen-plus", "https://dashscope.aliyuncs.com/compatible-mode/v1", "阿里云百炼（OpenAI 兼容，需先开通百炼）"),
        ("腾讯混元", "hunyuan-turbos-latest", "https://api.hunyuan.cloud.tencent.com/v1", "腾讯云大模型（OpenAI 兼容）"),
        ("百度千帆", "ernie-4.5-turbo-32k", "https://qianfan.baidubce.com/v2", "百度智能云千帆（OpenAI 兼容）"),
        ("DeepSeek", "deepseek-flash", "https://api.deepseek.com/v1", "深度求索（OpenAI 兼容，国内可直连；deepseek-flash=DeepSeek-V4.1-Flash）"),
        ("OpenRouter", "openai/gpt-4o-mini", "https://openrouter.ai/api/v1", "海外聚合中转，可调 GPT/Claude/Gemini（模型名见 openrouter.ai/models）"),
        ("Groq", "openai/gpt-oss-120b", "https://api.groq.com/openai/v1", "开源模型超高速推理（海外）"),
        ("OpenAI（GPT）", "gpt-5-mini", "https://api.openai.com/v1", "官方接口：国内网络不可直连，需代理或中转"),
        ("Google Gemini", "gemini-2.5-flash", "https://generativelanguage.googleapis.com/v1beta/openai", "谷歌官方 OpenAI 兼容端点：国内不可直连"),
        ("Anthropic Claude", "claude-sonnet-4-5", "https://api.anthropic.com/v1", "Anthropic 官方：国内不可直连，需代理"),
        ("xAI Grok", "grok-4.3", "https://api.x.ai/v1", "xAI 官方（OpenAI 兼容）：国内不可直连"),
        ("Mistral", "mistral-small-latest", "https://api.mistral.ai/v1", "Mistral 官方：国内不可直连")
    };

    public AiTranslateService(AppLog log)
    {
        _log = log;
    }

    /// <summary> 全部可选服务商：自定义置顶 + 内置 12 家。 </summary>
    public static List<(string Name, string Model, string BaseUrl, string Note)> GetAllProviders(Configuration cfg)
    {
        var list = new List<(string, string, string, string)>();
        if (cfg.CustomProviders != null)
        {
            foreach (var cp in cfg.CustomProviders)
            {
                if (string.IsNullOrWhiteSpace(cp.Name)) continue;
                list.Add((cp.Name.Trim(), cp.DefaultModel ?? "", cp.BaseUrl ?? "", "自定义供应商（可在 AI 设置中修改）"));
            }
        }
        list.AddRange(Providers);
        return list;
    }

    /// <summary> 按名称解析生效服务商（优先自定义，其次内置）；找不到返回 null（回退旧下标逻辑）。 </summary>
    private static (string Name, string Model, string BaseUrl, string Note)? FindByName(Configuration cfg)
    {
        var n = (cfg.AiProviderName ?? "").Trim();
        if (string.IsNullOrEmpty(n)) return null;
        if (cfg.CustomProviders != null)
        {
            foreach (var cp in cfg.CustomProviders)
            {
                if (string.IsNullOrWhiteSpace(cp.Name)) continue;
                if (cp.Name.Trim() == n)
                    return (cp.Name.Trim(), cp.DefaultModel ?? "", cp.BaseUrl ?? "", "自定义供应商");
            }
        }
        foreach (var p in Providers)
            if (p.Name == n) return p;
        return null;
    }

    /// <summary> 获取生效的 BaseUrl / 模型名。按名称优先（自定义/内置）；名称未设时回退旧下标：AiProvider &lt; 0 为手工自定义模式。 </summary>
    public static (string BaseUrl, string Model) ResolveEndpoint(Configuration cfg)
    {
        var named = FindByName(cfg);
        if (named != null)
        {
            var baseUrl = string.IsNullOrWhiteSpace(cfg.AiBaseUrl) ? named.Value.BaseUrl : cfg.AiBaseUrl.TrimEnd('/');
            var model = string.IsNullOrWhiteSpace(cfg.AiModel) ? named.Value.Model : cfg.AiModel.Trim();
            return (baseUrl, model);
        }
        if (cfg.AiProvider < 0)
        {
            var cb = (cfg.AiBaseUrl ?? "").Trim().TrimEnd('/');
            var cm = (cfg.AiModel ?? "").Trim();
            if (string.IsNullOrEmpty(cb) || string.IsNullOrEmpty(cm))
                return ("", ""); // 缺参数，由调用方提示
            return (cb, cm);
        }
        var p = Providers[Math.Clamp(cfg.AiProvider, 0, Providers.Length - 1)];
        var pb = string.IsNullOrWhiteSpace(cfg.AiBaseUrl) ? p.BaseUrl : cfg.AiBaseUrl.TrimEnd('/');
        var pm = string.IsNullOrWhiteSpace(cfg.AiModel) ? p.Model : cfg.AiModel.Trim();
        return (pb, pm);
    }

    /// <summary> 当前服务商的名字（名称优先；手工自定义模式返回「自定义」）。 </summary>
    public static string CurrentProviderName(Configuration cfg)
    {
        var named = FindByName(cfg);
        if (named != null) return named.Value.Name;
        return cfg.AiProvider < 0
            ? "自定义"
            : Providers[Math.Clamp(cfg.AiProvider, 0, Providers.Length - 1)].Name;
    }

    /// <summary> 单请求输出上限 max_tokens（按平台自动；未知平台沿用旧值避免 400）。
    /// ⚠ 取值原则：**宁可偏大不可偏小**——过大只是偶发 400（有自愈可自动纠正），
    /// 过小会把译文输出**截断**导致整批 JSON 解析失败（且不报 400、不触发自愈）。
    /// 须与 <see cref="MaxBatchChars"/> 的输入上限匹配（中文输出 token ≈ 字符数）。 </summary>
    public static long MaxTokensForModel(Configuration cfg)
    {
        // 必须用解析后的生效端点/模型判断平台（选预设服务商时 AiBaseUrl/AiModel 覆盖字段为空）
        var (epUrl, epModel) = ResolveEndpoint(cfg);
        var m = (epModel ?? "").ToLowerInvariant();
        var b = (epUrl ?? "").ToLowerInvariant();
        if (b.Contains("deepseek") || m.Contains("deepseek"))
            return 384000; // DeepSeek 官方最大输出 384K（api-docs.deepseek.com 定价页）
        if (b.Contains("bigmodel") || m.Contains("bigmodel") || b.Contains("moonshot"))
            return 16384;  // 智谱 GLM：实测上限 16384（超出即 400）；Kimi 同档保守值
        if (b.Contains("dashscope") || b.Contains("aliyuncs"))
            return 32000;  // 通义百炼（与 20000 字符输入匹配）
        return 16384;      // 其余平台：OpenAI gpt-4o 档位；Claude/Gemini 超出会 400 并由自愈纠正
    }

    /// <summary> 单批输入内容字符上限（按平台自动，防止超长被拒；条数上限同时生效）。
    /// ⚠ 必须与 <see cref="MaxTokensForModel"/> 的输出上限匹配：中文译文输出 token 数 ≈ 输入字符数，
    /// 输入上限超过输出上限时译文会被截断 → 整批 JSON 解析失败。 </summary>
    public static int MaxBatchChars(Configuration cfg)
    {
        var (epUrl, epModel) = ResolveEndpoint(cfg);
        var m = (epModel ?? "").ToLowerInvariant();
        var b = (epUrl ?? "").ToLowerInvariant();
        if (b.Contains("deepseek") || m.Contains("deepseek"))
            return 30000;  // DeepSeek：输出 384K，输入可放宽
        if (b.Contains("bigmodel") || m.Contains("bigmodel"))
            return 12000;  // 智谱：输出上限 16384 token，输入留足余量避免截断
        if (b.Contains("moonshot") || b.Contains("dashscope") || b.Contains("aliyuncs"))
            return 20000;
        return 12000;      // 其余平台保守值（对应 16384 输出上限）
    }

    /// <summary> 联网搜索：当前平台是否支持 OpenAI 兼容顶层 enable_search（仅通义/百炼）。 </summary>
    public static bool PlatformSupportsWebSearch(Configuration cfg)
    {
        var (epUrl, _) = ResolveEndpoint(cfg);
        var b = (epUrl ?? "").ToLowerInvariant();
        return b.Contains("dashscope") || b.Contains("aliyuncs");
    }

    /// <summary> 关闭深度思考：按平台/模型特征附加各家关闭思考参数（未知平台不传，防止 400）。 </summary>
    private static void ApplyNoDeepThink(JsonObject body, Configuration cfg)
    {
        if (!cfg.AiDisableThinking) return;
        var (epUrl, epModel) = ResolveEndpoint(cfg);
        var m = (epModel ?? "").ToLowerInvariant();
        var b = (epUrl ?? "").ToLowerInvariant();
        if (b.Contains("deepseek") || m.Contains("deepseek"))
        {
            body["thinking"] = new JsonObject { ["type"] = "disabled" };
        }
        else if (b.Contains("bigmodel") && m.Contains("glm-5.2"))
        {
            body["thinking"] = new JsonObject { ["type"] = "disabled" };
        }
        else if (b.Contains("moonshot") && m.Contains("kimi-k2.6"))
        {
            body["thinking"] = new JsonObject { ["type"] = "disabled" };
        }
        else if ((b.Contains("dashscope") || b.Contains("aliyuncs")) &&
                 (m.Contains("qwen3") || m.Contains("qwq")))
        {
            body["enable_thinking"] = false; // 通义百炼用独立字段
        }
    }

    /// <summary> 联网搜索：仅通义/百炼支持时注入 enable_search。 </summary>
    private static void ApplyWebSearch(JsonObject body, Configuration cfg)
    {
        if (cfg.AiWebSearch && PlatformSupportsWebSearch(cfg))
        {
            body["enable_search"] = true;
        }
    }

    /// <summary> 读取当前服务商保存的 API Key（旧版单一字段自动迁移兜底）。 </summary>
    public static string GetApiKey(Configuration cfg)
    {
        var name = CurrentProviderName(cfg);
        if (cfg.AiApiKeys != null && cfg.AiApiKeys.TryGetValue(name, out var k) && !string.IsNullOrWhiteSpace(k))
            return k;
        // 兼容旧版单一字段：仅在还没有任何分服务商 Key 时使用
        if (cfg.AiApiKeys == null || cfg.AiApiKeys.Count == 0)
            return cfg.AiApiKey ?? "";
        return "";
    }

    /// <summary> 保存当前服务商的 API Key。 </summary>
    public static void SetApiKey(Configuration cfg, string key)
    {
        var name = CurrentProviderName(cfg);
        cfg.AiApiKeys ??= new();
        if (string.IsNullOrWhiteSpace(key))
            cfg.AiApiKeys.Remove(name);
        else
            cfg.AiApiKeys[name] = key.Trim();
    }

    /// <summary> 测试连接（返回简短结果）。 </summary>
    public async Task<string> TestAsync(Configuration cfg)
    {
        try
        {
            var (baseUrl, model) = ResolveEndpoint(cfg);
            if (cfg.AiProvider < 0 && (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(model)))
                return "自定义模式需填写 API 地址与模型";
            var apiKey = GetApiKey(cfg);
            if (string.IsNullOrWhiteSpace(apiKey))
                return "未填写 API Key";
            var body = new JsonObject
            {
                ["model"] = model,
                ["messages"] = new JsonArray(
                    new JsonObject { ["role"] = "user", ["content"] = "你好，请回复：连接成功" }),
                ["temperature"] = 0.1,
                ["max_tokens"] = 50
            };
            var resp = await PostAsync(baseUrl, apiKey, body);
            var content = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                return $"连接失败（HTTP {(int)resp.StatusCode}）：{Truncate(content, 200)}";
            return "连接成功：" + Truncate(ExtractContent(content) ?? "", 80);
        }
        catch (Exception ex)
        {
            return "连接异常：" + ex.Message;
        }
    }

    /// <summary> 翻译未翻译文件。inputPath → outputPath。返回翻译成功的条目数。 </summary>
    public async Task<int> TranslateAsync(string inputPath, string outputPath, Configuration cfg, CancellationToken ct = default)
    {
        var apiKey = GetApiKey(cfg);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            LastResult = "未填写 API Key（请在「AI 设置」中配置）";
            return -1;
        }
        if (!File.Exists(inputPath))
        {
            LastResult = "未找到提取文件：" + inputPath;
            return -1;
        }

        var (baseUrl, model) = ResolveEndpoint(cfg);
        if (cfg.AiProvider < 0 && (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(model)))
        {
            LastResult = "自定义模式需填写 API 地址与模型（AI 设置）";
            return -1;
        }
        var root = JsonNode.Parse(File.ReadAllText(inputPath, Encoding.UTF8)) as JsonObject;
        if (root == null)
        {
            LastResult = "提取文件格式错误";
            return -1;
        }

        var options = root["_options"] as JsonObject ?? new JsonObject();
        var descriptions = root["_descriptions"] as JsonObject ?? new JsonObject();

        // 收集未翻译项（值为空/纯英文）
        var pending = new List<string>();
        foreach (var o in options)
            if (IsPending(o.Value)) pending.Add(o.Key);
        foreach (var d in descriptions)
            if (IsPending(d.Value)) pending.Add(d.Key);

        if (pending.Count == 0)
        {
            LastResult = "没有待翻译项（词典已全部覆盖，可直接「翻译写入MOD」）";
            return 0;
        }

        // 规则段
        var rules = root["翻译规则"] as JsonObject ?? ExtractService.BuildTranslationRules(cfg.DictionaryPath);

        var batchSize = Math.Clamp(cfg.AiBatchSize, 1, 500);
        var maxBatchChars = MaxBatchChars(cfg);
        var ok = 0;
        var errors = new List<string>();

        // 自动分批：条数不超过 batchSize，且内容字符数不超过平台上限（超限自动拆批）
        var batches = new List<List<string>>();
        var cur = new List<string>();
        var curChars = 0;
        foreach (var k in pending)
        {
            var itemChars = k.Length;
            if (cur.Count >= batchSize || (cur.Count > 0 && curChars + itemChars > maxBatchChars))
            {
                batches.Add(cur);
                cur = new List<string>();
                curChars = 0;
            }
            cur.Add(k);
            curChars += itemChars;
        }
        if (cur.Count > 0) batches.Add(cur);

        _log.Info($"AI 翻译：开始 {Path.GetFileName(inputPath)}（{pending.Count} 项待翻译，自动分 {batches.Count} 批）");

        for (var bi = 0; bi < batches.Count; bi++)
        {
            if (ct.IsCancellationRequested)
            {
                _log.Info($"AI 翻译：已取消（{Path.GetFileName(inputPath)}，已完成 {ok}/{pending.Count} 条）");
                break;
            }
            var batch = batches[bi];
            var batchObj = new JsonObject();
            foreach (var k in batch)
            {
                if (options.ContainsKey(k)) batchObj[k] = "";
                else if (descriptions.ContainsKey(k)) batchObj[k] = "";
            }

            var sysMsg = "你是 FFXIV 模组汉化助手。按以下规则把英文翻译为简体中文（纯中文，不带英文对照）。" +
                "只输出 JSON，不要输出任何其他文字。JSON 结构：{\"_options\":{...},\"_descriptions\":{...}}，键原样保留。\n\n" +
                rules.ToJsonString();

            try
            {
                _log.Info($"AI 翻译：{Path.GetFileName(inputPath)} 批次 {bi + 1}/{batches.Count}（{batch.Count} 项，当前：{KeySummary(batch[0])}）");
                // 带 max_tokens 自愈的请求（平台上限写错时：解析其自报上限 → 记住 → 以正确值重试本批一次）
                var (reqOk, text, reqErr) = await SendBatchWithSelfHealAsync(
                    baseUrl, apiKey, model, sysMsg, batchObj, cfg, ct);
                if (!reqOk)
                {
                    errors.Add($"批次 {bi + 1}：{reqErr}");
                    continue;
                }

                var parsed = ParseJson(text);
                if (parsed == null)
                {
                    errors.Add("AI 返回无法解析的内容（可能是限流/超长，建议减小批量）");
                    continue;
                }

                var got = 0;
                if (parsed["_options"] is JsonObject po)
                {
                    foreach (var kv in po)
                    {
                        if (options.ContainsKey(kv.Key) && kv.Value != null && kv.Value.ToString().Length > 0)
                        {
                            options[kv.Key] = kv.Value.ToString();
                            got++;
                        }
                    }
                }
                if (parsed["_descriptions"] is JsonObject pd)
                {
                    foreach (var kv in pd)
                    {
                        if (descriptions.ContainsKey(kv.Key) && kv.Value != null && kv.Value.ToString().Length > 0)
                        {
                            descriptions[kv.Key] = kv.Value.ToString();
                            got++;
                        }
                    }
                }
                ok += got;
                _log.Info($"AI 翻译：{Path.GetFileName(inputPath)} 已完成 {ok}/{pending.Count} 条");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 用户取消：中断在飞请求，已完成的批次照常写出，不再记为错误
                _log.Info($"AI 翻译：已取消（{Path.GetFileName(inputPath)}，已完成 {ok}/{pending.Count} 条）");
                break;
            }
            catch (Exception ex)
            {
                errors.Add("请求异常：" + ex.Message);
            }
        }

        // 写出 _已翻译.json（保留翻译规则）
        root["_options"] = options;
        root["_descriptions"] = descriptions;
        try
        {
            if (!Directory.Exists(Path.GetDirectoryName(outputPath))) Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.WriteAllText(outputPath, root.ToJsonString(JsonFile.Indented), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            errors.Add("写文件失败：" + ex.Message);
        }

        var sb = new StringBuilder();
        var cancelled = ct.IsCancellationRequested;
        sb.Append($"AI 翻译{(cancelled ? "已取消" : "完成")}：命中 {ok}/{pending.Count} 项 → {outputPath}");
        if (errors.Count > 0)
            sb.Append("；问题：" + string.Join("；", errors.Take(3)) + (errors.Count > 3 ? $" 等 {errors.Count} 条" : ""));
        LastResult = sb.ToString();
        _log.Info(LastResult);
        return ok;
    }

    private static bool IsPending(JsonNode? v)
        => v == null || v.ToString().Trim().Length == 0 || !ContainsChinese(v.ToString());

    /// <summary> 取 key 的原文段做进度摘要（key 格式：模组目录/文件||字段||原文）。 </summary>
    private static string KeySummary(string key)
    {
        var parts = key.Split(new[] { "||" }, StringSplitOptions.None);
        var text = parts.Length >= 3 ? parts[2] : key;
        return text.Length > 28 ? text[..28] + "…" : text;
    }

    private static bool ContainsChinese(string s)
    {
        foreach (var c in s)
            if (c >= 0x4E00 && c <= 0x9FFF) return true;
        return false;
    }

    private async Task<HttpResponseMessage> PostAsync(string baseUrl, string apiKey, JsonObject body,
        CancellationToken ct = default)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/chat/completions");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return await Http.SendAsync(req, ct); // 传入 token：取消时立即中断在飞请求，而非等它自然返回
    }

    /// <summary>
    /// 发送一批请求，含 max_tokens 自愈：平台拒绝（400 且提示 max_tokens）时解析其自报上限、
    /// 记住并以正确值重试本批一次——任何平台上限写错最多只浪费一次请求，不会整轮白跑。
    /// 返回 (是否成功, 正文文本, 错误摘要)；用户取消时抛 OperationCanceledException 由调用方处理。
    /// </summary>
    private async Task<(bool Ok, string? Text, string? Error)> SendBatchWithSelfHealAsync(
        string baseUrl, string apiKey, string model, string sysMsg, JsonObject batchObj,
        Configuration cfg, CancellationToken ct)
    {
        var maxTok = EffectiveMaxTokens(cfg, baseUrl);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var body = new JsonObject
            {
                ["model"] = model,
                ["messages"] = new JsonArray(
                    new JsonObject { ["role"] = "system", ["content"] = sysMsg },
                    new JsonObject { ["role"] = "user", ["content"] = "待翻译内容（键必须原样保留，值填中文译文）：\n" + batchObj.ToJsonString() }),
                ["temperature"] = cfg.AiTemperature,
                ["max_tokens"] = maxTok
            };
            ApplyNoDeepThink(body, cfg);
            ApplyWebSearch(body, cfg);

            using var resp = await PostAsync(baseUrl, apiKey, body, ct);
            var content = await resp.Content.ReadAsStringAsync(ct);
            if (resp.IsSuccessStatusCode) return (true, ExtractContent(content), null);

            // 自愈：400 且提到 max_tokens → 学上限、以正确值重试本批
            if (attempt == 0 && (int)resp.StatusCode == 400 &&
                content.Contains("max_tokens", StringComparison.OrdinalIgnoreCase))
            {
                var cap = ParseMaxTokensCap(content);
                if (cap > 0)
                {
                    _maxTokensLearned[HostKey(baseUrl)] = cap;
                    _log.Warn($"AI 翻译：平台拒绝 max_tokens（{Truncate(content, 100)}）——已自动下调到 {cap} 并重试本批");
                    maxTok = cap;
                    continue;
                }
            }
            // 模型名类错误（模型不存在/已下线）：平台模型名变化快，预设名可能过时 → 明确引导用户自行改
            // ⚠ 必须同时认英文与中文提示：实测智谱返回中文「模型不存在，请检查模型代码。」（不含 "model" 字样）
            var lowered = content.ToLowerInvariant();
            var modelErr =
                (lowered.Contains("model") && (lowered.Contains("not found") || lowered.Contains("not exist")
                    || lowered.Contains("invalid") || lowered.Contains("unknown") || lowered.Contains("does not exist")))
                || content.Contains("模型不存在") || content.Contains("模型代码")
                || content.Contains("模型名") || content.Contains("模型已下线") || content.Contains("无效的模型");
            var modelHint = modelErr
                ? "（模型名可能已过时：请到「AI 设置」把模型改成该平台当前可用的模型名）"
                : "";
            return (false, null, $"HTTP {(int)resp.StatusCode} " + Truncate(content, 120) + modelHint);
        }
        return (false, null, "max_tokens 自愈重试后仍失败");
    }

    private static string? ExtractContent(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var msg = doc.RootElement.GetProperty("choices")[0].GetProperty("message");
            var content = msg.TryGetProperty("content", out var c) ? c.GetString() : null;
            if (!string.IsNullOrWhiteSpace(content)) return content;
            // 推理模型（deepseek-reasoner / glm-4.7-flash 等）思考过程放 reasoning_content：
            // content 为空时兜底取用，避免整批被误判为「返回无法解析的内容」
            if (msg.TryGetProperty("reasoning_content", out var rc)) return rc.GetString();
            return content;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 从平台报错里解析其允许的 max_tokens 上限，如智谱返回
    /// <c>max_tokens参数非法：限制数值范围[1,16384]</c> → 16384。
    /// 目的是**自愈**：任何平台上限写错，最多浪费一次请求即可自动纠正。
    /// </summary>
    private static long ParseMaxTokensCap(string errorBody)
    {
        try
        {
            var m = Regex.Match(errorBody, @"\[\s*\d+\s*,\s*(\d{2,9})\s*\]");
            if (m.Success && long.TryParse(m.Groups[1].Value, out var cap) && cap > 0) return cap;
        }
        catch
        {
            /* 解析失败返回 0，走原错误路径 */
        }
        return 0;
    }

    private static string HostKey(string baseUrl)
    {
        try { return new Uri(baseUrl).Host; }
        catch { return baseUrl ?? ""; }
    }

    /// <summary> 实际使用的 max_tokens：取「按平台估算值」与「运行中学到的上限」中较小者。 </summary>
    private static long EffectiveMaxTokens(Configuration cfg, string baseUrl)
    {
        var want = MaxTokensForModel(cfg);
        if (_maxTokensLearned.TryGetValue(HostKey(baseUrl), out var cap) && cap > 0 && cap < want) return cap;
        return want;
    }

    /// <summary> 运行中学到的 max_tokens 上限（按端点 host 记）：被拒过一次后收敛到平台允许值。 </summary>
    private static readonly Dictionary<string, long> _maxTokensLearned = new(StringComparer.OrdinalIgnoreCase);

    private static JsonObject? ParseJson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        // 剥离 ```json 代码块
        var f = text.IndexOf("```");
        if (f >= 0)
        {
            var s = text.IndexOf('\n', f);
            var e = text.LastIndexOf("```");
            if (s >= 0 && e > s) text = text[(s + 1)..e].Trim();
        }
        try
        {
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (Exception)
        {
            // 尝试截取第一个 { 到最后一个 }
            var b = text.IndexOf('{');
            var en = text.LastIndexOf('}');
            if (b >= 0 && en > b)
            {
                try { return JsonNode.Parse(text[b..(en + 1)]) as JsonObject; }
                catch (Exception) { return null; }
            }
            return null;
        }
    }

    private static string Truncate(string s, int n)
        => s.Length <= n ? s : s[..n] + "…";
}
