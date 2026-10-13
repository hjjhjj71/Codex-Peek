using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace CodexPeek
{
    // 仅读取白名单元数据；不保存提示词、消息内容、工具参数或登录信息。
    public sealed class TelemetryReader : IDisposable
    {
        const int MaximumFiles = 512;
        const int MaximumLineBytes = 256 * 1024;
        const int PerFileBudget = 2 * 1024 * 1024;
        const int PollBudget = 16 * 1024 * 1024;
        readonly string home;
        readonly Func<DateTime> utcNow;
        readonly object gate = new object();
        readonly Dictionary<string, LogState> logs = new Dictionary<string, LogState>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> responseOwners = new Dictionary<string, string>(StringComparer.Ordinal);
        DateTime nextDiscovery = DateTime.MinValue;
        bool discoveryLimited;
        bool disposed;

        public TelemetryReader(string codexHome = null) : this(codexHome, delegate { return DateTime.UtcNow; }) { }

        internal TelemetryReader(string codexHome, Func<DateTime> clock)
        {
            home = codexHome ?? Environment.GetEnvironmentVariable("CODEX_HOME");
            if (String.IsNullOrWhiteSpace(home)) home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            utcNow = clock;
        }

        public UsageSnapshot ReadSnapshot()
        {
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException("TelemetryReader");
                DateTime now = utcNow().ToUniversalTime();
                if (now >= nextDiscovery) Discover(now);
                int budget = PollBudget;
                foreach (LogState log in logs.Values.OrderByDescending(x => x.WriteTimeUtc))
                {
                    if (budget <= 0) break;
                    budget -= ReadNewLines(log, Math.Min(PerFileBudget, budget));
                }
                return BuildSnapshot(now);
            }
        }

        void Discover(DateTime now)
        {
            nextDiscovery = now.AddSeconds(60);
            discoveryLimited = false;
            var files = new List<FileInfo>();
            int directories = 0, examined = 0;
            DateTime cutoff = now.ToLocalTime().Date.AddDays(-6).ToUniversalTime();
            foreach (string root in new[] { Path.Combine(home, "sessions"), Path.Combine(home, "archived_sessions") })
            {
                if (!Directory.Exists(root)) continue;
                var pending = new Stack<string>();
                pending.Push(root);
                while (pending.Count != 0 && directories < 2048 && examined < 12000)
                {
                    string directory = pending.Pop();
                    directories++;
                    try
                    {
                        foreach (string child in Directory.EnumerateDirectories(directory).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                        {
                            if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) pending.Push(child);
                        }
                        foreach (string path in Directory.EnumerateFiles(directory, "rollout*.jsonl", SearchOption.TopDirectoryOnly))
                        {
                            if (++examined > 12000) break;
                            var info = new FileInfo(path);
                            // 旧目录里的会话也可能刚刚继续运行，因此依据修改时间筛选。
                            if (info.LastWriteTimeUtc >= cutoff) files.Add(info);
                        }
                    }
                    catch (IOException) { discoveryLimited = true; }
                    catch (UnauthorizedAccessException) { discoveryLimited = true; }
                }
                if (pending.Count != 0) discoveryLimited = true;
            }
            if (files.Count > MaximumFiles) discoveryLimited = true;
            var selected = files.OrderByDescending(x => x.LastWriteTimeUtc).Take(MaximumFiles).ToList();
            var selectedPaths = new HashSet<string>(selected.Select(x => x.FullName), StringComparer.OrdinalIgnoreCase);
            foreach (string old in logs.Keys.Where(x => !selectedPaths.Contains(x)).ToList())
            {
                RemoveResponseOwnership(logs[old]);
                logs.Remove(old);
            }
            foreach (FileInfo file in selected)
            {
                LogState log;
                if (!logs.TryGetValue(file.FullName, out log))
                {
                    log = new LogState(file.FullName);
                    logs.Add(file.FullName, log);
                }
                log.WriteTimeUtc = file.LastWriteTimeUtc;
            }
        }

        void RemoveResponseOwnership(LogState log)
        {
            foreach (string key in log.Responses.Keys)
            {
                string owner;
                if (responseOwners.TryGetValue(key, out owner) && String.Equals(owner, log.Path, StringComparison.OrdinalIgnoreCase)) responseOwners.Remove(key);
            }
        }

        int ReadNewLines(LogState log, int budget)
        {
            int read = 0;
            try
            {
                using (var stream = new FileStream(log.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (stream.Length < log.Offset)
                    {
                        RemoveResponseOwnership(log);
                        log.Reset();
                    }
                    stream.Position = log.Offset;
                    byte[] block = new byte[16384];
                    while (read < budget)
                    {
                        int count = stream.Read(block, 0, Math.Min(block.Length, budget - read));
                        if (count == 0) break;
                        read += count;
                        log.Offset += count;
                        for (int i = 0; i < count; i++)
                        {
                            byte value = block[i];
                            if (value == 10)
                            {
                                if (!log.OversizedLine && log.Line.Count != 0)
                                {
                                    string line = Encoding.UTF8.GetString(log.Line.ToArray());
                                    ParseLine(log, line);
                                }
                                else if (log.OversizedLine) log.Limited = true;
                                log.Line.Clear();
                                log.OversizedLine = false;
                            }
                            else if (!log.OversizedLine)
                            {
                                if (log.Line.Count < MaximumLineBytes) log.Line.Add(value);
                                else { log.Line.Clear(); log.OversizedLine = true; }
                            }
                        }
                    }
                    log.CatchingUp = stream.Length > log.Offset;
                    log.ReadFailed = false;
                }
            }
            catch (IOException) { log.ReadFailed = true; }
            catch (UnauthorizedAccessException) { log.ReadFailed = true; }
            return read;
        }

        void ParseLine(LogState log, string line)
        {
            Dictionary<string, object> record;
            try { record = MetadataJson.Read(line); }
            catch (FormatException) { log.Limited = true; return; }
            string type = Text(record, "type");
            if (type != "session_meta" && type != "turn_context" && type != "token_usage_record" && type != "event_msg") return;
            var payload = Object(record, "payload");
            if (payload == null) return;
            if (type == "session_meta")
            {
                if (String.IsNullOrEmpty(log.Id)) log.Id = Text(payload, "id");
                if (!String.IsNullOrEmpty(Text(payload, "forked_from_id"))) log.IsFork = true;
                return;
            }
            DateTime? stamp = Timestamp(Value(record, "timestamp"));
            if (!stamp.HasValue) { log.Limited = true; return; }
            DateTime time = stamp.Value;
            if (type == "token_usage_record")
            {
                string ownerThread = Text(payload, "thread_id");
                // 新版日志能明确区分 fork 回放与本会话消耗。
                if (!String.IsNullOrEmpty(log.Id) && !String.IsNullOrEmpty(ownerThread) && ownerThread != log.Id) return;
                ReadModernUsage(log, payload, time);
            }
            if (type == "turn_context")
            {
                string model = Text(payload, "model");
                if (!String.IsNullOrEmpty(model)) log.Model = model;
                return;
            }
            if (!log.LastEventUtc.HasValue || time > log.LastEventUtc.Value) log.LastEventUtc = time;
            if (type != "event_msg")
            {
                TouchCurrent(log, time);
                return;
            }
            string eventType = Text(payload, "type");
            string turnId = Text(payload, "turn_id");
            if (String.IsNullOrEmpty(turnId)) turnId = log.CurrentTurn;
            if (eventType == "task_started" || eventType == "turn_started")
            {
                if (String.IsNullOrEmpty(turnId)) turnId = "anonymous-" + time.Ticks.ToString(CultureInfo.InvariantCulture);
                log.CurrentTurn = turnId;
                Turn turn = GetTurn(log, turnId);
                turn.StartUtc = Timestamp(Value(payload, "started_at")) ?? time;
                turn.LastActivityUtc = time;
                turn.EndUtc = null;
                turn.Running = true;
                log.AttentionUtc = null;
                log.LastTerminal = WorkState.Unknown;
            }
            else if (eventType == "task_complete" || eventType == "turn_complete" || eventType == "turn_completed")
            {
                Turn turn = GetTurn(log, turnId);
                turn.EndUtc = Timestamp(Value(payload, "completed_at")) ?? time;
                DateTime? explicitStart = Timestamp(Value(payload, "started_at"));
                if (explicitStart.HasValue) turn.StartUtc = explicitStart;
                double duration = Number(payload, "duration_ms");
                if (duration > 0) turn.DurationSeconds = duration / 1000.0;
                turn.Running = false;
                turn.LastActivityUtc = time;
                log.AttentionUtc = null;
                log.LastTerminal = WorkState.Idle;
            }
            else if (eventType == "turn_aborted" || eventType == "task_cancelled" || eventType == "task_interrupted")
            {
                GetTurn(log, turnId).Running = false;
                log.AttentionUtc = null;
                log.LastTerminal = WorkState.Idle;
            }
            else if (eventType == "error" || eventType == "stream_error" || eventType == "turn_failed" || eventType == "task_failed" || eventType == "approval_required" || eventType == "exec_approval_request" || eventType == "apply_patch_approval_request" || eventType == "request_user_input")
            {
                log.AttentionUtc = time;
                log.LastTerminal = WorkState.Unknown;
                if (eventType == "turn_failed" || eventType == "task_failed") GetTurn(log, turnId).Running = false;
            }
            else if (eventType == "approval_resolved" || eventType == "exec_approval_response" || eventType == "apply_patch_approval_response" || eventType == "user_input_response")
            {
                log.AttentionUtc = null;
                TouchCurrent(log, time);
            }
            else
            {
                if (eventType == "token_count") ReadLegacyUsage(log, payload, time);
                TouchCurrent(log, time);
            }
        }

        void ReadModernUsage(LogState log, Dictionary<string, object> payload, DateTime time)
        {
            var values = Object(payload, "usage");
            if (values == null) return;
            string response = Text(payload, "response_id");
            if (String.IsNullOrEmpty(response))
            {
                // 没有请求标识时，以累计值作幂等回退，避免重复发送的 token_count 被累加。
                var cumulative = Object(payload, "thread_token_usage");
                if (cumulative != null) AddLegacyCumulative(log, ReadTotals(cumulative), time, Text(payload, "turn_id"));
                log.Limited = true;
                return;
            }
            string existingOwner;
            if (responseOwners.TryGetValue(response, out existingOwner) && !String.Equals(existingOwner, log.Path, StringComparison.OrdinalIgnoreCase)) return;
            if (!responseOwners.ContainsKey(response))
            {
                if (responseOwners.Count >= 100000) { log.Limited = true; return; }
                responseOwners.Add(response, log.Path);
            }
            TokenTotals current = ReadTotals(values), previous;
            if (!log.Responses.TryGetValue(response, out previous)) previous = new TokenTotals();
            TokenTotals delta = PositiveDelta(current, previous);
            log.Responses[response] = Maximum(current, previous);
            log.HasModern = true;
            if (!log.FirstModernUtc.HasValue) log.FirstModernUtc = time;
            AddToDay(log.ModernDays, time, delta);
            string turnId = Text(payload, "turn_id");
            if (String.IsNullOrEmpty(turnId)) turnId = log.CurrentTurn;
            Turn turn = GetTurn(log, turnId);
            turn.ModernOutput += delta.Output;
            turn.LastActivityUtc = time;
        }

        void ReadLegacyUsage(LogState log, Dictionary<string, object> payload, DateTime time)
        {
            var info = Object(payload, "info");
            var cumulative = Object(info, "total_token_usage");
            if (cumulative == null) return;
            if (log.IsFork) { log.LegacyForkOmitted = true; return; }
            AddLegacyCumulative(log, ReadTotals(cumulative), time, log.CurrentTurn);
        }

        void AddLegacyCumulative(LogState log, TokenTotals current, DateTime time, string turnId)
        {
            TokenTotals delta = PositiveDelta(current, log.LegacyHighWater);
            // 累计值回退可能来自压缩/重放；保留高水位避免把历史再次计入。
            if (current.Total < log.LegacyHighWater.Total) log.Limited = true;
            log.LegacyHighWater = Maximum(current, log.LegacyHighWater);
            if (!log.FirstLegacyUtc.HasValue && delta.Total > 0) log.FirstLegacyUtc = time;
            AddToDay(log.LegacyDays, time, delta);
            GetTurn(log, turnId).LegacyOutput += delta.Output;
        }

        static void TouchCurrent(LogState log, DateTime time)
        {
            Turn turn;
            if (!String.IsNullOrEmpty(log.CurrentTurn) && log.Turns.TryGetValue(log.CurrentTurn, out turn) && turn.Running) turn.LastActivityUtc = time;
        }

        static Turn GetTurn(LogState log, string id)
        {
            id = String.IsNullOrEmpty(id) ? "unattributed" : id;
            Turn turn;
            if (!log.Turns.TryGetValue(id, out turn))
            {
                if (log.Turns.Count >= 10000)
                {
                    string oldest = log.Turns.OrderBy(x => x.Value.LastActivityUtc).First().Key;
                    log.Turns.Remove(oldest);
                    log.Limited = true;
                }
                turn = new Turn();
                log.Turns.Add(id, turn);
            }
            return turn;
        }

        UsageSnapshot BuildSnapshot(DateTime now)
        {
            var result = new UsageSnapshot();
            result.SpeedLabel = "最近一轮估算速度（含等待）";
            result.CoverageLimited = discoveryLimited;
            DateTime today = now.ToLocalTime().Date, firstDay = today.AddDays(-6);
            var countedSessions = new HashSet<string>(StringComparer.Ordinal);
            bool hasAttention = false;
            WorkState latestState = WorkState.Unknown;
            DateTime? latestStateTime = null;
            foreach (LogState log in logs.Values.OrderByDescending(x => x.LastEventUtc))
            {
                string id = String.IsNullOrEmpty(log.Id) ? log.Path : log.Id;
                if (!countedSessions.Add(id)) continue;
                var days = log.HasModern ? log.ModernDays : log.LegacyDays;
                long sessionTotal = 0;
                bool contributedToday = false, contributedWeek = false;
                foreach (var day in days)
                {
                    if (day.Key >= firstDay && day.Key <= today)
                    {
                        Add(result.SevenDays, day.Value);
                        sessionTotal += day.Value.Total;
                        contributedWeek |= day.Value.Total > 0;
                    }
                    if (day.Key == today) { Add(result.Today, day.Value); contributedToday |= day.Value.Total > 0; }
                }
                if (contributedToday) result.Today.Sessions++;
                if (contributedWeek) result.SevenDays.Sessions++;
                bool mixedVersionGap = log.HasModern && log.FirstLegacyUtc.HasValue && log.FirstModernUtc.HasValue && log.FirstLegacyUtc.Value < log.FirstModernUtc.Value;
                result.CoverageLimited |= log.Limited || log.CatchingUp || log.ReadFailed || mixedVersionGap || (log.LegacyForkOmitted && !log.HasModern);
                WorkState state = StateFor(log, now);
                if (state == WorkState.Working) result.ActiveCount++;
                hasAttention |= state == WorkState.Attention;
                // 已搁置的旧会话不能覆盖刚结束的新会话状态。
                if (log.LastEventUtc.HasValue && (!latestStateTime.HasValue || log.LastEventUtc > latestStateTime)) { latestState = state; latestStateTime = log.LastEventUtc; }
                if (log.LastEventUtc.HasValue && (!result.LastEventUtc.HasValue || log.LastEventUtc > result.LastEventUtc)) result.LastEventUtc = log.LastEventUtc;
                double? speed = null;
                DateTime? speedTime = null;
                foreach (Turn turn in log.Turns.Values)
                {
                    long output = log.HasModern ? turn.ModernOutput : turn.LegacyOutput;
                    if (!turn.EndUtc.HasValue || output <= 0) continue;
                    double seconds = turn.DurationSeconds ?? (turn.StartUtc.HasValue ? (turn.EndUtc.Value - turn.StartUtc.Value).TotalSeconds : 0);
                    if (seconds <= 0 || turn.EndUtc.Value > now.AddMinutes(1)) continue;
                    if (!speedTime.HasValue || turn.EndUtc > speedTime) { speed = output / seconds; speedTime = turn.EndUtc; }
                }
                if (speedTime.HasValue && (!result.SpeedObservedAtUtc.HasValue || speedTime > result.SpeedObservedAtUtc))
                {
                    result.TokensPerSecond = speed;
                    result.SpeedObservedAtUtc = speedTime;
                }
                if (result.Sessions.Count < 30 && log.LastEventUtc.HasValue)
                    result.Sessions.Add(new SessionSummary { Id = String.IsNullOrEmpty(log.Id) ? "本机会话" : log.Id, LastEventUtc = log.LastEventUtc.Value, Model = log.Model, Tokens = sessionTotal, State = state, TokensPerSecond = speed });
            }
            result.State = hasAttention ? WorkState.Attention : result.ActiveCount > 0 ? WorkState.Working : latestState;
            result.StatusDetail = result.State == WorkState.Working ? "本地日志显示正在工作（" + result.ActiveCount.ToString(CultureInfo.InvariantCulture) + " 个会话）" : result.State == WorkState.Attention ? "最近出现错误或等待确认，请查看 Codex" : result.State == WorkState.Idle ? "最近任务已完成或已中断" : logs.Count == 0 ? "未发现最近 7 天可读取的 Codex 会话日志" : "暂无近期活动，无法确认是否仍在工作";
            result.CoverageDescription = result.CoverageLimited ? "本机日志统计不完整：正在补读、扫描受限或部分记录无法可靠归属；近 7 天含今天" : "本机日志，近 7 个自然日（含今天）；缓存包含在输入中，推理包含在输出中";
            return result;
        }

        static WorkState StateFor(LogState log, DateTime now)
        {
            if (log.ReadFailed || log.CatchingUp) return WorkState.Unknown;
            if (log.AttentionUtc.HasValue && (now - log.AttentionUtc.Value).TotalSeconds >= -5 && (now - log.AttentionUtc.Value).TotalSeconds <= 180) return WorkState.Attention;
            bool unfinished = false;
            foreach (Turn turn in log.Turns.Values)
            {
                if (!turn.Running) continue;
                unfinished = true;
                double age = (now - turn.LastActivityUtc).TotalSeconds;
                if (age >= -5 && age <= 180) return WorkState.Working;
            }
            if (unfinished || log.AttentionUtc.HasValue) return WorkState.Unknown;
            return log.LastTerminal;
        }

        static TokenTotals ReadTotals(Dictionary<string, object> value)
        {
            long input = Long(value, "input_tokens"), output = Long(value, "output_tokens");
            return new TokenTotals { Input = input, CachedInput = Long(value, "cached_input_tokens"), Output = output, Reasoning = Long(value, "reasoning_output_tokens"), Total = Value(value, "total_tokens") == null ? input + output : Long(value, "total_tokens") };
        }
        static TokenTotals PositiveDelta(TokenTotals a, TokenTotals b) { return new TokenTotals { Input = Math.Max(0, a.Input - b.Input), CachedInput = Math.Max(0, a.CachedInput - b.CachedInput), Output = Math.Max(0, a.Output - b.Output), Reasoning = Math.Max(0, a.Reasoning - b.Reasoning), Total = Math.Max(0, a.Total - b.Total) }; }
        static TokenTotals Maximum(TokenTotals a, TokenTotals b) { return new TokenTotals { Input = Math.Max(a.Input, b.Input), CachedInput = Math.Max(a.CachedInput, b.CachedInput), Output = Math.Max(a.Output, b.Output), Reasoning = Math.Max(a.Reasoning, b.Reasoning), Total = Math.Max(a.Total, b.Total) }; }
        static void Add(TokenTotals target, TokenTotals value) { target.Input += value.Input; target.CachedInput += value.CachedInput; target.Output += value.Output; target.Reasoning += value.Reasoning; target.Total += value.Total; }
        static void AddToDay(Dictionary<DateTime, TokenTotals> days, DateTime time, TokenTotals value) { DateTime day = time.ToLocalTime().Date; TokenTotals total; if (!days.TryGetValue(day, out total)) { total = new TokenTotals(); days.Add(day, total); } Add(total, value); }
        static object Value(Dictionary<string, object> d, string key) { object value; return d != null && d.TryGetValue(key, out value) ? value : null; }
        static string Text(Dictionary<string, object> d, string key) { return Value(d, key) as string ?? ""; }
        static Dictionary<string, object> Object(Dictionary<string, object> d, string key) { return Value(d, key) as Dictionary<string, object>; }
        static double Number(Dictionary<string, object> d, string key) { object v = Value(d, key); return v is double ? (double)v : 0; }
        static long Long(Dictionary<string, object> d, string key) { double n = Number(d, key); return n >= 0 && n < Int64.MaxValue ? (long)n : 0; }
        static DateTime? Timestamp(object value)
        {
            DateTime result;
            if (value is string && DateTime.TryParse((string)value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out result)) return result;
            if (value is double)
            {
                double n = (double)value;
                try { return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(n > 100000000000 ? n : n * 1000); }
                catch (ArgumentOutOfRangeException) { return null; }
            }
            return null;
        }

        public void Dispose() { lock (gate) { disposed = true; logs.Clear(); responseOwners.Clear(); } }

        sealed class Turn
        {
            public DateTime? StartUtc, EndUtc;
            public DateTime LastActivityUtc;
            public double? DurationSeconds;
            public bool Running;
            public long ModernOutput, LegacyOutput;
        }
        sealed class LogState
        {
            public readonly string Path;
            public string Id = "", Model = "", CurrentTurn = "";
            public DateTime WriteTimeUtc;
            public DateTime? LastEventUtc, AttentionUtc, FirstModernUtc, FirstLegacyUtc;
            public long Offset;
            public bool IsFork, HasModern, Limited, CatchingUp, ReadFailed, OversizedLine, LegacyForkOmitted;
            public WorkState LastTerminal = WorkState.Unknown;
            public List<byte> Line = new List<byte>();
            public Dictionary<DateTime, TokenTotals> ModernDays = new Dictionary<DateTime, TokenTotals>();
            public Dictionary<DateTime, TokenTotals> LegacyDays = new Dictionary<DateTime, TokenTotals>();
            public Dictionary<string, TokenTotals> Responses = new Dictionary<string, TokenTotals>(StringComparer.Ordinal);
            public Dictionary<string, Turn> Turns = new Dictionary<string, Turn>(StringComparer.Ordinal);
            public TokenTotals LegacyHighWater = new TokenTotals();
            public LogState(string path) { Path = path; SetFileId(); }
            void SetFileId() { string name = System.IO.Path.GetFileNameWithoutExtension(Path); Guid guid; if (name.Length >= 36 && Guid.TryParse(name.Substring(name.Length - 36), out guid)) Id = guid.ToString(); }
            public void Reset()
            {
                Id = Model = CurrentTurn = ""; SetFileId(); Offset = 0;
                LastEventUtc = AttentionUtc = FirstModernUtc = FirstLegacyUtc = null;
                IsFork = HasModern = Limited = CatchingUp = ReadFailed = OversizedLine = LegacyForkOmitted = false;
                LastTerminal = WorkState.Unknown; Line.Clear(); ModernDays.Clear(); LegacyDays.Clear(); Responses.Clear(); Turns.Clear(); LegacyHighWater = new TokenTotals();
            }
        }

        // 对未知字段直接跳过字节对应的字符，不把对话字段反序列化为对象。
        sealed class MetadataJson
        {
            static readonly HashSet<string> Allowed = new HashSet<string>(new[] { "timestamp", "type", "payload", "id", "thread_id", "session_id", "turn_id", "root_turn_id", "response_id", "forked_from_id", "model", "started_at", "completed_at", "duration_ms", "usage", "thread_token_usage", "info", "total_token_usage", "input_tokens", "cached_input_tokens", "output_tokens", "reasoning_output_tokens", "total_tokens" }, StringComparer.Ordinal);
            readonly string text;
            int position;
            MetadataJson(string source) { text = source; }
            public static Dictionary<string, object> Read(string source) { var parser = new MetadataJson(source); parser.White(); return parser.ReadObject(0); }
            void White() { while (position < text.Length && Char.IsWhiteSpace(text[position])) position++; }
            void Expect(char c) { White(); if (position >= text.Length || text[position++] != c) throw new FormatException("Invalid metadata JSON"); }
            Dictionary<string, object> ReadObject(int depth)
            {
                if (depth > 24) throw new FormatException("Metadata nesting limit");
                Expect('{'); var map = new Dictionary<string, object>(StringComparer.Ordinal);
                White(); if (position < text.Length && text[position] == '}') { position++; return map; }
                while (position < text.Length)
                {
                    string key = ReadString(); Expect(':'); White();
                    if (Allowed.Contains(key)) map[key] = ReadValue(depth + 1); else SkipValue();
                    White(); if (position >= text.Length) break;
                    char separator = text[position++]; if (separator == '}') return map; if (separator != ',') break;
                }
                throw new FormatException("Incomplete metadata JSON");
            }
            object ReadValue(int depth)
            {
                White(); if (position >= text.Length) throw new FormatException("Missing value");
                char c = text[position]; if (c == '{') return ReadObject(depth); if (c == '"') return ReadString();
                if (c == '[') { SkipValue(); return null; }
                int start = position; while (position < text.Length && ",]} \t\r\n".IndexOf(text[position]) < 0) position++;
                double value; string token = text.Substring(start, position - start);
                return Double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? (object)value : null;
            }
            string ReadString()
            {
                Expect('"'); var result = new StringBuilder();
                while (position < text.Length)
                {
                    char c = text[position++]; if (c == '"') return result.ToString();
                    if (c == '\\')
                    {
                        if (position >= text.Length) break; c = text[position++];
                        if (c == 'u') { if (position + 4 > text.Length) break; int code; if (!Int32.TryParse(text.Substring(position, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code)) break; position += 4; c = (char)code; }
                        else if (c == 'n') c = '\n'; else if (c == 'r') c = '\r'; else if (c == 't') c = '\t'; else if (c == 'b') c = '\b'; else if (c == 'f') c = '\f';
                    }
                    if (result.Length >= 4096) throw new FormatException("Metadata string limit"); result.Append(c);
                }
                throw new FormatException("Invalid metadata string");
            }
            void SkipValue()
            {
                White(); int level = 0; bool inString = false, escaped = false;
                while (position < text.Length)
                {
                    char c = text[position];
                    if (inString) { position++; if (escaped) escaped = false; else if (c == '\\') escaped = true; else if (c == '"') inString = false; continue; }
                    if (c == '"') { inString = true; position++; continue; }
                    if (c == '{' || c == '[') { level++; position++; continue; }
                    if (c == '}' || c == ']') { if (level == 0) return; level--; position++; if (level == 0) return; continue; }
                    if (c == ',' && level == 0) return;
                    position++;
                }
                if (inString || level != 0) throw new FormatException("Incomplete ignored value");
            }
        }
    }
}
