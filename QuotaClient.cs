using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CodexPeek
{
    // 只读取 Codex 的现有登录，绝不刷新、修改或记录认证文件。
    public sealed class QuotaClient : IDisposable
    {
        private const string UsageEndpoint = "https://chatgpt.com/backend-api/wham/usage";
        private readonly string _authPath;
        private readonly HttpClient _http;
        private readonly SemaphoreSlim _refreshLock = new SemaphoreSlim(1, 1);
        private QuotaSnapshot _lastValid;
        private string _accountFingerprint;
        private bool _disposed;

        public QuotaClient(string codexHome = null)
            : this(codexHome, CreateHandler()) { }

        internal QuotaClient(string codexHome, HttpMessageHandler handler)
        {
            string home = codexHome;
            if (String.IsNullOrWhiteSpace(home)) home = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (String.IsNullOrWhiteSpace(home))
                home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            _authPath = Path.Combine(home, "auth.json");
            _http = new HttpClient(handler, true);
            _http.Timeout = TimeSpan.FromSeconds(15);
            _http.MaxResponseContentBufferSize = 1024 * 1024;
        }

        private static HttpMessageHandler CreateHandler()
        {
            return new HttpClientHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                // 使用 Windows 的系统代理设置；不把登录凭证发送到自定义额度服务。
                UseProxy = true
            };
        }

        public async Task<QuotaSnapshot> RefreshAsync(CancellationToken cancellationToken)
        {
            if (_disposed) throw new ObjectDisposedException("QuotaClient");
            await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Credentials credentials;
                try { credentials = ReadCredentials(); }
                catch (FileNotFoundException) { return ClearForLogin("未找到 Codex 登录，请先在 Codex 中登录 ChatGPT 账号"); }
                catch (DirectoryNotFoundException) { return ClearForLogin("未找到 Codex 登录，请先在 Codex 中登录 ChatGPT 账号"); }
                catch (UnauthorizedAccessException) { return Failure("无法读取 Codex 登录文件，请检查访问权限"); }
                catch (IOException) { return Failure("Codex 登录文件暂时无法读取"); }
                catch (ArgumentException) { return ClearForLogin("Codex 登录信息不可用，请重新登录"); }
                catch (InvalidOperationException) { return ClearForLogin("Codex 登录信息不可用，请重新登录"); }
                catch (FormatException) { return ClearForLogin("Codex 登录信息不可用，请重新登录"); }

                if (credentials == null) return ClearForLogin("需要 Codex 的 ChatGPT 登录；API Key 不提供订阅周限");
                if (!String.Equals(_accountFingerprint, credentials.Fingerprint, StringComparison.Ordinal))
                {
                    _lastValid = null;
                    _accountFingerprint = credentials.Fingerprint;
                }

                using (var request = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                    request.Headers.UserAgent.ParseAdd("CodexPeek/1.0");
                    if (!String.IsNullOrEmpty(credentials.AccountId))
                        request.Headers.Add("ChatGPT-Account-Id", credentials.AccountId);

                    try
                    {
                        using (HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false))
                        {
                            if (response.StatusCode == HttpStatusCode.Unauthorized)
                                return ClearForLogin("Codex 登录已过期，请在 Codex 中重新登录");
                            if (response.StatusCode == HttpStatusCode.Forbidden)
                                return Failure("额度查询被服务拒绝，请检查 Codex 登录或网络");
                            if ((int)response.StatusCode == 429)
                                return Failure("额度查询过于频繁，稍后自动重试");
                            if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400)
                                return Failure("额度服务要求跳转，已停止请求以保护登录凭证");
                            if (!response.IsSuccessStatusCode)
                                return Failure("额度服务暂不可用（HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + "）");

                            string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                            cancellationToken.ThrowIfCancellationRequested();
                            QuotaSnapshot snapshot = ParseUsageJson(json, DateTime.UtcNow);
                            _lastValid = Copy(snapshot);
                            return snapshot;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        return Failure("额度查询超时，稍后自动重试");
                    }
                    catch (HttpRequestException) { return Failure("暂时无法连接额度服务，请检查网络或 Windows 代理设置"); }
                    catch (IOException) { return Failure("读取额度响应失败，稍后自动重试"); }
                    catch (ArgumentException) { return Failure("额度响应无法识别，等待下次更新"); }
                    catch (InvalidOperationException) { return Failure("额度响应无法识别，等待下次更新"); }
                    catch (FormatException) { return Failure("额度响应无法识别，等待下次更新"); }
                }
            }
            finally { _refreshLock.Release(); }
        }

        private Credentials ReadCredentials()
        {
            string text;
            using (var stream = new FileStream(_authPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (stream.Length > 256 * 1024) throw new InvalidOperationException();
                using (var reader = new StreamReader(stream, Encoding.UTF8)) text = reader.ReadToEnd();
            }
            IDictionary<string, object> root = Deserialize(text);
            IDictionary<string, object> tokens = ObjectAt(root, "tokens");
            string token = StringAt(tokens, "access_token", "accessToken");
            if (String.IsNullOrWhiteSpace(token)) return null;
            if (!SafeHeader(token) || token.Length > 64 * 1024) throw new InvalidOperationException();
            string accountId = StringAt(tokens, "account_id", "accountId");
            IDictionary<string, object> claims = JwtClaims(token);
            IDictionary<string, object> authClaims = ObjectAt(claims, "https://api.openai.com/auth");
            if (String.IsNullOrWhiteSpace(accountId)) accountId = StringAt(authClaims, "chatgpt_account_id");
            if (!String.IsNullOrEmpty(accountId) && (!SafeHeader(accountId) || accountId.Length > 256))
                throw new InvalidOperationException();
            string subject = StringAt(claims, "sub");
            // 缺少账号标识时宁可在 token 变化后重读，也不沿用其他账号的额度。
            string identity = !String.IsNullOrEmpty(accountId) ? "account:" + accountId + "|subject:" + subject :
                !String.IsNullOrEmpty(subject) ? "subject:" + subject : "token:" + token;
            string fingerprint;
            using (SHA256 hash = SHA256.Create()) fingerprint = Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(identity)));
            return new Credentials { AccessToken = token, AccountId = accountId, Fingerprint = fingerprint };
        }

        private static IDictionary<string, object> JwtClaims(string token)
        {
            try
            {
                string[] parts = token.Split('.');
                if (parts.Length != 3) return null;
                string payload = parts[1].Replace('-', '+').Replace('_', '/');
                payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
                return Deserialize(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
            }
            catch (Exception exception)
            {
                if (exception is FormatException || exception is ArgumentException || exception is InvalidOperationException) return null;
                throw;
            }
        }

        private static bool SafeHeader(string value)
        {
            foreach (char character in value)
                if (character < 33 || character > 126) return false;
            return true;
        }

        internal static QuotaSnapshot ParseUsageJson(string json, DateTime nowUtc)
        {
            IDictionary<string, object> root = Deserialize(json);
            IDictionary<string, object> limits = ObjectAt(root, "rate_limit");
            if (limits == null)
            {
                IDictionary<string, object> byId = ObjectAt(root, "rateLimitsByLimitId", "rate_limits_by_limit_id");
                limits = ObjectAt(byId, "codex", "default", "codex-default");
                if (limits == null)
                {
                    IDictionary<string, object> candidate = ObjectAt(root, "rateLimits", "rate_limits");
                    string limitId = StringAt(candidate, "limitId", "limit_id");
                    if (String.IsNullOrEmpty(limitId) || IsGlobalLimit(limitId)) limits = candidate;
                }
            }
            if (limits == null && (ValueAt(root, "primary_window", "secondary_window", "primary", "secondary") != null)) limits = root;

            var snapshot = new QuotaSnapshot
            {
                FetchedAtUtc = nowUtc,
                Source = "ChatGPT 账号额度",
                Plan = SanitizeLabel(StringAt(root, "plan_type", "planType")),
                Message = "账号未返回周额度"
            };
            if (String.IsNullOrEmpty(snapshot.Plan)) snapshot.Plan = SanitizeLabel(StringAt(limits, "planType", "plan_type"));
            snapshot.Credits = ParseCredits(ObjectAt(root, "credits") ?? ObjectAt(limits, "credits"));
            AddWindow(snapshot, ObjectAt(limits, "primary_window", "primary"), nowUtc);
            AddWindow(snapshot, ObjectAt(limits, "secondary_window", "secondary"), nowUtc);
            foreach (RateWindow window in snapshot.Windows)
            {
                // 不能把 secondary 直接当成周限：不同套餐的 primary 也可能是周限。
                if (Math.Abs(window.WindowMinutes - 10080) <= 1)
                {
                    snapshot.RemainingPercent = 100.0 - window.UsedPercent;
                    snapshot.ResetsAtUtc = window.ResetsAtUtc;
                    snapshot.Message = "周额度已更新";
                    break;
                }
            }
            if (limits == null && String.IsNullOrEmpty(snapshot.Plan) && String.IsNullOrEmpty(snapshot.Credits) &&
                ValueAt(root, "rateLimitsByLimitId", "rate_limits_by_limit_id", "rateLimits", "rate_limits", "rate_limit") == null)
                throw new FormatException();
            return snapshot;
        }

        private static bool IsGlobalLimit(string limitId)
        {
            return String.Equals(limitId, "codex", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(limitId, "default", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(limitId, "codex-default", StringComparison.OrdinalIgnoreCase);
        }

        private static void AddWindow(QuotaSnapshot snapshot, IDictionary<string, object> data, DateTime nowUtc)
        {
            if (data == null) return;
            double? used = NumberAt(data, "used_percent", "usedPercent");
            if (!used.HasValue || used.Value < 0 || used.Value > 100) return;
            double? minutes = NumberAt(data, "windowDurationMins", "window_duration_mins", "window_minutes");
            if (!minutes.HasValue)
            {
                double? seconds = NumberAt(data, "limit_window_seconds", "window_duration_seconds");
                if (seconds.HasValue) minutes = seconds.Value / 60.0;
            }
            int duration = minutes.HasValue && minutes.Value > 0 && minutes.Value <= 525600 ? (int)Math.Round(minutes.Value) : 0;
            string name = Math.Abs(duration - 10080) <= 1 ? "周额度" : Math.Abs(duration - 300) <= 1 ? "5 小时额度" :
                duration > 0 ? "周期额度（" + duration.ToString(CultureInfo.InvariantCulture) + " 分钟）" : "周期未标明的额度";
            DateTime? resetsAt = ParseReset(ValueAt(data, "reset_at", "resetsAt", "resetAt"));
            if (!resetsAt.HasValue)
            {
                double? after = NumberAt(data, "reset_after_seconds", "resetAfterSeconds");
                if (after.HasValue && after.Value >= 0 && after.Value < 366 * 86400) resetsAt = nowUtc.AddSeconds(after.Value);
            }
            snapshot.Windows.Add(new RateWindow { Name = name, UsedPercent = used.Value, WindowMinutes = duration, ResetsAtUtc = resetsAt });
        }

        private static DateTime? ParseReset(object raw)
        {
            if (raw == null) return null;
            double numeric;
            string value = Convert.ToString(raw, CultureInfo.InvariantCulture);
            if (Double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out numeric) && !Double.IsNaN(numeric) && !Double.IsInfinity(numeric))
            {
                // 接受 Unix 秒，也兼容常见的毫秒表示；限制到合理日期，避免溢出。
                if (numeric > 100000000000) numeric /= 1000;
                if (numeric < 946684800 || numeric > 4102444800) return null;
                return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(numeric);
            }
            DateTime parsed;
            return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out parsed)
                ? (DateTime?)parsed.ToUniversalTime() : null;
        }

        private static string ParseCredits(IDictionary<string, object> credits)
        {
            if (credits == null) return "";
            if (String.Equals(StringAt(credits, "unlimited"), "true", StringComparison.OrdinalIgnoreCase)) return "不限量";
            decimal balance;
            if (Decimal.TryParse(StringAt(credits, "balance"), NumberStyles.Float, CultureInfo.InvariantCulture, out balance) && balance >= 0)
                return balance.ToString("0.##", CultureInfo.InvariantCulture);
            if (String.Equals(StringAt(credits, "has_credits", "hasCredits"), "false", StringComparison.OrdinalIgnoreCase)) return "0";
            return "";
        }

        private static string SanitizeLabel(string label)
        {
            if (String.IsNullOrWhiteSpace(label) || label.Length > 40) return "";
            foreach (char character in label)
                if (!Char.IsLetterOrDigit(character) && character != ' ' && character != '-' && character != '_') return "";
            return label;
        }

        private static IDictionary<string, object> Deserialize(string json)
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024, RecursionLimit = 48 };
            var result = serializer.DeserializeObject(json) as IDictionary<string, object>;
            if (result == null) throw new FormatException();
            return result;
        }

        private static object ValueAt(IDictionary<string, object> data, params string[] keys)
        {
            if (data == null) return null;
            foreach (string key in keys)
            {
                object value;
                if (data.TryGetValue(key, out value) && value != null) return value;
            }
            return null;
        }

        private static IDictionary<string, object> ObjectAt(IDictionary<string, object> data, params string[] keys)
        { return ValueAt(data, keys) as IDictionary<string, object>; }

        private static string StringAt(IDictionary<string, object> data, params string[] keys)
        { return Convert.ToString(ValueAt(data, keys), CultureInfo.InvariantCulture); }

        private static double? NumberAt(IDictionary<string, object> data, params string[] keys)
        {
            double value;
            return Double.TryParse(StringAt(data, keys), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && !Double.IsNaN(value) && !Double.IsInfinity(value)
                ? (double?)value : null;
        }

        private QuotaSnapshot ClearForLogin(string message)
        {
            _lastValid = null;
            _accountFingerprint = null;
            return new QuotaSnapshot { LoginRequired = true, Message = message, Source = "ChatGPT 账号额度" };
        }

        private QuotaSnapshot Failure(string message)
        {
            var result = _lastValid != null ? Copy(_lastValid) : new QuotaSnapshot { Source = "ChatGPT 账号额度" };
            result.IsStale = true;
            result.Message = message + (_lastValid != null ? "；显示上次成功结果" : "");
            return result;
        }

        private static QuotaSnapshot Copy(QuotaSnapshot source)
        {
            var result = new QuotaSnapshot
            {
                RemainingPercent = source.RemainingPercent, ResetsAtUtc = source.ResetsAtUtc, FetchedAtUtc = source.FetchedAtUtc,
                IsStale = source.IsStale, LoginRequired = source.LoginRequired, Message = source.Message,
                Source = source.Source, Plan = source.Plan, Credits = source.Credits
            };
            foreach (RateWindow window in source.Windows)
                result.Windows.Add(new RateWindow { Name = window.Name, UsedPercent = window.UsedPercent, WindowMinutes = window.WindowMinutes, ResetsAtUtc = window.ResetsAtUtc });
            return result;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _http.Dispose();
        }

        private sealed class Credentials
        {
            public string AccessToken;
            public string AccountId;
            public string Fingerprint;
        }
    }
}
