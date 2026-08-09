// Single HTTP seam for the SPA. Every request goes through here.
//
// Conventions (docs/api.md + Program.cs):
// - Editable-file GETs return an ETag header; PUTs require If-Match.
// - ETag mismatch → 409 whose body is the CURRENT file text and whose ETag
//   header is the CURRENT etag (surfaced as ConflictError for re-merge UI).
// - Other errors are RFC 7807 ProblemDetails.
// - Dev: Vite proxies /api → http://localhost:5211; prod: same origin.

// Hooks are unavailable here, so error copy comes from the module-level
// accessor, resolved at throw time (never cached at module load).
import { getStrings } from '../i18n';

export class ApiError extends Error {
  status: number;

  constructor(status: number, message: string) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
  }
}

export class ConflictError extends ApiError {
  currentText: string;
  currentETag: string;

  constructor(currentText: string, currentETag: string) {
    super(409, getStrings().errorFileChangedOnDisk);
    this.name = 'ConflictError';
    this.currentText = currentText;
    this.currentETag = currentETag;
  }
}

/** Strips W/ prefix and surrounding quotes; backend compares unquoted values. */
function unquoteETag(raw: string | null): string {
  if (!raw) return '';
  let value = raw.trim();
  if (value.startsWith('W/')) value = value.slice(2);
  return value.replace(/^"|"$/g, '');
}

/** Best-effort message out of a ProblemDetails (or plain-text) error body. */
async function errorMessage(response: Response): Promise<string> {
  try {
    const text = await response.text();
    if (text) {
      try {
        const problem = JSON.parse(text) as { detail?: string; title?: string };
        if (problem.detail) return problem.detail;
        if (problem.title) return problem.title;
      } catch {
        return text.slice(0, 500);
      }
    }
  } catch {
    // fall through to statusText
  }
  return response.statusText || `HTTP ${response.status}`;
}

async function throwApiError(response: Response): Promise<never> {
  throw new ApiError(response.status, await errorMessage(response));
}

// ---------------------------------------------------------------------------
// JSON verbs
// ---------------------------------------------------------------------------

export async function getJson<T>(path: string): Promise<T> {
  const response = await fetch(path, { headers: { Accept: 'application/json' } });
  if (!response.ok) await throwApiError(response);
  return (await response.json()) as T;
}

