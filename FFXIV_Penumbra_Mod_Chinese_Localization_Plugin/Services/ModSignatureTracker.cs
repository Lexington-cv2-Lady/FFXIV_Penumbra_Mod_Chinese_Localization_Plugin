using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace FFXIVPenumbraHanhua.Services;

/// <summary>
/// 「更新重覆盖」的文件签名扫描协调器：把原先散落在 <c>Plugin</c> 里的三项并发状态
/// （签名基线 / 变化队列 / 扫描防重入）收拢成一个可独立回归测试的类型。
/// 抽出原因：Plugin 构造函数依赖 Dalamud 注入的服务、无法在测试里实例化，
/// 而这些并发语义（防重入、有变化才入队、每帧限量消费）正是最易被回归破坏的部分。
///
/// 线程模型（与抽出前完全一致）：
/// - 主线程：<see cref="TryBeginScan"/> → 取模组快照 → <c>Task.Run</c> 逐一 <see cref="Observe"/>；以及 <see cref="Drain"/> 消费。
/// - 后台线程：只调 <see cref="Observe"/>（磁盘签名由调用方算好传入），不碰词典、不碰 Penumbra。
/// - 后台任务结束时必须 <see cref="EndScan"/>；未释放前 <see cref="TryBeginScan"/> 返回 false（防重入）。
/// </summary>
internal sealed class ModSignatureTracker
{
    private readonly ConcurrentDictionary<string, (long mtime, long size)> _baseline = new();
    private readonly ConcurrentQueue<string> _changed = new();
    private int _busy; // 0 = 空闲，1 = 扫描中

    /// <summary> 是否正有扫描在进行（测试 / 排障用）。 </summary>
    public bool IsScanning => Volatile.Read(ref _busy) == 1;

    /// <summary> 是否还有待消费的「签名有变化」模组。 </summary>
    public bool HasPending => !_changed.IsEmpty;

    /// <summary> 当前基线条目数（测试 / 排障用）。 </summary>
    public int BaselineCount => _baseline.Count;

    /// <summary>
    /// 尝试进入扫描态。返回 false = 上一轮尚未扫完，调用方应直接跳过本轮（防重入）。
    /// 返回 true 后必须配对调用 <see cref="EndScan"/>（后台任务的 finally 里调）。
    /// </summary>
    public bool TryBeginScan() => Interlocked.CompareExchange(ref _busy, 1, 0) == 0;

    /// <summary> 退出扫描态（可在任意线程调用；重复调用无副作用）。 </summary>
    public void EndScan() => Interlocked.Exchange(ref _busy, 0);

    /// <summary>
    /// 观察一次磁盘签名（后台线程调用）：与基线相同则不入队；不同（或尚无基线）则入队待主线程处理。
    /// </summary>
    public void Observe(string key, (long mtime, long size) sig)
    {
        if (_baseline.TryGetValue(key, out var prev) && prev == sig) return; // 无变化
        _changed.Enqueue(key);
    }

    /// <summary> 设定 / 更新某模组的基线（写回后同步，避免同一改动被反复触发）。 </summary>
    public void SetBaseline(string key, (long mtime, long size) sig) => _baseline[key] = sig;

    /// <summary> 清除某模组基线（模组已从磁盘消失时调用）。 </summary>
    public void RemoveBaseline(string key) => _baseline.TryRemove(key, out _);

    /// <summary> 取出至多 <paramref name="max"/> 个变化模组（主线程消费，每帧限量避免卡帧）。 </summary>
    public List<string> Drain(int max)
    {
        var list = new List<string>();
        while (list.Count < max && _changed.TryDequeue(out var key)) list.Add(key);
        return list;
    }

    /// <summary> 丢弃全部待消费项（模组根目录不可用时调用）。 </summary>
    public void ClearPending()
    {
        while (_changed.TryDequeue(out _)) { }
    }
}
