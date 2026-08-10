// TanStack Query hooks — the only data-access layer route components use.
// Query keys: ['stories'] | ['stories', id] | ['jobs'] | ['jobs', id]
//           | ['draft', storyId, variant] | ['universes']
//           | ['universes', id, 'files', name] | ['universes', id, 'pending-facts']
//           | ['voices'] | ['voice-gallery', 'languages' | 'offers', locale] | ['status']

import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  del,
  getJson,
  getWithETag,
  patchJson,
  postJson,
  putJson,
  uploadVoiceRecording,
} from './client';
import type {
  JobDto,
  SceneDto,
  StatusDto,
  StoryDetail,
  StorySummary,
  UniverseListItem,
  UniversePendingFact,
  VoiceDto,
  VoiceLanguageDto,
  VoiceOfferDto,
  VoicePatch,
} from './types';

/** Media URL helpers live in client.ts; re-exported so routes need one import. */
export { voiceOfferSampleUrl, voicePreviewUrl } from './client';

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
  /** renderTts: who reads it and how. The story remembers both for next time. */
  voiceId?: string;
  delivery?: string;
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

/**
 * What this variant was last rendered with. Voice and delivery are render choices, not part of
 * the story — the session file just remembers them so the next render can offer the same again.
 */
export function useRenderChoice(storyId: string, variant: string) {
  return useQuery({
    queryKey: ['render-choice', storyId, variant],
    queryFn: async (): Promise<{ voice: string; delivery: string }> => {
      const { text } = await getWithETag(
        `/api/stories/${encodeURIComponent(storyId)}/session/${encodeURIComponent(variant)}`,
      );
      const doc = JSON.parse(text) as { voice?: string; delivery?: string };
      return { voice: doc.voice ?? '', delivery: doc.delivery ?? 'natural' };
    },
    enabled: storyId.length > 0 && variant.length > 0,
    staleTime: 0,
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

/**
 * Re-records an installed voice's sample. Not what the play button does — a voice always
 * has a sample, so playing is a static file read; this is for when you want a fresh one.
 */
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

// ---- the shelf -------------------------------------------------------------

/** Step one of "add a voice": which languages have voices to offer. */
export function useVoiceLanguages() {
  return useQuery({
    queryKey: ['voice-gallery', 'languages'],
    queryFn: () => getJson<VoiceLanguageDto[]>('/api/voice-gallery/languages'),
    staleTime: Infinity,
  });
}

export function useVoiceOffers(locale: string) {
  return useQuery({
    queryKey: ['voice-gallery', 'offers', locale],
    queryFn: () => getJson<VoiceOfferDto[]>(`/api/voice-gallery?locale=${encodeURIComponent(locale)}`),
    enabled: locale.length > 0,
    staleTime: Infinity,
  });
}

/** Every catalog mutation ends the same way: the list re-reads /api/voices. */
function useVoiceMutation<TVariables, TResult = unknown>(
  mutationFn: (variables: TVariables) => Promise<TResult>,
) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn,
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['voices'] }),
  });
}

/**
 * Installs a shelf voice. Returns the job to watch: downloading and unpacking happen there,
 * and the voice is only real once it finishes — which is also when its sample exists.
 */
export function useInstallVoice() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (body: { key: string; name?: string }) =>
      postJson<{ jobId: string; voiceId: string }>('/api/voices/install', body),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['jobs'] }),
  });
}

/** Same install path, with the reader's own recording instead of a shelf asset. */
export function useUploadVoiceRecording() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({
      name,
      locale,
      file,
      onProgress,
    }: {
      name: string;
      locale: string;
      file: File;
      onProgress: (pct: number) => void;
    }) => uploadVoiceRecording(name, locale, file, onProgress),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['jobs'] }),
  });
}

export function usePatchVoice() {
  return useVoiceMutation(({ id, patch }: { id: string; patch: VoicePatch }) =>
    patchJson<VoiceDto>(`/api/voices/${encodeURIComponent(id)}`, patch),
  );
}

/** The reference recording may back several voices, so it only goes when explicitly asked for. */
export function useDeleteVoice() {
  return useVoiceMutation(({ id, deleteWav }: { id: string; deleteWav: boolean }) =>
    del(`/api/voices/${encodeURIComponent(id)}${deleteWav ? '?deleteWav=true' : ''}`),
  );
}

export function useSetDefaultVoice() {
  return useVoiceMutation((id: string) => putJson<void>('/api/voices/default', { id }));
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
