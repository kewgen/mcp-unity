using System;
using System.Threading;
using System.Threading.Tasks;
using McpUnity.Unity;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEditor;
using UnityEditor.SceneManagement;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using UnityEditor.TestTools.TestRunner.Api;
using McpUnity.Services;
using McpUnity.Utils;

namespace McpUnity.Tools
{
    /// <summary>
    /// Tool for running Unity Test Runner tests
    /// </summary>
    public class RunTestsTool : McpToolBase
    {
        private readonly ITestRunnerService _testRunnerService;

        public RunTestsTool(ITestRunnerService testRunnerService)
        {
            Name = "run_tests";
            Description = "Runs tests using Unity's Test Runner";
            IsAsync = true;
            _testRunnerService = testRunnerService;
        }
        
        /// <summary>
        /// Executes the RunTests tool asynchronously on the main thread.
        /// </summary>
        /// <param name="parameters">Tool parameters, including optional 'testMode' and 'testFilter'.</param>
        /// <param name="tcs">TaskCompletionSource to set the result or exception.</param>
        public override async void ExecuteAsync(JObject parameters, TaskCompletionSource<JObject> tcs)
        {
            // Parse parameters
            string testModeStr = parameters?["testMode"]?.ToObject<string>() ?? "EditMode";
            string testFilter = parameters?["testFilter"]?.ToObject<string>(); // Optional
            bool returnOnlyFailures = parameters?["returnOnlyFailures"]?.ToObject<bool>() ?? false; // Optional
            bool returnWithLogs = parameters?["returnWithLogs"]?.ToObject<bool>() ?? false; // Optional

            TestMode testMode = TestMode.EditMode;
            
            if (Enum.TryParse(testModeStr, true, out TestMode parsedMode))
            {
                testMode = parsedMode;
            }

            McpLogger.LogInfo($"Executing RunTestsTool: Mode={testMode}, Filter={testFilter ?? "(none)"}");

            // Unity-specific (парк, 03.09): в Play Mode TestRunner EditMode-тесты не запускает,
            // а мост молчал до клиентского таймаута — за 25.08–02.09 так набралось 66 отказов
            // run_tests по 300 с каждый. Отказываем сразу и в том же формате, что занятый мост
            // (McpUnityDomainReloadGate: type + retryAfterMs), чтобы вызывающий повторил, а не ждал.
            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                JObject playModeError = McpUnitySocketHandler.CreateErrorResponse(
                    "Unity is in Play Mode: EditMode tests cannot start. The request was not executed — " +
                    "exit Play Mode (play_mode_control action=exit) and retry.",
                    "play_mode_active");
                playModeError["error"]["retryAfterMs"] = McpUnityDomainReloadGate.RetryAfterMs;
                McpLogger.LogWarning("RunTestsTool rejected: Editor is in Play Mode");
                tcs.SetResult(playModeError);
                return;
            }


            // Unity-specific (парк, INC-052): грязная сцена на старте EditMode-тестов вызывает
            // модалку «Scene(s) Have Been Modified», которая блокирует TestRunner и MCP-мост.
            // Сцену в автоматизации пачкает сам рендер-экспорт (программное создание объектов
            // вьюера ставит dirty автоматически), сохранять этот мусор в .unity нельзя.
            // Политика dirtyScenePolicy: discard (default) — перечитать сцену с диска без
            // сохранения; fail — вернуть ошибку сразу; ignore — прежнее поведение (модалка).
            string dirtyScenePolicy = parameters?["dirtyScenePolicy"]?.ToObject<string>() ?? "discard";
            if (!string.Equals(dirtyScenePolicy, "ignore", StringComparison.OrdinalIgnoreCase))
            {
                var dirtyScenes = new List<Scene>();
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    Scene s = SceneManager.GetSceneAt(i);
                    if (s.isLoaded && s.isDirty) dirtyScenes.Add(s);
                }

                if (dirtyScenes.Count > 0)
                {
                    bool anyWithoutPath = dirtyScenes.Exists(s => string.IsNullOrEmpty(s.path));
                    if (string.Equals(dirtyScenePolicy, "fail", StringComparison.OrdinalIgnoreCase) || anyWithoutPath)
                    {
                        tcs.SetResult(McpUnitySocketHandler.CreateErrorResponse(
                            $"Scene(s) have unsaved changes ({dirtyScenes.Count}); test run would hang on the save prompt. " +
                            "Discard via Park/Debug/Reload Scene From Disk, save manually, or pass dirtyScenePolicy=discard.",
                            "dirty_scene"
                        ));
                        return;
                    }

                    // discard: перечитать активную сцену с диска — программный OpenScene(Single)
                    // молча отбрасывает несохранённые изменения всех открытых сцен, модалки нет.
                    string activePath = SceneManager.GetActiveScene().path;
                    McpLogger.LogInfo($"RunTestsTool: {dirtyScenes.Count} dirty scene(s) — discarding by reloading '{activePath}' from disk (dirtyScenePolicy=discard)");
                    EditorSceneManager.OpenScene(activePath, OpenSceneMode.Single);
                }
            }

            // Call the service to run tests
            // Unity-specific (парк, CMP-135 / INC-120): ExecuteAsync — async void, поэтому исключение отсюда
            // не достаётся вызывающему и запрос молча повисает. Любой отказ обязан прийти клиенту ответом.
            try
            {
                JObject result = await _testRunnerService.ExecuteTestsAsync(testMode, returnOnlyFailures, returnWithLogs, testFilter);
                tcs.TrySetResult(result);
            }
            catch (Exception ex)
            {
                McpLogger.LogError($"RunTestsTool failed: {ex.Message}\n{ex.StackTrace}");
                tcs.TrySetResult(McpUnitySocketHandler.CreateErrorResponse(
                    $"Test run failed: {ex.Message}", "test_run_error"));
            }
        }
    }
}