export async function postJson<T>(path: string, body: unknown): Promise<T> {
  const response = await fetch(path, {
    method: 'POST',
    headers:
      body === undefined
        ? { Accept: 'application/json' }
        : { Accept: 'application/json', 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!response.ok) await throwApiError(response);
  const text = await response.text();
  return (text ? JSON.parse(text) : undefined) as T;
}

/** PUT/PATCH with a JSON body — for resources that are not ETag-guarded text files. */
async function sendJson<T>(method: 'PUT' | 'PATCH', path: string, body: unknown): Promise<T> {
  const response = await fetch(path, {
    method,
    headers: { Accept: 'application/json', 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
  if (!response.ok) await throwApiError(response);
  const text = await response.text();
  return (text ? JSON.parse(text) : undefined) as T;
}

export function putJson<T>(path: string, body: unknown): Promise<T> {
  return sendJson<T>('PUT', path, body);
}

export function patchJson<T>(path: string, body: unknown): Promise<T> {
  return sendJson<T>('PATCH', path, body);
}

export async function del(path: string): Promise<void> {
  const response = await fetch(path, { method: 'DELETE' });
  if (!response.ok) await throwApiError(response);
}

// ---------------------------------------------------------------------------
// ETag-guarded text files (session JSON, draft scenes, universe files)
// ---------------------------------------------------------------------------

export async function getWithETag(path: string): Promise<{ text: string; etag: string }> {
  const response = await fetch(path);
  if (!response.ok) await throwApiError(response);
  return { text: await response.text(), etag: unquoteETag(response.headers.get('ETag')) };
}

/**
 * Content-Type per backend expectation: session routes, the global voice
 * catalog and *.json universe files carry raw JSON; draft scenes and markdown
 * universe files carry text. (The server reads the raw body either way — this
 * keeps semantics honest.)
 */
function contentTypeForPut(path: string): string {
  return path.includes('/session/') || path.endsWith('.json') || path === '/api/voices/catalog'
    ? 'application/json'
    : 'text/markdown; charset=utf-8';
}

/** PUT with If-Match; throws ConflictError (current text + etag) on 409. */
export async function putWithETag(
  path: string,
  body: string,
  etag: string,
): Promise<{ etag: string }> {
  const response = await fetch(path, {
    method: 'PUT',
    headers: { 'If-Match': `"${etag}"`, 'Content-Type': contentTypeForPut(path) },
    body,
  });
  if (response.status === 409) {
    throw new ConflictError(await response.text(), unquoteETag(response.headers.get('ETag')));
  }
  if (!response.ok) await throwApiError(response);
  // Successful PUT is 204 with the fresh ETag header.
  return { etag: unquoteETag(response.headers.get('ETag')) };
}

// ---------------------------------------------------------------------------
// Multipart uploads (with progress — phone capture, voice reference wavs)
// ---------------------------------------------------------------------------

export function uploadRecollection(
  storyId: string,
  person: string,
  file: File,
  onProgress: (pct: number) => void,
): Promise<void> {
  return uploadFile(
    `/api/stories/${encodeURIComponent(storyId)}/recollections?person=${encodeURIComponent(person)}`,
    file,
    onProgress,
  );
}

/** POST library/voices/<id>.wav; the server also drops the stale preview + conditionals cache. */
export function uploadVoiceReference(
  voiceId: string,
  file: File,
  onProgress: (pct: number) => void,
): Promise<void> {
  return uploadFile(`/api/voices/${encodeURIComponent(voiceId)}/reference`, file, onProgress);
}

function uploadFile(url: string, file: File, onProgress: (pct: number) => void): Promise<void> {
  return new Promise((resolve, reject) => {
    const xhr = new XMLHttpRequest();
    xhr.open('POST', url);
    xhr.responseType = 'text';

    xhr.upload.onprogress = (event) => {
      if (event.lengthComputable && event.total > 0)
        onProgress(Math.min(100, Math.round((event.loaded / event.total) * 100)));
    };
    xhr.onload = () => {
      if (xhr.status >= 200 && xhr.status < 300) {
        onProgress(100);
        resolve();
        return;
      }
      let message = xhr.statusText || `HTTP ${xhr.status}`;
      try {
        const problem = JSON.parse(xhr.responseText) as { detail?: string; title?: string };
        message = problem.detail ?? problem.title ?? message;
      } catch {
        if (xhr.responseText) message = xhr.responseText.slice(0, 500);
      }
      reject(new ApiError(xhr.status, message));
    };
    xhr.onerror = () => reject(new ApiError(0, getStrings().uploadNetworkError));
    xhr.onabort = () => reject(new ApiError(0, getStrings().uploadAborted));

    const form = new FormData();
    // Field name must be "file" — it binds to the IFormFile parameter.
    form.append('file', file, file.name);
    xhr.send(form);
  });
}

// ---------------------------------------------------------------------------
// Media URLs (range-enabled endpoints; hand straight to <audio src>)
// ---------------------------------------------------------------------------

export function audioUrl(storyId: string, variant: string): string {
  return `/api/stories/${encodeURIComponent(storyId)}/audio/${encodeURIComponent(variant)}`;
}

export function recollectionUrl(storyId: string, file: string): string {
  return `/api/stories/${encodeURIComponent(storyId)}/recollections/${encodeURIComponent(file)}`;
}

/** Rendered voice preview (library/voice-previews/<id>.mp3); 404 until a preview job ran. */
export function voicePreviewUrl(voiceId: string): string {
  return `/api/voices/${encodeURIComponent(voiceId)}/preview`;
}
