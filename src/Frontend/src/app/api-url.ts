export function isAllowedApiUrl(value: string, origin: string): boolean {
  try {
    const url = new URL(value === '/' ? origin : value);
    return (
      url.protocol === 'https:' ||
      (url.protocol === 'http:' &&
        (['localhost', '127.0.0.1'].includes(url.hostname) ||
          (value === '/' && url.hostname.endsWith('.local'))))
    );
  } catch {
    return false;
  }
}
