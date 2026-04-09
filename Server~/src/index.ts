// Import MCP SDK components
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { StdioServerTransport } from '@modelcontextprotocol/sdk/server/stdio.js';
import { McpUnity } from './unity/mcpUnity.js';
import { Logger, LogLevel } from './utils/logger.js';
import { registerCreateSceneTool } from './tools/createSceneTool.js';
import { registerMenuItemTool } from './tools/menuItemTool.js';
import { registerSelectGameObjectTool } from './tools/selectGameObjectTool.js';
import { registerAddPackageTool } from './tools/addPackageTool.js';
import { registerRunTestsTool } from './tools/runTestsTool.js';
import { registerSendConsoleLogTool } from './tools/sendConsoleLogTool.js';
import { registerGetConsoleLogsTool } from './tools/getConsoleLogsTool.js';
import { registerUpdateComponentTool } from './tools/updateComponentTool.js';
import { registerAddAssetToSceneTool } from './tools/addAssetToSceneTool.js';
import { registerUpdateGameObjectTool } from './tools/updateGameObjectTool.js';
import { registerCreatePrefabTool } from './tools/createPrefabTool.js';
import { registerDeleteSceneTool } from './tools/deleteSceneTool.js';
import { registerLoadSceneTool } from './tools/loadSceneTool.js';
import { registerSaveSceneTool } from './tools/saveSceneTool.js';
import { registerGetSceneInfoTool } from './tools/getSceneInfoTool.js';
import { registerUnloadSceneTool } from './tools/unloadSceneTool.js';
import { registerRecompileScriptsTool } from './tools/recompileScriptsTool.js';
import { registerGetGameObjectTool } from './tools/getGameObjectTool.js';
import { registerTransformTools } from './tools/transformTools.js';
import { registerCreateMaterialTool, registerAssignMaterialTool, registerModifyMaterialTool, registerGetMaterialInfoTool } from './tools/materialTools.js';
import { registerDuplicateGameObjectTool, registerDeleteGameObjectTool, registerReparentGameObjectTool } from './tools/gameObjectTools.js';
import { registerBatchExecuteTool } from './tools/batchExecuteTool.js';
import { registerPlayModeControlTool } from './tools/playModeControlTool.js';
import { registerFindObjectsTool } from './tools/findObjectsTool.js';
import { registerInspectObjectTool } from './tools/inspectObjectTool.js';
import { registerCaptureScreenshotTool } from './tools/captureScreenshotTool.js';
import { registerSimulateInputTool } from './tools/simulateInputTool.js';
import { registerCallMethodTool } from './tools/callMethodTool.js';
import { registerSetTextTool } from './tools/setTextTool.js';
import { registerPlayerPrefsTool } from './tools/playerPrefsTool.js';
import { registerTimeControlTool } from './tools/timeControlTool.js';
import { registerScreenInfoTool } from './tools/screenInfoTool.js';
import { registerWaitForTool } from './tools/waitForTool.js';
import { registerGetMenuItemsResource } from './resources/getMenuItemResource.js';
import { registerGetConsoleLogsResource } from './resources/getConsoleLogsResource.js';
import { registerGetHierarchyResource } from './resources/getScenesHierarchyResource.js';
import { registerGetPackagesResource } from './resources/getPackagesResource.js';
import { registerGetAssetsResource } from './resources/getAssetsResource.js';
import { registerGetTestsResource } from './resources/getTestsResource.js';
import { registerGetGameObjectResource } from './resources/getGameObjectResource.js';
import { registerGameObjectHandlingPrompt } from './prompts/gameobjectHandlingPrompt.js';

import { createConnection } from 'net';

/**
 * Check if a port is in use. If occupied by a stale MCP process, attempt to
 * connect and trigger a graceful shutdown before we start our own server.
 */
async function ensurePortAvailable(port: number, logger: Logger): Promise<void> {
  return new Promise<void>((resolve) => {
    const socket = createConnection({ port, host: 'localhost' }, () => {
      // Port is occupied — close probe and warn. The stale process will be
      // superseded when Unity's WebSocket accepts our new connection.
      socket.destroy();
      logger.warn(`Port ${port} is already in use (stale MCP process?). Will attempt connection anyway.`);
      resolve();
    });
    socket.on('error', () => {
      // Port is free — good.
      socket.destroy();
      resolve();
    });
    socket.setTimeout(2000, () => {
      socket.destroy();
      resolve();
    });
  });
}

// Initialize loggers
const serverLogger = new Logger('Server', LogLevel.INFO);
const unityLogger = new Logger('Unity', LogLevel.INFO);
const toolLogger = new Logger('Tools', LogLevel.INFO);
const resourceLogger = new Logger('Resources', LogLevel.INFO);

// Initialize the MCP server
const server = new McpServer (
  {
    name: "MCP Unity Server",
    version: "1.0.0"
  },
  {
    capabilities: {
      tools: {},
      resources: {},
      prompts: {},
    },
  }
);

// Initialize MCP HTTP bridge with Unity editor
const mcpUnity = new McpUnity(unityLogger);

