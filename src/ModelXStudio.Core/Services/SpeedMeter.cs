// =====================================================================
// 作者：zhenhua
// 功能：下载速度平滑器。把「两次读取之间的字节差 ÷ 时间差」这种瞬时值，
//       做成滑动窗口平均 + 指数平滑，供下载服务向界面回报，避免速度数字乱跳。
// 许可：ModelX Studio 非商业使用许可 1.0（见仓库根 LICENSE）。上游第三方组件（llama.cpp 等）按各自 MIT 许可授权。
// =====================================================================

using System.Collections.Generic;

namespace ModelXStudio.Core.Services;

/// <summary>
/// 速度平滑器：按顺序喂进「累计已下字节」，得到一个平稳的平均速度（字节/秒）。
///
/// 为什么要它：TCP 是突发到达的，一次 ReadAsync 可能攒了 256 KB，也可能只回来 3 KB。
/// 直接拿相邻两次读取算速度，量到的其实是「这块缓冲区花了几毫秒」，界面每几十毫秒
/// 刷一次、每次都是不同的噪声（用户看到的「速度跳得很快」）。这里做两层：
/// ① 滑动窗口：只用最近 N 秒内的首尾两点算平均，单次突发被摊平；
/// ② 指数平滑：在窗口平均之上再压一层，让曲线缓慢趋近而不是反复折返。
/// 时间请用 <c>Stopwatch</c>（单调时钟），不要用 DateTime.UtcNow —— 后者粒度只有约 15 ms，
/// 短窗口测速会凭空多出成倍的抖动。
/// </summary>
public sealed class SpeedMeter
{
    /// <summary>小于这个时间跨度不出数（秒）。只用于防「除以接近 0」，不是显示阈值。</summary>
    private const double MinElapsedSeconds = 0.05;

    private readonly List<Sample> _samples = new();
    private bool _primed;

    /// <summary>滑动窗口长度。越长越平稳、但反应越慢（默认 5 秒，够抹平一次 TCP 突发）。</summary>
    public TimeSpan Window { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 指数平滑系数，取值 0～1：越小越平（更看重历史），1 = 不平滑。
    /// 0.3 大致是「新数据只占三成」，肉眼看到的一位小数几乎不来回跳。
    /// </summary>
    public double Smoothing { get; set; } = 0.3;

    /// <summary>平滑后的速度（字节/秒）。还算不出来时是 0。</summary>
    public double BytesPerSecond { get; private set; }

    /// <summary>已喂进多少个采样点。少于 2 个时速度还不可信，界面可以先不显示。</summary>
    public int SampleCount => _samples.Count;

    /// <summary>
    /// 是否已经算出一个可信速度。刚开始那一拍可能正好一个字节都还没到（链路在握手/重试/停一下），
    /// 那个 0 不是「网速 0」，报上去界面就闪一下「0 KB/s」。
    /// </summary>
    public bool HasSpeed { get; private set; }

    /// <summary>丢历史重新开始（换源、重试、删残片重来之后累计字节会跳变，不能跨过去算差值）。</summary>
    public void Reset()
    {
        _samples.Clear();
        _primed = false;
        BytesPerSecond = 0;
        HasSpeed = false;
    }

    /// <summary>
    /// 喂一个采样点：totalBytes 是「到现在为止一共下了多少字节」（含续传已有的部分），
    /// nowSeconds 是单调时钟秒数。返回平滑后的字节/秒。
    /// </summary>
    public double Add(long totalBytes, double nowSeconds)
    {
        // 累计字节倒退 = 换了一次下载（重来、换源、删残片），旧点全部作废
        if (_samples.Count > 0 && totalBytes < _samples[0].TotalBytes) Reset();

        if (_samples.Count > 0 && nowSeconds <= _samples[^1].AtSeconds)
        {
            // 时刻没往前走（同一毫秒内被连喂两次）：只把最新点的字节数更新掉，不新增点。
            // 新增会产生「两个同一时刻的点」，窗口首尾跨度为 0，除出来的速度是天文数字。
            var last = _samples[^1];
            _samples[^1] = new Sample(last.AtSeconds, totalBytes);
            return BytesPerSecond;
        }

        _samples.Add(new Sample(nowSeconds, totalBytes));

        // 丢窗口外的旧点，但永远至少留两个点：刚开始那一两秒窗口还不满，
        // 用首尾跨度算等于「到目前为止的平均」，比按整窗硬算更贴近当前实况。
        var windowSeconds = Window.TotalSeconds;
        while (_samples.Count > 2 && nowSeconds - _samples[0].AtSeconds > windowSeconds)
            _samples.RemoveAt(0);

        var oldest = _samples[0];
        var elapsed = nowSeconds - oldest.AtSeconds;

        // 时间跨度太短就沿用上一个值。这里最容易闪出「0 KB/s」，比数值不准更难看。
        if (elapsed <= MinElapsedSeconds) return BytesPerSecond;

        var raw = (totalBytes - oldest.TotalBytes) / elapsed;

        if (!_primed)
        {
            // 第一个可信值直接采用，不从 0 爬坡 —— 否则每次下载开头都会演出
            // 「0 → 800 KB/s → 2.4 MB/s」那段和网速毫无关系的动画。
            // 但 raw = 0 不算可信：那一拍正好一个字节没到不等于链路停了，
            // 拿它当起始速度，头几拍会「0 → 真速 → 真速」地闪一下。
            if (raw <= 0) return BytesPerSecond;
            BytesPerSecond = raw;
            _primed = true;
            HasSpeed = true;
        }
        else
        {
            var alpha = Smoothing;
            if (alpha < 0) alpha = 0;
            if (alpha > 1) alpha = 1;
            BytesPerSecond += alpha * (raw - BytesPerSecond);
        }

        return BytesPerSecond;
    }

    private readonly struct Sample
    {
        public Sample(double atSeconds, long totalBytes)
        {
            AtSeconds = atSeconds;
            TotalBytes = totalBytes;
        }

        public double AtSeconds { get; }
        public long TotalBytes { get; }
    }
}
