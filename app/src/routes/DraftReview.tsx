/* DraftReview — przegląd i edycja szkicu, wariant po scenie (desktop-first).
   Sceny przychodzą sparsowane z backendu (useDraft) — klient nigdy nie parsuje
   draft.md. Zapis sceny idzie przez PUT z If-Match; konflikt otwiera dialog. */
import { useEffect, useMemo, useRef, useState } from 'react';
import type { ReactNode } from 'react';
import { Link, useNavigate, useParams } from 'react-router';
import * as Dialog from '@radix-ui/react-dialog';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { ApiError, ConflictError, del, getWithETag, putWithETag } from '../api/client';
import { enqueueJob, useDraft, useJobs } from '../api/queries';
import type { JobDto, SceneDto, VerifyFlag } from '../api/types';
import { strings } from '../strings';

// Kopia zapasowa sceny sprzed regeneracji (draft.<v>.<sceneId>.prev.md) — GET zwraca
// tekst albo 404 (wtedy przycisk "Przywróć poprzednią" się nie pokazuje), DELETE ją porzuca.
function prevPath(storyId: string, variant: string, sceneId: string): string {
  return `/api/stories/${storyId}/prev/${variant}/${sceneId}`;
}

/** Mapuje slug reguły weryfikacji na ludzką etykietę; nieznany slug wraca bez zmian. */
function ruleLabel(rule: string): string {
  switch (rule) {
    case 'given-drift':
      return strings.verifyRuleGivenDrift;
    case 'register':
      return strings.verifyRuleRegister;
    case 'anachronism':
      return strings.verifyRuleAnachronism;
    case 'naming':
      return strings.verifyRuleNaming;
    case 'hard-rules':
      return strings.verifyRuleHardRules;
    case 'canon':
      return strings.verifyRuleCanon;
    case 'tone':
      return strings.verifyRuleTone;
    default:
      return rule;
  }
}

/** Składa feedbackNote z zaznaczonych uwag weryfikacji i własnego tekstu użytkownika. */
function composeFeedbackNote(
  flags: readonly VerifyFlag[],
  checked: readonly boolean[],
  freeText: string,
): string {
  const lines = flags
    .filter((_, i) => checked[i] === true)
    .map((flag) => `- [${ruleLabel(flag.rule)}] ${flag.detail}`);
  const extra = freeText.trim();
  if (lines.length === 0) return extra;
  const base = `Zastosuj następujące uwagi weryfikacji:\n${lines.join('\n')}`;
  return extra.length > 0 ? `${base}\n\nDodatkowo: ${extra}` : base;
}

/** Wyciąga cytat z detail flagi weryfikacji: "..." albo „..." — proste dopasowanie. */
function extractQuote(detail: string): string | null {
  const straight = detail.match(/"([^"]+)"/);
  if (straight && straight[1].trim().length > 0) return straight[1];
  const typographic = detail.match(/[„“]([^„“”"]+)[”"]/);
  if (typographic && typographic[1].trim().length > 0) return typographic[1];
  return null;
}

/** Prosty string-match cytatów z flag w prozie; trafienia owinięte w <mark>. */
function highlightProse(text: string, flags: readonly VerifyFlag[]): ReactNode[] {
  const ranges: Array<{ start: number; end: number }> = [];
  for (const flag of flags) {
    const quote = extractQuote(flag.detail);
    if (!quote) continue;
    const index = text.indexOf(quote);
    if (index >= 0) ranges.push({ start: index, end: index + quote.length });
  }
  ranges.sort((a, b) => a.start - b.start);
  const merged: Array<{ start: number; end: number }> = [];
  for (const range of ranges) {
    const last = merged[merged.length - 1];
    if (last && range.start < last.end) last.end = Math.max(last.end, range.end);
    else merged.push({ ...range });
  }
  const parts: ReactNode[] = [];
  let position = 0;
  merged.forEach((range, i) => {
    if (range.start > position) parts.push(text.slice(position, range.start));
    parts.push(
      <mark key={i} className="rounded bg-rose-100 px-0.5 text-rose-900">
        {text.slice(range.start, range.end)}
      </mark>,
    );
    position = range.end;
  });
  if (position < text.length || parts.length === 0) parts.push(text.slice(position));
  return parts;
}

