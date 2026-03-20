using System;
using UnityEngine;
using Newtonsoft.Json.Linq;
using McpUnity.Unity;
using McpUnity.Utils;

namespace McpUnity.Tools
{
    public class TimeControlTool : McpToolBase
    {
        public TimeControlTool()
        {
            Name = "time_control";
            Description = "Gets or sets Unity Time.timeScale and retrieves time info";
        }

        public override JObject Execute(JObject parameters)
        {
            try
            {
                string action = parameters["action"]?.ToString() ?? "get";

                switch (action)
                {
                    case "get":
                        return new JObject
                        {
                            ["success"] = true,
                            ["type"] = "text",
                            ["message"] = $"Time.timeScale = {Time.timeScale}",
                            ["timeScale"] = Time.timeScale,
                            ["time"] = Time.time,
                            ["deltaTime"] = Time.deltaTime,
                            ["frameCount"] = Time.frameCount,
                            ["realtimeSinceStartup"] = Time.realtimeSinceStartup
                        };

                    case "set":
                        float? timeScale = parameters["timeScale"]?.Value<float>();
                        if (!timeScale.HasValue)
                        {
                            return McpUnitySocketHandler.CreateErrorResponse(
                                "Missing required parameter: timeScale", "validation_error");
                        }
                        if (timeScale.Value < 0f || timeScale.Value > 100f)
                        {
                            return McpUnitySocketHandler.CreateErrorResponse(
                                "timeScale must be between 0 and 100", "validation_error");
                        }
                        Time.timeScale = timeScale.Value;
                        return new JObject
                        {
                            ["success"] = true,
                            ["type"] = "text",
                            ["message"] = $"Time.timeScale set to {timeScale.Value}",
                            ["timeScale"] = Time.timeScale
                        };

                    default:
                        return McpUnitySocketHandler.CreateErrorResponse(
                            $"Unknown action: {action}. Valid: get, set", "validation_error");
                }
            }
            catch (Exception ex)
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    $"Error in time_control: {ex.Message}", "tool_execution_error");
            }
        }
    }
}
