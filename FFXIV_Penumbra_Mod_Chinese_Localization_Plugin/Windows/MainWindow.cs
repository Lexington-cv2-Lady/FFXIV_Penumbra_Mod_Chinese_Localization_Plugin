using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using FFXIVPenumbraHanhua.Services;

namespace FFXIVPenumbraHanhua.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly PenumbraService penumbra;
    private readonly DictionaryService dict;
    private readonly HanhuaService hanhua;

    private int _selected = -1;
    private bool _autoRefresh = true;
    private bool _showMarked;   // 勾选「已翻译」：只看有标记的模组；默认只显示未翻译
    private string _modSearch = ""; // 模组搜索（名称/目录，与「已翻译」筛选叠加）

    // 多选集合（批量翻译 / 批量备份）：存模组目录名（抗 Penumbra 列表增删导致的下标漂移）
    private readonly HashSet<string> _selectedSet = new(StringComparer.OrdinalIgnoreCase);
    // 列表鼠标框选（空白处拖动拉框多选）
    private readonly ListDragSelect _listDrag = new();
    // 框选期间锁定窗口位置：ImGui 默认把「在空白处按下拖动」当成移动窗口，需在框选时把它锁住
    private System.Numerics.Vector2? _dragWinLock;
    private System.Numerics.Vector2 _posWhileIdle;
    private System.Numerics.Vector2 _listRectMin;
    private System.Numerics.Vector2 _listRectMax;
    private const ImGuiWindowFlags BaseFlags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;

    // 左右分栏比例（分隔条可拖动）
    private float _split = 0.34f;
    private bool _draggingSplit;
    // 「已翻译」标记缓存：避免每帧对每个模组 File.Exists（2 秒 TTL 或显式失效）
    private readonly Dictionary<string, bool> _markCache = new();
    private DateTime _markCacheTime = DateTime.MinValue;
    // 断线自动重连节流：未连接时不再每帧发起 IPC 调用
    private DateTime _lastAutoRefresh = DateTime.MinValue;

    // 详情区
    private ModFileInfo? _selectedFile;
    private string _result = "";

    // 一键汉化（智能分流）：①提取 -> ②词典预填 -> ③AI翻译（无Key自动降级）-> ④汇总 -> ⑤写入本模组
    // 后台任务约定：Task 内对 _ocStatus/_result 等状态字段只做整串赋值（引用写入原子，无读改写），
    // UI 每帧轮询读取；后台不得对这些字段做 += 等复合操作，新增状态字段沿用整串赋值模式。
    private bool _ocSummary = true; // 提取方式：true=汇总提取（默认），false=按模组提取
    private Task? _ocTask;
    private CancellationTokenSource? _ocCts;
    private string _ocStatus = "";
    private bool _ocGuidePending; // 无 Key 完成第一段后弹指引窗
    private bool _ocStopRequested; // 已请求停止（避免重复点击反复改写状态文字）
    // 模组还原（HS API / 手动安装 PMP）
    private Task? _restoreTask;
    private string _restoreStatus = "";
    // 「重新下载」二次确认（覆盖模组文件的重操作）
    private bool _restoreArmed2;
    private DateTime _restoreArmedUntil2 = DateTime.MinValue;
    // 详情区「还原备份」二次确认（覆盖性操作）
    private bool _restoreBakArmed;
    private DateTime _restoreBakArmedUntil = DateTime.MinValue;

    // 详情区文件列表/英文快照缓存：Draw 每帧执行，按（目录 mtime, 内部 json 最大 mtime）判定失效，
    // 避免大模组（数十 group 文件）每帧全量解析 JSON。仅 UI 线程访问。
    private string? _detailFilesKey;
    private (DateTime Dir, DateTime Files) _detailFilesStamp;
    private List<ModFileInfo>? _detailFilesCache;
    private string? _snapKey;
    private DateTime _snapStamp;
    private ModFileInfo? _snapCache;

    /// <summary> 翻译管线「仅提取勾选」用：当前勾选的模组列表（保持 Penumbra 列表顺序）。 </summary>
    public IReadOnlyList<ModEntry> SelectedMods
    {
        get
        {
            return penumbra.Mods.Where(m => _selectedSet.Contains(m.Directory)).ToList();
        }
    }

    /// <summary> 当前可见（筛选后）模组是否已全部勾选（供流程窗口的「全选」显示状态）。 </summary>
    public bool AllVisibleSelected
    {
        get
        {
            var visible = BuildVisibleList();
            return visible.Count > 0 && visible.All(i => _selectedSet.Contains(penumbra.Mods[i].Directory));
        }
    }

    /// <summary> 勾选 / 取消勾选当前可见（筛选后）的全部模组（供流程窗口的「全选」调用）。 </summary>
    public void SetAllVisibleSelection(bool selected)
    {
        _selectedSet.Clear();
        if (!selected) return;
        foreach (var i in BuildVisibleList()) _selectedSet.Add(penumbra.Mods[i].Directory);
    }

    public MainWindow(Plugin plugin, PenumbraService penumbra, DictionaryService dict, HanhuaService hanhua)
        : base("模组汉化###HanhuaMain", BaseFlags)
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(720, 420),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

        this.plugin = plugin;
        this.penumbra = penumbra;
        this.dict = dict;
        this.hanhua = hanhua;
        this.penumbra.PenumbraDisposed += OnPenumbraDisposed;
        this.penumbra.ModsChanged += OnModsChanged;
    }

    private void OnPenumbraDisposed()
    {
        _selected = -1;
        _selectedFile = null;
        penumbra.Status = "Penumbra 已卸载，请重载插件后重试";
    }

    private void OnModsChanged()
    {
        _markCache.Clear(); // 勾选集存目录名，不受模组增删影响；仅详情聚焦下标需校正
        if (_selected >= penumbra.Mods.Count)
        {
            _selected = -1;
            _selectedFile = null;
            _result = "";
        }
    }

    public void Dispose()
    {
        penumbra.PenumbraDisposed -= OnPenumbraDisposed;
        penumbra.ModsChanged -= OnModsChanged;
    }

    public override void Draw()
    {
        // 一键汉化（无 Key）第一段完成 -> 打开指引弹窗（顶层作用域，详情区状态无关）
        if (_ocGuidePending && (_ocTask == null || _ocTask.IsCompleted))
        {
            ImGui.OpenPopup("一键汉化：下一步");
            _ocGuidePending = false;
        }

        // 顶部功能导航
        DrawNavBar();

        // 状态条
        DrawStatusBar();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // 双栏：左 = 模组列表（分隔条可拖动），右 = 详情；底部留固定高度给日志预览
        var avail = ImGui.GetContentRegionAvail();
        const float logH = 90f;
        var bodyH = Math.Max(100f, avail.Y - logH);
        var splitterW = 6f * ImGuiHelpers.GlobalScale;
        var listW = Math.Max(200f, avail.X * _split);
        var y0 = ImGui.GetCursorPosY();

        // 记录「空闲」时的窗口位置：起拖帧窗口尚未被 ImGui 位移，用它作为锁定基准
        if (!_listDrag.Armed && !_listDrag.Active)
            _posWhileIdle = ImGui.GetWindowPos();

        // 记录左侧列表子区域的屏幕矩形（仅框选该区域时锁定窗口，不影响拖标题栏移动窗口）
        _listRectMin = ImGui.GetCursorScreenPos();
        _listRectMax = _listRectMin + new Vector2(listW, bodyH);
        using (var left = ImRaii.Child("##ModList", new Vector2(listW, bodyH), true))
        {
            if (left.Success)
            {
                // 模组搜索框（名称/目录，与「已翻译」筛选叠加；全选等操作只作用于过滤后的可见列表）
                ImGui.SetNextItemWidth(-1f);
                ImGui.InputTextWithHint("##ModSearch", "搜索模组（名称 / 目录）…", ref _modSearch, 256);
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("按名称或目录过滤当前列表；清空恢复全量。全选/已选计数只作用于过滤后的列表");
                }
                ImGui.Spacing();
                var visible = BuildVisibleList();
                DrawModListHeader(visible);
                ImGui.Spacing();
                if (visible.Count == 0)
                {
                    Ui.Hint(_showMarked
                        ? "没有「已翻译」标记的模组（取消勾选查看未翻译）"
                        : "没有未翻译的模组（勾选「已翻译」查看已翻译）");
                }
                else
                {
                    // 列表独立滚动区：头部（已翻译筛选）固定置顶
                    using (var scroll = ImRaii.Child("##ModListScroll", new Vector2(-1, -1), false))
                    {
                        if (scroll.Success)
                        {
                            _listDrag.Begin();
                            var interactive = !_listDrag.Active; // 正在框选时屏蔽行点击，避免起拖行被误切换
                            for (var k = 0; k < visible.Count; k++)
                            {
                                var i = visible[k];
                                var mod = penumbra.Mods[i];
                                var isChecked = _selectedSet.Contains(mod.Directory);
                                var rowTop = ImGui.GetCursorScreenPos().Y;
                                // 紧凑行：小内边距 -> 勾选框更小、行更矮，窗口缩小时一屏可见更多
                                ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(3f, 2f) * ImGuiHelpers.GlobalScale);
                                if (ImGui.Checkbox($"##sel{i}", ref isChecked) && interactive)
                                {
                                    if (isChecked)
                                    {
                                        _selectedSet.Add(mod.Directory);
                                        _selected = i; // 勾选即聚焦详情
                                        _selectedFile = null;
                                        _result = "";
                                    }
                                    else
                                    {
                                        _selectedSet.Remove(mod.Directory);
                                        if (_selected == i)
                                        {
                                            _selected = -1; // 取消勾选 -> 退回一键汉化（伪）视图
                                            _selectedFile = null;
                                            _result = "";
                                        }
                                    }
                                }
                                ImGui.SameLine();
                                var name = mod.Name.Length > 0 ? mod.Name : mod.Directory;
                                if (ImGui.Selectable(Truncate(name, ImGui.GetContentRegionAvail().X) + $"##{i}", _selected == i) && interactive)
                                {
                                    _selected = i;
                                    _selectedFile = null;
                                    _result = "";
                                    // 点击模组名同时切换勾选（与备份管理一致）
                                    if (!_selectedSet.Add(mod.Directory)) _selectedSet.Remove(mod.Directory);
                                }
                                ImGui.PopStyleVar();
                                _listDrag.Row(i, rowTop, rowTop + ImGui.GetFrameHeight());
                                if (ImGui.IsItemHovered())
                                {
                                    ImGui.SetTooltip(mod.Directory + "\n点击即选中查看详情并切换勾选；按住左键拖动即框选多选");
                                }
                            }
                            // 框选命中 -> 勾选（只增不减）
                            foreach (var i in _listDrag.End()) _selectedSet.Add(penumbra.Mods[i].Directory);
                        }
                    }
                }
            }
        }

        // ── 框选时的窗口移动处理 ──
        // ImGui 默认把「在窗口空白处按下拖动」当作移动窗口，导致框选时窗口跟着跑。
        // 方案：① 鼠标在列表内（或已在框选）时，给窗口临时加 NoMove 标志，从源头阻止 ImGui 开始移动；
        //       ② 兜底：若框选中窗口仍被位移，用空闲时记下的位置强制回位。
        var mouseNow = ImGui.GetMousePos();
        var hoverList = mouseNow.X >= _listRectMin.X && mouseNow.X <= _listRectMax.X
                        && mouseNow.Y >= _listRectMin.Y && mouseNow.Y <= _listRectMax.Y;
        Flags = BaseFlags | ((hoverList || _listDrag.Armed || _listDrag.Active) ? ImGuiWindowFlags.NoMove : 0);

        if (_listDrag.Active)
        {
            _dragWinLock ??= _posWhileIdle;
            ImGui.SetWindowPos(_dragWinLock.Value);
        }
        else
        {
            _dragWinLock = null;
        }

        // 可拖动分隔条：InvisibleButton 消费点击（防止误拖窗口）+ 宽热区 + 手动坐标计算
        // 竖条 Y 直接取左栏 Child 绘制后的实际屏幕矩形，保证与左右栏上下完全对齐
        var hotW = 12f * ImGuiHelpers.GlobalScale;
        var draw = ImGui.GetWindowDrawList();
        var winPos = ImGui.GetWindowPos();
        var rmin = ImGui.GetWindowContentRegionMin();
        var barMin = ImGui.GetItemRectMin();
        var barMax = ImGui.GetItemRectMax();
        var barX = barMin.X + listW;
        var barY = barMin.Y;
        var barBottom = barMax.Y;
        var mouse = ImGui.GetMousePos();
        var io = ImGui.GetIO();

        ImGui.SetCursorPos(new Vector2(listW, y0));
        ImGui.InvisibleButton("##splitter", new Vector2(hotW, avail.Y));
        var hoverBar = ImGui.IsItemHovered();
        var activeBar = ImGui.IsItemActive();
        if (hoverBar || activeBar || _draggingSplit)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll);
            if (activeBar) _draggingSplit = true;
            if (_draggingSplit && io.MouseDown[0])
            {
                _split = Math.Clamp((mouse.X - (winPos.X + rmin.X)) / avail.X, 0.18f, 0.78f);
            }
            if (!io.MouseDown[0]) _draggingSplit = false;
        }
        draw.AddRectFilled(new Vector2(barX, barY), new Vector2(barX + splitterW, barBottom),
            ImGui.GetColorU32(new Vector4(0.7f, 0.7f, 0.7f, _draggingSplit || hoverBar ? 0.6f : 0.25f)));

        ImGui.SetCursorPos(new Vector2(listW + hotW, y0));
        using (var right = ImRaii.Child("##ModDetail", new Vector2(Math.Max(100f, avail.X - listW - hotW), bodyH), true))
        {
            if (!right.Success) return;
            DrawDetail();
        }

        // 底部日志预览（最近 6 条，自动滚动到底）
        ImGui.Separator();
        using (var logBox = ImRaii.Child("##MainLog", new Vector2(0, logH), true))
        {
            var entries = plugin.AppLog.Snapshot(); // 新->旧
            var show = entries.Take(6).Reverse().ToList(); // 旧->新显示
            foreach (var e in show)
            {
                var color = e.Lv switch
                {
                    AppLog.Level.Error => new Vector4(1f, 0.45f, 0.45f, 1f),
                    AppLog.Level.Warn => new Vector4(1f, 0.85f, 0.4f, 1f),
                    _ => new Vector4(0.75f, 0.85f, 0.95f, 1f)
                };
                ImGui.TextColored(color, $"[{e.Time:HH:mm:ss}] {e.Text}");
            }
            if (show.Count > 0)
            {
                ImGui.SetScrollHereY(1f);
            }
        }

        // 一键汉化（无 Key）指引弹窗
        var guideOpen = true;
        if (ImGui.BeginPopupModal("一键汉化：下一步", ref guideOpen, ImGuiWindowFlags.AlwaysAutoResize))
        {
            Ui.Hint("已按词典预填完成（未配置 API Key，本轮未调用 AI）。接下来三步：");
            ImGui.TextWrapped(
                "1. 点「打开翻译目录」，把其中的 _未翻译.json（连同 翻译规则.json）交给外部 AI；" + "\n" +
                "2. 翻好后改名为 _已翻译.json 放回翻译目录；" + "\n" +
                "3. 回到本窗口点「汇总并写入」完成写回。");
            if (ImGui.Button("打开翻译目录", new Vector2(150 * ImGuiHelpers.GlobalScale, 0)))
            {
                try
                {
                    var dir = plugin.Configuration.TranslationPath;
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
                }
                catch { }
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("知道了"))
            {
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }
    }

    /// <summary> 可见模组索引：默认只显示未翻译模组；勾选「已翻译」后只看有标记的模组。 </summary>
    private List<int> BuildVisibleList()
    {
        // 标记状态走缓存：每帧对每模组 File.Exists 在模组多时磁盘压力过大
        if ((DateTime.Now - _markCacheTime).TotalSeconds > 2)
        {
            _markCache.Clear();
            _markCacheTime = DateTime.Now;
        }
        var q = _modSearch.Trim();
        var list = new List<int>();
        for (var i = 0; i < penumbra.Mods.Count; i++)
        {
            var dir = penumbra.Mods[i].Directory;
            if (!_markCache.TryGetValue(dir, out var hasMark))
            {
                hasMark = plugin.Mark.HasMark(dir);
                _markCache[dir] = hasMark;
            }
            if (hasMark != _showMarked) continue;
            if (q.Length > 0
                && !penumbra.Mods[i].Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                && !dir.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            list.Add(i);
        }
        return list;
    }

    /// <summary> 模组列表标题行：标题自适应剩余宽度，「已翻译」筛选始终靠右缘（随分隔条同步移动）。 </summary>
    private void DrawModListHeader(IReadOnlyList<int> visible)
    {
        var total = penumbra.Mods.Count;
        var title = _showMarked
            ? $"模组列表（已翻译 {visible.Count}/{total}）"
            : $"模组列表（未翻译 {visible.Count}/{total}）";
        var availW = ImGui.GetContentRegionAvail().X;

        // 右侧「全选」+「已翻译」勾选框宽度估算（勾选框 ≈ 帧高，加文字和间距）
        var frameH = ImGui.GetFrameHeight();
        var checkAllW = frameH + ImGui.CalcTextSize("全选").X + 10f * ImGuiHelpers.GlobalScale;
        var checkMarkW = frameH + ImGui.CalcTextSize("已翻译").X + 10f * ImGuiHelpers.GlobalScale;
        var rightBlock = checkAllW + checkMarkW + 8f * ImGuiHelpers.GlobalScale;

        // 标题占用剩余宽度（超长截断）；勾选框靠右缘，随分隔条拖动同步移动
        ImGui.TextUnformatted(FitTitle(title, Math.Max(40f, availW - rightBlock)));
        ImGui.SameLine(Math.Max(40f, availW - rightBlock));
        var allSel = AllVisibleSelected;
        if (ImGui.Checkbox("全选", ref allSel))
        {
            SetAllVisibleSelection(allSel); // 第一次点=全选，再点=全部取消
            if (!allSel)
            {
                _selected = -1;
                _selectedFile = null;
                _result = "";
            }
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("全选/取消当前列表（随「已翻译」筛选）。全取消后详情区回到一键汉化（伪）");
        }
        ImGui.SameLine(Math.Max(40f, availW - checkMarkW));
        if (ImGui.Checkbox("已翻译", ref _showMarked))
        {
            // 切换筛选时清空勾选/选中，避免误操作被隐藏的模组
            _selectedSet.Clear();
            _selected = -1;
            _selectedFile = null;
            _result = "";
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("默认：只显示未翻译模组（隐藏已翻译）；勾选后：只显示有「已翻译」标记的模组");
        }
    }

    /// <summary> 标题按宽度截断（超出加省略号），保证行内按钮位置不随文字长度跳动。 </summary>
    private static string FitTitle(string t, float maxW)
    {
        if (ImGui.CalcTextSize(t).X <= maxW) return t;
        var s = "";
        for (var i = 0; i < t.Length; i++)
        {
            var c = t[i];
            if (ImGui.CalcTextSize(s + c + "…").X > maxW) break;
            s += c;
        }
        return s + "…";
    }

    /// <summary> 顶部功能导航：各功能独立窗口。AI 设置置顶——全自动流程第一步就是配 Key（用户要求）。 </summary>
    private void DrawNavBar()
    {
        if (ImGui.Button("AI 设置"))
        {
            plugin.ToggleAiSettingsUi();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("供应商 / API Key / 模型 / AI 配置列表（自定义服务商、清空预设配置）/ 测试连接");
        }
        Ui.SameLineIfFits(Ui.ButtonWidth("半自动汉化流程"));
        if (ImGui.Button("半自动汉化流程"))
        {
            plugin.TogglePipelineUi();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("① 提取英文 -> ② 预翻译 -> ③ AI 翻译 -> ④ 汇总已翻译内容 -> ⑤ 翻译写入MOD\n（⑤ 直写版即本页：勾选模组 -> 翻译并写入）");
        }
        Ui.SameLineIfFits(Ui.ButtonWidth("备份管理"));
        if (ImGui.Button("备份管理"))
        {
            plugin.ToggleBackupUi();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("创建 / 还原 / 删除备份");
        }
        Ui.SameLineIfFits(Ui.ButtonWidth("目录和词典管理"));
        if (ImGui.Button("目录和词典管理"))
        {
            plugin.ToggleDictionaryUi();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("词典目录 / 翻译目录 / 备份份数设置，以及词典加载状态与各来源词条统计");
        }
        Ui.SameLineIfFits(Ui.ButtonWidth("Wiki提取"));
        if (ImGui.Button("Wiki提取"))
        {
            plugin.ToggleWikiUi();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("从灰机 wiki 抓取官方中/英名，按分类写入 词典目录\\wiki_术语对照\\");
        }
        Ui.SameLineIfFits(Ui.ButtonWidth("日志"));
        if (ImGui.Button("日志"))
        {
            plugin.ToggleLogUi();
        }
        Ui.SameLineIfFits(Ui.ButtonWidth("开发功能"));
        if (ImGui.Button("开发功能"))
        {
            plugin.ToggleDevUi();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("实验性开关与调试按钮（新用户无需关注）");
        ImGui.Separator();
    }

    private void DrawStatusBar()
    {
        ImGui.TextWrapped(penumbra.Status);
        Ui.SameLineIfFits(ImGui.CalcTextSize("|").X);
        ImGui.TextColored(new Vector4(0.6f, 0.85f, 1f, 1f), "|");
        Ui.SameLineIfFits(ImGui.CalcTextSize(dict.Status).X);
        ImGui.TextWrapped(dict.Status);


        if (_autoRefresh && penumbra.Mods.Count == 0 && penumbra.Status.StartsWith("未连接", StringComparison.Ordinal)
            && (DateTime.Now - _lastAutoRefresh).TotalSeconds >= 3)
        {
            _lastAutoRefresh = DateTime.Now;
            penumbra.Refresh();
        }
    }

    private void DrawDetail()
    {
        // 详情区显示规则：勾选集中且点选了某模组 -> 显示该模组详情；否则显示一键汉化（伪）批量区
        var focusedDir = _selected >= 0 && _selected < penumbra.Mods.Count
            ? penumbra.Mods[_selected].Directory : null;
        if (focusedDir == null || !_selectedSet.Contains(focusedDir))
        {
            // 新用户引导：词典/翻译目录未设置时，详情区先引导配置，设置完后自动隐藏
            var cfg = plugin.Configuration;
            var dictMissing = string.IsNullOrWhiteSpace(cfg.DictionaryPath) || !Directory.Exists(cfg.DictionaryPath);
            var transMissing = string.IsNullOrWhiteSpace(cfg.TranslationPath) || !Directory.Exists(cfg.TranslationPath);
            if (dictMissing || transMissing)
            {
                ImGui.TextWrapped("欢迎使用模组汉化插件！开始前需要先设置两个目录：");
                ImGui.Spacing();
                if (dictMissing)
                {
                    Ui.ColoredWrapped(new Vector4(1f, 0.75f, 0.3f, 1f), "[!] 词典目录未设置（存放 我的翻译 / 个性翻译 / wiki 术语 / AI知识库）");
                }
                else
                {
                    Ui.ColoredWrapped(new Vector4(0.55f, 0.9f, 0.55f, 1f), "词典目录已设置");
                }
                if (transMissing)
                {
                    Ui.ColoredWrapped(new Vector4(1f, 0.75f, 0.3f, 1f), "[!] 翻译目录未设置（提取英文 / AI翻译 的输入输出目录）");
                }
                else
                {
                    Ui.ColoredWrapped(new Vector4(0.55f, 0.9f, 0.55f, 1f), "翻译目录已设置");
                }
                ImGui.Spacing();
                if (ImGui.Button("打开目录和词典管理，配置目录"))
                {
                    plugin.ToggleDictionaryUi();
                }
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("在「目录和词典管理」窗口中填写词典目录与翻译目录并点击「保存设置」");
                }
                ImGui.Spacing();
                Ui.Hint("目录设置完成后，本提示自动消失，可正常开始汉化。");
                return;
            }
            Ui.Hint("点击模组名称：勾选并查看/编辑详情；未勾选时可在下方一键汉化当前列表：");
            ImGui.Spacing();
            DrawOneClickAll();
            return;
        }

        var mod = penumbra.Mods[_selected];
        var modRoot2 = penumbra.GetModRoot();
        var modFullPath = Path.Combine(modRoot2 ?? "", mod.Directory);

        // 模组行：HS 模组=「重新下载」（Heliosphere 还原）；其它=「打开」；模组名可点击打开文件夹
        var openW = 56f * ImGuiHelpers.GlobalScale;
        var isHsMod = ModRestoreService.HasHsMeta(modFullPath);
        if (isHsMod && _restoreTask != null && !_restoreTask.IsCompleted)
        {
            ImGui.BeginDisabled();
            ButtonWithShadow("还原中…", new Vector2(openW + 24f * ImGuiHelpers.GlobalScale, 0), () => { }, null);
            ImGui.EndDisabled();
        }
        else if (isHsMod)
        {
            // 覆盖模组文件的重操作 -> 二次确认（首次点击变「确认」，3 秒内再点才执行）
            var rArmed = _restoreArmed2 && DateTime.Now < _restoreArmedUntil2;
            if (rArmed) Ui.PushDanger();
            ButtonWithShadow(rArmed ? "确认还原" : "重新下载", new Vector2(openW, 0),
                () =>
                {
                    if (!rArmed)
                    {
                        _restoreArmed2 = true;
                        _restoreArmedUntil2 = DateTime.Now.AddSeconds(3);
                        _result = "[!] 重新下载会覆盖本模组的选项文本（还原前自动备份），3 秒内再点一次确认";
                    }
                    else
                    {
                        _restoreArmed2 = false;
                        StartRestore(mod, modFullPath);
                    }
                },
                "从 Heliosphere 重新获取该模组的原始选项信息\n为防误点：首次点击只给确认提示，需 3 秒内再点一次");
            if (rArmed) Ui.PopDanger();
        }
        else
        {
            ButtonWithShadow("打开", new Vector2(openW, 0), () => OpenModFolder(modFullPath),
                "打开模组文件夹\n" + modFullPath);
        }
        ImGui.SameLine();
        ImGui.TextUnformatted("模组：");
        ImGui.SameLine();
        if (ImGui.Selectable(mod.Name + "##openModFolder"))
        {
            OpenModFolder(modFullPath);
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("点击打开模组文件夹\n" + modFullPath);
        }
        if (isHsMod)
        {
            // 提示（非按钮）：HS 模组这行是「重新下载」，说明打开文件夹要走点模组名。
            // 用灰色不用彩色——彩色会让它看起来像个按不动的按钮；措辞直接写明操作方式。
            ImGui.SameLine();
            Ui.Hint("（点模组名打开文件夹）");
        }
        if (_restoreTask != null && !_restoreTask.IsCompleted)
        {
            Ui.Hint(_restoreStatus);
        }
        else if (_restoreTask != null && _restoreTask.IsCompleted)
        {
            _result = _restoreStatus;
            _restoreTask = null;
            penumbra.Refresh();
            ReloadSelectedFile();
        }
        Ui.Hint($"目录：{mod.Directory}");
        ImGui.Spacing();

        // 文件列表（带缓存：每帧只做 mtime 校验，文件增删改自动失效）
        var modRoot = penumbra.GetModRoot();
        var files = new List<ModFileInfo>();
        if (!string.IsNullOrEmpty(modRoot))
        {
            files = ReadDetailFiles(System.IO.Path.Combine(modRoot, mod.Directory));
        }

        if (files.Count == 0)
        {
            // 有 meta.json 但无 Groups（纯文件替换模组，如动画/武器替换）与完全无选项文件，都走这里
            var metaPath = string.IsNullOrEmpty(modRoot)
                ? ""
                : System.IO.Path.Combine(modRoot, mod.Directory, "meta.json");
            if (metaPath.Length > 0 && File.Exists(metaPath))
            {
                ImGui.TextWrapped("该模组没有可汉化的选项：meta.json 中没有 Groups（属纯文件替换模组，如动画/武器替换），无需翻译。");
            }
            else
            {
                ImGui.TextWrapped("该模组没有可汉化的选项：未找到 meta.json / group_*.json。");
            }
            ImGui.Spacing();
            Ui.Hint("可创建「已翻译」标记，将其从主列表「未翻译」筛选中隐藏：");
            DrawMarkButton(mod); // 无选项模组同样允许手动标记
            ImGui.Spacing();
            Plugin.ResultBox("##MainResult", _result, "操作结果将显示在这里");
            return;
        }

        // files 每帧重建为新对象：按 Path 重新绑定当前选中文件，
        // 否则跨帧 ReferenceEquals 恒 false -> 选中高亮丢失、编辑区用旧对象
        _selectedFile = files.FirstOrDefault(x => x.Path == _selectedFile?.Path) ?? files[0];

        ImGui.TextUnformatted("文件（点击查看选项）:");
        ImGui.Spacing();
        // 文件多的模组（group 文件几十个）默认只显示前 3 条，折叠其余；「显示全部」走独立窗口
        var shownFiles = files.Count > 4 ? files.Take(3).ToList() : files;
        foreach (var f in shownFiles)
        {
            if (ImGui.Selectable($"{(f.IsMeta ? "[新] " : "")}{f.FileName}##file", ReferenceEquals(_selectedFile, f)))
            {
                _selectedFile = f;
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip($"文件：{f.FileName}\n完整路径：{f.Path}\n选项组：{f.Groups.Count} 个 / 选项：{CountOptions(f)} 项");
            }
        }
        if (files.Count > 4)
        {
            Ui.Hint($"… 其余 {files.Count - 3} 个文件已折叠");
            if (ImGui.Button($"显示全部 {files.Count} 个文件（独立窗口）"))
            {
                plugin.ToggleFileListUi();
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("在独立窗口查看本模组全部文件与选项树（只读），可跳回主窗口编辑任意文件");
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // 选项编辑入口：表单迁到独立二级窗口，主详情区只放一个按钮
        var file = _selectedFile!;
        ImGui.TextWrapped($"当前文件：{file.FileName}　（{file.Groups.Count} 组 / {CountOptions(file)} 项）");
        if (ImGui.Button("选项编辑…"))
        {
            plugin.ToggleOptionEditUi();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("在独立窗口打开：直接改中英文组名/选项，保存即写回模组并触发游戏重载");
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // 翻译按钮
        if (ImGui.Button("词典翻译写入（覆写旧译法）"))
        {
            _result = "";
            var changed = hanhua.TranslateMod(mod.Directory, mod.Name);
            _result = hanhua.LastResult;
            penumbra.Refresh();
            ReloadSelectedFile(); // 刷新详情区与编辑缓冲：旧英文缓冲若被「保存修改」写回会覆盖刚翻译的中文
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip($"词典翻译写入（离线·覆写旧译法）：{mod.Name}\n（先自动备份，再写回 meta.json / group_*.json，随后触发 Penumbra 重载）");
        }

        // 还原备份（本地 zip；与「重新下载」不同：不联网，直接用最近一次写回前的备份覆盖当前选项文本）
        ImGui.SameLine();
        var bakRoot = penumbra.GetModRoot();
        var latestBak = string.IsNullOrEmpty(bakRoot)
            ? null
            : plugin.Backup.ListBackups(bakRoot).FirstOrDefault(b => b.ModDir == mod.Directory);
        var bakArmed = _restoreBakArmed && DateTime.Now < _restoreBakArmedUntil;
        if (latestBak == null)
        {
            ImGui.BeginDisabled();
            ImGui.Button("还原备份");
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("本模组暂无备份（备份在首次写回/保存修改时自动创建）");
        }
        else
        {
            if (bakArmed) Ui.PushDanger();
            if (ImGui.Button(bakArmed ? "确认还原" : "还原备份"))
            {
                if (!bakArmed)
                {
                    _restoreBakArmed = true;
                    _restoreBakArmedUntil = DateTime.Now.AddSeconds(3);
                    _result = $"[!] 将用最新备份「{latestBak.FileName}」还原本模组（覆盖当前选项文本，保留已勾选状态），3 秒内再点一次确认";
                }
                else
                {
                    _restoreBakArmed = false;
                    var ok = plugin.Backup.Restore(bakRoot!, latestBak);
                    _result = ok ? plugin.Backup.LastResult : "还原失败，详见日志";
                    penumbra.Refresh();
                    ReloadSelectedFile();
                }
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip($"用本地最新备份还原本模组：{latestBak.FileName}\n" +
                                 "回到备份时的内容（通常为英文），自动清除「已翻译」标记并重载游戏；备份本身移至回收站。\n" +
                                 "为防误点：首次点击只提示，3 秒内再点一次才执行。");
            }
            if (bakArmed) Ui.PopDanger();
        }

        // ── 一键汉化（单模组版：智能分流，有 Key 全自动；无 Key 停在词典预填，等外部 AI）──
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.BeginGroup();
        ImGui.TextWrapped("一键汉化（提取 -> 词典预填 -> AI翻译 -> 汇总 -> 写入本模组）");
        if (ImGui.RadioButton("汇总提取（默认）", _ocSummary)) _ocSummary = true;
        Ui.SameLineIfFits(Ui.ButtonWidth("按模组提取") + ImGui.GetFrameHeight());
        if (ImGui.RadioButton("按模组提取", !_ocSummary)) _ocSummary = false;
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("汇总提取：本模组条目合并进 全部模组_未翻译.json\n按模组提取：单独生成 <模组名>_未翻译.json\n两种写回效果相同，只影响文件组织方式");
        }

        if (_ocTask != null && !_ocTask.IsCompleted)
        {
            // 翻译中：状态 + 红色「停止」按钮（中断类操作统一红色，一眼可辨）
            ImGui.TextWrapped(_ocStatus);
            ImGui.Spacing();
            Ui.PushDanger();
            if (ImGui.Button("停止翻译", new Vector2(120f * ImGuiHelpers.GlobalScale, 0)) && !_ocStopRequested)
            {
                _ocStopRequested = true;
                _ocCts?.Cancel();
                _ocStatus = "正在停止…（已中断当前请求；已翻完的部分会保留并写盘）";
            }
            Ui.PopDanger();
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("停止：不再发送新批次，正在请求中的那批也会被立即中断。\n" +
                                 "[!] 已翻完的批次会保留并写盘（那部分额度已消耗，不浪费）。");
            }
            if (_ocStopRequested)
            {
                ImGui.SameLine();
                Ui.Hint("已请求停止，等待当前批次收尾…");
            }
        }
        else
        {
            Ui.PushAccent();
            var aiClicked = ImGui.Button("AI 一键汉化（提取->AI->汇总->写入）");
            Ui.PopAccent();
            if (aiClicked)
            {
                StartOneClick(new List<ModEntry> { mod });
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("自动完成：提取本模组英文 -> 词典预填 -> AI 翻译（已配 Key 时）-> 汇总进词典 -> 写回本模组并重载。\n未配置 Key 时自动停在词典预填，把生成的 _未翻译.json 交给外部 AI 即可。");
            }
            Ui.SameLineIfFits(Ui.ButtonWidth("复制翻译提示词"));
            if (ImGui.Button("复制翻译提示词"))
            {
                CopyAiPrompt();
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("把「格式要求 + 全部待翻译内容」整段复制到剪贴板，直接粘给任意 AI 对话框；\n" +
                                 "翻好后复制 AI 回复，回来点「从剪贴板导入译文」。");
            }
            Ui.SameLineIfFits(Ui.ButtonWidth("从剪贴板导入译文"));
            if (ImGui.Button("从剪贴板导入译文"))
            {
                ImportFromClipboard();
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("把 AI 回复的 JSON 复制后点这里：解析并写成 _已翻译.json，随后点「汇总并写入」完成写回。");
            }
            Ui.SameLineIfFits(Ui.ButtonWidth("汇总并写入"));
            if (ImGui.Button("汇总并写入"))
            {
                SumupAndWrite(new List<ModEntry> { mod });
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("外部 AI 翻完后点这个：把翻译目录里的 _已翻译.json 汇总进词典，再写回本模组并重载。");
            }
            // 轮询任务完成：清任务状态 + UI 线程收尾
            if (_ocTask != null && _ocTask.IsCompleted)
            {
                _ocTask = null;
                _ocCts?.Dispose();
                _ocCts = null;
                penumbra.Refresh();
                ReloadSelectedFile();
            }
        }
        ImGui.EndGroup();
        FrameLastGroup();

        // 已翻译标记 + 查漏补缺
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawMarkButton(mod);
        Ui.SameLineIfFits(Ui.ButtonWidth("查漏补缺"));
        if (ImGui.Button("查漏补缺"))
        {
            _result = CheckGaps(files);
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("扫描当前模组未翻译的选项/描述（不受「已翻译」标记影响）");
        }

        // 详情区操作结果：带边框统一风格
        ImGui.Spacing();
        Plugin.ResultBox("##MainResult", _result, "操作结果将显示在这里（如：已保存 N 项修改…）");
    }

    /// <summary>
    /// 一键汉化（智能分流）：① 提取（默认汇总提取，可选按模组）-> ② 词典预填 ->
    /// 有 Key：③ AI 翻译 -> ④ 汇总 -> ⑤ 写回；无 Key：停在 ②，引导走外部 AI 后用「汇总并写入」。
    /// mods 可以是单个模组（详情区）也可以是当前列表全部（未选中时的“一键汉化（伪）”）。
    /// </summary>
    private void StartOneClick(List<ModEntry> mods)
    {
        if (mods.Count == 0)
        {
            _result = "当前列表没有模组";
            return;
        }
        var modRoot = penumbra.GetModRoot();
        if (string.IsNullOrEmpty(modRoot))
        {
            _result = "无法获取 Penumbra 模组根目录";
            return;
        }
        var cfg = plugin.Configuration;
        if (string.IsNullOrWhiteSpace(cfg.DictionaryPath) || !Directory.Exists(cfg.DictionaryPath))
        {
            _result = "请先在「目录和词典管理」设置词典目录";
            return;
        }
        var transDir = cfg.TranslationPath;
        try
        {
            if (!Directory.Exists(transDir)) Directory.CreateDirectory(transDir);
        }
        catch (Exception ex)
        {
            _result = "翻译目录不可用：" + ex.Message;
            return;
        }

        var hasKey = !string.IsNullOrWhiteSpace(AiTranslateService.GetApiKey(cfg));
        var summary = _ocSummary;
        _result = "";
        _ocStopRequested = false; // 新一轮任务：复位停止标记
        _ocStatus = $"① 提取英文（{mods.Count} 个模组）…";
        _ocCts = new CancellationTokenSource();
        var ct = _ocCts.Token;

        _ocTask = Task.Run(() =>
        {
            var log = new StringBuilder();
            try
            {
                // ① 提取（用户显式点按钮，不受「已翻译」标记影响）
                var n = summary
                    ? plugin.Extract.Extract(mods, skipMarked: false, transDir, modRoot)
                    : plugin.Extract.ExtractPerMod(mods, skipMarked: false, transDir, modRoot);
                if (n < 0)
                {
                    _ocStatus = "① 提取失败：" + plugin.Extract.LastResult;
                    _result = _ocStatus;
                    return;
                }
                var outputs = plugin.Extract.LastOutputPaths.ToList();
                log.Append(plugin.Extract.LastResult);
                _ocStatus = $"① 提取完成（{n} 项）-> ② 词典预填…";

                // ② 词典预填（能翻的先翻上，交给 AI 的就少了）
                var hit = 0;
                foreach (var f in outputs) hit += Math.Max(0, plugin.Extract.PrefillFile(f));
                log.Append($"；词典预填 {hit} 项");

                if (!hasKey)
                {
                    _ocStatus = "未配置 API Key：已按词典预填完成 ";
                    _ocGuidePending = true;
                    _result = log +
                              $"\n把翻译目录里的 {Path.GetFileName(outputs[0])} 等文件交给外部 AI（连同 翻译规则.json），" +
                              "翻好后改名为 _已翻译.json 放回翻译目录，再点「汇总并写入」。";
                    return;
                }

                // ③ AI 翻译
                foreach (var input in outputs)
                {
                    if (ct.IsCancellationRequested) break;
                    _ocStatus = $"③ AI 翻译：{Path.GetFileName(input)}…";
                    var output = input.Replace("_未翻译.json", "_已翻译.json");
                    plugin.AiTranslate.TranslateAsync(input, output, cfg, ct).GetAwaiter().GetResult();
                    log.Append('\n').Append(plugin.AiTranslate.LastResult);
                }
                if (ct.IsCancellationRequested)
                {
                    _ocStatus = "已取消（已翻译部分写盘保留）";
                    _result = log + "\n稍后可点「汇总并写入」继续。";
                    return;
                }

                // ④ 汇总 + 重载词典 -> ⑤ 写回
                _ocStatus = "④ 汇总已翻译内容…";
                SumupCore(transDir, log);
                _ocStatus = "⑤ 翻译写入MOD…";
                plugin.Import.ApplyDictionary(modRoot, plugin.Dict, mods);
                log.Append('\n').Append(plugin.Import.LastResult);
                _ocStatus = "完成 ";
                _result = log.ToString();
            }
            catch (Exception ex)
            {
                _ocStatus = "出错：" + ex.Message;
                _result = "一键汉化出错：" + ex.Message;
            }
        });
    }

    /// <summary>
    /// 全自动汉化入口（启动 / 新模组触发，静默执行）：取所有未翻译模组跑完整五步闭环。
    /// 前置检查：已有任务在跑跳过；未配 Key / 目录静默跳过；主窗口未打开也照常跑（结果留状态栏+日志）。
    /// </summary>
    public void StartAutoHanhua()
    {
        try
        {
            if (_ocTask != null && !_ocTask.IsCompleted)
            {
                plugin.AppLog.Info("[全自动] 已有汉化任务在跑，本轮自动触发跳过");
                return;
            }
            var cfg = plugin.Configuration;
            if (string.IsNullOrWhiteSpace(cfg.DictionaryPath) || !Directory.Exists(cfg.DictionaryPath))
            {
                plugin.AppLog.Info("[全自动] 词典目录未配置，跳过自动触发");
                return;
            }
            if (string.IsNullOrWhiteSpace(cfg.TranslationPath))
            {
                plugin.AppLog.Info("[全自动] 翻译目录未配置，跳过自动触发");
                return;
            }
            if (string.IsNullOrWhiteSpace(AiTranslateService.GetApiKey(cfg)))
            {
                plugin.AppLog.Info("[全自动] 未配置 API Key，跳过自动触发");
                return;
            }
            // 全部未翻译模组（无「已翻译」标记）
            var mods = penumbra.Mods.Where(m => !plugin.Mark.HasMark(m.Directory)).ToList();
            if (mods.Count == 0)
            {
                plugin.AppLog.Info("[全自动] 没有未翻译模组，无需处理");
                return;
            }
            plugin.AppLog.Info($"[全自动] 开始：{mods.Count} 个未翻译模组（提取->预填->AI->汇总->写回）");
            _ocSummary = true; // 全自动默认汇总提取（同原文跨模组天然去重友好）
            StartOneClick(mods);
        }
        catch (Exception ex)
        {
            plugin.AppLog.Error($"[全自动] 触发失败：{ex.Message}");
        }
    }

    /// <summary> 外部 AI 流程收尾：把翻译目录里的 _已翻译.json 汇总进词典，再写回指定模组并重载。 </summary>
    private void SumupAndWrite(List<ModEntry> mods)
    {
        if (mods.Count == 0)
        {
            _result = "当前列表没有模组";
            return;
        }
        var modRoot = penumbra.GetModRoot();
        if (string.IsNullOrEmpty(modRoot))
        {
            _result = "无法获取 Penumbra 模组根目录";
            return;
        }
        var transDir = plugin.Configuration.TranslationPath;
        var log = new StringBuilder();
        if (!SumupCore(transDir, log))
        {
            _result = "未找到 _已翻译.json：请先把外部 AI 翻好的文件改名为 <名称>_已翻译.json 放回翻译目录。";
            return;
        }
        plugin.Import.ApplyDictionary(modRoot, plugin.Dict, mods);
        log.Append('\n').Append(plugin.Import.LastResult);
        penumbra.Refresh();
        ReloadSelectedFile();
        _result = log.ToString();
    }

    /// <summary> ④ 汇总翻译目录下所有 _已翻译.json 进词典（有新增才重载词典）。返回是否找到并处理了文件。 </summary>
    private bool SumupCore(string transDir, StringBuilder log)
    {
        var files = Directory.Exists(transDir)
            ? Directory.GetFiles(transDir, "*_已翻译.json", SearchOption.TopDirectoryOnly).ToList()
            : new List<string>();
        if (files.Count == 0) return false;
        var added = 0;
        foreach (var f in files) added += Math.Max(0, plugin.Sumup.Sumup(f, plugin.Configuration.DictionaryPath));
        log.Append($"④ 汇总：{files.Count} 个文件，新增 {added} 条");
        if (added > 0) plugin.ReloadDictionary();
        return true;
    }

    /// <summary>
    /// 未选中模组时的「一键汉化（伪）」：对当前列表（默认即全部未翻译模组）执行
    /// ①提取 -> ②词典预填 -> ③AI翻译（无Key降级）-> ④汇总 -> ⑤写入。
    /// 称“伪”是因为走外部 AI 时中途需要人工把文件送去翻译再放回。
    /// </summary>
    private void DrawOneClickAll()
    {
        ImGui.BeginGroup();
        // 全自动开关（默认关；需已配 API Key，运行静默，进度在下方状态栏）
        var cfg = plugin.Configuration;
        var onStart = cfg.AutoHanhuaOnStart;
        if (ImGui.Checkbox("启动后自动汉化", ref onStart) && onStart != cfg.AutoHanhuaOnStart)
        {
            cfg.AutoHanhuaOnStart = onStart;
            cfg.Save();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("插件启动约 10 秒后自动扫一遍未翻译模组并跑完整流程（提取->词典预填->AI->汇总->写回）。\n" +
                             "需已在「AI 设置」配好 Key；未配 Key 时静默跳过，不打扰。");
        }
        Ui.SameLineIfFits(Ui.ButtonWidth("新模组自动汉化") + ImGui.GetFrameHeight());
        var onNewMod = cfg.AutoHanhuaOnNewMod;
        if (ImGui.Checkbox("新模组自动汉化", ref onNewMod) && onNewMod != cfg.AutoHanhuaOnNewMod)
        {
            cfg.AutoHanhuaOnNewMod = onNewMod;
            cfg.Save();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Penumbra 里新模组加入后自动跑完整汉化流程。\n需已配 Key；运行中已有任务在跑时自动排队跳过本轮。");
        }

        ImGui.Spacing();
        if (ImGui.RadioButton("汇总提取（默认）", _ocSummary)) _ocSummary = true;
        Ui.SameLineIfFits(Ui.ButtonWidth("按模组提取") + ImGui.GetFrameHeight());
        if (ImGui.RadioButton("按模组提取", !_ocSummary)) _ocSummary = false;
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("汇总提取：全部条目合并进 全部模组_未翻译.json\n按模组提取：每个模组单独生成 <模组名>_未翻译.json\n两种写回效果相同，只影响文件组织方式");
        }

        if (_ocTask != null && !_ocTask.IsCompleted)
        {
            // 翻译中：状态 + 红色「停止」按钮（中断类操作统一红色，一眼可辨）
            ImGui.TextWrapped(_ocStatus);
            ImGui.Spacing();
            Ui.PushDanger();
            if (ImGui.Button("停止翻译", new Vector2(120f * ImGuiHelpers.GlobalScale, 0)) && !_ocStopRequested)
            {
                _ocStopRequested = true;
                _ocCts?.Cancel();
                _ocStatus = "正在停止…（已中断当前请求；已翻完的部分会保留并写盘）";
            }
            Ui.PopDanger();
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("停止：不再发送新批次，正在请求中的那批也会被立即中断。\n" +
                                 "[!] 已翻完的批次会保留并写盘（那部分额度已消耗，不浪费）。");
            }
            if (_ocStopRequested)
            {
                ImGui.SameLine();
                Ui.Hint("已请求停止，等待当前批次收尾…");
            }
        }
        else
        {
            Ui.PushAccent();
            var fakeClicked = ImGui.Button("一键汉化（伪）");
            Ui.PopAccent();
            if (fakeClicked)
            {
                StartOneClick(BuildVisibleList().Select(i => penumbra.Mods[i]).ToList());
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("对当前列表（默认即全部未翻译模组）自动完成：提取 -> 词典预填 -> AI 翻译（已配 Key 时）-> 汇总 -> 写回并重载。\n" +
                                 "称「伪」：未配 Key 时会停在词典预填，需要人工把 _未翻译.json 交给外部 AI、翻好放回后点「汇总并写入」。");
            }
            Ui.SameLineIfFits(Ui.ButtonWidth("复制翻译提示词"));
            if (ImGui.Button("复制翻译提示词"))
            {
                CopyAiPrompt();
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("把「格式要求 + 全部待翻译内容」整段复制到剪贴板，直接粘给任意 AI 对话框；\n" +
                                 "翻好后复制 AI 回复，回来点「从剪贴板导入译文」。");
            }
            Ui.SameLineIfFits(Ui.ButtonWidth("从剪贴板导入译文"));
            if (ImGui.Button("从剪贴板导入译文"))
            {
                ImportFromClipboard();
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("把 AI 回复的 JSON 复制后点这里：解析并写成 _已翻译.json，随后点「汇总并写入」完成写回。");
            }
            Ui.SameLineIfFits(Ui.ButtonWidth("汇总并写入"));
            if (ImGui.Button("汇总并写入"))
            {
                SumupAndWrite(BuildVisibleList().Select(i => penumbra.Mods[i]).ToList());
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("外部 AI 翻完后点这个：把翻译目录里的 _已翻译.json 汇总进词典，再写回当前列表全部模组并重载。");
            }
            // 轮询任务完成：清任务状态 + UI 线程收尾
            if (_ocTask != null && _ocTask.IsCompleted)
            {
                _ocTask = null;
                _ocCts?.Dispose();
                _ocCts = null;
                penumbra.Refresh();
                ReloadSelectedFile();
            }
        }
        ImGui.EndGroup();
        FrameLastGroup();
    }

    /// <summary>
    /// 复制翻译提示词：把「格式要求 + 待翻译 JSON 全文」整段放进剪贴板，
    /// 直接粘给任意 AI 对话框（含支持知识库的对话式 AI：智谱清言 / 豆包 等），比上传文件更省事。
    /// </summary>
    private void CopyAiPrompt()
    {
        var transDir = plugin.Configuration.TranslationPath;
        if (string.IsNullOrWhiteSpace(transDir) || !Directory.Exists(transDir))
        {
            _result = "翻译目录未设置：请先在「目录和词典管理」配置";
            return;
        }
        var files = Directory.GetFiles(transDir, "*_未翻译.json", SearchOption.TopDirectoryOnly).ToList();
        if (files.Count == 0)
        {
            _result = "未找到 _未翻译.json：请先点「一键汉化（伪）」或到「半自动汉化流程」① 提取英文";
            return;
        }
        try
        {
            var prompt = plugin.Extract.BuildExternalPrompt(files);
            ImGui.SetClipboardText(prompt);
            var lines = prompt.Count(c => c == '\n');
            _result = $"已复制翻译提示词（含 {files.Count} 个文件的待翻译内容）到剪贴板\n" +
                      "把它整段粘给 AI 对话框，翻好后复制 AI 回复，回来点「从剪贴板导入译文」。";
        }
        catch (Exception ex)
        {
            _result = "生成提示词失败：" + ex.Message;
        }
    }

    /// <summary>
    /// 从剪贴板导入译文：解析 AI 回复的 JSON（容忍 ``` 包裹）-> 写成 _已翻译.json，
    /// 随后点「汇总并写入」即可完成写回。闭合「复制提示词 -> 对话式 AI -> 导入」的外链路线。
    /// </summary>
    private void ImportFromClipboard()
    {
        var transDir = plugin.Configuration.TranslationPath;
        if (string.IsNullOrWhiteSpace(transDir) || !Directory.Exists(transDir))
        {
            _result = "翻译目录未设置：请先在「目录和词典管理」配置";
            return;
        }
        var text = ImGui.GetClipboardText();
        if (string.IsNullOrWhiteSpace(text))
        {
            _result = "剪贴板为空";
            return;
        }
        var root = ParseJsonLenient(text);
        if (root == null)
        {
            _result = "剪贴板内容不是合法 JSON：请复制 AI 回复的完整 JSON（以 { 开头、} 结尾）";
            return;
        }
        if (root["_options"] is not JsonObject && root["_descriptions"] is not JsonObject)
        {
            _result = "JSON 里没有 _options / _descriptions：请让 AI 按提示词的输出结构重发";
            return;
        }
        var count = 0;
        foreach (var sec in new[] { "_options", "_descriptions" })
        {
            if (root[sec] is not JsonObject obj) continue;
            foreach (var kv in obj)
            {
                var v = kv.Value?.ToString() ?? "";
                if (v.Length > 0) count++;
            }
        }
        if (count == 0)
        {
            _result = "JSON 里的译文全是空的：AI 可能只回显了原文，请重新让它翻译";
            return;
        }

        // 与已有 _未翻译.json 配对命名（单个时同名替换，多个时用通用名）
        var untranslated = Directory.GetFiles(transDir, "*_未翻译.json", SearchOption.TopDirectoryOnly).ToList();
        var outName = untranslated.Count == 1
            ? Path.GetFileName(untranslated[0]).Replace("_未翻译.json", "_已翻译.json")
            : "剪贴板导入_已翻译.json";
        var outPath = Path.Combine(transDir, outName);
        try
        {
            // 保留翻译规则段，便于后续复用与追溯
            if (root["翻译规则"] == null)
                root["翻译规则"] = ExtractService.BuildTranslationRules(plugin.Configuration.DictionaryPath);
            File.WriteAllText(outPath, root.ToJsonString(JsonFile.Indented), Encoding.UTF8);
            _result = $"已从剪贴板导入 {count} 项译文 -> {outName}\n接着点「汇总并写入」即可写回模组。";
        }
        catch (Exception ex)
        {
            _result = "导入失败：" + ex.Message;
        }
    }

    /// <summary> 宽松 JSON 解析：容忍 ```json 代码块与前后夹带的说明文字。 </summary>
    private static JsonObject? ParseJsonLenient(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        var fence = text.IndexOf("```");
        if (fence >= 0)
        {
            var start = text.IndexOf('\n', fence);
            var end = text.LastIndexOf("```");
            if (start >= 0 && end > start) text = text[(start + 1)..end].Trim();
        }
        try
        {
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (Exception)
        {
            // 截取首个 { 到末尾最后一个 }（AI 前后夹了说明文字时）
            var b = text.IndexOf('{');
            var e = text.LastIndexOf('}');
            if (b >= 0 && e > b)
            {
                try { return JsonNode.Parse(text[b..(e + 1)]) as JsonObject; }
                catch (Exception) { return null; }
            }
            return null;
        }
    }

    /// <summary> 给刚用 BeginGroup/EndGroup 画好的内容区域外围加一个圆角框（自适应内容大小）。 </summary>
    private static void FrameLastGroup()
    {
        var mn = ImGui.GetItemRectMin();
        var mx = ImGui.GetItemRectMax();
        ImGui.GetWindowDrawList().AddRect(mn - new Vector2(7f, 6f), mx + new Vector2(7f, 6f),
            ImGui.GetColorU32(new Vector4(0.42f, 0.72f, 1f, 0.5f)), 8f);
    }

    /// <summary> 「创建 / 删除已翻译标记」按钮（带缓存失效）。有选项与无选项模组共用。 </summary>
    private void DrawMarkButton(ModEntry mod)
    {
        var mark = plugin.Mark;
        var marked = mark.HasMark(mod.Directory);
        if (ImGui.Button(marked ? "删除「已翻译」标记" : "创建「已翻译」标记"))
        {
            if (marked)
            {
                _result = mark.Remove(mod.Directory)
                    ? "已删除标记，提取英文时将重新处理该模组"
                    : "删除标记失败：无法写入模组目录";
            }
            else
            {
                _result = mark.Create(mod.Directory)
                    ? "已创建标记，提取英文时将自动跳过该模组"
                    : "创建标记失败：无法写入模组目录（请确认模组目录存在且可写）";
            }
            _markCache.Remove(mod.Directory); // 立即失效标记缓存，列表筛选即时更新
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("模组目录创建无后缀「已翻译」文件：提取英文/翻译时自动跳过；查漏补缺不受影响；备份还原时自动删除");
        }
    }

    /// <summary>
    /// 重新下载（还原）：HS 模组走 Heliosphere API 取英文选项树；
    /// 手动安装模组从 手动安装 目录匹配原始 PMP 还原自带文本。
    /// 备份在查询/匹配成功后、写文件前进行（查询失败不建备份，避免重复产生备份包）。
    /// </summary>
    private void StartRestore(ModEntry mod, string modFullPath)
    {
        _result = "";
        _restoreStatus = "正在获取原始选项…";
        var isHs = ModRestoreService.HasHsMeta(modFullPath);
        var modRoot = penumbra.GetModRoot() ?? "";
        var manualDir = Path.Combine(Path.GetDirectoryName(modRoot) ?? "", "手动安装");
        _restoreTask = Task.Run(async () =>
        {
            try
            {
                _restoreStatus = isHs ? "正在从 Heliosphere 获取原始选项…" : "正在从原始 PMP 还原…";
                string msg;
                if (isHs)
                    msg = await plugin.ModRestore.RestoreFromHeliosphereAsync(modFullPath, BackupNow);
                else
                {
                    var pmp = ModRestoreService.FindOriginalPmp(Path.GetFileName(modFullPath), mod.Name, manualDir);
                    if (pmp == null)
                    {
                        _restoreStatus = "未在 手动安装 目录找到匹配的原始 PMP";
                        _result = _restoreStatus;
                        return;
                    }
                    msg = plugin.ModRestore.RestoreFromPmp(modFullPath, pmp, BackupNow);
                }
                plugin.Penumbra.Reload(mod.Directory, mod.Name);
                plugin.AppLog.Info($"[重新下载] {mod.Name}：{msg}");
                _restoreStatus = msg + " ";
                _result = msg;
            }
            catch (Exception ex)
            {
                plugin.AppLog.Error($"[重新下载] {mod.Name}：还原失败：{ex.Message}");
                _restoreStatus = "还原失败：" + ex.Message;
                _result = _restoreStatus;
            }
        });

        // 写文件前回调：备份当前状态；失败抛异常中止还原（此时尚未改动任何文件）
        void BackupNow()
        {
            _restoreStatus = "正在备份当前状态…";
            var zip = plugin.Backup.CreateModZip(modFullPath, plugin.Configuration.BackupCount);
            if (zip == null) throw new InvalidOperationException("还原前备份失败，未改动任何文件");
        }
    }

    /// <summary> 查漏补缺：列出模组文件中仍为英文的选项/组名/描述。 </summary>
    private string CheckGaps(List<ModFileInfo> files)
    {
        var gaps = new List<string>();
        foreach (var f in files)
        {
            foreach (var g in f.Groups)
            {
                if (g.Name.Length > 0 && !dict.ContainsChinese(g.Name)) gaps.Add(g.Name);
                foreach (var o in g.Options)
                {
                    if (o.Name.Length > 0 && !dict.ContainsChinese(o.Name)) gaps.Add(o.Name);
                    if (!string.IsNullOrWhiteSpace(o.Description) && !dict.ContainsChinese(o.Description)) gaps.Add(o.Description);
                }
            }
        }
        if (gaps.Count == 0) return "查漏补缺：未发现未翻译条目（全部已中文）";
        var shown = gaps.Distinct().Take(20).ToList();
        return $"查漏补缺：发现 {gaps.Count} 条未翻译\n" + string.Join("\n", shown) +
               (gaps.Count > 20 ? $"\n… 其余 {gaps.Count - 20} 条" : "");
    }

    /// <summary> 带投影阴影的按钮：先画右下偏移阴影，再画按钮，增加层次感（消除扁平突兀感）。 </summary>
    private static void ButtonWithShadow(string label, Vector2 size, Action onClick, string? tooltip = null)
    {
        var draw = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        var rounding = ImGui.GetStyle().FrameRounding;
        var shadowColor = ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.38f));
        // 阴影：右下偏移 2px，圆角与按钮一致
        draw.AddRectFilled(new Vector2(pos.X + 2f, pos.Y + 3f),
            new Vector2(pos.X + size.X + 2f, pos.Y + size.Y + 3f),
            shadowColor, rounding);
        draw.AddRectFilled(new Vector2(pos.X + 1.5f, pos.Y + 2.5f),
            new Vector2(pos.X + size.X + 1.5f, pos.Y + size.Y + 2.5f),
            ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.18f)), rounding);

        if (ImGui.Button(label, size))
        {
            onClick();
        }
        if (tooltip != null && ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(tooltip);
        }
    }

    /// <summary> 超长文本截断省略号（防止列表项溢出窗口边界）。 </summary>
    private static string Truncate(string text, float maxWidth)
    {
        if (maxWidth <= 10f || ImGui.CalcTextSize(text).X <= maxWidth) return text;
        var result = text;
        while (result.Length > 1 && ImGui.CalcTextSize(result + "…").X > maxWidth)
        {
            result = result[..^1];
        }
        return result + "…";
    }

    /// <summary> 打开模组文件夹（explorer）。 </summary>
    private void OpenModFolder(string modFullPath)    {
        if (Directory.Exists(modFullPath))
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{modFullPath}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _result = "打开模组文件夹失败：" + ex.Message;
            }
        }
        else
        {
            _result = "模组目录不存在：" + modFullPath;
        }
    }

    /// <summary> 保存详情区编辑：备份（zip）-> 写回输入框内容 -> 重读文件 -> 触发 Penumbra 重载。 </summary>
    internal void SaveEdits(ModFileInfo file, ModEntry mod, Dictionary<string,string> bufs)
    {
        try
        {
            // 先自动备份整个模组（zip），防翻车
            var modDirPath = Path.GetDirectoryName(file.Path) ?? "";
            var modDirName = Path.GetFileName(modDirPath);
            plugin.Backup.CreateModZip(modDirPath, plugin.Configuration.BackupCount);

            // 保存前若文件仍为纯英文：存英文快照（改中文后仍可用原文覆写）
            try
            {
                var before = File.ReadAllText(file.Path);
                plugin.Snapshot.SaveIfEnglish(modDirName, file.FileName, before);
            }
            catch (Exception)
            {
                /* 快照失败不影响保存 */
            }
            var snap = plugin.Snapshot.GetEnglish(modDirName, file.FileName);
            var sediments = new List<(string ModDir, string FileName, string Field, string En, string Zh)>();

            var node = JsonNode.Parse(File.ReadAllText(file.Path)) as JsonObject;
            if (node == null)
            {
                _result = "保存失败：无法解析文件";
                plugin.AppLog.Error($"[保存修改] {mod.Directory}/{file.FileName}：解析失败，未写回");
                return;
            }

            var changed = 0;
            // 定位 Groups：顶层（新版 meta）或 Mod.Groups 包装（旧版），与解析/写回同规则
            JsonArray? groupsArr = node["Groups"] as JsonArray;
            if (groupsArr == null && node["Mod"] is JsonObject modWrap)
                groupsArr = modWrap["Groups"] as JsonArray;

            foreach (var kv in bufs)
            {
                var parts = kv.Key.Split('|');
                if (parts.Length < 2) continue;

                if (parts.Length == 2 && parts[1].StartsWith("G")) // 组名（key: 路径|G组索引）
                {
                    var gi = int.Parse(parts[1].Substring(1));
                    if (file.IsMeta && groupsArr != null && gi < groupsArr.Count &&
                        groupsArr[gi] is JsonObject gObj)
                    {
                        gObj["Name"] = kv.Value;
                        changed++;
                        var enG = snap?.Groups.FirstOrDefault(x => x.Index == gi)?.Name;
                        if (!string.IsNullOrWhiteSpace(enG) && dict.ContainsChinese(kv.Value) && kv.Value != enG)
                            sediments.Add((modDirName, file.FileName, "Name", enG, kv.Value));
                    }
                    else if (!file.IsMeta && gi == 0)
                    {
                        node["Name"] = kv.Value;
                        changed++;
                        var enG0 = snap?.Groups.FirstOrDefault(x => x.Index == 0)?.Name;
                        if (!string.IsNullOrWhiteSpace(enG0) && dict.ContainsChinese(kv.Value) && kv.Value != enG0)
                            sediments.Add((modDirName, file.FileName, "Name", enG0, kv.Value));
                    }
                }
                else if (parts.Length >= 3) // 选项名（key: 路径|组索引|选项索引）
                {
                    var gi = int.Parse(parts[1]);
                    var oi = int.Parse(parts[2]);
                    JsonArray? opts = null;
                    if (file.IsMeta)
                    {
                        if (groupsArr != null && gi < groupsArr.Count && groupsArr[gi] is JsonObject gObj2)
                            opts = gObj2["Options"] as JsonArray;
                    }
                    else
                    {
                        opts = node["Options"] as JsonArray;
                    }
                    if (opts != null && oi < opts.Count && opts[oi] is JsonObject oObj)
                    {
                        oObj["Name"] = kv.Value;
                        changed++;
                        var enO = snap?.Groups.FirstOrDefault(x => x.Index == gi)?
                            .Options.FirstOrDefault(x => x.Index == oi)?.Name;
                        if (!string.IsNullOrWhiteSpace(enO) && dict.ContainsChinese(kv.Value) && kv.Value != enO)
                            sediments.Add((modDirName, file.FileName, "Opt", enO, kv.Value));
                    }
                }
            }

            File.WriteAllText(file.Path, node.ToJsonString(JsonFile.Indented));
            _result = $"已保存 {changed} 项修改（原文件已自动备份）";
            plugin.AppLog.Info($"[保存修改] {mod.Directory}/{file.FileName}：已保存 {changed} 项（已自动备份）");

            if (sediments.Count > 0)
            {
                plugin.Sumup.Sediment(sediments, plugin.Configuration.DictionaryPath);
                _result += "\n" + plugin.Sumup.LastResult;
                plugin.ReloadDictionary();
            }

            ReloadSelectedFile();
            penumbra.Reload(mod.Directory, mod.Name); // 触发 Penumbra 重新加载该模组（按目录+名称匹配），游戏内立即生效
        }
        catch (Exception ex)
        {
            _result = "保存失败：" + ex.Message;
            plugin.AppLog.Error($"[保存修改] {mod.Directory}/{file.FileName}：保存失败：{ex.Message}");
        }
    }

    /// <summary> 重新读取当前模组的文件，刷新编辑缓冲。 </summary>
    internal void ReloadSelectedFile()
    {
        var modRoot = penumbra.GetModRoot();
        if (_selected < 0 || _selected >= penumbra.Mods.Count || string.IsNullOrEmpty(modRoot))
        {
            _selectedFile = null;
            return;
        }
        var mod = penumbra.Mods[_selected];
        var files = plugin.ModFiles.ReadModFiles(Path.Combine(modRoot, mod.Directory));
        _selectedFile = files.FirstOrDefault(x => x.Path == _selectedFile?.Path) ?? files.FirstOrDefault();
    }

    /// <summary> 详情区专用 ReadModFiles：按（目录 mtime, 内部 json 最大 mtime）缓存，文件增删改自动失效。仅 UI 线程。 </summary>
    private List<ModFileInfo> ReadDetailFiles(string modDir)
    {
        var stamp = (Dir: DateTime.MinValue, Files: DateTime.MinValue);
        try
        {
            if (Directory.Exists(modDir))
            {
                stamp.Dir = Directory.GetLastWriteTimeUtc(modDir);
                foreach (var f in Directory.EnumerateFiles(modDir, "*.json"))
                {
                    var t = File.GetLastWriteTimeUtc(f);
                    if (t > stamp.Files) stamp.Files = t;
                }
            }
        }
        catch { /* 取 mtime 失败按未缓存处理 */ }

        if (_detailFilesCache != null && _detailFilesKey == modDir && _detailFilesStamp.Equals(stamp))
            return _detailFilesCache;

        var list = plugin.ModFiles.ReadModFiles(modDir);
        _detailFilesKey = modDir;
        _detailFilesStamp = stamp;
        _detailFilesCache = list;
        return list;
    }

    /// <summary> 详情区专用英文快照读取：按快照文件 mtime 缓存解析结果。仅 UI 线程。 </summary>
    internal ModFileInfo? GetEnglishCached(string modDir, string fileName)
    {
        var p = Path.Combine(plugin.Configuration.DictionaryPath ?? "", ".英文快照", modDir, fileName);
        DateTime stamp = DateTime.MinValue;
        try { if (File.Exists(p)) stamp = File.GetLastWriteTimeUtc(p); } catch { }

        var key = modDir + "|" + fileName;
        if (_snapKey == key && _snapStamp == stamp) return _snapCache;

        var parsed = plugin.Snapshot.GetEnglish(modDir, fileName);
        _snapKey = key;
        _snapStamp = stamp;
        _snapCache = parsed;
        return parsed;
    }

    /// <summary> 供选项编辑窗口使用：当前聚焦的文件（未选中返回 null）。 </summary>
    internal ModFileInfo? FocusedFile => _selectedFile;

    /// <summary> 供选项编辑窗口使用：主窗口最近一次操作结果文本。 </summary>
    internal string ResultText => _result;

    /// <summary> 供文件总览窗口使用：当前聚焦的模组（未选中返回 null）。 </summary>
    internal ModEntry? FocusedMod => _selected >= 0 && _selected < penumbra.Mods.Count ? penumbra.Mods[_selected] : null;

    /// <summary> 供文件总览窗口使用：读取当前聚焦模组的文件列表（带 mtime 缓存）。 </summary>
    internal List<ModFileInfo> FilesForFocusedMod()
    {
        var modRoot = penumbra.GetModRoot();
        var mod = FocusedMod;
        if (mod == null || string.IsNullOrEmpty(modRoot)) return new List<ModFileInfo>();
        return ReadDetailFiles(Path.Combine(modRoot, mod.Directory));
    }

    /// <summary> 文件总览窗口跳转：主窗口选中指定文件并置前。 </summary>
    public void FocusFile(string path)
    {
        var hit = FilesForFocusedMod().FirstOrDefault(x => x.Path == path);
        if (hit != null)
        {
            _selectedFile = hit;
        }
        IsOpen = true;
    }

    private static int CountOptions(ModFileInfo f)
    {
        var n = 0;
        foreach (var g in f.Groups) n += g.Options.Count;
        return n;
    }
}
