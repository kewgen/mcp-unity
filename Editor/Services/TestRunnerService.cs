using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using McpUnity.Unity;
using McpUnity.Utils;
using UnityEngine;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using Newtonsoft.Json.Linq;

namespace McpUnity.Services
{
    /// <summary>
    /// Service for accessing Unity Test Runner functionality
    /// Implements ICallbacks for TestRunnerApi.
    /// </summary>
    public class TestRunnerService : ITestRunnerService, ICallbacks
    {
        private readonly TestRunnerApi _testRunnerApi;
        /// <summary>
        /// Сериализация run_tests: параллельные MCP-запросы иначе перезаписывают один общий _tcs и зависают без ответа.
        /// </summary>
        private readonly SemaphoreSlim _runGate = new SemaphoreSlim(1, 1);
        private TaskCompletionSource<JObject> _tcs;
        /// <summary>
        /// Unity-specific (парк, 03.09): прогон, брошенный по таймауту, продолжает жить в Unity —
        /// его RunFinished приходит позже. Прежний код при таймауте не обнулял _tcs, поэтому
        /// поздний колбэк резолвил TaskCompletionSource УЖЕ СЛЕДУЮЩЕГО клиента и перезаписывал
        /// attraction-editmode.xml чужими результатами: 02.09 так прогон теста корпуса получил
        /// ответ соседнего прогона и «падал» на несобранной чужой сборке. Флаг гасит ровно один
        /// поздний RunFinished — тот, что относится к брошенному прогону.
        /// </summary>
        private bool _abandonedRunPending;
        private bool _returnOnlyFailures;
        private bool _returnWithLogs;
        private List<ITestResultAdaptor> _results;

        /// <summary>
        /// Constructor
        /// </summary>
        public TestRunnerService()
        {
            _testRunnerApi = ScriptableObject.CreateInstance<TestRunnerApi>();
            _results = new List<ITestResultAdaptor>();
            _testRunnerApi.RegisterCallbacks(this);
        }

        /// <summary>
        /// Async retrieval of all tests using TestRunnerApi callbacks
        /// </summary>
        /// <param name="testModeFilter">Optional test mode filter (EditMode, PlayMode, or empty for all)</param>
        /// <returns>List of test items matching the specified test mode, or all tests if no mode specified</returns>
        public async Task<List<ITestAdaptor>> GetAllTestsAsync(string testModeFilter = "")
        {
            var tests = new List<ITestAdaptor>();
            var tasks = new List<Task<List<ITestAdaptor>>>();

            if (string.IsNullOrEmpty(testModeFilter) || testModeFilter.Equals("EditMode", StringComparison.OrdinalIgnoreCase))
            {
                tasks.Add(RetrieveTestsAsync(TestMode.EditMode));
            }
            if (string.IsNullOrEmpty(testModeFilter) || testModeFilter.Equals("PlayMode", StringComparison.OrdinalIgnoreCase))
            {
                tasks.Add(RetrieveTestsAsync(TestMode.PlayMode));
            }

            var results = await Task.WhenAll(tasks);

            foreach (var result in results)
            {
                tests.AddRange(result);
            }

            return tests;
        }

        /// <summary>
        /// Executes tests and returns a JSON summary.
        /// </summary>
        /// <param name="testMode">The test mode to run (EditMode or PlayMode).</param>
        /// <param name="returnOnlyFailures">If true, only failed test results are included in the output.</param>
        /// <param name="returnWithLogs">If true, all logs are included in the output.</param>
        /// <param name="testFilter">A filter string to select specific tests to run.</param>
        /// <returns>Task that resolves with test results when tests are complete</returns>
        public async Task<JObject> ExecuteTestsAsync(TestMode testMode, bool returnOnlyFailures, bool returnWithLogs, string testFilter = "")
        {
            await _runGate.WaitAsync();
            try
            {
                var filter = new Filter { testMode = testMode };

                _tcs = new TaskCompletionSource<JObject>();
                // Новый прогон Unity не начнёт, пока не завершился предыдущий, поэтому к этому
                // моменту поздний RunFinished брошенного прогона либо уже погашен, либо не придёт.
                _abandonedRunPending = false;
                _returnOnlyFailures = returnOnlyFailures;
                _returnWithLogs = returnWithLogs;

                if (!string.IsNullOrEmpty(testFilter))
                {
                    // Полное имя теста (содержит точку) — точечный запуск через testNames.
                    // Иначе — grain-фильтр: Unity Filter.groupNames трактует строку как regex по
                    // FullName ("JavaParityAttraction" запускает все классы с этим префиксом).
                    if (testFilter.Contains("."))
                        filter.testNames = new[] { testFilter };
                    else
                        filter.groupNames = new[] { testFilter };
                }

                _testRunnerApi.Execute(new ExecutionSettings(filter));

                return await WaitForCompletionAsync(
                    McpUnitySettings.Instance.RequestTimeoutSeconds);
            }
            catch
            {
                TryReleaseRunGateAfterAbortedStart();
                throw;
            }
        }
        
