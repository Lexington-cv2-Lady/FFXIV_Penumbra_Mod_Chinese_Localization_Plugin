using System.Linq;
using FFXIVPenumbraHanhua.Services;
using Xunit;

namespace FFXIVPenumbraHanhua.Tests;

/// <summary>
/// 回归测试：后台签名扫描的并发语义（审查方第三轮【低】①）。
/// 覆盖防重入（TryBeginScan/EndScan）、「有变化才入队」、每帧限量消费不丢项。
/// 这些状态原先是 Plugin 的私有字段，Plugin 依赖 Dalamud 注入服务、无法在测试内实例化，
/// 故收拢为 ModSignatureTracker 后直接测其语义。
/// </summary>
public class ModSignatureTrackerTests
{
    [Fact]
    public void TryBeginScan_BlocksReentry_UntilEndScan()
    {
        var t = new ModSignatureTracker();

        Assert.True(t.TryBeginScan());
        Assert.True(t.IsScanning);
        Assert.False(t.TryBeginScan()); // 上一轮未结束 → 防重入，调用方跳过本轮

        t.EndScan();
        Assert.False(t.IsScanning);
        Assert.True(t.TryBeginScan());  // 释放后可再次进入
        t.EndScan();
    }

    [Fact]
    public void EndScan_IsIdempotent()
    {
        var t = new ModSignatureTracker();
        t.EndScan(); // 未进入即释放：不应破坏状态
        t.EndScan();

        Assert.True(t.TryBeginScan());
        t.EndScan();
    }

    [Fact]
    public void Observe_SkipsOnlyWhenBaselineMatches()
    {
        var t = new ModSignatureTracker();
        var sig = (mtime: 100L, size: 10L);

        t.Observe("modA", sig);
        Assert.Equal(new[] { "modA" }, t.Drain(8)); // 首轮无基线 → 入队

        t.Observe("modA", sig);
        t.ClearPending(); // 无基线时仍会入队（与整改前语义一致，由主线程处理后同步基线）

        t.SetBaseline("modA", sig);
        t.Observe("modA", sig);
        Assert.False(t.HasPending); // 基线一致 → 不入队（用户改选项导致重写才会走到这里）

        t.Observe("modA", (200L, 10L));
        Assert.True(t.HasPending); // 签名变化 → 入队
        Assert.Equal(new[] { "modA" }, t.Drain(8));
    }

    [Fact]
    public void Drain_LimitsPerFrameBatch_WithoutDroppingAny()
    {
        var t = new ModSignatureTracker();
        for (var i = 0; i < 10; i++) t.Observe("mod" + i, (i, 1));

        var first = t.Drain(8); // 每帧限量
        Assert.Equal(8, first.Count);
        Assert.True(t.HasPending);

        var second = t.Drain(8);
        Assert.Equal(2, second.Count);
        Assert.False(t.HasPending);

        // 10 个全部取到，且不重复（旧写法会多出队一个并丢掉）
        Assert.Equal(10, first.Concat(second).Distinct().Count());
    }

    [Fact]
    public void RemoveBaseline_ForcesNextObserveToEnqueue()
    {
        var t = new ModSignatureTracker();
        var sig = (100L, 10L);

        t.SetBaseline("modA", sig);
        t.Observe("modA", sig);
        Assert.False(t.HasPending);

        t.RemoveBaseline("modA"); // 模组已从磁盘消失
        Assert.Equal(0, t.BaselineCount);

        t.Observe("modA", sig);
        Assert.True(t.HasPending); // 无基线 → 重新纳入观察
    }

    [Fact]
    public void ConcurrentBeginScan_OnlyOneWinner()
    {
        var t = new ModSignatureTracker();
        var winners = 0;

        System.Threading.Tasks.Parallel.For(0, 32, _ =>
        {
            if (t.TryBeginScan()) System.Threading.Interlocked.Increment(ref winners);
        });

        Assert.Equal(1, winners); // 并发抢入只允许一个
        t.EndScan();
    }
}
