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

describe('Anthropic API over proxy', () => {
  it(
    'reaches Anthropic models endpoint using configured proxy',
    async () => {
      const proxyUrl =
        process.env.HTTPS_PROXY ||
        process.env.HTTP_PROXY ||
        process.env.https_proxy ||
        process.env.http_proxy;
      const apiKey = process.env.ANTHROPIC_API_KEY;

      if (!proxyUrl) {
        // Делаем тест безопасным для CI без прокси: отсутствие прокси не должно ломать общий прогон.
        console.warn('Skip: proxy env is not configured');
        return;
      }

      if (!apiKey) {
        console.warn('Skip: ANTHROPIC_API_KEY is not configured');
        return;
      }

      const response = await axios.get('https://api.anthropic.com/v1/models', {
        headers: {
          'x-api-key': apiKey,
          'anthropic-version': '2023-06-01',
        },
        proxy: buildProxyConfig(proxyUrl),
        timeout: 30000,
        validateStatus: () => true,
      });

      expect(response.status).toBe(200);
      expect(response.data).toBeDefined();
    },
    35000
  );
});
