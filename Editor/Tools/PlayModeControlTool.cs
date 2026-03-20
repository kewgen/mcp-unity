using System;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Newtonsoft.Json.Linq;
using McpUnity.Unity;
using McpUnity.Utils;
using Unity.EditorCoroutines.Editor;
using System.Collections;

namespace McpUnity.Tools
{
    public class PlayModeControlTool : McpToolBase
    {
        public PlayModeControlTool()
        {
            Name = "play_mode_control";
            Description = "Controls Unity Play Mode: enter, exit, pause, resume, or get current state";
            IsAsync = true;
        }

        public override void ExecuteAsync(JObject parameters, TaskCompletionSource<JObject> tcs)
        {
            try
            {
                string action = parameters["action"]?.ToString();
                if (string.IsNullOrEmpty(action))
                {
                    tcs.SetResult(McpUnitySocketHandler.CreateErrorResponse(
                        "Missing required parameter: action", "validation_error"));
                    return;
                }

                switch (action)
                {
                    case "get_state":
                        tcs.SetResult(CreateStateResponse());
                        break;
                    case "enter":
                        if (EditorApplication.isPlaying)
                        {
                            tcs.SetResult(CreateStateResponse("Already in Play Mode"));
                            return;
                        }
                        // Auto-save dirty scenes to prevent the "Save Scene" dialog from blocking
                        EditorSceneManager.SaveOpenScenes();
                        EditorCoroutineUtility.StartCoroutineOwnerless(
                            WaitForPlayModeChange(true, tcs));
                        EditorApplication.isPlaying = true;
                        break;
                    case "exit":
                        if (!EditorApplication.isPlaying)
                        {
                            tcs.SetResult(CreateStateResponse("Already in Edit Mode"));
                            return;
                        }
                        EditorCoroutineUtility.StartCoroutineOwnerless(
                            WaitForPlayModeChange(false, tcs));
                        EditorApplication.isPlaying = false;
                        break;
                    case "pause":
                        if (!EditorApplication.isPlaying)
                        {
                            tcs.SetResult(McpUnitySocketHandler.CreateErrorResponse(
                                "Cannot pause: not in Play Mode", "validation_error"));
                            return;
                        }
                        EditorApplication.isPaused = true;
                        tcs.SetResult(CreateStateResponse("Paused"));
                        break;
                    case "resume":
                        if (!EditorApplication.isPlaying)
                        {
                            tcs.SetResult(McpUnitySocketHandler.CreateErrorResponse(
                                "Cannot resume: not in Play Mode", "validation_error"));
                            return;
                        }
                        EditorApplication.isPaused = false;
                        tcs.SetResult(CreateStateResponse("Resumed"));
                        break;
                    default:
                        tcs.SetResult(McpUnitySocketHandler.CreateErrorResponse(
                            $"Unknown action: {action}. Valid actions: enter, exit, pause, resume, get_state",
                            "validation_error"));
                        break;
                }
            }
            catch (Exception ex)
            {
                tcs.SetResult(McpUnitySocketHandler.CreateErrorResponse(
                    $"Error in play_mode_control: {ex.Message}", "tool_execution_error"));
            }
        }

        private IEnumerator WaitForPlayModeChange(bool targetPlaying, TaskCompletionSource<JObject> tcs)
        {
            float timeout = 30f;
            float elapsed = 0f;
            while (EditorApplication.isPlaying != targetPlaying && elapsed < timeout)
            {
                elapsed += 0.1f;
                yield return new EditorWaitForSeconds(0.1f);
            }

            if (EditorApplication.isPlaying == targetPlaying)
            {
                tcs.TrySetResult(CreateStateResponse(
                    targetPlaying ? "Entered Play Mode" : "Exited Play Mode"));
            }
            else
            {
                tcs.TrySetResult(McpUnitySocketHandler.CreateErrorResponse(
                    "Timeout waiting for Play Mode transition", "timeout_error"));
            }
        }

        private JObject CreateStateResponse(string message = null)
        {
            return new JObject
            {
                ["success"] = true,
                ["type"] = "text",
                ["message"] = message ?? $"Play Mode state retrieved",
                ["isPlaying"] = EditorApplication.isPlaying,
                ["isPaused"] = EditorApplication.isPaused,
                ["isCompiling"] = EditorApplication.isCompiling
            };
        }
    }
}
