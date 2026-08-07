import { describe, it, expect } from '@jest/globals';
import axios, { AxiosRequestConfig } from 'axios';

type ProxyConfig = NonNullable<AxiosRequestConfig['proxy']>;

function buildProxyConfig(proxyUrl: string): ProxyConfig {
  const parsed = new URL(proxyUrl);
  return {
    protocol: parsed.protocol.replace(':', ''),
    host: parsed.hostname,
    port: Number(parsed.port || (parsed.protocol === 'https:' ? 443 : 80)),
    auth:
      parsed.username || parsed.password
        ? {
            username: decodeURIComponent(parsed.username),
            password: decodeURIComponent(parsed.password),
          }
        : undefined,
  };
}

describe('Claude API subscription check over proxy', () => {
  it(
    'returns successful response for minimal Claude request when subscription is active',
    async () => {
      const proxyUrl =
        process.env.HTTPS_PROXY ||
        process.env.HTTP_PROXY ||
        process.env.https_proxy ||
        process.env.http_proxy;
      const apiKey = process.env.ANTHROPIC_API_KEY;

      // Тест не ломает общий прогон в окружениях без секрета или прокси.
      if (!proxyUrl) {
        console.warn('Skip: proxy env is not configured');
        return;
      }

      if (!apiKey) {
        console.warn('Skip: ANTHROPIC_API_KEY is not configured');
        return;
      }

      const response = await axios.post(
        'https://api.anthropic.com/v1/messages',
        {
          model: 'claude-3-5-haiku-20241022',
          max_tokens: 16,
          messages: [{ role: 'user', content: 'ping' }],
        },
        {
          headers: {
            'x-api-key': apiKey,
            'anthropic-version': '2023-06-01',
            'content-type': 'application/json',
          },
          proxy: buildProxyConfig(proxyUrl),
          timeout: 30000,
          validateStatus: () => true,
        }
      );

      if (response.status === 402 || response.status === 429) {
        const errType = response.data?.error?.type;
        const errMessage = response.data?.error?.message;
        throw new Error(
          `Subscription/quota check failed: HTTP ${response.status}, type=${String(errType)}, message=${String(errMessage)}`
        );
      }

      expect(response.status).toBe(200);
      expect(response.data?.id).toBeTruthy();
      expect(response.data?.content).toBeDefined();
    },
    35000
  );
});
