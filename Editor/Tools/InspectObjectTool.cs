using System;
using UnityEngine;
using UnityEngine.UI;
using Newtonsoft.Json.Linq;
using McpUnity.Unity;
using McpUnity.Utils;

namespace McpUnity.Tools
{
    public class InspectObjectTool : McpToolBase
    {
        public InspectObjectTool()
        {
            Name = "inspect_object";
            Description = "Inspects a GameObject: screen position, world position, bounds, text content, parent info";
        }

        public override JObject Execute(JObject parameters)
        {
            try
            {
                string objectPath = parameters["objectPath"]?.ToString();
                int? instanceId = parameters["instanceId"]?.Value<int>();
                string query = parameters["query"]?.ToString() ?? "all";

                GameObject go = null;
                if (instanceId.HasValue)
                {
                    go = UnityEditor.EditorUtility.InstanceIDToObject(instanceId.Value) as GameObject;
                }
                else if (!string.IsNullOrEmpty(objectPath))
                {
                    go = GameObject.Find(objectPath);
                }

                if (go == null)
                {
                    return McpUnitySocketHandler.CreateErrorResponse(
                        $"GameObject not found: {objectPath ?? instanceId?.ToString()}", "validation_error");
                }

                var result = new JObject
                {
                    ["success"] = true,
                    ["type"] = "text",
                    ["name"] = go.name,
                    ["instanceId"] = go.GetInstanceID(),
                    ["path"] = GetGameObjectPath(go)
                };

                if (query == "all" || query == "world_position")
                {
                    var pos = go.transform.position;
                    var rot = go.transform.eulerAngles;
                    var scale = go.transform.lossyScale;
                    result["worldPosition"] = new JObject
                    {
                        ["x"] = pos.x, ["y"] = pos.y, ["z"] = pos.z
                    };
                    result["worldRotation"] = new JObject
                    {
                        ["x"] = rot.x, ["y"] = rot.y, ["z"] = rot.z
                    };
                    result["worldScale"] = new JObject
                    {
                        ["x"] = scale.x, ["y"] = scale.y, ["z"] = scale.z
                    };
                }

                if (query == "all" || query == "screen_position")
                {
                    var camera = Camera.main;
                    if (camera != null)
                    {
                        var screenPos = camera.WorldToScreenPoint(go.transform.position);
                        result["screenPosition"] = new JObject
                        {
                            ["x"] = screenPos.x,
                            ["y"] = screenPos.y,
                            ["z"] = screenPos.z,
                            ["isVisible"] = screenPos.z > 0
                        };
                    }
                    else
                    {
                        result["screenPosition"] = JValue.CreateNull();
                        result["screenPositionError"] = "No main camera found";
                    }
                }

                if (query == "all" || query == "bounds")
                {
                    var renderer = go.GetComponent<Renderer>();
                    var rectTransform = go.GetComponent<RectTransform>();

                    if (renderer != null)
                    {
                        var bounds = renderer.bounds;
                        result["bounds"] = new JObject
                        {
                            ["center"] = new JObject { ["x"] = bounds.center.x, ["y"] = bounds.center.y, ["z"] = bounds.center.z },
                            ["size"] = new JObject { ["x"] = bounds.size.x, ["y"] = bounds.size.y, ["z"] = bounds.size.z },
                            ["min"] = new JObject { ["x"] = bounds.min.x, ["y"] = bounds.min.y, ["z"] = bounds.min.z },
                            ["max"] = new JObject { ["x"] = bounds.max.x, ["y"] = bounds.max.y, ["z"] = bounds.max.z }
                        };
                    }
                    else if (rectTransform != null)
                    {
                        var rect = rectTransform.rect;
                        result["bounds"] = new JObject
                        {
                            ["rect"] = new JObject
                            {
                                ["x"] = rect.x, ["y"] = rect.y,
                                ["width"] = rect.width, ["height"] = rect.height
                            },
                            ["anchoredPosition"] = new JObject
                            {
                                ["x"] = rectTransform.anchoredPosition.x,
                                ["y"] = rectTransform.anchoredPosition.y
                            },
                            ["sizeDelta"] = new JObject
                            {
                                ["x"] = rectTransform.sizeDelta.x,
                                ["y"] = rectTransform.sizeDelta.y
                            }
                        };
                    }
                    else
                    {
                        result["bounds"] = JValue.CreateNull();
                    }
                }

                if (query == "all" || query == "text")
                {
                    string textContent = null;

                    var uiText = go.GetComponent<Text>();
                    if (uiText != null) textContent = uiText.text;

                    if (textContent == null)
                    {
                        var tmpText = go.GetComponent("TMP_Text");
                        if (tmpText != null)
                        {
                            var textProp = tmpText.GetType().GetProperty("text");
                            textContent = textProp?.GetValue(tmpText) as string;
                        }
                    }

                    if (textContent == null)
                    {
                        var inputField = go.GetComponent<InputField>();
                        if (inputField != null) textContent = inputField.text;
                    }

                    result["text"] = textContent != null ? (JToken)textContent : JValue.CreateNull();
                }

                if (query == "all" || query == "parent")
                {
                    var parent = go.transform.parent;
                    if (parent != null)
                    {
                        result["parent"] = new JObject
                        {
                            ["name"] = parent.name,
                            ["instanceId"] = parent.gameObject.GetInstanceID(),
                            ["path"] = GetGameObjectPath(parent.gameObject)
                        };
                    }
                    else
                    {
                        result["parent"] = JValue.CreateNull();
                    }

                    result["childCount"] = go.transform.childCount;
                }

                result["message"] = $"Inspected {go.name} (query: {query})";
                return result;
            }
            catch (Exception ex)
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    $"Error in inspect_object: {ex.Message}", "tool_execution_error");
            }
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
