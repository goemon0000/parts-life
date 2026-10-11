using System.Net;

namespace PartsLife.Update;

/// <summary>
/// いつ確認しに行くか。**多くて 1 日 1 回。**
///
///   - 起動から 60 秒後（起動の邪魔をしない）に、前回から 24 時間経っていれば
///   - 起きている間は 1 時間ごとに「24 時間経ったか」だけを見る（通信はしない）
///   - 回数制限（403 / 429）を受けたら 24 時間は行かない。Retry-After や X-RateLimit-Reset が
///     もっと先を指していればそこまで（ただし 7 日で打ち切る。変な値で永久に止まらないように）
///   - 「今すぐ確認」は 24 時間を待たない。ただし回数制限の待ちは守る
/// 時計が戻された（前回の確認が未来にある）ときは、確認してよいことにする。でないと戻した分だけ止まる。
/// </summary>
public static class UpdateSchedule
{
    public static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);
    public static readonly TimeSpan Poll = TimeSpan.FromHours(1);
    public static readonly TimeSpan RateLimitBackoff = TimeSpan.FromHours(24);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromDays(7);

    public static bool InBackoff(UpdateState s, DateTimeOffset now) =>
        s.BackoffUntilUtc is { } until && now < until && until - now <= MaxBackoff;

    /// <summary>決まった時刻の確認をしてよいか。</summary>
    public static bool IsDue(UpdateState s, DateTimeOffset now)
    {
        if (InBackoff(s, now)) return false;
        if (s.LastCheckUtc is not { } last) return true;
        if (last > now) return true;   // 時計が戻された
        return now - last >= Interval;
    }

    /// <summary>「今すぐ確認」を押してよいか（回数制限の待ちの間だけ押せない）。</summary>
    public static bool CanCheckNow(UpdateState s, DateTimeOffset now) => !InBackoff(s, now);

    /// <summary>次に決まった時刻の確認をする時刻（献立に出す用）。</summary>
    public static DateTimeOffset NextDue(UpdateState s, DateTimeOffset now)
    {
        var next = s.LastCheckUtc is { } last && last <= now ? last + Interval : now;
        if (InBackoff(s, now) && s.BackoffUntilUtc!.Value > next) next = s.BackoffUntilUtc.Value;
        return next;
    }

    /// <summary>回数制限を受けたか。GitHub は制限に当たると 403（または 429）を返す。</summary>
    public static bool IsRateLimited(HttpStatusCode code) =>
        code is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests;

    /// <summary>
    /// 回数制限を受けたとき、いつまで待つか。24 時間と、相手の言う時刻の遅い方。7 日で打ち切る。
    /// </summary>
    public static DateTimeOffset BackoffUntil(DateTimeOffset now, TimeSpan? retryAfter, DateTimeOffset? rateLimitReset)
    {
        var until = now + RateLimitBackoff;
        if (retryAfter is { } ra && ra > TimeSpan.Zero && now + ra > until) until = now + ra;
        if (rateLimitReset is { } reset && reset > until) until = reset;
        var cap = now + MaxBackoff;
        return until > cap ? cap : until;
    }
}
