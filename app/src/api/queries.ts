// TanStack Query hooks — the only data-access layer route components use.
// Query keys: ['stories'] | ['stories', id] | ['jobs'] | ['jobs', id]
//           | ['draft', storyId, variant] | ['universes']
//           | ['universes', id, 'files', name] | ['universes', id, 'pending-facts']
//           | ['status']

import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { getJson, getWithETag, postJson } from './client';
import type {
  JobDto,
  SceneDto,
  StatusDto,
  StoryDetail,
  StorySummary,
  UniverseListItem,
  UniversePendingFact,
} from './types';

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
// Status
// ---------------------------------------------------------------------------

export function useStatus() {
  return useQuery({
    queryKey: ['status'],
    queryFn: () => getJson<StatusDto>('/api/status'),
    staleTime: 60_000,
  });
}
