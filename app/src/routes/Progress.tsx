/* Progress — widok postępu zadania dla wariantu opowieści.
   Cały stan pochodzi z odpytywania API (useJobs + szczegóły zadania),
   więc przeładowanie strony niczego nie gubi. */
import { useEffect, useMemo, useRef, useState } from 'react';
import { Link, useParams } from 'react-router';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { getJson, postJson } from '../api/client';
import { useJobs, useStory } from '../api/queries';
import type { JobDto } from '../api/types';
import { strings } from '../strings';

/** Kształt odpowiedzi GET /api/jobs/{id} (Program.cs) — ogon logu przychodzi w polu `log`. */
interface JobDetailResponse {
  id: string;
  type: string;
  state: string;
  stage: string;
  storyId: string;
  variant: string;
  sceneId?: string | null;
  createdUtc: string;
  startedUtc?: string | null;
  finishedUtc?: string | null;
  costUsd?: number | null;
  error?: string | null;
  elapsedSeconds?: number | null;
  log?: string[];
  logTail?: string[];
}

const STATE_LABELS: Record<string, string> = {
  queued: strings.jobQueued,
  running: strings.jobRunning,
  succeeded: strings.jobSucceeded,
  failed: strings.jobFailed,
  cancelled: strings.jobCancelled,
};

const TYPE_LABELS: Record<string, string> = {
  transcribe: strings.jobTypeTranscribe,
  generate: strings.jobTypeGenerate,
  regenScene: strings.jobTypeRegenScene,
  verify: strings.jobTypeVerify,
  renderTts: strings.jobTypeRenderTts,
};

interface ChecklistItem {
  label: string;
  detail?: string;
}

/**
 * Lista etapów wyprowadzona z job.stage (JobRunnerService):
 * generate: extract → outline → "scene sN/M" → verify → bible (→ "done"),
 * regenScene: "regen sN" → verify, pozostałe typy mają jeden etap.
 */
function buildChecklist(type: string, stage: string, state: string): { items: ChecklistItem[]; current: number } {
  const finished = state === 'succeeded' || stage === 'done';
  if (type === 'generate') {
    const items: ChecklistItem[] = [
      { label: strings.stageExtract },
      { label: strings.stageOutlineStep },
      { label: strings.stageScenes },
      { label: strings.stageVerify },
      { label: strings.stageBible },
    ];
    let current = 0;
    if (stage === 'outline') current = 1;
    else if (stage.startsWith('scene')) {
      current = 2;
      const m = stage.match(/^scene s(\d+)\/(\d+)$/);
      items[2].detail = m ? `scena ${m[1]} z ${m[2]}` : stage.replace(/^scene\s*/, '');
    } else if (stage === 'verify') current = 3;
    else if (stage === 'bible') current = 4;
    if (finished) current = items.length;
    return { items, current };
  }
  if (type === 'regenScene') {
    const m = stage.match(/^regen (s\d+)/);
    const items: ChecklistItem[] = [
      { label: strings.jobTypeRegenScene, detail: m ? m[1] : undefined },
      { label: strings.stageVerify },
    ];
    let current = stage === 'verify' ? 1 : 0;
    if (finished) current = items.length;
    return { items, current };
  }
  const items: ChecklistItem[] = [{ label: TYPE_LABELS[type] ?? (stage || type) }];
  return { items, current: finished ? items.length : 0 };
}

function formatElapsed(totalSeconds: number): string {
  const h = Math.floor(totalSeconds / 3600);
  const m = Math.floor((totalSeconds % 3600) / 60);
  const s = totalSeconds % 60;
  return h > 0
    ? `${h}:${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}`
    : `${m}:${String(s).padStart(2, '0')}`;
}

function stateBadgeClass(state: string): string {
  switch (state) {
    case 'running':
      return 'bg-violet-100 text-violet-700';
    case 'queued':
      return 'bg-amber-100 text-amber-700';
    case 'succeeded':
      return 'bg-emerald-100 text-emerald-700';
    case 'failed':
      return 'bg-rose-100 text-rose-700';
    case 'cancelled':
      return 'bg-stone-200 text-stone-600';
    default:
      return 'bg-stone-100 text-stone-600';
  }
}

function Spinner() {
  return (
    <span
      className="inline-block h-5 w-5 animate-spin rounded-full border-2 border-stone-300 border-t-violet-600"
      aria-hidden="true"
    />
  );
}

