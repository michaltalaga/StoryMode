// TanStack Query hooks — the only data-access layer route components use.
// Query keys: ['stories'] | ['stories', id] | ['jobs'] | ['jobs', id]
//           | ['draft', storyId, variant] | ['universes']
//           | ['universes', id, 'files', name] | ['universes', id, 'pending-facts']
//           | ['voices'] | ['voice-providers'] | ['status']

import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  del,
  getJson,
  getWithETag,
  patchJson,
  postJson,
  putJson,
  uploadVoiceReference,
} from './client';
import type {
  JobDto,
  SceneDto,
  StatusDto,
  StoryDetail,
  StorySummary,
  UniverseListItem,
  UniversePendingFact,
  VoiceCreate,
  VoiceDto,
  VoicePatch,
  VoiceProviderDto,
} from './types';

/** Media URL helpers live in client.ts; re-exported so routes need one import. */
export { voicePreviewUrl } from './client';

// ---------------------------------------------------------------------------
// Stories
// ---------------------------------------------------------------------------

export function useStories() {
  return useQuery({
    queryKey: ['stories'],
    queryFn: () => getJson<StorySummary[]>('/api/stories'),
    staleTime: 10_000,
  });
}

export function useStory(id: string) {
  return useQuery({
    queryKey: ['stories', id],
    queryFn: () => getJson<StoryDetail>(`/api/stories/${encodeURIComponent(id)}`),
    enabled: id.length > 0,
    staleTime: 5_000,
  });
}

// ---------------------------------------------------------------------------
// Jobs
// ---------------------------------------------------------------------------

/** GET /api/jobs items — same as JobDto minus the log tail (detail-only). */
type JobSummaryWire = Omit<JobDto, 'logTail' | 'file' | 'elapsedSeconds'>;

/** GET /api/jobs/{id} — the log tail arrives under `log` on the wire. */
type JobDetailWire = Omit<JobDto, 'logTail'> & { log: string[] };

function isActive(job: { state: JobDto['state'] }): boolean {
  return job.state === 'queued' || job.state === 'running';
}

/** Polls every 2 s while any job is queued/running, else every 10 s. */
export function useJobs() {
  return useQuery({
    queryKey: ['jobs'],
    queryFn: async (): Promise<JobDto[]> => {
      const jobs = await getJson<JobSummaryWire[]>('/api/jobs');
      return jobs.map((job) => ({ ...job, logTail: [] }));
    },
    refetchInterval: (query) => (query.state.data?.some(isActive) ? 2_000 : 10_000),
    // Renders take minutes and phone users switch apps while waiting: keep polling
    // in the background so completion effects fire instead of stalling until return.
    refetchIntervalInBackground: true,
  });
}

/** Single job with live log tail (Progress view); 2 s poll while active. */
export function useJob(id: string) {
  return useQuery({
    queryKey: ['jobs', id],
    queryFn: async (): Promise<JobDto> => {
      const { log, ...job } = await getJson<JobDetailWire>(`/api/jobs/${encodeURIComponent(id)}`);
      return { ...job, logTail: log };
    },
    enabled: id.length > 0,
    refetchInterval: (query) => {
      const job = query.state.data;
      return job === undefined || isActive(job) ? 2_000 : false;
    },
    refetchIntervalInBackground: true,
  });
}

export interface EnqueueJobBody {
  type: string;
  variant: string;
  sceneId?: string;
  feedbackNote?: string;
  /** Recollection file name — required by the backend for transcribe jobs. */
  file?: string;
}

export function enqueueJob(storyId: string, body: EnqueueJobBody): Promise<{ jobId: string }> {
  return postJson<{ jobId: string }>(`/api/stories/${encodeURIComponent(storyId)}/jobs`, body);
}

export function useEnqueueJob() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ storyId, body }: { storyId: string; body: EnqueueJobBody }) =>
      enqueueJob(storyId, body),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['jobs'] }),
  });
}

export function cancelJob(id: string): Promise<{ id: string; state: JobDto['state'] }> {
  return postJson(`/api/jobs/${encodeURIComponent(id)}/cancel`, undefined);
}

export function useCancelJob() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: cancelJob,
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['jobs'] }),
  });
}

// ---------------------------------------------------------------------------
// Draft
// ---------------------------------------------------------------------------

export interface DraftWithETag {
  scenes: SceneDto[];
  /** ETag of draft.<variant>.md — send as If-Match on scene PUTs. */
  etag: string;
}

