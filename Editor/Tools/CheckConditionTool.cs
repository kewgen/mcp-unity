using System;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using Newtonsoft.Json.Linq;
using McpUnity.Unity;
using McpUnity.Utils;

namespace McpUnity.Tools
{
    public class CheckConditionTool : McpToolBase
    {
        public CheckConditionTool()
        {
            Name = "check_condition";
            Description = "Checks if a condition is met (used internally by wait_for). Returns conditionMet: true/false.";
        }

        public override JObject Execute(JObject parameters)
        {
            try
            {
                string condition = parameters["condition"]?.ToString();
                string target = parameters["target"]?.ToString();

                if (string.IsNullOrEmpty(condition) || string.IsNullOrEmpty(target))
                {
                    return McpUnitySocketHandler.CreateErrorResponse(
                        "Missing required parameters: condition, target", "validation_error");
                }

                bool conditionMet = false;
                string details = "";

                switch (condition)
                {
                    case "object_exists":
                        var go = GameObject.Find(target);
                        conditionMet = go != null && go.activeInHierarchy;
                        details = conditionMet ? $"Object '{target}' exists and is active" : $"Object '{target}' not found";
                        break;

                    case "object_not_exists":
                        var goNe = GameObject.Find(target);
                        conditionMet = goNe == null || !goNe.activeInHierarchy;
                        details = conditionMet ? $"Object '{target}' does not exist" : $"Object '{target}' still exists";
                        break;

                    case "scene_loaded":
                        for (int i = 0; i < SceneManager.sceneCount; i++)
                        {
                            var scene = SceneManager.GetSceneAt(i);
                            if (scene.name == target && scene.isLoaded)
                            {
                                conditionMet = true;
                                break;
                            }
                        }
                        details = conditionMet ? $"Scene '{target}' is loaded" : $"Scene '{target}' not loaded";
                        break;

                    case "property_equals":
                        conditionMet = CheckPropertyEquals(parameters, out details);
                        break;

                    default:
                        return McpUnitySocketHandler.CreateErrorResponse(
                            $"Unknown condition: {condition}. Valid: object_exists, object_not_exists, scene_loaded, property_equals",
                            "validation_error");
                }

                return new JObject
                {
                    ["success"] = true,
                    ["type"] = "text",
                    ["conditionMet"] = conditionMet,
                    ["message"] = details,
                    ["details"] = details
                };
            }
            catch (Exception ex)
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    $"Error in check_condition: {ex.Message}", "tool_execution_error");
            }
        }

        private bool CheckPropertyEquals(JObject parameters, out string details)
        {
            string target = parameters["target"]?.ToString(); // objectPath
            string component = parameters["component"]?.ToString();
            string property = parameters["property"]?.ToString();
            var expectedValue = parameters["value"];

            if (string.IsNullOrEmpty(component) || string.IsNullOrEmpty(property))
            {
                details = "property_equals requires: target (objectPath), component, property, value";
                return false;
            }

            var go = GameObject.Find(target);
            if (go == null)
            {
                details = $"Object '{target}' not found";
                return false;
            }

            var comp = go.GetComponents<Component>()
                .FirstOrDefault(c => c != null && c.GetType().Name == component);
            if (comp == null)
            {
                details = $"Component '{component}' not found on '{target}'";
                return false;
            }

            var prop = comp.GetType().GetProperty(property);
            if (prop == null)
            {
                var field = comp.GetType().GetField(property);
                if (field != null)
                {
                    var currentValue = field.GetValue(comp);
                    bool match = currentValue?.ToString() == expectedValue?.ToString();
                    details = $"{component}.{property} = {currentValue} (expected: {expectedValue})";
                    return match;
                }
                details = $"Property/field '{property}' not found on '{component}'";
                return false;
            }

            var propValue = prop.GetValue(comp);
            bool equals = propValue?.ToString() == expectedValue?.ToString();
            details = $"{component}.{property} = {propValue} (expected: {expectedValue})";
            return equals;
        }
    }
}
