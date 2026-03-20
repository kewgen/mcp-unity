using System;
using UnityEngine;
using Newtonsoft.Json.Linq;
using McpUnity.Unity;
using McpUnity.Utils;

namespace McpUnity.Tools
{
    public class PlayerPrefsTool : McpToolBase
    {
        public PlayerPrefsTool()
        {
            Name = "player_prefs";
            Description = "Manages PlayerPrefs: get, set, delete, has, delete_all";
        }

        public override JObject Execute(JObject parameters)
        {
            try
            {
                string action = parameters["action"]?.ToString();
                string key = parameters["key"]?.ToString();
                string type = parameters["type"]?.ToString() ?? "string";

                if (string.IsNullOrEmpty(action))
                {
                    return McpUnitySocketHandler.CreateErrorResponse(
                        "Missing required parameter: action", "validation_error");
                }

                switch (action)
                {
                    case "get":
                        if (string.IsNullOrEmpty(key))
                            return McpUnitySocketHandler.CreateErrorResponse("Missing key", "validation_error");
                        if (!PlayerPrefs.HasKey(key))
                            return new JObject { ["success"] = true, ["type"] = "text", ["message"] = $"Key '{key}' not found", ["exists"] = false };

                        object value;
                        switch (type)
                        {
                            case "int": value = PlayerPrefs.GetInt(key); break;
                            case "float": value = PlayerPrefs.GetFloat(key); break;
                            default: value = PlayerPrefs.GetString(key); break;
                        }
                        return new JObject
                        {
                            ["success"] = true, ["type"] = "text",
                            ["message"] = $"PlayerPrefs['{key}'] = {value}",
                            ["exists"] = true, ["value"] = JToken.FromObject(value), ["valueType"] = type
                        };

                    case "set":
                        if (string.IsNullOrEmpty(key))
                            return McpUnitySocketHandler.CreateErrorResponse("Missing key", "validation_error");
                        var val = parameters["value"];
                        if (val == null)
                            return McpUnitySocketHandler.CreateErrorResponse("Missing value", "validation_error");

                        switch (type)
                        {
                            case "int": PlayerPrefs.SetInt(key, val.Value<int>()); break;
                            case "float": PlayerPrefs.SetFloat(key, val.Value<float>()); break;
                            default: PlayerPrefs.SetString(key, val.ToString()); break;
                        }
                        PlayerPrefs.Save();
                        return new JObject
                        {
                            ["success"] = true, ["type"] = "text",
                            ["message"] = $"Set PlayerPrefs['{key}'] = {val} ({type})"
                        };

                    case "delete":
                        if (string.IsNullOrEmpty(key))
                            return McpUnitySocketHandler.CreateErrorResponse("Missing key", "validation_error");
                        PlayerPrefs.DeleteKey(key);
                        PlayerPrefs.Save();
                        return new JObject
                        {
                            ["success"] = true, ["type"] = "text",
                            ["message"] = $"Deleted PlayerPrefs['{key}']"
                        };

                    case "has":
                        if (string.IsNullOrEmpty(key))
                            return McpUnitySocketHandler.CreateErrorResponse("Missing key", "validation_error");
                        bool exists = PlayerPrefs.HasKey(key);
                        return new JObject
                        {
                            ["success"] = true, ["type"] = "text",
                            ["message"] = $"PlayerPrefs.HasKey('{key}') = {exists}",
                            ["exists"] = exists
                        };

                    case "delete_all":
                        PlayerPrefs.DeleteAll();
                        PlayerPrefs.Save();
                        return new JObject
                        {
                            ["success"] = true, ["type"] = "text",
                            ["message"] = "All PlayerPrefs deleted"
                        };

                    default:
                        return McpUnitySocketHandler.CreateErrorResponse(
                            $"Unknown action: {action}. Valid: get, set, delete, has, delete_all",
                            "validation_error");
                }
            }
            catch (Exception ex)
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    $"Error in player_prefs: {ex.Message}", "tool_execution_error");
            }
        }
    }
}