export function useDraft(storyId: string, variant: string) {
  return useQuery({
    queryKey: ['draft', storyId, variant],
    queryFn: async (): Promise<DraftWithETag> => {
      const { text, etag } = await getWithETag(
        `/api/stories/${encodeURIComponent(storyId)}/draft/${encodeURIComponent(variant)}`,
      );
      return { scenes: JSON.parse(text) as SceneDto[], etag };
    },
    enabled: storyId.length > 0 && variant.length > 0,
  });
}

// ---------------------------------------------------------------------------
// Universes
// ---------------------------------------------------------------------------

export function useUniverses() {
  return useQuery({
    queryKey: ['universes'],
    queryFn: () => getJson<UniverseListItem[]>('/api/universes'),
    staleTime: 60_000,
  });
}

/** `name` may contain one slash (tones/<tone>.md) — encode per segment. */
function encodeFilePath(name: string): string {
  return name.split('/').map(encodeURIComponent).join('/');
}

export function useUniverseFile(id: string, name: string) {
  return useQuery({
    queryKey: ['universes', id, 'files', name],
    queryFn: () => getWithETag(`/api/universes/${encodeURIComponent(id)}/files/${encodeFilePath(name)}`),
    enabled: id.length > 0 && name.length > 0,
  });
}

export function usePendingFacts(universeId: string) {
  return useQuery({
    queryKey: ['universes', universeId, 'pending-facts'],
    queryFn: () =>
      getJson<UniversePendingFact[]>(
        `/api/universes/${encodeURIComponent(universeId)}/pending-facts`,
      ),
    enabled: universeId.length > 0,
  });
}

/** POST bible approve; caller should invalidate the universe's pending-facts key. */
export function approveFacts(
  storyId: string,
  variant: string,
  acceptedLineIds: string[],
): Promise<{ appended: number }> {
  return postJson(
    `/api/stories/${encodeURIComponent(storyId)}/bible/${encodeURIComponent(variant)}/approve`,
    { acceptedLineIds },
  );
}

// ---------------------------------------------------------------------------
// Voices (global catalog — library/voices.json)
// ---------------------------------------------------------------------------

export function useVoices() {
  return useQuery({
    queryKey: ['voices'],
    queryFn: () => getJson<VoiceDto[]>('/api/voices'),
    staleTime: 30_000,
  });
}

/** Enqueues a previewVoice job; the rendered mp3 shows up at voicePreviewUrl(id). */
export function previewVoice(voiceId: string): Promise<{ jobId: string }> {
  return postJson<{ jobId: string }>(
    `/api/voices/${encodeURIComponent(voiceId)}/preview`,
    undefined,
  );
}

export function usePreviewVoice() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: previewVoice,
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['jobs'] }),
  });
}

/** TTS engines this build has registered — the "engine" select in the add-voice dialog. */
export function useVoiceProviders() {
  return useQuery({
    queryKey: ['voice-providers'],
    queryFn: () => getJson<VoiceProviderDto[]>('/api/voices/providers'),
    staleTime: Infinity,
  });
}

/** Every catalog mutation ends the same way: the cards re-read /api/voices. */
function useVoiceMutation<TVariables>(mutationFn: (variables: TVariables) => Promise<unknown>) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn,
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['voices'] }),
  });
}

/** Create or replace a catalog entry (the id is immutable, so editing reuses this). */
export function useSaveVoice() {
  return useVoiceMutation((body: VoiceCreate) => postJson<VoiceDto>('/api/voices', body));
}

export function usePatchVoice() {
  return useVoiceMutation(({ id, patch }: { id: string; patch: VoicePatch }) =>
    patchJson<VoiceDto>(`/api/voices/${encodeURIComponent(id)}`, patch),
  );
}

/** The reference wav may back several entries, so it only goes when explicitly asked for. */
export function useDeleteVoice() {
  return useVoiceMutation(({ id, deleteWav }: { id: string; deleteWav: boolean }) =>
    del(`/api/voices/${encodeURIComponent(id)}${deleteWav ? '?deleteWav=true' : ''}`),
  );
}

export function useSetDefaultVoice() {
  return useVoiceMutation((id: string) => putJson<void>('/api/voices/default', { id }));
}

/** Multipart wav upload; the server drops the now-stale preview and conditionals cache. */
export function useUploadVoiceReference() {
  return useVoiceMutation(
    ({ id, file, onProgress }: { id: string; file: File; onProgress: (pct: number) => void }) =>
      uploadVoiceReference(id, file, onProgress),
  );
}

// ---------------------------------------------------------------------------
// Status
// ---------------------------------------------------------------------------

export function useStatus() {
  return useQuery({
    queryKey: ['status'],
    queryFn: () => getJson<StatusDto>('/api/status'),
    staleTime: 60_000,
  });
}
