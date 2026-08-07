using System;
using UnityEngine;
using Newtonsoft.Json.Linq;
using McpUnity.Unity;

namespace McpUnity.Tools
{
    /// <summary>
    /// MCP tool for controlling the park viewport camera (pan, zoom, center on cell).
    /// Works with RuntimeParkNativeViewer component which owns PanOffsetX/Y and ZoomLevel.
    /// </summary>
    public class CameraControlTool : McpToolBase
    {
        // Isometric cell dimensions (standard port packer_320_480_full)
        private const int CellW = 70;
        private const int CellH = 36;

        public CameraControlTool()
        {
            Name = "camera_control";
            Description = "Controls park viewport camera: get/set pan offset, zoom, center on map cell";
        }

        public override JObject Execute(JObject parameters)
        {
            try
            {
                string action = parameters["action"]?.ToString() ?? "get";

                switch (action)
                {
                    case "get":    return GetState();
                    case "set":    return SetState(parameters);
                    case "move":   return Move(parameters);
                    case "zoom":   return Zoom(parameters);
                    case "center": return CenterOnCell(parameters);
                    default:
                        return McpUnitySocketHandler.CreateErrorResponse(
                            $"Unknown action: {action}. Valid: get, set, move, zoom, center",
                            "validation_error");
                }
            }
            catch (Exception ex)
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    $"camera_control error: {ex.Message}", "tool_execution_error");
            }
        }

        private static MonoBehaviour FindViewer()
        {
            // Search by type name to avoid hard dependency on pfp2 assembly
            foreach (var mb in UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
            {
                if (mb.GetType().Name == "RuntimeParkNativeViewer")
                    return mb;
            }
            return null;
        }

        private static int GetPanX(MonoBehaviour viewer) =>
            (int)viewer.GetType().GetProperty("PanOffsetX")?.GetValue(viewer);

        private static int GetPanY(MonoBehaviour viewer) =>
            (int)viewer.GetType().GetProperty("PanOffsetY")?.GetValue(viewer);

        private static float GetZoom(MonoBehaviour viewer) =>
            (float)viewer.GetType().GetProperty("ZoomLevel")?.GetValue(viewer);

        private static void SetPanX(MonoBehaviour viewer, int value) =>
            viewer.GetType().GetProperty("PanOffsetX")?.SetValue(viewer, value);

        private static void SetPanY(MonoBehaviour viewer, int value) =>
            viewer.GetType().GetProperty("PanOffsetY")?.SetValue(viewer, value);

        private static void SetZoom(MonoBehaviour viewer, float value) =>
            viewer.GetType().GetProperty("ZoomLevel")?.SetValue(viewer, value);

        private JObject GetState()
        {
            var viewer = FindViewer();
            if (viewer == null)
                return McpUnitySocketHandler.CreateErrorResponse(
                    "RuntimeParkNativeViewer not found in scene", "tool_execution_error");

            return new JObject
            {
                ["success"] = true,
                ["type"] = "text",
                ["message"] = $"Camera: pan=({GetPanX(viewer)},{GetPanY(viewer)}), zoom={GetZoom(viewer):F2}",
                ["panX"] = GetPanX(viewer),
                ["panY"] = GetPanY(viewer),
                ["zoom"] = GetZoom(viewer),
                ["screenW"] = Screen.width,
                ["screenH"] = Screen.height
            };
        }

        private JObject SetState(JObject parameters)
        {
            var viewer = FindViewer();
            if (viewer == null)
                return McpUnitySocketHandler.CreateErrorResponse(
                    "RuntimeParkNativeViewer not found in scene", "tool_execution_error");

            if (parameters["panX"] != null)
                SetPanX(viewer, parameters["panX"].Value<int>());
            if (parameters["panY"] != null)
                SetPanY(viewer, parameters["panY"].Value<int>());
            if (parameters["zoom"] != null)
                SetZoom(viewer, parameters["zoom"].Value<float>());

            return new JObject
            {
                ["success"] = true,
                ["type"] = "text",
                ["message"] = $"Camera set: pan=({GetPanX(viewer)},{GetPanY(viewer)}), zoom={GetZoom(viewer):F2}",
                ["panX"] = GetPanX(viewer),
                ["panY"] = GetPanY(viewer),
                ["zoom"] = GetZoom(viewer)
            };
        }

        private JObject Move(JObject parameters)
        {
            var viewer = FindViewer();
            if (viewer == null)
                return McpUnitySocketHandler.CreateErrorResponse(
                    "RuntimeParkNativeViewer not found in scene", "tool_execution_error");

            int dx = parameters["deltaX"]?.Value<int>() ?? 0;
            int dy = parameters["deltaY"]?.Value<int>() ?? 0;
            SetPanX(viewer, GetPanX(viewer) + dx);
            SetPanY(viewer, GetPanY(viewer) + dy);

            return new JObject
            {
                ["success"] = true,
                ["type"] = "text",
                ["message"] = $"Camera moved by ({dx},{dy}), now pan=({GetPanX(viewer)},{GetPanY(viewer)})",
                ["panX"] = GetPanX(viewer),
                ["panY"] = GetPanY(viewer)
            };
        }

        private JObject Zoom(JObject parameters)
        {
            var viewer = FindViewer();
            if (viewer == null)
                return McpUnitySocketHandler.CreateErrorResponse(
                    "RuntimeParkNativeViewer not found in scene", "tool_execution_error");

            float level = parameters["level"]?.Value<float>() ?? 1f;
            SetZoom(viewer, level);

            return new JObject
            {
                ["success"] = true,
                ["type"] = "text",
                ["message"] = $"Zoom set to {GetZoom(viewer):F2}",
                ["zoom"] = GetZoom(viewer)
            };
        }

        private JObject CenterOnCell(JObject parameters)
        {
            var viewer = FindViewer();
            if (viewer == null)
                return McpUnitySocketHandler.CreateErrorResponse(
                    "RuntimeParkNativeViewer not found in scene", "tool_execution_error");

            int cx = parameters["cellX"]?.Value<int>() ?? 0;
            int cy = parameters["cellY"]?.Value<int>() ?? 0;

            // Isometric cell→pixel: same formula as Java Map.getXfromCXY / getYfromCXY
            int cellW2 = CellW / 2;
            int cellH2 = CellH / 2;
            int pixX = (cx - cy) * cellW2;
            int pixY = (cx + cy) * cellH2;

            // Center on screen (480x320 game resolution)
            int screenW = 480;
            int screenH = 320;
            int panX = -pixX + screenW / 2;
            int panY = -pixY + screenH / 2;

            SetPanX(viewer, panX);
            SetPanY(viewer, panY);

            return new JObject
            {
                ["success"] = true,
                ["type"] = "text",
                ["message"] = $"Centered on cell ({cx},{cy}), pan=({panX},{panY})",
                ["panX"] = panX,
                ["panY"] = panY,
                ["cellX"] = cx,
                ["cellY"] = cy
            };
        }
    }
}
