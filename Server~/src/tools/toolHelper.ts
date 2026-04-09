import { McpUnity } from '../unity/mcpUnity.js';
import { Logger } from '../utils/logger.js';

/**
 * Sends a request to Unity with automatic retry on connection failure.
 * Delegates to McpUnity.sendRequestWithRetry() which handles per-tool timeouts
 * and unified retry logic.
 *
 * Runtime tools (Play Mode) use maxRetries=5 for resilience during domain reloads.
 * Edit-mode tools can call mcpUnity.sendRequestWithRetry() directly with lower retries.
 */
export async function sendWithRetry(
  mcpUnity: McpUnity,
  method: string,
  params: any,
  logger: Logger,
  options?: {
    maxRetries?: number;
    retryIntervalMs?: number;
    timeoutMs?: number;
  }
): Promise<any> {
  return mcpUnity.sendRequestWithRetry(method, params, {
    maxRetries: options?.maxRetries ?? 5,
    retryIntervalMs: options?.retryIntervalMs ?? 2000,
    timeoutMs: options?.timeoutMs,
  });
}