function Spinner() {
  return (
    <span
      className="inline-block h-4 w-4 animate-spin rounded-full border-2 border-stone-300 border-t-violet-600"
      aria-hidden="true"
    />
  );
}

const actionButtonClass =
  'min-h-9 rounded-lg border border-stone-300 px-3 py-1.5 text-sm font-medium text-stone-700 hover:bg-stone-100 disabled:opacity-50';

function AutogrowTextarea(props: { value: string; onChange: (value: string) => void }) {
  const ref = useRef<HTMLTextAreaElement | null>(null);
  useEffect(() => {
    const el = ref.current;
    if (!el) return;
    el.style.height = 'auto';
    el.style.height = `${el.scrollHeight + 2}px`;
  }, [props.value]);
  return (
    <textarea
      ref={ref}
      value={props.value}
      onChange={(e) => props.onChange(e.target.value)}
      spellCheck={false}
      autoFocus
      className="w-full resize-none overflow-hidden rounded-lg border border-stone-300 bg-white p-4 font-serif text-base leading-relaxed text-stone-900 focus:border-amber-500 focus:outline-none focus:ring-2 focus:ring-amber-200"
    />
  );
}

interface SceneCardProps {
  storyId: string;
  variant: string;
  scene: SceneDto;
  value: string;
  dirty: boolean;
  editing: boolean;
  saving: boolean;
  restoring: boolean;
  savedFlash: boolean;
  regenActive: boolean;
  onChange: (text: string) => void;
  onSave: () => void;
  onStartEdit: () => void;
  onStopEdit: () => void;
  onDiscard: () => void;
  onRegen: () => void;
  onRestore: (prevText: string) => void;
}

function SceneCard(props: SceneCardProps) {
  const { scene } = props;
  const [menuOpen, setMenuOpen] = useState(false);

  // Sonda pliku draft.<v>.<sceneId>.prev.md — brak trasy albo pliku ⇒ null ⇒ bez przycisku.
  const prevQuery = useQuery({
    queryKey: ['draftPrev', props.storyId, props.variant, scene.sceneId],
    retry: false,
    queryFn: async () => {
      try {
        return await getWithETag(prevPath(props.storyId, props.variant, scene.sceneId));
      } catch (error) {
        if (error instanceof ApiError) return null;
        throw error;
      }
    },
  });
  const prevText = prevQuery.data?.text ?? null;

  const toggleEditLabel = props.editing ? strings.preview : strings.edit;

  const actions = (
    <>
      <button
        type="button"
        className={actionButtonClass}
        onClick={() => {
          setMenuOpen(false);
          if (props.editing) props.onStopEdit();
          else props.onStartEdit();
        }}
      >
        {toggleEditLabel}
      </button>
      <button
        type="button"
        className="min-h-9 rounded-lg border border-violet-200 px-3 py-1.5 text-sm font-medium text-violet-700 hover:bg-violet-50 disabled:opacity-50"
        onClick={() => {
          setMenuOpen(false);
          props.onRegen();
        }}
        disabled={props.regenActive}
      >
        Regeneruj
      </button>
      {prevText !== null && (
        <button
          type="button"
          className={actionButtonClass}
          disabled={props.restoring}
          onClick={() => {
            setMenuOpen(false);
            if (window.confirm(strings.sceneRestoreConfirm))
              props.onRestore(prevText);
          }}
        >
          {props.restoring && (
            <span className="mr-2 inline-flex align-middle">
              <Spinner />
            </span>
          )}
          Przywróć poprzednią
        </button>
      )}
    </>
  );

  return (
    <section
      id={`scene-${scene.sceneId}`}
      className={`relative scroll-mt-6 rounded-xl border border-stone-200 bg-white shadow-sm ${
        props.regenActive ? 'opacity-60' : ''
      }`}
    >
      {props.regenActive && (
        <div className="absolute inset-0 z-10 flex items-center justify-center rounded-xl bg-white/60">
          <div className="flex items-center gap-3 rounded-full bg-white px-4 py-2 shadow">
            <Spinner />
            <span className="text-sm text-stone-600">{strings.sceneRegenInProgress}</span>
          </div>
        </div>
      )}

      <header className="flex items-start justify-between gap-3 border-b border-stone-100 px-4 py-3 sm:px-6">
        <div className="min-w-0">
          <div className="font-mono text-xs text-stone-400">{scene.sceneId}</div>
          <h2 className="truncate text-lg font-semibold text-stone-900">
            {scene.title || strings.untitled}
          </h2>
        </div>
        {/* Desktop: akcje widoczne; telefon: schowane za menu „⋯" */}
        <div className="hidden shrink-0 items-center gap-2 sm:flex">{actions}</div>
        <button
          type="button"
          aria-label={strings.moreActions}
          onClick={() => setMenuOpen((open) => !open)}
          className="flex h-11 w-11 shrink-0 items-center justify-center rounded-lg text-xl text-stone-500 hover:bg-stone-100 sm:hidden"
        >
          ⋯
        </button>
      </header>

      {menuOpen && <div className="flex flex-wrap gap-2 border-b border-stone-100 px-4 py-2 sm:hidden">{actions}</div>}

      {scene.flags.length > 0 && (
        <ul className="space-y-1 border-b border-rose-100 bg-rose-50 px-4 py-3 sm:px-6">
          {scene.flags.map((flag, i) => (
            <li key={i} className="text-sm text-rose-800">
              <span
                title={flag.rule}
                className="mr-2 inline-block rounded bg-rose-200 px-1.5 py-0.5 text-xs font-medium"
              >
                {ruleLabel(flag.rule)}
              </span>
              {flag.detail}
            </li>
          ))}
        </ul>
      )}

      <div className="px-4 py-4 sm:px-6 sm:py-5">
        {props.editing ? (
          <AutogrowTextarea value={props.value} onChange={props.onChange} />
        ) : (
          <div className="whitespace-pre-wrap font-serif text-base leading-relaxed text-stone-900">
            {highlightProse(props.value, scene.flags)}
          </div>
        )}

        {(props.editing || props.dirty || props.savedFlash) && (
          <div className="mt-3 flex flex-wrap items-center gap-2">
            {props.dirty && (
              <>
                <button
                  type="button"
                  onClick={props.onSave}
                  disabled={props.saving}
                  className="min-h-10 rounded-lg bg-amber-600 px-4 py-2 text-sm font-semibold text-white hover:bg-amber-700 disabled:opacity-50"
                >
                  Zapisz
                </button>
                <button
                  type="button"
                  onClick={() => {
                    if (window.confirm(strings.discardConfirm)) props.onDiscard();
                  }}
                  disabled={props.saving}
                  className={actionButtonClass}
                >
                  Odrzuć zmiany
                </button>
              </>
            )}
            {props.saving && <Spinner />}
            {props.savedFlash && <span className="text-sm text-emerald-600">{strings.saved}</span>}
            {props.dirty && !props.saving && (
              <span className="text-sm text-amber-600">{strings.unsavedChanges}</span>
            )}
          </div>
        )}
      </div>
    </section>
  );
}