        /// <summary>
        /// Asynchronously retrieves all test adaptors for the specified test mode.
        /// </summary>
        /// <param name="mode">The test mode to retrieve tests for (EditMode or PlayMode).</param>
        /// <returns>A task that resolves to a list of ITestAdaptor representing all tests in the given mode.</returns>
        private Task<List<ITestAdaptor>> RetrieveTestsAsync(TestMode mode)
        {
            var tcs = new TaskCompletionSource<List<ITestAdaptor>>();
            var tests = new List<ITestAdaptor>();

            _testRunnerApi.RetrieveTestList(mode, adaptor =>
            {
                CollectTestItems(adaptor, tests);
                tcs.SetResult(tests);
            });

            return tcs.Task;
        }
        
        /// <summary>
        /// Recursively collect test items from test adaptors
        /// </summary>
        private void CollectTestItems(ITestAdaptor testAdaptor, List<ITestAdaptor> tests)
        {
            if (testAdaptor.IsSuite)
            {
                // For suites (namespaces, classes), collect all children
                foreach (var child in testAdaptor.Children)
                {
                    CollectTestItems(child, tests);
                }
            }
            else
            {
                tests.Add(testAdaptor);
            }
        }

        #region ICallbacks Implementation

        /// <summary>
        /// Called when the test run starts.
        /// </summary>
        public void RunStarted(ITestAdaptor testsToRun)
        {
            if (_tcs == null)
                return;
            
            _results.Clear();
            McpLogger.LogInfo($"Test run started: {testsToRun?.Name}");
        }

        /// <summary>
        /// Called when an individual test starts.
        /// </summary>
        public void TestStarted(ITestAdaptor test)
        {
            // Optionally implement per-test start logic or logging.
        }

        /// <summary>
        /// Called when an individual test finishes.
        /// </summary>
        public void TestFinished(ITestResultAdaptor result)
        {
            if (_tcs == null)
                return;
            
            _results.Add(result);
        }

        /// <summary>
        /// Called when the test run finishes.
        /// </summary>
        public void RunFinished(ITestResultAdaptor result)
        {
            // Поздний колбэк брошенного по таймауту прогона: его результат никому не адресован,
            // а гейт под него уже освобождён. Ни XML, ни TCS, ни Release — иначе он отдаст свои
            // результаты следующему клиенту и лишний раз откроет семафор (park, 03.09).
            if (_abandonedRunPending)
            {
                _abandonedRunPending = false;
                McpLogger.LogWarning($"run_tests: late RunFinished from abandoned run ignored (result={result?.ResultState})");
                return;
            }

            // Пишем сводку в XML под preflight L2 (`run-render-parity-check.sh`); без файла gate считает MCP-артефакт отсутствующим.
            WriteAttractionParityGateXml(result);

            if (_tcs != null)
            {
                var summary = BuildResultJson(_results, result);
                _tcs.TrySetResult(summary);
                _tcs = null;
            }

            SafeReleaseRunGate();
        }

        #endregion

        #region Helpers

