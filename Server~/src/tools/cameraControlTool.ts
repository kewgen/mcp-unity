import * as z from 'zod';
import { Logger } from '../utils/logger.js';
import { McpUnity } from '../unity/mcpUnity.js';
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { McpUnityError, ErrorType } from '../utils/errors.js';
import { CallToolResult } from '@modelcontextprotocol/sdk/types.js';

const toolName = 'camera_control';
const toolDescription = 'Controls the park viewport camera: get/set pan offset, zoom level, center on map cell. Works with RuntimeParkNativeViewer in the pfp2 park scene.';

const paramsSchema = z.object({
  action: z.enum(['get', 'set', 'move', 'zoom', 'center'])
    .describe('Action: get (read state), set (absolute pan/zoom), move (relative pan), zoom (set zoom level), center (center on isometric cell)'),
  panX: z.number().int().optional()
    .describe('Horizontal pan offset (for set action)'),
  panY: z.number().int().optional()
    .describe('Vertical pan offset (for set action)'),
  deltaX: z.number().int().optional()
    .describe('Relative horizontal pan delta (for move action)'),
  deltaY: z.number().int().optional()
    .describe('Relative vertical pan delta (for move action)'),
  zoom: z.number().min(0.1).max(3.0).optional()
    .describe('Zoom level 0.1-3.0 (for set action)'),
  level: z.number().min(0.1).max(3.0).optional()
    .describe('Zoom level 0.1-3.0 (for zoom action)'),
  cellX: z.number().int().optional()
    .describe('Isometric cell X coordinate (for center action)'),
  cellY: z.number().int().optional()
    .describe('Isometric cell Y coordinate (for center action)'),
});

export function registerCameraControlTool(server: McpServer, mcpUnity: McpUnity, logger: Logger) {
  logger.info(`Registering tool: ${toolName}`);

  server.tool(
    toolName,
    toolDescription,
    paramsSchema.shape,
    async (params: z.infer<typeof paramsSchema>) => {
      try {
        logger.info(`Executing tool: ${toolName}`, params);
        const result = await toolHandler(mcpUnity, params);
        logger.info(`Tool execution successful: ${toolName}`);
        return result;
      } catch (error) {
        logger.error(`Tool execution failed: ${toolName}`, error);
        throw error;
      }
    }
  );
}

async function toolHandler(mcpUnity: McpUnity, params: z.infer<typeof paramsSchema>): Promise<CallToolResult> {
  const response = await mcpUnity.sendRequestWithRetry(toolName, params);

  if (!response.success) {
    throw new McpUnityError(
      ErrorType.TOOL_EXECUTION,
      response.message || 'Camera control failed'
    );
  }

  return {
    content: [{
      type: 'text' as const,
      text: response.message
    }],
    data: {
      panX: response.panX,
      panY: response.panY,
      zoom: response.zoom,
      cellX: response.cellX,
      cellY: response.cellY,
      screenW: response.screenW,
      screenH: response.screenH,
    }
  };
}