export default function DraftReview() {
  const { id = '', variant = '' } = useParams();
  const navigate = useNavigate();
  const queryClient = useQueryClient();

  const draftPath = `/api/stories/${id}/draft/${variant}`;
  const draftQuery = useDraft(id, variant);
  const rawDraft: unknown = draftQuery.data;

  // useDraft może zwracać SceneDto[] albo { scenes, etag } — obsługujemy oba kształty.
  const scenes: SceneDto[] = useMemo(() => {
    if (Array.isArray(rawDraft)) return rawDraft as SceneDto[];
    if (rawDraft && typeof rawDraft === 'object' && Array.isArray((rawDraft as { scenes?: unknown }).scenes))
      return (rawDraft as { scenes: SceneDto[] }).scenes;
    return [];
  }, [rawDraft]);

  const hookETag =
    rawDraft && typeof rawDraft === 'object' && !Array.isArray(rawDraft)
      ? ((rawDraft as { etag?: string }).etag ?? null)
      : null;

  // ETag szkicu do If-Match. Gdy hook go nie niesie, dociągamy nagłówek tuż po
  // każdym odświeżeniu danych (okno wyścigu jest wąskie, a PUT i tak zgłosi 409).
  const etagRef = useRef<string | null>(null);
  useEffect(() => {
    if (hookETag) {
      etagRef.current = hookETag;
      return;
    }
    if (!draftQuery.dataUpdatedAt) return;
    let cancelled = false;
    getWithETag(draftPath)
      .then((r) => {
        if (!cancelled) etagRef.current = r.etag;
      })
      .catch(() => {});
    return () => {
      cancelled = true;
    };
  }, [hookETag, draftQuery.dataUpdatedAt, draftPath]);

  const [edits, setEdits] = useState<Record<string, string>>({});
  const [editingIds, setEditingIds] = useState<Record<string, boolean>>({});
  const [savedFlash, setSavedFlash] = useState<string | null>(null);
  const [conflict, setConflict] = useState<{ sceneId: string; etag: string } | null>(null);
  const [regenFor, setRegenFor] = useState<string | null>(null);
  const [feedbackNote, setFeedbackNote] = useState('');
  // Zaznaczenia uwag weryfikacji w dialogu regeneracji — tablica równoległa do flags sceny.
  const [checkedFlags, setCheckedFlags] = useState<boolean[]>([]);

  const jobs = (useJobs().data ?? []) as JobDto[];

  // Aktywne regeneracje per scena — karta przygaszona ze spinnerem.
  const activeRegenScenes = useMemo(() => {
    const set = new Set<string>();
    for (const j of jobs)
      if (
        j.storyId === id &&
        j.variant === variant &&
        j.type === 'regenScene' &&
        j.sceneId &&
        (j.state === 'queued' || j.state === 'running')
      )
        set.add(j.sceneId);
    return set;
  }, [jobs, id, variant]);

  const variantBusy = jobs.some(
    (j) => j.storyId === id && j.variant === variant && (j.state === 'queued' || j.state === 'running'),
  );

  // Koniec regeneracji: dociągamy świeży szkic i porzucamy lokalne edycje tych scen.
  const prevActiveRef = useRef<string[]>([]);
  useEffect(() => {
    const current = [...activeRegenScenes];
    const finished = prevActiveRef.current.filter((sceneId) => !current.includes(sceneId));
    prevActiveRef.current = current;
    if (finished.length === 0) return;
    setEdits((prev) => {
      const next = { ...prev };
      for (const sceneId of finished) delete next[sceneId];
      return next;
    });
    void queryClient.invalidateQueries();
  }, [activeRegenScenes, queryClient]);

  const saveMutation = useMutation({
    mutationFn: async (vars: { sceneId: string; text: string }) => {
      const etag = etagRef.current ?? (await getWithETag(draftPath)).etag;
      const result = await putWithETag(`${draftPath}/scenes/${vars.sceneId}`, vars.text, etag);
      return { sceneId: vars.sceneId, etag: result.etag };
    },
    onSuccess: ({ sceneId, etag }) => {
      etagRef.current = etag;
      setEdits((prev) => {
        const next = { ...prev };
        delete next[sceneId];
        return next;
      });
      setEditingIds((prev) => ({ ...prev, [sceneId]: false }));
      setSavedFlash(sceneId);
      window.setTimeout(() => setSavedFlash((s) => (s === sceneId ? null : s)), 2500);
      void queryClient.invalidateQueries();
    },
    onError: (error, vars) => {
      if (error instanceof ConflictError) setConflict({ sceneId: vars.sceneId, etag: error.currentETag });
    },
  });

  // Przywrócenie: tekst prev.md idzie jako treść sceny (backend odetnie wiodący
  // marker — StripLeadingMarker), potem opcjonalne usunięcie pliku prev.
  const restoreMutation = useMutation({
    mutationFn: async (vars: { sceneId: string; text: string }) => {
      const etag = etagRef.current ?? (await getWithETag(draftPath)).etag;
      const result = await putWithETag(`${draftPath}/scenes/${vars.sceneId}`, vars.text, etag);
      try {
        await del(prevPath(id, variant, vars.sceneId));
      } catch {
        // porządkowe — nieudane usunięcie kopii nie unieważnia przywrócenia
      }
      return { sceneId: vars.sceneId, etag: result.etag };
    },
    onSuccess: ({ sceneId, etag }) => {
      etagRef.current = etag;
      setEdits((prev) => {
        const next = { ...prev };
        delete next[sceneId];
        return next;
      });
      void queryClient.invalidateQueries();
    },
    onError: (error, vars) => {
      if (error instanceof ConflictError) setConflict({ sceneId: vars.sceneId, etag: error.currentETag });
    },
  });

  const regenMutation = useMutation({
    mutationFn: (vars: { sceneId: string; note: string }) =>
      enqueueJob(id, {
        type: 'regenScene',
        variant,
        sceneId: vars.sceneId,
        feedbackNote: vars.note.length > 0 ? vars.note : undefined,
      }),
    onSuccess: () => {
      setRegenFor(null);
      setFeedbackNote('');
      setCheckedFlags([]);
      void queryClient.invalidateQueries();
    },
  });

  // Scena, której dotyczy otwarty dialog regeneracji (jej flagi zasilają checklistę).
  const regenScene = regenFor !== null ? (scenes.find((s) => s.sceneId === regenFor) ?? null) : null;
  const regenFlags = regenScene?.flags ?? [];
  const regenCanSubmit = checkedFlags.some(Boolean) || feedbackNote.trim().length > 0;

  const renderTtsMutation = useMutation({
    mutationFn: () => enqueueJob(id, { type: 'renderTts', variant }),
    onSuccess: () => {
      void queryClient.invalidateQueries();
      void navigate(`/stories/${id}/v/${variant}/progress`);
    },
  });

  function resolveConflict(keepMine: boolean) {
    if (!conflict) return;
    etagRef.current = conflict.etag;
    if (!keepMine) {
      const { sceneId } = conflict;
      setEdits((prev) => {
        const next = { ...prev };
        delete next[sceneId];
        return next;
      });
    }
    setConflict(null);
    void queryClient.invalidateQueries();
  }

  const header = (
    <header className="mb-6 flex flex-wrap items-end justify-between gap-3">
      <div className="min-w-0">
        <Link to={`/stories/${id}`} className="text-sm text-stone-500 hover:text-stone-700">
          ← {strings.backToStory}
        </Link>
        <h1 className="mt-1 flex flex-wrap items-center gap-2 text-2xl font-semibold text-stone-900">
          {strings.draftReviewTitle}{' '}
          <span className="rounded bg-violet-100 px-2 py-0.5 text-sm font-medium text-violet-700">{variant}</span>
        </h1>
      </div>
      <div className="flex items-center gap-2">
        <Link
          to={`/stories/${id}/v/${variant}/progress`}
          className="inline-flex min-h-11 items-center rounded-lg border border-stone-300 px-4 py-2 text-sm font-medium text-stone-700 hover:bg-stone-100"
        >
          Postęp
        </Link>
        {scenes.length > 0 && (
          <button
            type="button"
            onClick={() => renderTtsMutation.mutate()}
            disabled={variantBusy || renderTtsMutation.isPending}
            className="min-h-11 rounded-lg bg-amber-600 px-4 py-2 text-sm font-semibold text-white hover:bg-amber-700 disabled:opacity-50"
          >
            Renderuj audio
          </button>
        )}
      </div>
    </header>
  );

  if (draftQuery.isLoading) {
    return (
      <div className="mx-auto w-full max-w-6xl px-4 py-6">
        {header}
        <div className="flex items-center gap-3 rounded-xl border border-stone-200 bg-white p-6 shadow-sm">
          <Spinner />
          <span className="text-stone-600">{strings.draftLoading}</span>
        </div>
      </div>
    );
  }

  if (scenes.length === 0) {
    return (
      <div className="mx-auto w-full max-w-6xl px-4 py-6">
        {header}
        <div className="rounded-xl border border-stone-200 bg-white p-8 text-center shadow-sm">
          <p className="text-stone-600">{strings.draftEmptyForVariant}</p>
          <div className="mt-6 flex flex-col gap-3 sm:flex-row sm:justify-center">
            <Link
              to={`/stories/${id}/v/${variant}/builder`}
              className="rounded-lg border border-stone-300 px-4 py-2.5 text-sm font-medium text-stone-700 hover:bg-stone-100"
            >
              Otwórz kreator
            </Link>
            <Link
              to={`/stories/${id}/v/${variant}/progress`}
              className="rounded-lg border border-stone-300 px-4 py-2.5 text-sm font-medium text-stone-700 hover:bg-stone-100"
            >
              Zobacz postęp
            </Link>
          </div>
        </div>
      </div>
    );
  }

  return (
    <div className="mx-auto w-full max-w-6xl px-4 py-6">
      {header}

      {renderTtsMutation.isError && (
        <p className="mb-4 text-sm text-rose-600">{strings.renderTtsEnqueueError}</p>
      )}

      {/* Telefon: poziomy pasek skoków do scen */}
      <div className="mb-4 flex gap-2 overflow-x-auto pb-1 lg:hidden">
        {scenes.map((scene) => (
          <a
            key={scene.sceneId}
            href={`#scene-${scene.sceneId}`}
            className="flex shrink-0 items-center gap-1 whitespace-nowrap rounded-full border border-stone-200 bg-white px-3 py-1.5 text-xs text-stone-600"
          >
            {scene.sceneId}
            {scene.flags.length > 0 && (
              <span className="inline-flex h-4 min-w-4 items-center justify-center rounded-full bg-rose-600 px-1 text-[10px] font-semibold text-white">
                {scene.flags.length}
              </span>
            )}
          </a>
        ))}
      </div>

      <div className="flex gap-8">
        {/* Desktop: lewa szyna z listą scen */}
        <aside className="hidden w-64 shrink-0 lg:block">
          <nav className="sticky top-6 space-y-1">
            {scenes.map((scene) => (
              <a
                key={scene.sceneId}
                href={`#scene-${scene.sceneId}`}
                className="flex items-center justify-between gap-2 rounded-lg px-3 py-2 text-sm text-stone-700 hover:bg-stone-100"
              >
                <span className="truncate">
                  <span className="mr-2 font-mono text-xs text-stone-400">{scene.sceneId}</span>
                  {scene.title || strings.untitled}
                </span>
                {scene.flags.length > 0 && (
                  <span className="inline-flex h-5 min-w-5 shrink-0 items-center justify-center rounded-full bg-rose-600 px-1.5 text-xs font-semibold text-white">
                    {scene.flags.length}
                  </span>
                )}
              </a>
            ))}
          </nav>
        </aside>

        <main className="min-w-0 flex-1 space-y-6">
          {scenes.map((scene) => {
            const sceneId = scene.sceneId;
            const editText = edits[sceneId];
            return (
              <SceneCard
                key={sceneId}
                storyId={id}
                variant={variant}
                scene={scene}
                value={editText ?? scene.text}
                dirty={editText !== undefined && editText !== scene.text}
                editing={editingIds[sceneId] === true}
                saving={saveMutation.isPending && saveMutation.variables?.sceneId === sceneId}
                restoring={restoreMutation.isPending && restoreMutation.variables?.sceneId === sceneId}
                savedFlash={savedFlash === sceneId}
                regenActive={activeRegenScenes.has(sceneId)}
                onChange={(text) => setEdits((prev) => ({ ...prev, [sceneId]: text }))}
                onSave={() => saveMutation.mutate({ sceneId, text: edits[sceneId] ?? scene.text })}
                onStartEdit={() => setEditingIds((prev) => ({ ...prev, [sceneId]: true }))}
                onStopEdit={() => setEditingIds((prev) => ({ ...prev, [sceneId]: false }))}
                onDiscard={() => {
                  setEdits((prev) => {
                    const next = { ...prev };
                    delete next[sceneId];
                    return next;
                  });
                  setEditingIds((prev) => ({ ...prev, [sceneId]: false }));
                }}
                onRegen={() => {
                  setRegenFor(sceneId);
                  setFeedbackNote('');
                  // Wszystkie uwagi weryfikacji domyślnie zaznaczone do zastosowania.
                  setCheckedFlags(scene.flags.map(() => true));
                }}
                onRestore={(prevText) => restoreMutation.mutate({ sceneId, text: prevText })}
              />
            );
          })}
        </main>
      </div>

      {/* Dialog regeneracji sceny: checklista uwag weryfikacji + pole na własne uwagi */}
      <Dialog.Root
        open={regenFor !== null}
        onOpenChange={(open) => {
          if (!open) {
            setRegenFor(null);
            setFeedbackNote('');
            setCheckedFlags([]);
          }
        }}
      >
        <Dialog.Portal>
          <Dialog.Overlay className="fixed inset-0 z-40 bg-stone-900/40" />
          <Dialog.Content className="fixed left-1/2 top-1/2 z-50 max-h-[85vh] w-[calc(100vw-2rem)] max-w-md -translate-x-1/2 -translate-y-1/2 overflow-y-auto rounded-xl bg-white p-6 shadow-xl">
            <Dialog.Title className="text-lg font-semibold text-stone-900">
              {strings.sceneRegenTitle}
            </Dialog.Title>
            <Dialog.Description className="mt-1 text-sm text-stone-500">
              {strings.sceneLabel} {regenFor}. {strings.sceneRegenHint}
            </Dialog.Description>
            {regenFlags.length > 0 && (
              <div className="mt-4">
                <div className="text-sm font-medium text-stone-700">{strings.verifyFlagsTitle}</div>
                <ul className="mt-2 space-y-1">
                  {regenFlags.map((flag, i) => (
                    <li key={i}>
                      <label className="flex cursor-pointer items-start gap-3 rounded-lg px-2 py-1.5 hover:bg-stone-50">
                        <input
                          type="checkbox"
                          checked={checkedFlags[i] === true}
                          onChange={(e) =>
                            setCheckedFlags((prev) => {
                              const next = [...prev];
                              next[i] = e.target.checked;
                              return next;
                            })
                          }
                          className="mt-0.5 h-5 w-5 shrink-0 accent-violet-600"
                        />
                        <span className="min-w-0">
                          <span className="block text-sm font-medium text-stone-900" title={flag.rule}>
                            {ruleLabel(flag.rule)}
                          </span>
                          <span className="mt-0.5 block text-xs text-stone-500">{flag.detail}</span>
                        </span>
                      </label>
                    </li>
                  ))}
                </ul>
              </div>
            )}
            <label className="mt-4 block text-sm font-medium text-stone-700" htmlFor="regen-feedback-note">
              {strings.sceneRegenOwnNotesLabel}
            </label>
            <textarea
              id="regen-feedback-note"
              value={feedbackNote}
              onChange={(e) => setFeedbackNote(e.target.value)}
              rows={4}
              placeholder={strings.sceneRegenPlaceholder}
              className="mt-1 w-full rounded-lg border border-stone-300 p-3 text-sm text-stone-900 focus:border-violet-500 focus:outline-none focus:ring-2 focus:ring-violet-200"
            />
            {regenMutation.isError && (
              <p className="mt-2 text-sm text-rose-600">{strings.enqueueError}</p>
            )}
            <div className="mt-4 flex flex-wrap items-center justify-end gap-2">
              {!regenCanSubmit && (
                <span className="mr-auto text-xs text-stone-500">{strings.sceneRegenNothingSelectedHint}</span>
              )}
              <Dialog.Close className={actionButtonClass}>{strings.cancel}</Dialog.Close>
              <button
                type="button"
                onClick={() => {
                  if (regenFor)
                    regenMutation.mutate({
                      sceneId: regenFor,
                      note: composeFeedbackNote(regenFlags, checkedFlags, feedbackNote),
                    });
                }}
                disabled={!regenCanSubmit || regenMutation.isPending}
                className="min-h-10 rounded-lg bg-violet-600 px-4 py-2 text-sm font-semibold text-white hover:bg-violet-700 disabled:opacity-50"
              >
                Regeneruj
              </button>
            </div>
          </Dialog.Content>
        </Dialog.Portal>
      </Dialog.Root>

      {/* Dialog konfliktu ETag — jak w kreatorze */}
      <Dialog.Root
        open={conflict !== null}
        onOpenChange={(open) => {
          if (!open) setConflict(null);
        }}
      >
        <Dialog.Portal>
          <Dialog.Overlay className="fixed inset-0 z-40 bg-stone-900/40" />
          <Dialog.Content className="fixed left-1/2 top-1/2 z-50 w-[calc(100vw-2rem)] max-w-md -translate-x-1/2 -translate-y-1/2 rounded-xl bg-white p-6 shadow-xl">
            <Dialog.Title className="text-lg font-semibold text-stone-900">
              {strings.draftConflictDiskTitle}
            </Dialog.Title>
            <Dialog.Description className="mt-1 text-sm text-stone-500">
              {strings.sceneLabel} {conflict?.sceneId}: {strings.draftConflictDiskBody}
            </Dialog.Description>
            <div className="mt-4 flex flex-col gap-2 sm:flex-row sm:justify-end">
              <button type="button" onClick={() => resolveConflict(false)} className={actionButtonClass}>
                Wczytaj wersję z dysku
              </button>
              <button
                type="button"
                onClick={() => resolveConflict(true)}
                className="min-h-10 rounded-lg bg-amber-600 px-4 py-2 text-sm font-semibold text-white hover:bg-amber-700"
              >
                Zachowaj moją i zapisz ponownie
              </button>
            </div>
          </Dialog.Content>
        </Dialog.Portal>
      </Dialog.Root>
    </div>
  );
}
