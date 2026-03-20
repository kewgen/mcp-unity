using System;
using UnityEngine;
using UnityEngine.UI;
using Newtonsoft.Json.Linq;
using McpUnity.Unity;
using McpUnity.Utils;

namespace McpUnity.Tools
{
    public class SetTextTool : McpToolBase
    {
        public SetTextTool()
        {
            Name = "set_text";
            Description = "Sets text on UI elements (Text, TMP_Text, InputField, TMP_InputField)";
        }

        public override JObject Execute(JObject parameters)
        {
            try
            {
                string objectPath = parameters["objectPath"]?.ToString();
                int? instanceId = parameters["instanceId"]?.Value<int>();
                string text = parameters["text"]?.ToString();
                bool submit = parameters["submit"]?.Value<bool>() ?? false;

                if (text == null)
                {
                    return McpUnitySocketHandler.CreateErrorResponse(
                        "Missing required parameter: text", "validation_error");
                }

                GameObject go = null;
                if (instanceId.HasValue)
                    go = UnityEditor.EditorUtility.InstanceIDToObject(instanceId.Value) as GameObject;
                else if (!string.IsNullOrEmpty(objectPath))
                    go = GameObject.Find(objectPath);

                if (go == null)
                {
                    return McpUnitySocketHandler.CreateErrorResponse(
                        $"GameObject not found", "validation_error");
                }

                // Try InputField first (includes TMP_InputField via reflection)
                var inputField = go.GetComponent<InputField>();
                if (inputField != null)
                {
                    inputField.text = text;
                    if (submit)
                    {
                        inputField.onEndEdit?.Invoke(text);
                        inputField.onSubmit?.Invoke(text);
                    }
                    return CreateSuccessResponse(go.name, "InputField", text);
                }

                // Try TMP_InputField via reflection
                var tmpInputField = go.GetComponent("TMP_InputField");
                if (tmpInputField != null)
                {
                    var textProp = tmpInputField.GetType().GetProperty("text");
                    if (textProp != null)
                    {
                        textProp.SetValue(tmpInputField, text);
                        if (submit)
                        {
                            var onEndEdit = tmpInputField.GetType().GetField("onEndEdit");
                            var onSubmitField = tmpInputField.GetType().GetField("onSubmit");
                            if (onEndEdit != null)
                            {
                                var evt = onEndEdit.GetValue(tmpInputField);
                                evt?.GetType().GetMethod("Invoke")?.Invoke(evt, new object[] { text });
                            }
                        }
                        return CreateSuccessResponse(go.name, "TMP_InputField", text);
                    }
                }

                // Try Unity UI Text
                var uiText = go.GetComponent<Text>();
                if (uiText != null)
                {
                    uiText.text = text;
                    return CreateSuccessResponse(go.name, "Text", text);
                }

                // Try TMP_Text via reflection
                var tmpText = go.GetComponent("TMP_Text");
                if (tmpText != null)
                {
                    var textProp = tmpText.GetType().GetProperty("text");
                    if (textProp != null)
                    {
                        textProp.SetValue(tmpText, text);
                        return CreateSuccessResponse(go.name, "TMP_Text", text);
                    }
                }

                return McpUnitySocketHandler.CreateErrorResponse(
                    $"No text component found on {go.name}", "validation_error");
            }
            catch (Exception ex)
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    $"Error in set_text: {ex.Message}", "tool_execution_error");
            }
        }

        private JObject CreateSuccessResponse(string objectName, string componentType, string text)
        {
            return new JObject
            {
                ["success"] = true,
                ["type"] = "text",
                ["message"] = $"Set text on {objectName} ({componentType}): \"{text}\"",
                ["componentType"] = componentType
            };
        }
    }
}
