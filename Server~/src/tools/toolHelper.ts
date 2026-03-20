import { McpUnity } from '../unity/mcpUnity.js';
import { McpUnityError, ErrorType } from '../utils/errors.js';
import { Logger } from '../utils/logger.js';

function sleep(ms: number): Promise<void> {
  return new Promise(resolve => setTimeout(resolve, ms));
}

/**
 * Sends a request to Unity with automatic retry on connection failure.
 * If the WebSocket is disconnected (e.g. during domain reload after Play Mode transition),
 * waits for reconnection and retries the request.
 *
 * @param mcpUnity McpUnity instance
 * @param method Tool name
 * @param params Tool parameters
 * @param logger Logger instance
 * @param options Retry options
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
  const maxRetries = options?.maxRetries ?? 5;
  const retryInterval = options?.retryIntervalMs ?? 2000;
  const timeout = options?.timeoutMs ?? 10000;

  for (let attempt = 0; attempt <= maxRetries; attempt++) {
    try {
      const response = await mcpUnity.sendRequest(
        { method, params },
        { timeout }
      );
      return response;
    } catch (error) {
      const isConnectionError = error instanceof McpUnityError &&
        (error.type === ErrorType.CONNECTION || error.type === ErrorType.TIMEOUT);

      if (!isConnectionError || attempt === maxRetries) {
        throw error;
      }

      logger.info(
        `${method}: connection unavailable (attempt ${attempt + 1}/${maxRetries + 1}), ` +
        `retrying in ${retryInterval / 1000}s... (likely domain reload in progress)`
      );
      await sleep(retryInterval);
    }
  }

  throw new McpUnityError(
    ErrorType.CONNECTION,
    `${method}: failed after ${maxRetries + 1} attempts. Unity may be in domain reload or Play Mode transition. Try again in a few seconds.`
  );
}
