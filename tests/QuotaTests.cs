using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace CodexPeek.Tests
{
    public static class QuotaTests
    {
        private static int _checks;
        private static readonly DateTime Now = new DateTime(2026, 10, 13, 8, 0, 0, DateTimeKind.Utc);
        private const string ValidUsage = "{\"plan_type\":\"plus\",\"rate_limit\":{\"primary_window\":{\"used_percent\":12,\"limit_window_seconds\":18000},\"secondary_window\":{\"used_percent\":34,\"limit_window_seconds\":604800,\"reset_at\":1792000000}},\"credits\":{\"has_credits\":false,\"unlimited\":false}}";

        public static int Run()
        {
            _checks = 0;
            ParserFixtures();
            ClientFixtures().GetAwaiter().GetResult();
            return _checks;
        }

        private static void ParserFixtures()
        {
            QuotaSnapshot quota = QuotaClient.ParseUsageJson(ValidUsage, Now);
            Check(quota.RemainingPercent == 66, "normal weekly remaining");
            Check(quota.Windows.Count == 2 && quota.Windows[0].WindowMinutes == 300, "both windows retained");
            Check(quota.Plan == "plus" && quota.Credits == "0", "safe metadata");
            Check(quota.ResetsAtUtc.HasValue && quota.ResetsAtUtc.Value.Kind == DateTimeKind.Utc, "UTC reset");
            Check(!quota.IsStale && quota.FetchedAtUtc == Now, "fresh timestamp");

            quota = QuotaClient.ParseUsageJson("{\"rate_limit\":{\"primary_window\":{\"used_percent\":\"7.5\",\"limit_window_seconds\":\"604800\",\"reset_after_seconds\":\"300\"},\"secondary_window\":{\"used_percent\":95,\"limit_window_seconds\":18000}}}", Now);
            Check(quota.RemainingPercent == 92.5, "primary may be weekly; numeric strings");
            Check(quota.ResetsAtUtc == Now.AddSeconds(300), "relative reset");

            quota = QuotaClient.ParseUsageJson("{\"rate_limit\":{\"secondary_window\":{\"used_percent\":50,\"limit_window_seconds\":18000}}}", Now);
            Check(!quota.RemainingPercent.HasValue, "secondary must not imply weekly");
            quota = QuotaClient.ParseUsageJson("{\"rate_limit\":{\"secondary_window\":{\"used_percent\":50}}}", Now);
            Check(!quota.RemainingPercent.HasValue && quota.Windows.Count == 1, "unknown period cannot imply weekly");

            quota = QuotaClient.ParseUsageJson("{\"rateLimitsByLimitId\":{\"codex-spark\":{\"secondary\":{\"usedPercent\":99,\"windowDurationMins\":10080}},\"codex\":{\"planType\":\"pro\",\"secondary\":{\"usedPercent\":\"20\",\"windowDurationMins\":\"10080\",\"resetsAt\":\"1792000000000\"},\"credits\":{\"balance\":\"12.50\"}}}}", Now);
            Check(quota.RemainingPercent == 80 && quota.Plan == "pro", "global Codex selected instead of Spark");
            Check(quota.Credits == "12.5" && quota.ResetsAtUtc.HasValue, "credit number and millisecond reset");
            quota = QuotaClient.ParseUsageJson("{\"rateLimitsByLimitId\":{\"codex-spark\":{\"secondary\":{\"usedPercent\":99,\"windowDurationMins\":10080}}}}", Now);
            Check(!quota.RemainingPercent.HasValue, "model-only limits cannot become global weekly");

            quota = QuotaClient.ParseUsageJson("{\"rateLimits\":{\"limitId\":\"codex-spark\",\"secondary\":{\"usedPercent\":99,\"windowDurationMins\":10080}}}", Now);
            Check(!quota.RemainingPercent.HasValue, "legacy model limit rejected");
            quota = QuotaClient.ParseUsageJson("{\"rateLimits\":{\"secondary\":{\"usedPercent\":100,\"windowDurationMins\":10080,\"resetsAt\":\"2026-10-20T00:00:00Z\"}}}", Now);
            Check(quota.RemainingPercent == 0 && quota.ResetsAtUtc == new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc), "legacy shape and zero remaining");

            quota = QuotaClient.ParseUsageJson("{\"rate_limit\":{\"secondary_window\":{\"used_percent\":\"NaN\",\"limit_window_seconds\":604800}},\"plan_type\":\"plus\\r\\nsecret\",\"credits\":{\"balance\":\"not a number\"}}", Now);
            Check(!quota.RemainingPercent.HasValue && quota.Plan == "" && quota.Credits == "", "invalid values suppressed");
            quota = QuotaClient.ParseUsageJson("{\"rate_limit\":{\"secondary_window\":{\"used_percent\":-1,\"limit_window_seconds\":604800}},\"credits\":{\"unlimited\":true}}", Now);
            Check(!quota.RemainingPercent.HasValue && quota.Credits == "不限量", "negative percent rejected");
            quota = QuotaClient.ParseUsageJson("{\"plan_type\":\"free\",\"rate_limit\":null}", Now);
            Check(!quota.RemainingPercent.HasValue, "missing weekly is represented as unknown");

            bool rejected = false;
            try { QuotaClient.ParseUsageJson("{\"error\":\"arbitrary server content\"}", Now); }
            catch (FormatException) { rejected = true; }
            Check(rejected, "unrecognized body is not accepted as successful quota");
        }

        private static async Task ClientFixtures()
        {
            string directory = Path.Combine(Path.GetTempPath(), "CodexPeekQuotaTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string authPath = Path.Combine(directory, "auth.json");
            try
            {
                WriteAuth(authPath, "account-one");
                var handler = new StubHandler { Body = ValidUsage };
                using (var client = new QuotaClient(directory, handler))
                {
                    QuotaSnapshot quota = await client.RefreshAsync(CancellationToken.None);
                    Check(quota.RemainingPercent == 66, "client successful query");
                    Check(handler.LastUri == "https://chatgpt.com/backend-api/wham/usage" && handler.LastMethod == "GET", "only fixed GET endpoint");
                    Check(handler.HasBearer && handler.HasAccountHeader, "existing login forwarded to fixed endpoint");
                    Check(File.ReadAllText(authPath).Contains("fixture-token"), "authentication file preserved");
                    quota.Windows.Clear();
                    handler.Status = HttpStatusCode.ServiceUnavailable;
                    quota = await client.RefreshAsync(CancellationToken.None);
                    Check(quota.IsStale && quota.RemainingPercent == 66 && quota.Windows.Count == 2, "failure retains isolated same-account copy");
                    Check(!quota.Message.Contains("arbitrary server content"), "response content is not echoed");

                    WriteAuth(authPath, "account-two");
                    quota = await client.RefreshAsync(CancellationToken.None);
                    Check(quota.IsStale && !quota.RemainingPercent.HasValue, "account switch invalidates prior quota");

                    handler.Status = HttpStatusCode.OK;
                    quota = await client.RefreshAsync(CancellationToken.None);
                    Check(quota.RemainingPercent == 66, "new account refresh");
                    handler.Status = HttpStatusCode.Unauthorized;
                    quota = await client.RefreshAsync(CancellationToken.None);
                    Check(quota.LoginRequired && !quota.RemainingPercent.HasValue, "401 clears old quota");

                    handler.Status = HttpStatusCode.OK;
                    await client.RefreshAsync(CancellationToken.None);
                    handler.Status = HttpStatusCode.Found;
                    quota = await client.RefreshAsync(CancellationToken.None);
                    Check(quota.IsStale && quota.RemainingPercent == 66, "redirect is rejected");

                    handler.Status = HttpStatusCode.OK;
                    handler.Body = "not json and must never appear in the message";
                    quota = await client.RefreshAsync(CancellationToken.None);
                    Check(quota.IsStale && !quota.Message.Contains("not json"), "malformed response retains cache without exposing response");

                    handler.ThrowTimeout = true;
                    quota = await client.RefreshAsync(CancellationToken.None);
                    Check(quota.IsStale && quota.Message.Contains("超时"), "timeout is represented as stale");
                    handler.ThrowTimeout = false;

                    bool canceled = false;
                    using (var cancellation = new CancellationTokenSource())
                    {
                        cancellation.Cancel();
                        try { await client.RefreshAsync(cancellation.Token); }
                        catch (OperationCanceledException) { canceled = true; }
                    }
                    Check(canceled, "caller cancellation propagates");

                    File.Delete(authPath);
                    quota = await client.RefreshAsync(CancellationToken.None);
                    Check(quota.LoginRequired && !quota.RemainingPercent.HasValue, "logout clears cached account data");
                    File.WriteAllText(authPath, "{\"OPENAI_API_KEY\":\"fixture-api-key\"}");
                    quota = await client.RefreshAsync(CancellationToken.None);
                    Check(quota.LoginRequired && !quota.RemainingPercent.HasValue, "API key cannot be mistaken for subscription login");
                }
            }
            finally
            {
                if (File.Exists(authPath)) File.Delete(authPath);
                Directory.Delete(directory);
            }
        }

        private static void WriteAuth(string path, string account)
        { File.WriteAllText(path, "{\"tokens\":{\"access_token\":\"fixture-token\",\"account_id\":\"" + account + "\"}}"); }

        private static void Check(bool condition, string name)
        {
            _checks++;
            if (!condition) throw new InvalidOperationException("Quota fixture failed: " + name);
        }

        private sealed class StubHandler : HttpMessageHandler
        {
            public string Body;
            public HttpStatusCode Status = HttpStatusCode.OK;
            public string LastUri;
            public string LastMethod;
            public bool HasBearer;
            public bool HasAccountHeader;
            public bool ThrowTimeout;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                LastUri = request.RequestUri.AbsoluteUri;
                LastMethod = request.Method.Method;
                HasBearer = request.Headers.Authorization != null && request.Headers.Authorization.Scheme == "Bearer";
                HasAccountHeader = request.Headers.Contains("ChatGPT-Account-Id");
                if (ThrowTimeout) throw new TaskCanceledException();
                return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Body ?? "{}") });
            }
        }
    }
}
