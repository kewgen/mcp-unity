using System;
using System.Threading.Tasks;
using UnityEngine;
using UnityEditor;
using Newtonsoft.Json.Linq;
using McpUnity.Unity;
using McpUnity.Utils;
using Unity.EditorCoroutines.Editor;
using System.Collections;

namespace McpUnity.Tools
{
    public class CaptureScreenshotTool : McpToolBase
    {
        public CaptureScreenshotTool()
        {
            Name = "capture_screenshot";
            Description = "Captures a screenshot of the current game view or scene view as base64 PNG";
            IsAsync = true;
        }

        public override void ExecuteAsync(JObject parameters, TaskCompletionSource<JObject> tcs)
        {
            EditorCoroutineUtility.StartCoroutineOwnerless(CaptureCoroutine(parameters, tcs));
        }

        private IEnumerator CaptureCoroutine(JObject parameters, TaskCompletionSource<JObject> tcs)
        {
            int width = parameters["width"]?.Value<int>() ?? 0;
            int height = parameters["height"]?.Value<int>() ?? 0;
            string cameraName = parameters["camera"]?.ToString();
            int superSize = parameters["superSize"]?.Value<int>() ?? 1;

            try
            {
                Camera camera = null;
                if (!string.IsNullOrEmpty(cameraName))
                {
                    var cameraGo = GameObject.Find(cameraName);
                    if (cameraGo != null)
                        camera = cameraGo.GetComponent<Camera>();
                }
                if (camera == null)
                    camera = Camera.main;
                if (camera == null)
                    camera = UnityEngine.Object.FindFirstObjectByType<Camera>();

                if (camera == null)
                {
                    tcs.TrySetResult(McpUnitySocketHandler.CreateErrorResponse(
                        "No camera found in scene", "validation_error"));
                    yield break;
                }

                if (width <= 0) width = EditorApplication.isPlaying ? Screen.width : 960;
                if (height <= 0) height = EditorApplication.isPlaying ? Screen.height : 540;

                width *= superSize;
                height *= superSize;

                // Render to texture
                var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
                var prevRT = camera.targetTexture;
                camera.targetTexture = rt;
                camera.Render();
                camera.targetTexture = prevRT;

                RenderTexture.active = rt;
                var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                tex.Apply();
                RenderTexture.active = null;

                byte[] pngBytes = tex.EncodeToPNG();
                string base64 = Convert.ToBase64String(pngBytes);

                // Cleanup
                UnityEngine.Object.DestroyImmediate(tex);
                UnityEngine.Object.DestroyImmediate(rt);

                tcs.TrySetResult(new JObject
                {
                    ["success"] = true,
                    ["type"] = "image",
                    ["message"] = $"Screenshot captured ({width}x{height})",
                    ["data"] = base64,
                    ["mimeType"] = "image/png",
                    ["width"] = width,
                    ["height"] = height
                });
            }
            catch (Exception ex)
            {
                tcs.TrySetResult(McpUnitySocketHandler.CreateErrorResponse(
                    $"Error capturing screenshot: {ex.Message}", "tool_execution_error"));
            }
        }
    }
}
