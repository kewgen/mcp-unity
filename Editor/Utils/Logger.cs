using UnityEngine;
using McpUnity.Unity;

namespace McpUnity.Utils
{
    /// <summary>
    /// Special logger to use inside the MCP Unity Editor project
    /// </summary>
    public static class McpLogger
    {
        private const string LogPrefix = "[MCP Unity] ";

        /// <summary>
        /// Префикс строки от конкретного клиента: имя агента из X-Client-Name (вкладка
        /// Claude Code, AGENT_ID) — «[MCP R Park] …». Без имени — общий «[MCP Unity] ».
        /// </summary>
        public static string ClientPrefix(string clientName)
            => string.IsNullOrWhiteSpace(clientName) ? LogPrefix : "[MCP " + clientName.Trim() + "] ";

        /// <summary>Info-строка от имени клиента (см. <see cref="ClientPrefix"/>).</summary>
        public static void LogInfoFor(string clientName, string message)
        {
            if (McpUnitySettings.Instance.EnableInfoLogs)
            {
                Debug.LogFormat(LogType.Log, LogOption.NoStacktrace, null, "{0}{1}", ClientPrefix(clientName), message);
            }
        }
        
        /// <summary>
        /// Log an info message if info logs are enabled
        /// </summary>
        /// <param name="message">Message to log</param>
        public static void LogInfo(string message)
        {
            if (McpUnitySettings.Instance.EnableInfoLogs)
            {
                Debug.LogFormat(LogType.Log, LogOption.NoStacktrace, null, "{0}{1}", LogPrefix, message);
            }
        }
        
        /// <summary>
        /// Log a warning message
        /// </summary>
        /// <param name="message">Message to log</param>
        public static void LogWarning(string message)
        {
            Debug.LogWarning($"{LogPrefix}{message}");
        }
        
        /// <summary>
        /// Log an error message
        /// </summary>
        /// <param name="message">Message to log</param>
        public static void LogError(string message)
        {
            Debug.LogError($"{LogPrefix}{message}");
        }
    }
}