// Register all tools into the MCP server
registerMenuItemTool(server, mcpUnity, toolLogger);
registerSelectGameObjectTool(server, mcpUnity, toolLogger);
registerAddPackageTool(server, mcpUnity, toolLogger);
registerRunTestsTool(server, mcpUnity, toolLogger);
registerSendConsoleLogTool(server, mcpUnity, toolLogger);
registerGetConsoleLogsTool(server, mcpUnity, toolLogger);
registerUpdateComponentTool(server, mcpUnity, toolLogger);
registerAddAssetToSceneTool(server, mcpUnity, toolLogger);
registerUpdateGameObjectTool(server, mcpUnity, toolLogger);
registerCreatePrefabTool(server, mcpUnity, toolLogger);
registerCreateSceneTool(server, mcpUnity, toolLogger);
registerDeleteSceneTool(server, mcpUnity, toolLogger);
registerLoadSceneTool(server, mcpUnity, toolLogger);
registerSaveSceneTool(server, mcpUnity, toolLogger);
registerGetSceneInfoTool(server, mcpUnity, toolLogger);
registerUnloadSceneTool(server, mcpUnity, toolLogger);
registerRecompileScriptsTool(server, mcpUnity, toolLogger);
registerGetGameObjectTool(server, mcpUnity, toolLogger);
registerTransformTools(server, mcpUnity, toolLogger);
registerDuplicateGameObjectTool(server, mcpUnity, toolLogger);
registerDeleteGameObjectTool(server, mcpUnity, toolLogger);
registerReparentGameObjectTool(server, mcpUnity, toolLogger);

// Register Material Tools
registerCreateMaterialTool(server, mcpUnity, toolLogger);
registerAssignMaterialTool(server, mcpUnity, toolLogger);
registerModifyMaterialTool(server, mcpUnity, toolLogger);
registerGetMaterialInfoTool(server, mcpUnity, toolLogger);

// Register AltTester-replacement tools
registerPlayModeControlTool(server, mcpUnity, toolLogger);
registerFindObjectsTool(server, mcpUnity, toolLogger);
registerInspectObjectTool(server, mcpUnity, toolLogger);
registerCaptureScreenshotTool(server, mcpUnity, toolLogger);
registerSimulateInputTool(server, mcpUnity, toolLogger);
registerCallMethodTool(server, mcpUnity, toolLogger);
registerSetTextTool(server, mcpUnity, toolLogger);
registerPlayerPrefsTool(server, mcpUnity, toolLogger);
registerTimeControlTool(server, mcpUnity, toolLogger);
registerScreenInfoTool(server, mcpUnity, toolLogger);
registerWaitForTool(server, mcpUnity, toolLogger);

// Register Batch Execute Tool (high-priority for performance)
registerBatchExecuteTool(server, mcpUnity, toolLogger);

// Register all resources into the MCP server
registerGetTestsResource(server, mcpUnity, resourceLogger);
registerGetGameObjectResource(server, mcpUnity, resourceLogger);
registerGetMenuItemsResource(server, mcpUnity, resourceLogger);
registerGetConsoleLogsResource(server, mcpUnity, resourceLogger);
registerGetHierarchyResource(server, mcpUnity, resourceLogger);
registerGetPackagesResource(server, mcpUnity, resourceLogger);
registerGetAssetsResource(server, mcpUnity, resourceLogger);

// Register all prompts into the MCP server
registerGameObjectHandlingPrompt(server);

// Server startup function
async function startServer() {
  try {
    // Initialize STDIO transport for MCP client communication
    const stdioTransport = new StdioServerTransport();
    
    // Connect the server to the transport
    await server.connect(stdioTransport);

    serverLogger.info('MCP Server started');
    
    // Get the client name from the MCP server, env var, or fallback
    const clientName = server.server.getClientVersion()?.name
      || process.env.MCP_CLIENT_NAME
      || 'Unknown MCP Client';
    serverLogger.info(`Connected MCP client: ${clientName}`);
    
    // Check port availability before connecting to Unity
    const unityPort = parseInt(process.env.UNITY_PORT || '8090', 10);
    await ensurePortAvailable(unityPort, serverLogger);

    // Start Unity Bridge connection with client name in headers
    await mcpUnity.start(clientName);
    
  } catch (error) {
    serverLogger.error('Failed to start server', error);
    process.exit(1);
  }
}

// Graceful shutdown handler
let isShuttingDown = false;
async function shutdown() {
  if (isShuttingDown) return;
  isShuttingDown = true;

  try {
    serverLogger.info('Shutting down...');
    await mcpUnity.stop();
    await server.close();
  } catch (error) {
    // Ignore errors during shutdown
  }
  process.exit(0);
}

// Start the server
startServer();

// Handle shutdown signals
process.on('SIGINT', shutdown);
process.on('SIGTERM', shutdown);
process.on('SIGHUP', shutdown);

// Handle stdin close (when MCP client disconnects)
process.stdin.on('close', shutdown);
process.stdin.on('end', shutdown);
process.stdin.on('error', shutdown);

// Handle uncaught exceptions - exit cleanly if it's just a closed pipe
process.on('uncaughtException', (error: NodeJS.ErrnoException) => {
  // EPIPE/EOF errors are expected when the MCP client disconnects
  if (error.code === 'EPIPE' || error.code === 'EOF' || error.code === 'ERR_USE_AFTER_CLOSE') {
    shutdown();
    return;
  }
  serverLogger.error('Uncaught exception', error);
  process.exit(1);
});

// Handle unhandled promise rejections
process.on('unhandledRejection', (reason) => {
  serverLogger.error('Unhandled rejection', reason);
  process.exit(1);
});