        /// <summary>
        /// NUnit-подобный минимальный XML: корневой элемент с атрибутами failed/skipped/passed (как у Unity -testResults).
        /// </summary>
        private static void WriteAttractionParityGateXml(ITestResultAdaptor runResult)
        {
            try
            {
                var projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
                if (string.IsNullOrEmpty(projectRoot))
                    return;

                var relative = Path.Combine("out", "parity-results", "attraction-editmode.xml");
                var path = Path.Combine(projectRoot, relative);
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                int passed = runResult.PassCount;
                int failed = runResult.FailCount;
                int skipped = runResult.SkipCount;
                var state = XmlEscape(runResult.ResultState ?? "");
                var xml =
                    "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
                    $"<test-run failed=\"{failed}\" skipped=\"{skipped}\" passed=\"{passed}\" result=\"{state}\" />\n";
                File.WriteAllText(path, xml);
                McpLogger.LogInfo($"Parity gate L2 test summary written: {path}");
            }
            catch (Exception ex)
            {
                McpLogger.LogError($"Failed to write attraction-editmode.xml: {ex.Message}");
            }
        }

        private static string XmlEscape(string s)
        {
            if (string.IsNullOrEmpty(s))
                return "";
            return s
                .Replace("&", "&amp;")
                .Replace("\"", "&quot;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;");
        }

        private async Task<JObject> WaitForCompletionAsync(int timeoutSeconds)
        {
            var pending = _tcs;
            if (pending == null)
            {
                return McpUnitySocketHandler.CreateErrorResponse("Test runner not initialized", "test_runner_error");
            }

            var delayTask = Task.Delay(TimeSpan.FromSeconds(timeoutSeconds));
            var winner = await Task.WhenAny(pending.Task, delayTask);
            
            if (winner != pending.Task)
            {
                pending.TrySetResult(
                    McpUnitySocketHandler.CreateErrorResponse(
                        $"Test run timed out after {timeoutSeconds} seconds",
                        "test_runner_timeout"));
                // Иначе _runGate остаётся занятым до RunFinished; при зависшем Test Runner все следующие run_tests висят на WaitAsync без лога Executing.
                McpLogger.LogWarning("run_tests: timeout — releasing gate so MCP queue can proceed (late RunFinished may follow)");
                // Снимаем ссылку на брошенный TCS: иначе следующий клиент присвоит свой _tcs,
                // а поздний RunFinished этого прогона резолвит ЕГО (park, 03.09).
                if (ReferenceEquals(_tcs, pending)) _tcs = null;
                _abandonedRunPending = true;
                SafeReleaseRunGate();
            }
            return await pending.Task;
        }

        private void SafeReleaseRunGate()
        {
            try
            {
                _runGate.Release();
            }
            catch (SemaphoreFullException)
            {
                McpLogger.LogWarning("run_tests gate release skipped (already released)");
            }
        }

        /// <summary>
        /// Если Execute упал до старта раннера, освобождаем слот — иначе все последующие run_tests зависнут на WaitAsync.
        /// </summary>
        private void TryReleaseRunGateAfterAbortedStart()
        {
            _tcs = null;
            SafeReleaseRunGate();
        }

        private JObject BuildResultJson(List<ITestResultAdaptor> results, ITestResultAdaptor result)
        {
            var arr = new JArray(results
                .Where(r => !r.HasChildren)
                .Where(r => !_returnOnlyFailures || r.ResultState.StartsWith("Failed"))
                .Select(r => new JObject {
                    ["name"]      = r.Name,
                    ["fullName"]  = r.FullName,
                    ["state"]     = r.ResultState,
                    ["message"]   = r.Message,
                    ["duration"]  = r.Duration,
                    ["logs"]      = _returnWithLogs ? r.Output : null,
                    ["stackTrace"] = r.StackTrace
                }));

            int testCount = result.PassCount + result.SkipCount + result.FailCount;
            return new JObject { 
                ["success"]           = true,
                ["type"]              = "text",
                ["message"]           = $"{result.Test.Name} test run completed: {result.PassCount}/{testCount} passed - {result.FailCount}/{testCount} failed - {result.SkipCount}/{testCount} skipped",
                ["resultState"]       = result.ResultState,
                ["durationSeconds"]   = result.Duration,
                ["testCount"]         = results.Count,
                ["passCount"]         = result.PassCount,
                ["failCount"]         = result.FailCount,
                ["skipCount"]         = result.SkipCount,
                ["results"]           = arr
            };
        }

        #endregion
    }
}
