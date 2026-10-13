using System;
using System.Collections.Generic;

namespace CodexPeek
{
    public enum WorkState { Unknown, Idle, Working, Attention, Offline }

    public sealed class RateWindow
    {
        public string Name;
        public double UsedPercent;
        public int WindowMinutes;
        public DateTime? ResetsAtUtc;
    }

    public sealed class QuotaSnapshot
    {
        public double? RemainingPercent;
        public DateTime? ResetsAtUtc;
        public DateTime? FetchedAtUtc;
        public bool IsStale;
        public bool LoginRequired;
        public string Message = "正在读取额度";
        public string Source = "";
        public string Plan = "";
        public string Credits = "";
        public List<RateWindow> Windows = new List<RateWindow>();
    }

    public sealed class TokenTotals
    {
        public long Input;
        public long CachedInput;
        public long Output;
        public long Reasoning;
        public long Total;
        public int Sessions;
    }

    public sealed class SessionSummary
    {
        public string Id;
        public DateTime LastEventUtc;
        public string Model = "";
        public long Tokens;
        public WorkState State;
        public double? TokensPerSecond;
    }

    public sealed class UsageSnapshot
    {
        public TokenTotals Today = new TokenTotals();
        public TokenTotals SevenDays = new TokenTotals();
        public double? TokensPerSecond;
        public DateTime? SpeedObservedAtUtc;
        public string SpeedLabel = "最近一轮估算速度";
        public DateTime? LastEventUtc;
        public WorkState State = WorkState.Unknown;
        public int ActiveCount;
        public string StatusDetail = "等待本地活动数据";
        public string CoverageDescription = "仅统计本机可读取的会话日志";
        public bool CoverageLimited;
        public List<SessionSummary> Sessions = new List<SessionSummary>();
    }
}
