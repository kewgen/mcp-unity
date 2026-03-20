using System;
using UnityEngine;
using Newtonsoft.Json.Linq;
using McpUnity.Unity;
using McpUnity.Utils;

namespace McpUnity.Tools
{
    public class ScreenInfoTool : McpToolBase
    {
        public ScreenInfoTool()
        {
            Name = "screen_info";
            Description = "Gets screen size or converts coordinates between screen and world space";
        }

        public override JObject Execute(JObject parameters)
        {
            try
            {
                string action = parameters["action"]?.ToString() ?? "get_size";
                string cameraName = parameters["camera"]?.ToString();

                Camera camera = null;
                if (!string.IsNullOrEmpty(cameraName))
                {
                    var cameraGo = GameObject.Find(cameraName);
                    if (cameraGo != null) camera = cameraGo.GetComponent<Camera>();
                }
                if (camera == null) camera = Camera.main;

                switch (action)
                {
                    case "get_size":
                        return new JObject
                        {
                            ["success"] = true,
                            ["type"] = "text",
                            ["message"] = $"Screen: {Screen.width}x{Screen.height} @ {Screen.dpi}dpi",
                            ["width"] = Screen.width,
                            ["height"] = Screen.height,
                            ["dpi"] = Screen.dpi,
                            ["fullScreen"] = Screen.fullScreen,
                            ["orientation"] = Screen.orientation.ToString()
                        };

                    case "screen_to_world":
                        if (camera == null)
                            return McpUnitySocketHandler.CreateErrorResponse("No camera found", "validation_error");

                        float sx = parameters["x"]?.Value<float>() ?? 0f;
                        float sy = parameters["y"]?.Value<float>() ?? 0f;
                        float sz = parameters["z"]?.Value<float>() ?? camera.nearClipPlane;
                        var worldPos = camera.ScreenToWorldPoint(new Vector3(sx, sy, sz));
                        return new JObject
                        {
                            ["success"] = true,
                            ["type"] = "text",
                            ["message"] = $"Screen ({sx},{sy},{sz}) -> World ({worldPos.x:F3},{worldPos.y:F3},{worldPos.z:F3})",
                            ["worldPosition"] = new JObject { ["x"] = worldPos.x, ["y"] = worldPos.y, ["z"] = worldPos.z }
                        };

                    case "world_to_screen":
                        if (camera == null)
                            return McpUnitySocketHandler.CreateErrorResponse("No camera found", "validation_error");

                        float wx = parameters["x"]?.Value<float>() ?? 0f;
                        float wy = parameters["y"]?.Value<float>() ?? 0f;
                        float wz = parameters["z"]?.Value<float>() ?? 0f;
                        var screenPos = camera.WorldToScreenPoint(new Vector3(wx, wy, wz));
                        return new JObject
                        {
                            ["success"] = true,
                            ["type"] = "text",
                            ["message"] = $"World ({wx},{wy},{wz}) -> Screen ({screenPos.x:F1},{screenPos.y:F1},{screenPos.z:F3})",
                            ["screenPosition"] = new JObject { ["x"] = screenPos.x, ["y"] = screenPos.y, ["z"] = screenPos.z },
                            ["isVisible"] = screenPos.z > 0
                        };

                    default:
                        return McpUnitySocketHandler.CreateErrorResponse(
                            $"Unknown action: {action}. Valid: get_size, screen_to_world, world_to_screen",
                            "validation_error");
                }
            }
            catch (Exception ex)
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    $"Error in screen_info: {ex.Message}", "tool_execution_error");
            }
        }
    }
}
