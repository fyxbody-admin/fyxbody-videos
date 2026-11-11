// Backend only. Never bundle this module or VIDEOS_PARTNER_KEY into browser code.
export class VideosApiError extends Error {
  constructor(status, problem, retryAfter) {
    super(`Videos API ${status}: ${problem.code ?? 'request_failed'}`);
    this.status = status;
    this.code = problem.code;
    this.requestId = problem.requestId;
    this.retryAfter = retryAfter;
  }
}

export class VideosClient {
  #apiKey;
  constructor({ baseUrl = process.env.VIDEOS_API, apiKey = process.env.VIDEOS_PARTNER_KEY } = {}) {
    this.baseUrl = new URL(baseUrl);
    if (this.baseUrl.protocol !== 'https:' && !(this.baseUrl.protocol === 'http:' && ['localhost', '127.0.0.1'].includes(this.baseUrl.hostname))) {
      throw new Error('Use HTTPS, or HTTP for local development only');
    }
    if (this.baseUrl.username || this.baseUrl.password || this.baseUrl.search || this.baseUrl.hash) throw new Error('Invalid API base URL');
    if (!apiKey || /[\r\n]/.test(apiKey)) throw new Error('Missing or invalid backend credential');
    this.#apiKey = apiKey;
  }

  async request(path, { method = 'GET', body, idempotencyKey } = {}) {
    const endpoint = new URL(path, this.baseUrl);
    if (endpoint.origin !== this.baseUrl.origin || !endpoint.pathname.startsWith('/v1/') || endpoint.username || endpoint.password) {
      throw new Error('Request must target /v1/ on the configured API origin');
    }
    const headers = { Authorization: `Bearer ${this.#apiKey}` };
    if (body !== undefined) headers['Content-Type'] = 'application/json';
    if (idempotencyKey) headers['Idempotency-Key'] = idempotencyKey;
    const response = await fetch(endpoint, {
      method, headers, body: body === undefined ? undefined : JSON.stringify(body),
      signal: AbortSignal.timeout(10_000), redirect: 'error', cache: 'no-store'
    });
    if (!response.ok) {
      const problem = await response.json().catch(() => ({}));
      throw new VideosApiError(response.status, problem, response.headers.get('Retry-After'));
    }
    return response.json();
  }

  catalogue(page = 1, pageSize = 25) {
    return this.request(`/v1/videos?page=${encodeURIComponent(page)}&pageSize=${encodeURIComponent(pageSize)}`);
  }

  // Authorize the application's user and map its exercise to video/asset before calling.
  // Persist attemptId and reuse it on timeout; a new UUID creates another billable session.
  playback(videoId, videoAssetId, attemptId) {
    if (!attemptId || attemptId.length > 128 || /[\r\n]/.test(attemptId)) throw new Error('Invalid attemptId');
    return this.request(`/v1/videos/${encodeURIComponent(videoId)}/playback-sessions`, {
      method: 'POST', idempotencyKey: attemptId, body: { videoAssetId }
    });
  }

  // Validate each session belongs to the authenticated application user before proxying.
  events(events) { return this.request('/v1/playback-events', { method: 'POST', body: { events } }); }
  usage(month) { return this.request(`/v1/usage${month ? `?month=${encodeURIComponent(month)}` : ''}`); }
}
