using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace FFXIVPenumbraHanhua.Windows;

/// <summary>
/// 列表鼠标框选：在列表内任意位置按下并拖动超过阈值，即拉出选框，框到的行自动被勾选（只增不减，
/// 不会取消已勾选项，因此不会误清空选择）。用法：进入列表滚动 Child 后调用 <see cref="Begin"/>；
/// 每绘制一行调用 <see cref="Row"/>（传入该行的屏幕上下边界 Y）；行循环结束后调用 <see cref="End"/>，
/// 返回本帧被框中的行索引。
/// 
/// 与点击的区分：按下时不立即框选，只有在按住期间移动超过 <c>MouseDragThreshold</c> 才算框选；
/// 在此之前/纯点击（未移动）按普通点击处理。调用方在绘制行时应以 <see cref="Active"/>（拖动中，
/// 取上一帧状态）为条件屏蔽行的点击，避免起拖那一行被误切换。
/// 滚动条区域不触发框选。
/// </summary>
internal sealed class ListDragSelect
{
    private bool _armed;      // 已在列表内按下，等待判断是点击还是拖动
    private bool _dragging;   // 已超过拖动阈值，正在框选
    private Vector2 _start;
    private readonly List<(int Index, float Top, float Bottom)> _rows = new();

    /// <summary> 是否正在框选（拖动阈值已过；用于屏蔽底层行的点击）。 </summary>
    public bool Active => _dragging;

    /// <summary> 是否已在列表内按下（点击/拖动判定中；用于锁定窗口位置，避免 ImGui 把拖动当成移动窗口）。 </summary>
    public bool Armed => _armed;

    /// <summary> 每帧进入列表后调用，清空上一帧收集的行矩形。 </summary>
    public void Begin() => _rows.Clear();

    /// <summary> 记录一行的屏幕上下边界 Y（用于与选框求交）。 </summary>
    public void Row(int index, float top, float bottom) => _rows.Add((index, top, bottom));

    /// <summary> 行绘制结束后调用；返回本帧被选框覆盖、需要勾选的行索引集合。 </summary>
    public IReadOnlyList<int> End()
    {
        var hits = new List<int>();
        var io = ImGui.GetIO();
        var mouse = io.MousePos;

        // 当前列表可视区（Child 的屏幕矩形；右侧扣除滚动条，避免拖滚动条被误判为框选）
        var wPos = ImGui.GetWindowPos();
        var wSize = ImGui.GetWindowSize();
        var min = wPos;
        var max = new Vector2(wPos.X + wSize.X - ImGui.GetStyle().ScrollbarSize, wPos.Y + wSize.Y);
        var inList = mouse.X >= min.X && mouse.X <= max.X && mouse.Y >= min.Y && mouse.Y <= max.Y;

        // 列表内按下即预备（不要求空白处：行是整行宽的 Selectable，几乎没有空白可点）
        if (ImGui.IsMouseClicked(0) && inList)
        {
            _armed = true;
            _dragging = false;
            _start = mouse;
        }

        // 按住并移动超过阈值 -> 进入框选（区分纯点击）
        if (_armed && io.MouseDown[0] && !_dragging &&
            Vector2.Distance(mouse, _start) > io.MouseDragThreshold)
        {
            _dragging = true;
        }

        if (_dragging)
        {
            var cMin = Vector2.Max(Vector2.Min(_start, mouse), min);
            var cMax = Vector2.Min(Vector2.Max(_start, mouse), max);
            if (cMax.X > cMin.X && cMax.Y > cMin.Y)
            {
                var dl = ImGui.GetWindowDrawList();
                dl.AddRectFilled(cMin, cMax, ImGui.GetColorU32(new Vector4(0.30f, 0.62f, 1f, 0.25f)));
                dl.AddRect(cMin, cMax, ImGui.GetColorU32(new Vector4(0.50f, 0.78f, 1f, 0.95f)));
                foreach (var r in _rows)
                {
                    if (r.Bottom >= cMin.Y && r.Top <= cMax.Y) hits.Add(r.Index);
                }
            }
        }

        if (!io.MouseDown[0])
        {
            _armed = false;
            _dragging = false;
        }
        return hits;
    }
}
