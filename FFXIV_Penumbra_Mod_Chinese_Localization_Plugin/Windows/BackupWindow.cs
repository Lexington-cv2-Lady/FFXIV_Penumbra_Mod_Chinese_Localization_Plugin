using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using FFXIVPenumbraHanhua.Services;

namespace FFXIVPenumbraHanhua.Windows;

/// <summary> 备份管理窗口：创建备份 / 还原备份 / 删除备份。 </summary>
public class BackupWindow : Window, IDisposable
{
    private readonly Plugin _plugin;
    private readonly BackupManager _backup;
    private readonly MarkService _mark;

    // 模组多选：存模组目录名（抗列表变化下标漂移）
    private readonly HashSet<string> _modSet = new(StringComparer.OrdinalIgnoreCase);
    // 备份多选（勾选模组时自动同步全选其备份）：存备份文件完整路径
    private readonly HashSet<string> _bakSet = new(StringComparer.OrdinalIgnoreCase);
    private bool _syncBak;
    // 两个列表各自的鼠标框选状态
    private readonly ListDragSelect _modDrag = new();
    private readonly ListDragSelect _bakDrag = new();

    private string _result = "";
    private bool _needRefresh;
    // 还原二次确认（覆盖模组的重操作）：首次点击变「再点一次确认」，3 秒内再点才执行
    private bool _restoreArmed;
    private DateTime _restoreArmedUntil = DateTime.MinValue;
    // 备份列表缓存：避免每帧全库磁盘枚举（5 秒过期；有勾选时不自动过期防下标漂移；操作后显式失效）
    private List<BackupManager.BackupInfo>? _backupsCache;
    private DateTime _backupsCacheTime = DateTime.MinValue;

    // 左右分栏比例（分隔条可拖动）
    private float _split = 0.36f;
    private bool _draggingSplit;

    // 框选时锁定窗口位置（ImGui 默认把空白处拖动当移动窗口）：两个列表区域 + 位置锁定
    private Vector2 _modRectMin, _modRectMax, _bakRectMin, _bakRectMax;
    private Vector2? _dragWinLock;
    private Vector2 _posWhileIdle;

    public BackupWindow(Plugin plugin) : base("备份管理###HanhuaBackup")
    {
        Size = new Vector2(640, 480);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(480, 360),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

        _plugin = plugin;
        _backup = plugin.Backup;
        _mark = plugin.Mark;
        _plugin.Penumbra.ModsChanged += OnModsChanged;
    }

    private void OnModsChanged()
    {
        _backupsCache = null; // 勾选集存目录名/路径，不受模组列表变化影响
    }

