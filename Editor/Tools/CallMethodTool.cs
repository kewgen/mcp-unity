using System;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEditor;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using McpUnity.Unity;
using McpUnity.Utils;

namespace McpUnity.Tools
{
    public class CallMethodTool : McpToolBase
    {
        public CallMethodTool()
        {
            Name = "call_method";
            Description = "Calls a method or gets/sets a property on a component or static type via reflection";
        }

        public override JObject Execute(JObject parameters)
        {
            try
            {
                bool isStatic = parameters["isStatic"]?.Value<bool>() ?? false;
                string methodName = parameters["methodName"]?.ToString();
                string typeName = parameters["typeName"]?.ToString();
                string component = parameters["component"]?.ToString();
                var paramsArray = parameters["parameters"] as JArray;

                if (string.IsNullOrEmpty(methodName))
                {
                    return McpUnitySocketHandler.CreateErrorResponse(
                        "Missing required parameter: methodName", "validation_error");
                }

                if (isStatic)
                {
                    return CallStaticMethod(typeName, methodName, paramsArray);
                }
                else
                {
                    return CallInstanceMethod(parameters, component, methodName, paramsArray);
                }
            }
            catch (Exception ex)
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    $"Error in call_method: {ex.Message}", "tool_execution_error");
            }
        }

        private JObject CallStaticMethod(string typeName, string methodName, JArray paramsArray)
        {
            if (string.IsNullOrEmpty(typeName))
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    "Missing required parameter: typeName for static call", "validation_error");
            }

            Type type = FindType(typeName);
            if (type == null)
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    $"Type not found: {typeName}", "validation_error");
            }

            // Try property first
            var prop = type.GetProperty(methodName, BindingFlags.Public | BindingFlags.Static);
            if (prop != null)
            {
                if (paramsArray != null && paramsArray.Count > 0)
                {
                    // Set property
                    object value = ConvertParameter(paramsArray[0], prop.PropertyType);
                    prop.SetValue(null, value);
                    return CreateSuccessResponse($"Set static property {typeName}.{methodName}", null);
                }
                else
                {
                    // Get property
                    object result = prop.GetValue(null);
                    return CreateSuccessResponse($"Got static property {typeName}.{methodName}", result);
                }
            }

            // Try method
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name == methodName)
                .ToArray();

            if (methods.Length == 0)
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    $"Method/property '{methodName}' not found on {typeName}", "validation_error");
            }

            return InvokeMethod(null, methods, paramsArray, $"{typeName}.{methodName}");
        }

        private JObject CallInstanceMethod(JObject parameters, string componentName, string methodName, JArray paramsArray)
        {
            string objectPath = parameters["objectPath"]?.ToString();
            int? instanceId = parameters["instanceId"]?.Value<int>();

            GameObject go = null;
            if (instanceId.HasValue)
                go = EditorUtility.InstanceIDToObject(instanceId.Value) as GameObject;
            else if (!string.IsNullOrEmpty(objectPath))
                go = GameObject.Find(objectPath);

            if (go == null)
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    $"GameObject not found", "validation_error");
            }

            Component comp = null;
            if (!string.IsNullOrEmpty(componentName))
            {
                comp = go.GetComponents<Component>()
                    .FirstOrDefault(c => c != null && c.GetType().Name == componentName);
            }
            else
            {
                // Try to find method on any component
                foreach (var c in go.GetComponents<Component>())
                {
                    if (c == null) continue;
                    if (c.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance) != null ||
                        c.GetType().GetProperty(methodName, BindingFlags.Public | BindingFlags.Instance) != null)
                    {
                        comp = c;
                        break;
                    }
                }
            }

            if (comp == null)
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    $"Component '{componentName}' not found on {go.name}", "validation_error");
            }

            var type = comp.GetType();

            // Try property first
            var prop = type.GetProperty(methodName, BindingFlags.Public | BindingFlags.Instance);
            if (prop != null)
            {
                if (paramsArray != null && paramsArray.Count > 0)
                {
                    object value = ConvertParameter(paramsArray[0], prop.PropertyType);
                    prop.SetValue(comp, value);
                    return CreateSuccessResponse($"Set property {type.Name}.{methodName}", null);
                }
                else
                {
                    object result = prop.GetValue(comp);
                    return CreateSuccessResponse($"Got property {type.Name}.{methodName}", result);
                }
            }

            // Try method
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.Name == methodName)
                .ToArray();

            if (methods.Length == 0)
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    $"Method/property '{methodName}' not found on {type.Name}", "validation_error");
            }

            return InvokeMethod(comp, methods, paramsArray, $"{type.Name}.{methodName}");
        }

        private JObject InvokeMethod(object target, MethodInfo[] methods, JArray paramsArray, string displayName)
        {
            int paramCount = paramsArray?.Count ?? 0;

            // Find best overload by parameter count
            var method = methods.FirstOrDefault(m => m.GetParameters().Length == paramCount)
                ?? methods.FirstOrDefault();

            if (method == null)
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    $"No suitable overload for {displayName} with {paramCount} parameters", "validation_error");
            }

            var methodParams = method.GetParameters();
            object[] args = new object[methodParams.Length];

            for (int i = 0; i < methodParams.Length; i++)
            {
                if (paramsArray != null && i < paramsArray.Count)
                {
                    args[i] = ConvertParameter(paramsArray[i], methodParams[i].ParameterType);
                }
                else if (methodParams[i].HasDefaultValue)
                {
                    args[i] = methodParams[i].DefaultValue;
                }
            }

            object result = method.Invoke(target, args);
            return CreateSuccessResponse($"Called {displayName}", result);
        }

        private object ConvertParameter(JToken token, Type targetType)
        {
            if (token == null || token.Type == JTokenType.Null)
                return null;

            return token.ToObject(targetType);
        }

        private Type FindType(string typeName)
        {
            Type type = Type.GetType(typeName);
            if (type != null) return type;

            // Search common Unity assemblies
            string[] prefixes = { "UnityEngine.", "UnityEditor.", "" };
            foreach (var prefix in prefixes)
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    type = assembly.GetType(prefix + typeName);
                    if (type != null) return type;
                }
            }

            // Search by name only
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                type = assembly.GetTypes().FirstOrDefault(t => t.Name == typeName);
                if (type != null) return type;
            }

            return null;
        }

        private JObject CreateSuccessResponse(string message, object result)
        {
            var response = new JObject
            {
                ["success"] = true,
                ["type"] = "text",
                ["message"] = message
            };

            if (result != null)
            {
                try
                {
                    string serialized = JsonConvert.SerializeObject(result, new JsonSerializerSettings
                    {
                        ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
                        MaxDepth = 3
                    });
                    response["result"] = JToken.Parse(serialized);
                }
                catch
                {
                    response["result"] = result.ToString();
                }
            }

            return response;
        }
    }
}
