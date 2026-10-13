using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace CodexPeek.Tests
{
    public static class TelemetryTests
    {
        static int assertions;
        public static int Run()
        {
            assertions = 0;
            LegacyCounters();
            ModernCountersAndFork();
            DayBoundary();
            ActivityAndSpeed();
            ZeroDurationAndPartialLines();
            AttentionExpiryAndIgnoredContent();
            return assertions;
        }

        static void LegacyCounters()
        {
            using (var fixture = new Fixture())
            {
                fixture.Append(Meta("test"), Count(fixture.Now.AddSeconds(-4), 1000, 100, 200, 20), Count(fixture.Now.AddSeconds(-3), 1000, 100, 200, 20), Count(fixture.Now.AddSeconds(-2), 1500, 150, 300, 30));
                UsageSnapshot first = fixture.Reader.ReadSnapshot();
                Equal(1650L, first.Today.Total, "旧累计计数去重");
                Equal(1500L, first.Today.Input, "输入包含缓存");
                Equal(300L, first.Today.CachedInput, "缓存单独展示");
                Equal(150L, first.Today.Output, "输出包含推理");
                Equal(30L, first.Today.Reasoning, "推理不重复相加");
                Equal(1650L, fixture.Reader.ReadSnapshot().Today.Total, "重复轮询幂等");
                fixture.Append(Count(fixture.Now, 1700, 170, 350, 35));
                Equal(1870L, fixture.Reader.ReadSnapshot().Today.Total, "追加读取增量");
            }
        }

        static void ModernCountersAndFork()
        {
            using (var fixture = new Fixture())
            {
                fixture.Append(Meta("test"), Usage(fixture.Now.AddSeconds(-3), "test", "r1", "t1", 1000, 100), Count(fixture.Now.AddSeconds(-3), 1000, 100, 0, 0), Usage(fixture.Now.AddSeconds(-2), "test", "r1", "t1", 1000, 100), Usage(fixture.Now.AddSeconds(-1), "test", "r2", "t1", 500, 50), Count(fixture.Now, 1500, 150, 0, 0));
                Equal(1650L, fixture.Reader.ReadSnapshot().Today.Total, "新记录优先，旧事件不重复计数");
                fixture.Append(Usage(fixture.Now, "test", "r2", "t1", 500, 60));
                Equal(1660L, fixture.Reader.ReadSnapshot().Today.Total, "同请求更新仅计算差值");
            }
            using (var fixture = new Fixture())
            {
                var meta = Meta("test");
                ((Dictionary<string, object>)meta["payload"])["forked_from_id"] = "parent";
                fixture.Append(meta, Usage(fixture.Now.AddSeconds(-5), "parent", "parent-response", "p", 8000, 800), Count(fixture.Now.AddSeconds(-4), 8000, 800, 0, 0), Usage(fixture.Now, "test", "own-response", "t", 100, 10));
                Equal(110L, fixture.Reader.ReadSnapshot().Today.Total, "fork 只计本会话新记录");
            }
        }

        static void DayBoundary()
        {
            using (var fixture = new Fixture())
            {
                DateTime midnight = fixture.Now.ToLocalTime().Date.ToUniversalTime();
                fixture.Append(Meta("test"), Count(midnight.AddSeconds(-1), 1000, 100, 200, 20), Count(midnight.AddSeconds(1), 1500, 150, 300, 30));
                UsageSnapshot result = fixture.Reader.ReadSnapshot();
                Equal(550L, result.Today.Total, "跨日只归属当日增量");
                Equal(1650L, result.SevenDays.Total, "七个自然日汇总");
                Equal(1, result.Today.Sessions, "按会话去重");
            }
        }

        static void ActivityAndSpeed()
        {
            using (var fixture = new Fixture())
            {
                DateTime start = fixture.Now.AddSeconds(-10);
                fixture.Append(Meta("test"), Event(start, "task_started", "t"), Usage(fixture.Now.AddSeconds(-1), "test", "r", "t", 1000, 120));
                Equal(WorkState.Working, fixture.Reader.ReadSnapshot().State, "近期明确启动显示工作中");
                var complete = Event(fixture.Now, "task_complete", "t");
                ((Dictionary<string, object>)complete["payload"])["duration_ms"] = 10000;
                fixture.Append(complete);
                UsageSnapshot done = fixture.Reader.ReadSnapshot();
                Equal(WorkState.Idle, done.State, "完成清除绿灯");
                Equal(12.0, done.TokensPerSecond.Value, "速度使用输出除以完整回合时间");
                fixture.Append(Event(fixture.Now, "task_started", "t2"));
                Equal(WorkState.Working, fixture.Reader.ReadSnapshot().State, "下一回合启动");
                fixture.Now = fixture.Now.AddSeconds(181);
                Equal(WorkState.Unknown, fixture.Reader.ReadSnapshot().State, "静默超时为未知而非错误");
                fixture.Append(Event(fixture.Now, "approval_required", "t2"));
                Equal(WorkState.Attention, fixture.Reader.ReadSnapshot().State, "明确等待确认显示注意");
                fixture.Append(Event(fixture.Now, "turn_aborted", "t2"));
                Equal(WorkState.Idle, fixture.Reader.ReadSnapshot().State, "中断清除注意状态");
            }
        }

        static void ZeroDurationAndPartialLines()
        {
            using (var fixture = new Fixture())
            {
                fixture.Append(Meta("test"), Event(fixture.Now, "task_started", "t"), Usage(fixture.Now, "test", "r", "t", 100, 10), Event(fixture.Now, "task_complete", "t"));
                Equal(false, fixture.Reader.ReadSnapshot().TokensPerSecond.HasValue, "零时长不伪造速度");
                string line = new JavaScriptSerializer().Serialize(Usage(fixture.Now, "test", "r2", "t", 100, 10));
                File.AppendAllText(fixture.FilePath, line.Substring(0, line.Length / 2), new UTF8Encoding(false));
                Equal(110L, fixture.Reader.ReadSnapshot().Today.Total, "尚未写完的行延后解析");
                File.AppendAllText(fixture.FilePath, line.Substring(line.Length / 2) + "\n", new UTF8Encoding(false));
                Equal(220L, fixture.Reader.ReadSnapshot().Today.Total, "完成换行后计入");
                File.AppendAllText(fixture.FilePath, "{malformed}\n", new UTF8Encoding(false));
                Equal(true, fixture.Reader.ReadSnapshot().CoverageLimited, "损坏行显式标为不完整");
            }
        }

        static void AttentionExpiryAndIgnoredContent()
        {
            using (var fixture = new Fixture())
            {
                fixture.Append(Meta("test"), Event(fixture.Now, "task_started", "t"), Event(fixture.Now, "task_failed", "t"));
                Equal(WorkState.Attention, fixture.Reader.ReadSnapshot().State, "明确失败显示红色");
                fixture.Now = fixture.Now.AddSeconds(181);
                Equal(WorkState.Unknown, fixture.Reader.ReadSnapshot().State, "历史错误不会永久红灯");
                fixture.Append(Map("type", "response_item", "timestamp", fixture.Now.ToString("o", CultureInfo.InvariantCulture), "payload", Map("type", "message", "content", new object[] { Map("type", "task_started", "text", "{\"type\":\"task_started\",\"turn_id\":\"fake\"}") })));
                Equal(WorkState.Unknown, fixture.Reader.ReadSnapshot().State, "消息内容不作为任务状态");
                fixture.Append(Event(fixture.Now, "task_started", "next"), Event(fixture.Now.AddSeconds(1), "task_complete", "next"));
                Equal(WorkState.Idle, fixture.Reader.ReadSnapshot().State, "后续完成清除历史错误");
            }
            using (var fixture = new Fixture())
            {
                fixture.Append(Meta("test"), Count(fixture.Now.AddSeconds(-20), 100, 10, 0, 0), Usage(fixture.Now, "test", "r", "t", 200, 20));
                var result = fixture.Reader.ReadSnapshot();
                Equal(220L, result.Today.Total, "混合版本日志优先可信新记录");
                Equal(true, result.CoverageLimited, "混合版本历史缺口显式标注");
            }
        }

        static Dictionary<string, object> Meta(string id) { return Map("type", "session_meta", "payload", Map("id", id, "base_instructions", Map("text", "sensitive text must never be interpreted"))); }
        static Dictionary<string, object> Event(DateTime time, string kind, string turn) { return Map("timestamp", time.ToString("o", CultureInfo.InvariantCulture), "type", "event_msg", "payload", Map("type", kind, "turn_id", turn)); }
        static Dictionary<string, object> Count(DateTime time, long input, long output, long cached, long reasoning) { return Map("timestamp", time.ToString("o", CultureInfo.InvariantCulture), "type", "event_msg", "payload", Map("type", "token_count", "info", Map("total_token_usage", Totals(input, output, cached, reasoning)))); }
        static Dictionary<string, object> Usage(DateTime time, string thread, string response, string turn, long input, long output) { return Map("timestamp", time.ToString("o", CultureInfo.InvariantCulture), "type", "token_usage_record", "payload", Map("thread_id", thread, "response_id", response, "turn_id", turn, "usage", Totals(input, output, 0, 0))); }
        static Dictionary<string, object> Totals(long input, long output, long cached, long reasoning) { return Map("input_tokens", input, "output_tokens", output, "cached_input_tokens", cached, "reasoning_output_tokens", reasoning, "total_tokens", input + output); }
        static Dictionary<string, object> Map(params object[] pairs) { var result = new Dictionary<string, object>(); for (int i = 0; i < pairs.Length; i += 2) result.Add((string)pairs[i], pairs[i + 1]); return result; }
        static void Equal<T>(T expected, T actual, string message) { assertions++; if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException(message + ": expected " + expected + ", actual " + actual); }

        sealed class Fixture : IDisposable
        {
            readonly string home;
            public readonly string FilePath;
            public readonly TelemetryReader Reader;
            public DateTime Now = new DateTime(2026, 10, 13, 12, 0, 0, DateTimeKind.Local).ToUniversalTime();
            public Fixture()
            {
                home = Path.Combine(Path.GetTempPath(), "CodexPeekTelemetryTest-" + Guid.NewGuid().ToString("N"));
                string folder = Path.Combine(home, "sessions"); Directory.CreateDirectory(folder);
                FilePath = Path.Combine(folder, "rollout-test.jsonl"); File.WriteAllText(FilePath, "", new UTF8Encoding(false));
                File.SetLastWriteTimeUtc(FilePath, Now);
                Reader = new TelemetryReader(home, delegate { return Now; });
            }
            public void Append(params Dictionary<string, object>[] records)
            {
                var serializer = new JavaScriptSerializer(); var builder = new StringBuilder();
                foreach (var record in records) builder.Append(serializer.Serialize(record)).Append('\n');
                File.AppendAllText(FilePath, builder.ToString(), new UTF8Encoding(false)); File.SetLastWriteTimeUtc(FilePath, Now);
            }
            public void Dispose()
            {
                Reader.Dispose();
                string temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string resolvedHome = Path.GetFullPath(home);
                if (resolvedHome.StartsWith(temporaryRoot + "CodexPeekTelemetryTest-", StringComparison.OrdinalIgnoreCase) && Directory.Exists(resolvedHome)) Directory.Delete(resolvedHome, true);
            }
        }
    }
}