    public void Dispose()
    {
        _plugin.Penumbra.ModsChanged -= OnModsChanged;
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

    public override void Draw()
    {
        var modRoot = _plugin.Penumbra.GetModRoot();
        var mods = _plugin.Penumbra.Mods;

        if (_needRefresh)
        {
            _needRefresh = false;
            _backupsCache = null;
            _bakSet.Clear();
            _syncBak = true; // 操作后按当前勾选的模组重新同步备份全选
        }

        if (string.IsNullOrEmpty(modRoot) || !Directory.Exists(modRoot))
        {
            ImGui.TextWrapped("无法获取 Penumbra 模组根目录");
            return;
        }

        // 备份列表缓存：每帧全库枚举模组目录磁盘开销过大；操作后 _needRefresh 显式失效
        if (_backupsCache == null || (_bakSet.Count == 0 && (DateTime.Now - _backupsCacheTime).TotalSeconds > 5))
        {
            _backupsCache = _backup.ListBackups(modRoot);
            _backupsCacheTime = DateTime.Now;
        }
        var allBackups = _backupsCache;

        // 顶部说明
        ImGui.TextWrapped($"备份文件：yyyy-MM-dd_HH-mm-ss备份.zip（每个模组一个 zip，自动备份轮转保留 {_plugin.Configuration.BackupCount} 份）");
        Ui.Hint($"全库共 {allBackups.Count} 个备份文件，涉及 {allBackups.Select(b => b.ModDir).Distinct().Count()} 个模组；旧 .json.bak_* 仍可识别还原");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var avail = ImGui.GetContentRegionAvail();
        var hotW = 12f * ImGuiHelpers.GlobalScale;
        var y0 = ImGui.GetCursorPosY();

        // 记录空闲窗口位置，供框选时锁定窗口用
        if (!_modDrag.Armed && !_modDrag.Active && !_bakDrag.Armed && !_bakDrag.Active)
            _posWhileIdle = ImGui.GetWindowPos();

        // 分栏尺寸：左右永远并排。左按比例（下限 100、上限不超过剩余宽减右栏下限），右取剩余（下限 90）
        var rightMin = 90f;
        var listW = avail.X * _split;
        if (listW < 100f) listW = 100f;
        if (listW > avail.X - rightMin - hotW) listW = Math.Max(100f, avail.X - rightMin - hotW);
        var rightW = Math.Max(rightMin, avail.X - listW - hotW);
        // 高度：分栏区域让出底部（按钮行 + 结果日志区）
        var listH = Math.Max(120f, avail.Y - 112f);

        // ── 左：模组列表（多选，头部固定置顶） ──
        ImGui.SetCursorPos(new Vector2(0f, y0));
        _modRectMin = ImGui.GetCursorScreenPos();
        _modRectMax = _modRectMin + new Vector2(listW, listH);
        using (var left = ImRaii.Child("##BackupMods", new Vector2(listW, listH), true))
        {
            if (left.Success)
            {
                var allSelected = mods.Count > 0 && mods.All(m => _modSet.Contains(m.Directory));
                ImGui.TextUnformatted($"模组（{mods.Count}）");
                ImGui.SameLine();
                if (ImGui.Checkbox("全选", ref allSelected))
                {
                    _modSet.Clear();
                    if (allSelected)
                    {
                        foreach (var m in mods) _modSet.Add(m.Directory);
                    }
                    _syncBak = true;
                }
                ImGui.Spacing();

                // 列表独立滚动区：头部（全选）固定置顶
                using (var scroll = ImRaii.Child("##BackupModsScroll", new Vector2(-1, -1), false))
                {
                    if (scroll.Success)
                    {
                        _modDrag.Begin();
                        var modInteractive = !_modDrag.Active; // 框选中屏蔽行点击
                        for (var i = 0; i < mods.Count; i++)
                        {
                            var checkedItem = _modSet.Contains(mods[i].Directory);
                            var marked = _mark.HasMark(mods[i].Directory);
                            var rowTop = ImGui.GetCursorScreenPos().Y;
                            // 紧凑行：小内边距 → 勾选框更小、行更矮，窗口缩小时一屏可见更多选项
                            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(3f, 2f) * ImGuiHelpers.GlobalScale);
                            if (ImGui.Checkbox($"##m{i}", ref checkedItem) && modInteractive)
                            {
                                if (checkedItem) _modSet.Add(mods[i].Directory);
                                else _modSet.Remove(mods[i].Directory);
                                _syncBak = true;
                            }
                            ImGui.SameLine();
                            // 整行可点击：点击模组名同样切换勾选（超长自动截断省略号，防止溢出左栏边界）
                            var label = (marked ? "[已翻译] " : "") + mods[i].Name;
                            if (ImGui.Selectable(Truncate(label, ImGui.GetContentRegionAvail().X) + $"##ms{i}", checkedItem) && modInteractive)
                            {
                                if (checkedItem) { _modSet.Remove(mods[i].Directory); }
                                else { _modSet.Add(mods[i].Directory); }
                                _syncBak = true;
                            }
                            ImGui.PopStyleVar();
                            _modDrag.Row(i, rowTop, rowTop + ImGui.GetFrameHeight());
                            if (ImGui.IsItemHovered())
                            {
                                ImGui.SetTooltip(mods[i].Directory + "\n点击整行切换勾选；按住左键拖动可框选多个");
                            }
                        }
                        // 框选命中 → 勾选（只增不减），并联动备份列表
                        var hitMods = _modDrag.End();
                        if (hitMods.Count > 0)
                        {
                            foreach (var i in hitMods) _modSet.Add(mods[i].Directory);
                            _syncBak = true;
                        }
                    }
                }
            }
        }
        // 可拖动分隔条：InvisibleButton 消费点击（防止误拖窗口）+ 宽热区 + 手动坐标计算
        // 竖条 Y 直接取左栏 Child 绘制后的实际屏幕矩形，保证与左右栏上下完全对齐
        var draw = ImGui.GetWindowDrawList();
        var winPos = ImGui.GetWindowPos();
        var rmin = ImGui.GetWindowContentRegionMin();
        var barMin = ImGui.GetItemRectMin();
        var barMax = ImGui.GetItemRectMax();
        var barX = barMin.X + listW + (hotW - 6f * ImGuiHelpers.GlobalScale) * 0.5f; // 竖条精确居中于左右栏缝隙
        var barY = barMin.Y;
        var barBottom = barMax.Y;
        var mouse = ImGui.GetMousePos();
        var io = ImGui.GetIO();

        ImGui.SetCursorPos(new Vector2(listW, y0));
        ImGui.InvisibleButton("##splitter", new Vector2(hotW, listH));
        var hoverBar = ImGui.IsItemHovered();
        var activeBar = ImGui.IsItemActive();
        if (hoverBar || activeBar || _draggingSplit)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll);
            if (activeBar) _draggingSplit = true;
            if (_draggingSplit && io.MouseDown[0])
            {
                _split = Math.Clamp((mouse.X - (winPos.X + rmin.X)) / avail.X, 0.2f, 0.78f);
            }
            if (!io.MouseDown[0]) _draggingSplit = false;
        }
        draw.AddRectFilled(new Vector2(barX, barY), new Vector2(barX + 6f * ImGuiHelpers.GlobalScale, barBottom),
            ImGui.GetColorU32(new Vector4(0.42f, 0.72f, 1f, _draggingSplit || hoverBar ? 0.9f : 0.45f)));

        // ── 右：备份列表（选中模组的备份，多选） ──
        var selectedMods = _modSet;
        var relevant = allBackups.Where(b => selectedMods.Count == 0 || selectedMods.Contains(b.ModDir)).ToList();

        // 模组勾选变化 → 自动全选其备份（用户仍可手动取消个别备份）
        if (_syncBak)
        {
            _syncBak = false;
            _bakSet.Clear();
            foreach (var b in relevant) _bakSet.Add(b.BakPath);
        }

        // 右 Child：显式钉死同一行位置（与主窗口一致，防止 InvisibleButton 后光标被推进导致换行）
        ImGui.SetCursorPos(new Vector2(listW + hotW, y0));
        _bakRectMin = ImGui.GetCursorScreenPos();
        _bakRectMax = _bakRectMin + new Vector2(rightW, listH);
        using (var right = ImRaii.Child("##BackupList", new Vector2(rightW, listH), true))
        {
            if (right.Success)
            {
                var allBak = relevant.Count > 0 && _bakSet.Count == relevant.Count;
                ImGui.TextUnformatted($"备份（{relevant.Count}）");
                ImGui.SameLine();
                if (ImGui.Checkbox("全选备份", ref allBak))
                {
                    _bakSet.Clear();
                    if (allBak)
                    {
                        foreach (var b in relevant) _bakSet.Add(b.BakPath);
                    }
                }
                ImGui.Spacing();

                // 列表独立滚动区：头部（全选备份）固定置顶
                using (var scroll = ImRaii.Child("##BackupListScroll", new Vector2(-1, -1), false))
                {
                    if (scroll.Success)
                    {
                        _bakDrag.Begin();
                        var bakInteractive = !_bakDrag.Active; // 框选中屏蔽行点击
                        for (var i = 0; i < relevant.Count; i++)
                        {
                            var b = relevant[i];
                            var checkedItem = _bakSet.Contains(b.BakPath);
                            var rowTop = ImGui.GetCursorScreenPos().Y;
                            // 紧凑行：小内边距 → 勾选框更小、行更矮
                            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(3f, 2f) * ImGuiHelpers.GlobalScale);
                            if (ImGui.Checkbox($"##b{i}", ref checkedItem) && bakInteractive)
                            {
                                if (checkedItem) _bakSet.Add(b.BakPath);
                                else _bakSet.Remove(b.BakPath);
                            }
                            ImGui.SameLine();
                            // 整行可点击：点击备份名同样切换勾选（超长自动截断省略号，防止溢出右栏边界）
                            if (ImGui.Selectable(Truncate($"{b.ModDir}  \\  {b.FileName}", ImGui.GetContentRegionAvail().X) + $"##bs{i}", checkedItem) && bakInteractive)
                            {
                                if (checkedItem) _bakSet.Remove(b.BakPath);
                                else _bakSet.Add(b.BakPath);
                            }
                            ImGui.PopStyleVar();
                            _bakDrag.Row(i, rowTop, rowTop + ImGui.GetFrameHeight());
                            if (ImGui.IsItemHovered())
                            {
                                ImGui.SetTooltip($"备份时间：{b.Time:yyyy-MM-dd HH:mm:ss}\n完整路径：{b.BakPath}\n点击整行切换勾选；按住左键拖动可框选多个");
                            }
                        }
                        // 框选命中 → 勾选（只增不减）
                        foreach (var i in _bakDrag.End()) _bakSet.Add(relevant[i].BakPath);
                    }
                }
            }
        }

        // ── 底部按钮：创建选中备份（左）/ 删除选中备份（行内居中）/ 还原选中备份（贴右缘） ──
        ImGui.Spacing();
        var rowW = ImGui.GetContentRegionAvail().X; // 整行内容宽（画第一个按钮前量取）
        var delW = Ui.ButtonWidth("删除选中备份");
        var resW = Ui.ButtonWidth("还原选中备份");

        // 左：创建选中备份
        if (ImGui.Button("创建选中备份"))
        {
            var m = 0;
            foreach (var dir in _modSet)
            {
                if (_backup.ManualBackup(Path.Combine(modRoot, dir), _plugin.Configuration.BackupCount) > 0) m++;
            }
            _result = _modSet.Count == 0
                ? "未勾选模组（左侧勾选要备份的模组）"
                : $"已创建备份：{m} 个模组（zip 轮转保留 {_plugin.Configuration.BackupCount} 份）";
            _needRefresh = true;
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("对左侧勾选的模组手动创建备份（meta.json + group_*.json）");
        }

        // 中：删除选中备份（整行水平居中；窄窗口时不与左钮重叠）
        ImGui.SameLine();
        ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), (rowW - delW) / 2f));
        if (ImGui.Button("删除选中备份"))
        {
            var ok = 0;
            foreach (var b in relevant)
            {
                if (_bakSet.Contains(b.BakPath) && _backup.Delete(modRoot, b)) ok++;
            }
            _result = _bakSet.Count == 0
                ? "未勾选备份（右侧勾选要删除的备份）"
                : $"已删除 {ok} 个备份（移至回收站，可还原）";
            _needRefresh = true;
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("删除勾选的备份文件（移至回收站，误删可还原）");
        }

        // 右：还原选中备份（贴右缘；窄窗口时不与中钮重叠）
        // 覆盖模组文件的重操作 → 二次确认：首次点击变「再点一次确认」，3 秒内再点才执行
        ImGui.SameLine();
        ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), rowW - resW));
        var armed = _restoreArmed && DateTime.Now < _restoreArmedUntil;
        if (armed)
        {
            Ui.PushDanger();
        }
        if (ImGui.Button(armed ? "确认还原（覆盖模组）" : "还原选中备份"))
        {
            if (!armed)
            {
                _restoreArmed = true;
                _restoreArmedUntil = DateTime.Now.AddSeconds(3);
                _result = _bakSet.Count == 0
                    ? "未勾选备份（右侧勾选要还原的备份）"
                    : $"⚠ 将要还原 {_bakSet.Count} 个备份并覆盖模组文件，3 秒内再点一次确认";
            }
            else
            {
                _restoreArmed = false;
                var ok = 0;
                foreach (var b in relevant)
                {
                    if (_bakSet.Contains(b.BakPath) && _backup.Restore(modRoot, b)) ok++;
                }
                _result = _bakSet.Count == 0
                    ? "未勾选备份（右侧勾选要还原的备份）"
                    : $"已还原 {ok} 个备份（原备份已移至回收站，模组已还原为备份时的内容）";
                _needRefresh = true;
            }
        }
        if (armed)
        {
            Ui.PopDanger();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("把备份内容覆盖回原文件（还原后该备份移至回收站）。\n为防误点：首次点击只给确认提示，需 3 秒内再点一次才真正执行。");
        }

        ImGui.Spacing();
        // ── 结果日志区：固定高度、带边框、可滚动，完整显示操作结果（风格与其他窗口统一） ──
        Plugin.ResultBox("##BackupResult", _result, "操作结果将显示在这里（如：已还原 N 个备份…）");

        // ── 框选时阻止窗口移动（同主窗口）──
        // ImGui 默认把「在空白处按下拖动」当移动窗口；鼠标在任一列表内或正在框选时给窗口加 NoMove。
        var mouseNow = ImGui.GetMousePos();
        var inMod = InRect(mouseNow, _modRectMin, _modRectMax);
        var inBak = InRect(mouseNow, _bakRectMin, _bakRectMax);
        var wantNoMove = inMod || inBak || _modDrag.Armed || _modDrag.Active || _bakDrag.Armed || _bakDrag.Active;
        Flags = wantNoMove ? ImGuiWindowFlags.NoMove : ImGuiWindowFlags.None;

        if (_modDrag.Active || _bakDrag.Active)
        {
            _dragWinLock ??= _posWhileIdle;
            ImGui.SetWindowPos(_dragWinLock.Value);
        }
        else
        {
            _dragWinLock = null;
        }
    }

    private static bool InRect(Vector2 p, Vector2 min, Vector2 max)
        => p.X >= min.X && p.X <= max.X && p.Y >= min.Y && p.Y <= max.Y;
}
