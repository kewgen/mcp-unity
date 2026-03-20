using System;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Newtonsoft.Json.Linq;
using McpUnity.Unity;
using McpUnity.Utils;

namespace McpUnity.Tools
{
    public class FindObjectsTool : McpToolBase
    {
        public FindObjectsTool()
        {
            Name = "find_objects";
            Description = "Finds GameObjects by tag, layer, component type, text content, partial name match, or screen coordinates";
        }

        public override JObject Execute(JObject parameters)
        {
            try
            {
                string by = parameters["by"]?.ToString();
                string value = parameters["value"]?.ToString();
                int maxResults = parameters["maxResults"]?.Value<int>() ?? 50;
                bool activeOnly = parameters["activeOnly"]?.Value<bool>() ?? true;

                if (string.IsNullOrEmpty(by))
                {
                    return McpUnitySocketHandler.CreateErrorResponse(
                        "Missing required parameter: by", "validation_error");
                }

                List<GameObject> results;

                switch (by)
                {
                    case "tag":
                        if (string.IsNullOrEmpty(value))
                            return McpUnitySocketHandler.CreateErrorResponse("Missing required parameter: value for tag search", "validation_error");
                        try
                        {
                            results = GameObject.FindGameObjectsWithTag(value).ToList();
                        }
                        catch (UnityException)
                        {
                            return McpUnitySocketHandler.CreateErrorResponse($"Tag '{value}' is not defined", "validation_error");
                        }
                        break;

                    case "layer":
                        if (string.IsNullOrEmpty(value))
                            return McpUnitySocketHandler.CreateErrorResponse("Missing required parameter: value for layer search", "validation_error");
                        int layer = LayerMask.NameToLayer(value);
                        if (layer == -1)
                        {
                            if (int.TryParse(value, out int layerNum))
                                layer = layerNum;
                            else
                                return McpUnitySocketHandler.CreateErrorResponse($"Layer '{value}' not found", "validation_error");
                        }
                        results = GetAllGameObjects(activeOnly).Where(go => go.layer == layer).ToList();
                        break;

                    case "component":
                        if (string.IsNullOrEmpty(value))
                            return McpUnitySocketHandler.CreateErrorResponse("Missing required parameter: value for component search", "validation_error");
                        results = FindByComponent(value, activeOnly);
                        break;

                    case "text":
                        if (string.IsNullOrEmpty(value))
                            return McpUnitySocketHandler.CreateErrorResponse("Missing required parameter: value for text search", "validation_error");
                        results = FindByTextContent(value, activeOnly);
                        break;

                    case "name_contains":
                        if (string.IsNullOrEmpty(value))
                            return McpUnitySocketHandler.CreateErrorResponse("Missing required parameter: value for name search", "validation_error");
                        results = GetAllGameObjects(activeOnly)
                            .Where(go => go.name.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0)
                            .ToList();
                        break;

                    case "coordinates":
                        return FindAtCoordinates(parameters);

                    default:
                        return McpUnitySocketHandler.CreateErrorResponse(
                            $"Unknown search type: {by}. Valid: tag, layer, component, text, name_contains, coordinates",
                            "validation_error");
                }

                if (!activeOnly)
                {
                    // already handled
                }

                var limited = results.Take(maxResults).ToList();
                var objectsArray = new JArray();
                foreach (var go in limited)
                {
                    objectsArray.Add(GameObjectToJson(go));
                }

                return new JObject
                {
                    ["success"] = true,
                    ["type"] = "text",
                    ["message"] = $"Found {results.Count} object(s) (showing {limited.Count})",
                    ["totalCount"] = results.Count,
                    ["objects"] = objectsArray
                };
            }
            catch (Exception ex)
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    $"Error in find_objects: {ex.Message}", "tool_execution_error");
            }
        }

        private List<GameObject> GetAllGameObjects(bool activeOnly)
        {
            var all = new List<GameObject>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                {
                    CollectGameObjects(root, all, activeOnly);
                }
            }
            return all;
        }

        private void CollectGameObjects(GameObject go, List<GameObject> list, bool activeOnly)
        {
            if (activeOnly && !go.activeInHierarchy) return;
            list.Add(go);
            for (int i = 0; i < go.transform.childCount; i++)
            {
                CollectGameObjects(go.transform.GetChild(i).gameObject, list, activeOnly);
            }
        }

        private List<GameObject> FindByComponent(string componentName, bool activeOnly)
        {
            // Try common Unity types first
            Type type = Type.GetType(componentName)
                ?? Type.GetType($"UnityEngine.{componentName}, UnityEngine")
                ?? Type.GetType($"UnityEngine.UI.{componentName}, UnityEngine.UI")
                ?? Type.GetType($"TMPro.{componentName}, Unity.TextMeshPro");

            if (type == null)
            {
                // Search all assemblies
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    type = assembly.GetType(componentName);
                    if (type != null) break;
                    // Try without namespace
                    type = assembly.GetTypes().FirstOrDefault(t => t.Name == componentName);
                    if (type != null) break;
                }
            }

            if (type == null)
                return new List<GameObject>();

            return GetAllGameObjects(activeOnly)
                .Where(go => go.GetComponent(type) != null)
                .ToList();
        }

        private List<GameObject> FindByTextContent(string text, bool activeOnly)
        {
            var results = new List<GameObject>();
            foreach (var go in GetAllGameObjects(activeOnly))
            {
                // Unity UI Text
                var uiText = go.GetComponent<Text>();
                if (uiText != null && uiText.text != null &&
                    uiText.text.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    results.Add(go);
                    continue;
                }

                // TMP Text (via reflection to avoid hard dependency)
                var tmpText = go.GetComponent("TMP_Text");
                if (tmpText != null)
                {
                    var textProp = tmpText.GetType().GetProperty("text");
                    if (textProp != null)
                    {
                        string tmpValue = textProp.GetValue(tmpText) as string;
                        if (tmpValue != null && tmpValue.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            results.Add(go);
                        }
                    }
                }
            }
            return results;
        }

        private JObject FindAtCoordinates(JObject parameters)
        {
            float x = parameters["x"]?.Value<float>() ?? 0f;
            float y = parameters["y"]?.Value<float>() ?? 0f;

            if (!UnityEditor.EditorApplication.isPlaying)
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    "Coordinate search requires Play Mode", "validation_error");
            }

            var results = new JArray();

            // Physics 3D raycast
            var camera = Camera.main;
            if (camera != null)
            {
                var ray = camera.ScreenPointToRay(new Vector3(x, y, 0));
                var hits = Physics.RaycastAll(ray, Mathf.Infinity);
                foreach (var hit in hits)
                {
                    results.Add(GameObjectToJson(hit.collider.gameObject));
                }

                // Physics 2D raycast
                var hits2d = Physics2D.RaycastAll(camera.ScreenToWorldPoint(new Vector3(x, y, 0)), Vector2.zero);
                foreach (var hit in hits2d)
                {
                    results.Add(GameObjectToJson(hit.collider.gameObject));
                }
            }

            // UI EventSystem raycast
            var eventSystem = UnityEngine.EventSystems.EventSystem.current;
            if (eventSystem != null)
            {
                var pointerData = new UnityEngine.EventSystems.PointerEventData(eventSystem)
                {
                    position = new Vector2(x, y)
                };
                var raycastResults = new List<UnityEngine.EventSystems.RaycastResult>();
                eventSystem.RaycastAll(pointerData, raycastResults);
                foreach (var r in raycastResults)
                {
                    results.Add(GameObjectToJson(r.gameObject));
                }
            }

            return new JObject
            {
                ["success"] = true,
                ["type"] = "text",
                ["message"] = $"Found {results.Count} object(s) at ({x}, {y})",
                ["totalCount"] = results.Count,
                ["objects"] = results
            };
        }

        private JObject GameObjectToJson(GameObject go)
        {
            var components = new JArray();
            foreach (var comp in go.GetComponents<Component>())
            {
                if (comp != null)
                    components.Add(comp.GetType().Name);
            }

            return new JObject
            {
                ["name"] = go.name,
                ["instanceId"] = go.GetInstanceID(),
                ["path"] = GetGameObjectPath(go),
                ["tag"] = go.tag,
                ["layer"] = LayerMask.LayerToName(go.layer),
                ["active"] = go.activeInHierarchy,
                ["components"] = components
            };
        }

        private string GetGameObjectPath(GameObject go)
        {
            string path = go.name;
            var t = go.transform.parent;
            while (t != null)
            {
                path = t.name + "/" + path;
                t = t.parent;
            }
            return "/" + path;
        }
    }
}