export default function Progress() {
  const { id = '', variant = '' } = useParams();
  const queryClient = useQueryClient();

  const jobsQuery = useJobs();
  const jobs = (jobsQuery.data ?? []) as JobDto[];
  const storyQuery = useStory(id);
  const storyTitle = (storyQuery.data as unknown as { title?: string } | undefined)?.title ?? id;

  // Najnowsze zadanie tego wariantu (transcribe ma variant "" i nigdy tu nie pasuje).
  const job = useMemo(
    () =>
      jobs
        .filter((j) => j.storyId === id && j.variant === variant)
        .sort((a, b) => (a.createdUtc < b.createdUtc ? 1 : a.createdUtc > b.createdUtc ? -1 : 0))[0],
    [jobs, id, variant],
  );

  const jobId = job?.id;
  const jobActive = job !== undefined && (job.state === 'queued' || job.state === 'running');

  // Szczegóły zadania (ogon logu, koszt); job.state w kluczu wymusza ostatnie
  // pobranie po zakończeniu, żeby log i koszt były kompletne.
  const detailQuery = useQuery({
    queryKey: ['progressJobDetail', jobId, job?.state],
    enabled: jobId !== undefined,
    refetchInterval: jobActive ? 2000 : false,
    queryFn: () => getJson<JobDetailResponse>(`/api/jobs/${jobId}`),
  });
  const detail = detailQuery.data;

  const type = detail?.type ?? job?.type ?? '';
  const state = detail?.state ?? job?.state ?? '';
  const stage = detail?.stage ?? job?.stage ?? '';
  const costUsd = detail?.costUsd ?? job?.costUsd;
  const error = detail?.error ?? job?.error;
  const startedUtc = detail?.startedUtc ?? job?.startedUtc;
  const finishedUtc = detail?.finishedUtc ?? job?.finishedUtc;
  const logTail: string[] = detail?.log ?? detail?.logTail ?? job?.logTail ?? [];

  // Licznik czasu — tyka co sekundę, dopóki zadanie trwa.
  const running = state === 'running';
  const [tick, setTick] = useState(0);
  useEffect(() => {
    if (!running) return;
    const timer = window.setInterval(() => setTick((n) => n + 1), 1000);
    return () => window.clearInterval(timer);
  }, [running]);

  const elapsedSeconds = useMemo(() => {
    if (!startedUtc) return null;
    const start = Date.parse(startedUtc);
    if (Number.isNaN(start)) return null;
    const end = finishedUtc ? Date.parse(finishedUtc) : Date.now();
    return Math.max(0, Math.floor((end - start) / 1000));
  }, [startedUtc, finishedUtc, tick]);

  // Log: monospace scrollback doklejony do dołu, chyba że użytkownik odscrollował.
  const logRef = useRef<HTMLDivElement | null>(null);
  const pinnedRef = useRef(true);
  const logKey = `${logTail.length}:${logTail[logTail.length - 1] ?? ''}`;
  useEffect(() => {
    const el = logRef.current;
    if (el && pinnedRef.current) el.scrollTop = el.scrollHeight;
  }, [logKey]);

  const cancelMutation = useMutation({
    mutationFn: (targetJobId: string) => postJson<{ id: string; state: string }>(`/api/jobs/${targetJobId}/cancel`, {}),
    onSuccess: () => {
      void queryClient.invalidateQueries();
    },
  });

  const header = (
    <header className="mb-6">
      <Link to={`/stories/${id}`} className="text-sm text-stone-500 hover:text-stone-700">
        ← {storyTitle}
      </Link>
      <div className="mt-1 flex flex-wrap items-center gap-2">
        <h1 className="text-2xl font-semibold text-stone-900">
          {TYPE_LABELS[type] ?? strings.progressTitle}
        </h1>
        <span className="rounded bg-violet-100 px-2 py-0.5 text-sm font-medium text-violet-700">{variant}</span>
      </div>
    </header>
  );

  if (jobsQuery.isLoading) {
    return (
      <div className="mx-auto w-full max-w-3xl px-4 py-6">
        {header}
        <div className="flex items-center gap-3 rounded-xl border border-stone-200 bg-white p-6 shadow-sm">
          <Spinner />
          <span className="text-stone-600">{strings.progressLoadingJobs}</span>
        </div>
      </div>
    );
  }

  if (!job) {
    return (
      <div className="mx-auto w-full max-w-3xl px-4 py-6">
        {header}
        <div className="rounded-xl border border-stone-200 bg-white p-8 text-center shadow-sm">
          <p className="text-stone-600">
            {strings.progressNoJobs}
          </p>
          {jobsQuery.isError && (
            <p className="mt-2 text-sm text-rose-600">{strings.jobsLoadError}</p>
          )}
          <div className="mt-6 flex flex-col gap-3 sm:flex-row sm:justify-center">
            <Link
              to={`/stories/${id}`}
              className="rounded-lg border border-stone-300 px-4 py-2.5 text-sm font-medium text-stone-700 hover:bg-stone-100"
            >
              Wróć do opowieści
            </Link>
            <Link
              to={`/stories/${id}/v/${variant}/builder`}
              className="rounded-lg border border-stone-300 px-4 py-2.5 text-sm font-medium text-stone-700 hover:bg-stone-100"
            >
              Otwórz kreator
            </Link>
            <Link
              to={`/stories/${id}/v/${variant}/draft`}
              className="rounded-lg border border-stone-300 px-4 py-2.5 text-sm font-medium text-stone-700 hover:bg-stone-100"
            >
              Zobacz szkic
            </Link>
          </div>
        </div>
      </div>
    );
  }

  const { items, current } = buildChecklist(type, stage, state);

  return (
    <div className="mx-auto w-full max-w-3xl px-4 py-6">
      {header}

      <div className="space-y-5">
        <div className="rounded-xl border border-stone-200 bg-white p-5 shadow-sm sm:p-6">
          <div className="flex flex-wrap items-center gap-x-4 gap-y-2 text-sm text-stone-600">
            <span className={`rounded-full px-2.5 py-0.5 text-xs font-medium ${stateBadgeClass(state)}`}>
              {STATE_LABELS[state] ?? state}
            </span>
            {elapsedSeconds !== null && (
              <span>
                Czas: <span className="font-mono">{formatElapsed(elapsedSeconds)}</span>
              </span>
            )}
            {typeof costUsd === 'number' && (
              <span>Koszt: {costUsd.toLocaleString('pl-PL', { style: 'currency', currency: 'USD' })}</span>
            )}
          </div>

          {state === 'queued' && (
            <p className="mt-3 text-sm text-stone-500">
              {strings.jobQueuedNote}
            </p>
          )}

          <ol className="mt-5 space-y-3">
            {items.map((item, index) => {
              const isDone = index < current;
              const isCurrent = index === current;
              return (
                <li key={item.label} className="flex items-center gap-3">
                  {isDone ? (
                    <span className="flex h-6 w-6 shrink-0 items-center justify-center rounded-full bg-emerald-100 text-sm text-emerald-700">
                      ✓
                    </span>
                  ) : isCurrent && running ? (
                    <span className="flex h-6 w-6 shrink-0 items-center justify-center">
                      <Spinner />
                    </span>
                  ) : isCurrent && state === 'failed' ? (
                    <span className="flex h-6 w-6 shrink-0 items-center justify-center rounded-full bg-rose-100 text-sm text-rose-700">
                      ✕
                    </span>
                  ) : isCurrent && state === 'cancelled' ? (
                    <span className="flex h-6 w-6 shrink-0 items-center justify-center rounded-full bg-stone-200 text-sm text-stone-600">
                      –
                    </span>
                  ) : isCurrent ? (
                    <span className="h-6 w-6 shrink-0 animate-pulse rounded-full border-2 border-amber-400" />
                  ) : (
                    <span className="h-6 w-6 shrink-0 rounded-full border-2 border-stone-200" />
                  )}
                  <span
                    className={
                      isDone ? 'text-stone-500' : isCurrent ? 'font-medium text-stone-900' : 'text-stone-400'
                    }
                  >
                    {item.label}
                    {item.detail && isCurrent && (
                      <span className="ml-2 text-sm font-normal text-violet-600">{item.detail}</span>
                    )}
                  </span>
                </li>
              );
            })}
          </ol>

          {jobActive && (
            <div className="mt-5 border-t border-stone-100 pt-4">
              <button
                type="button"
                onClick={() => {
                  if (jobId && window.confirm(strings.jobCancelConfirm))
                    cancelMutation.mutate(jobId);
                }}
                disabled={cancelMutation.isPending}
                className="min-h-11 rounded-lg border border-rose-200 px-4 py-2 text-sm font-medium text-rose-700 hover:bg-rose-50 disabled:opacity-50"
              >
                Przerwij zadanie
              </button>
            </div>
          )}
        </div>

        {state === 'succeeded' && (
          <div className="grid gap-3 sm:grid-cols-2">
            <Link
              to={`/stories/${id}/v/${variant}/draft`}
              className="flex min-h-14 items-center justify-center rounded-xl bg-amber-600 px-6 text-lg font-semibold text-white shadow-sm hover:bg-amber-700"
            >
              Przejrzyj szkic
            </Link>
            <Link
              to="/listen"
              className="flex min-h-14 items-center justify-center rounded-xl bg-violet-600 px-6 text-lg font-semibold text-white shadow-sm hover:bg-violet-700"
            >
              Słuchaj
            </Link>
          </div>
        )}

        {state === 'failed' && (
          <div className="rounded-xl border border-rose-200 bg-rose-50 p-4">
            <p className="text-sm font-medium text-rose-800">
              {strings.jobFailedNote}
            </p>
            {error && (
              <pre className="mt-2 overflow-x-auto whitespace-pre-wrap font-mono text-xs text-rose-700">{error}</pre>
            )}
          </div>
        )}

        {logTail.length > 0 && (
          <div className="rounded-xl border border-stone-200 bg-white p-4 shadow-sm sm:p-5">
            <h2 className="mb-2 text-sm font-medium text-stone-500">{strings.jobLog}</h2>
            <div
              ref={logRef}
              onScroll={(e) => {
                const el = e.currentTarget;
                pinnedRef.current = el.scrollHeight - el.scrollTop - el.clientHeight < 48;
              }}
              className="h-64 overflow-y-auto rounded-lg bg-stone-950 p-3 font-mono text-xs leading-relaxed text-stone-200"
            >
              {logTail.map((line, i) => (
                <div key={i} className="whitespace-pre-wrap break-words">
                  {line}
                </div>
              ))}
            </div>
          </div>
        )}
      </div>
    </div>
  );
}
