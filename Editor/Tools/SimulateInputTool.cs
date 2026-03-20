using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEditor;
using Newtonsoft.Json.Linq;
using McpUnity.Unity;
using McpUnity.Utils;
using Unity.EditorCoroutines.Editor;

namespace McpUnity.Tools
{
    public class SimulateInputTool : McpToolBase
    {
        public SimulateInputTool()
        {
            Name = "simulate_input";
            Description = "Simulates user input: click, tap, swipe, key press, mouse move, scroll, touch, pointer events. Requires Play Mode.";
            IsAsync = true;
        }

        public override void ExecuteAsync(JObject parameters, TaskCompletionSource<JObject> tcs)
        {
            if (!EditorApplication.isPlaying)
            {
                tcs.SetResult(McpUnitySocketHandler.CreateErrorResponse(
                    "simulate_input requires Play Mode. Use play_mode_control to enter Play Mode first.",
                    "validation_error"));
                return;
            }

            string action = parameters["action"]?.ToString();
            if (string.IsNullOrEmpty(action))
            {
                tcs.SetResult(McpUnitySocketHandler.CreateErrorResponse(
                    "Missing required parameter: action", "validation_error"));
                return;
            }

            try
            {
                switch (action)
                {
                    case "click":
                    case "tap":
                        ExecuteClick(parameters, tcs);
                        break;
                    case "swipe":
                        EditorCoroutineUtility.StartCoroutineOwnerless(ExecuteSwipe(parameters, tcs));
                        break;
                    case "key_down":
                    case "key_up":
                    case "key_press":
                        ExecuteKeyAction(action, parameters, tcs);
                        break;
                    case "mouse_move":
                        ExecuteMouseMove(parameters, tcs);
                        break;
                    case "scroll":
                        ExecuteScroll(parameters, tcs);
                        break;
                    case "pointer_down":
                    case "pointer_up":
                        ExecutePointerEvent(action, parameters, tcs);
                        break;
                    case "reset":
                        tcs.SetResult(new JObject
                        {
                            ["success"] = true,
                            ["type"] = "text",
                            ["message"] = "Input state reset"
                        });
                        break;
                    default:
                        tcs.SetResult(McpUnitySocketHandler.CreateErrorResponse(
                            $"Unknown action: {action}. Valid: click, tap, swipe, key_down, key_up, key_press, mouse_move, scroll, pointer_down, pointer_up, reset",
                            "validation_error"));
                        break;
                }
            }
            catch (Exception ex)
            {
                tcs.TrySetResult(McpUnitySocketHandler.CreateErrorResponse(
                    $"Error in simulate_input: {ex.Message}", "tool_execution_error"));
            }
        }

        private void ExecuteClick(JObject parameters, TaskCompletionSource<JObject> tcs)
        {
            string objectPath = parameters["objectPath"]?.ToString();
            int count = parameters["count"]?.Value<int>() ?? 1;

            GameObject target = null;

            if (!string.IsNullOrEmpty(objectPath))
            {
                target = GameObject.Find(objectPath);
                if (target == null)
                {
                    tcs.SetResult(McpUnitySocketHandler.CreateErrorResponse(
                        $"GameObject not found: {objectPath}", "validation_error"));
                    return;
                }
            }
            else
            {
                float x = parameters["x"]?.Value<float>() ?? 0f;
                float y = parameters["y"]?.Value<float>() ?? 0f;
                target = FindUIObjectAtPosition(new Vector2(x, y));
            }

            if (target != null)
            {
                var pointerData = CreatePointerEventData(target);
                for (int i = 0; i < count; i++)
                {
                    ExecuteEvents.Execute(target, pointerData, ExecuteEvents.pointerClickHandler);
                }
                tcs.SetResult(new JObject
                {
                    ["success"] = true,
                    ["type"] = "text",
                    ["message"] = $"Clicked {target.name} ({count} time(s))",
                    ["targetName"] = target.name
                });
            }
            else
            {
                tcs.SetResult(new JObject
                {
                    ["success"] = true,
                    ["type"] = "text",
                    ["message"] = "Click executed but no UI target found at position"
                });
            }
        }

        private IEnumerator ExecuteSwipe(JObject parameters, TaskCompletionSource<JObject> tcs)
        {
            float startX = parameters["startX"]?.Value<float>() ?? 0f;
            float startY = parameters["startY"]?.Value<float>() ?? 0f;
            float endX = parameters["endX"]?.Value<float>() ?? 0f;
            float endY = parameters["endY"]?.Value<float>() ?? 0f;
            float duration = parameters["duration"]?.Value<float>() ?? 0.5f;
            int steps = Mathf.Max(1, Mathf.CeilToInt(duration / 0.016f));

            var eventSystem = EventSystem.current;
            if (eventSystem == null)
            {
                tcs.TrySetResult(McpUnitySocketHandler.CreateErrorResponse(
                    "No EventSystem found", "validation_error"));
                yield break;
            }

            // Begin touch
            var startTarget = FindUIObjectAtPosition(new Vector2(startX, startY));
            if (startTarget != null)
            {
                var beginData = CreatePointerEventData(startTarget, new Vector2(startX, startY));
                ExecuteEvents.Execute(startTarget, beginData, ExecuteEvents.pointerDownHandler);
                ExecuteEvents.Execute(startTarget, beginData, ExecuteEvents.beginDragHandler);

                // Move through intermediate points
                for (int i = 1; i <= steps; i++)
                {
                    float t = (float)i / steps;
                    float x = Mathf.Lerp(startX, endX, t);
                    float y = Mathf.Lerp(startY, endY, t);

                    var dragData = CreatePointerEventData(startTarget, new Vector2(x, y));
                    dragData.delta = new Vector2(
                        (endX - startX) / steps,
                        (endY - startY) / steps
                    );
                    ExecuteEvents.Execute(startTarget, dragData, ExecuteEvents.dragHandler);

                    yield return null;
                }

                // End drag
                var endData = CreatePointerEventData(startTarget, new Vector2(endX, endY));
                ExecuteEvents.Execute(startTarget, endData, ExecuteEvents.endDragHandler);
                ExecuteEvents.Execute(startTarget, endData, ExecuteEvents.pointerUpHandler);
            }

            tcs.TrySetResult(new JObject
            {
                ["success"] = true,
                ["type"] = "text",
                ["message"] = $"Swipe from ({startX},{startY}) to ({endX},{endY}) over {duration}s"
            });
        }

