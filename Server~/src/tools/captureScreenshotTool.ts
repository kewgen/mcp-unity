import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { McpUnity } from '../unity/mcpUnity.js';
import { McpUnityError, ErrorType } from '../utils/errors.js';
import * as z from 'zod';
import { Logger } from '../utils/logger.js';
import { CallToolResult } from '@modelcontextprotocol/sdk/types.js';

const toolName = 'capture_screenshot';
const toolDescription = 'Captures a screenshot of the current game/scene view as a PNG image. Works in both Edit and Play mode.';

const paramsSchema = z.object({
  width: z.number().int().min(1).max(3840).optional().describe('Screenshot width in pixels (default: screen width or 960)'),
  height: z.number().int().min(1).max(2160).optional().describe('Screenshot height in pixels (default: screen height or 540)'),
  camera: z.string().optional().describe('Camera GameObject path (default: Main Camera)'),
  superSize: z.number().int().min(1).max(4).optional().describe('Resolution multiplier (default: 1)')
});

export function registerCaptureScreenshotTool(server: McpServer, mcpUnity: McpUnity, logger: Logger) {
  logger.info(`Registering tool: ${toolName}`);

  server.tool(
    toolName,
    toolDescription,
    paramsSchema.shape,
    async (params: z.infer<typeof paramsSchema>): Promise<CallToolResult> => {
      try {
        logger.info(`Executing tool: ${toolName}`, params);
        const response = await mcpUnity.sendRequest({
          method: toolName,
          params
        });

        if (!response.success) {
          throw new McpUnityError(
            ErrorType.TOOL_EXECUTION,
            response.message || 'Failed to capture screenshot'
          );
        }

        logger.info(`Tool execution successful: ${toolName} (${response.width}x${response.height})`);

        return {
          content: [
            {
              type: 'image' as const,
              data: response.data,
              mimeType: 'image/png'
            },
            {
              type: 'text' as const,
              text: `Screenshot captured: ${response.width}x${response.height}`
            }
          ]
        };
      } catch (error) {
        logger.error(`Tool execution failed: ${toolName}`, error);
        throw error;
      }
    }
  );
}