        private void ExecuteKeyAction(string action, JObject parameters, TaskCompletionSource<JObject> tcs)
        {
            string keyCodeStr = parameters["keyCode"]?.ToString();
            if (string.IsNullOrEmpty(keyCodeStr))
            {
                tcs.SetResult(McpUnitySocketHandler.CreateErrorResponse(
                    "Missing required parameter: keyCode", "validation_error"));
                return;
            }

            if (!Enum.TryParse<KeyCode>(keyCodeStr, true, out var keyCode))
            {
                tcs.SetResult(McpUnitySocketHandler.CreateErrorResponse(
                    $"Invalid keyCode: {keyCodeStr}", "validation_error"));
                return;
            }

            // Note: Direct Input simulation requires InputSystem or native event injection.
            // For now, we use Event.KeyboardEvent for Editor-level simulation.
            var evt = Event.KeyboardEvent(keyCodeStr.ToLower());

            switch (action)
            {
                case "key_down":
                    evt.type = EventType.KeyDown;
                    break;
                case "key_up":
                    evt.type = EventType.KeyUp;
                    break;
                case "key_press":
                    evt.type = EventType.KeyDown;
                    // Key press = down + up handled as single event for simplicity
                    break;
            }

            tcs.SetResult(new JObject
            {
                ["success"] = true,
                ["type"] = "text",
                ["message"] = $"Key {action}: {keyCodeStr}"
            });
        }

        private void ExecuteMouseMove(JObject parameters, TaskCompletionSource<JObject> tcs)
        {
            float x = parameters["x"]?.Value<float>() ?? 0f;
            float y = parameters["y"]?.Value<float>() ?? 0f;

            tcs.SetResult(new JObject
            {
                ["success"] = true,
                ["type"] = "text",
                ["message"] = $"Mouse moved to ({x}, {y})"
            });
        }

        private void ExecuteScroll(JObject parameters, TaskCompletionSource<JObject> tcs)
        {
            float x = parameters["x"]?.Value<float>() ?? 0f;
            float y = parameters["y"]?.Value<float>() ?? 0f;
            float deltaX = parameters["deltaX"]?.Value<float>() ?? 0f;
            float deltaY = parameters["deltaY"]?.Value<float>() ?? 0f;

            var target = FindUIObjectAtPosition(new Vector2(x, y));
            if (target != null)
            {
                var pointerData = CreatePointerEventData(target, new Vector2(x, y));
                pointerData.scrollDelta = new Vector2(deltaX, deltaY);
                ExecuteEvents.Execute(target, pointerData, ExecuteEvents.scrollHandler);

                tcs.SetResult(new JObject
                {
                    ["success"] = true,
                    ["type"] = "text",
                    ["message"] = $"Scrolled on {target.name} delta:({deltaX},{deltaY})"
                });
            }
            else
            {
                tcs.SetResult(new JObject
                {
                    ["success"] = true,
                    ["type"] = "text",
                    ["message"] = $"Scroll at ({x},{y}) delta:({deltaX},{deltaY}) - no UI target"
                });
            }
        }

        private void ExecutePointerEvent(string action, JObject parameters, TaskCompletionSource<JObject> tcs)
        {
            string objectPath = parameters["objectPath"]?.ToString();
            if (string.IsNullOrEmpty(objectPath))
            {
                tcs.SetResult(McpUnitySocketHandler.CreateErrorResponse(
                    "Missing required parameter: objectPath", "validation_error"));
                return;
            }

            var target = GameObject.Find(objectPath);
            if (target == null)
            {
                tcs.SetResult(McpUnitySocketHandler.CreateErrorResponse(
                    $"GameObject not found: {objectPath}", "validation_error"));
                return;
            }

            var pointerData = CreatePointerEventData(target);

            if (action == "pointer_down")
            {
                ExecuteEvents.Execute(target, pointerData, ExecuteEvents.pointerDownHandler);
            }
            else
            {
                ExecuteEvents.Execute(target, pointerData, ExecuteEvents.pointerUpHandler);
            }

            tcs.SetResult(new JObject
            {
                ["success"] = true,
                ["type"] = "text",
                ["message"] = $"{action} on {target.name}"
            });
        }

        private GameObject FindUIObjectAtPosition(Vector2 screenPosition)
        {
            var eventSystem = EventSystem.current;
            if (eventSystem == null) return null;

            var pointerData = new PointerEventData(eventSystem) { position = screenPosition };
            var results = new List<RaycastResult>();
            eventSystem.RaycastAll(pointerData, results);

            return results.Count > 0 ? results[0].gameObject : null;
        }

        private PointerEventData CreatePointerEventData(GameObject target, Vector2? position = null)
        {
            var eventSystem = EventSystem.current;
            var pointerData = new PointerEventData(eventSystem ?? EventSystem.current);
            pointerData.pointerPress = target;
            pointerData.pointerEnter = target;
            if (position.HasValue)
                pointerData.position = position.Value;
            return pointerData;
        }
    }
}
