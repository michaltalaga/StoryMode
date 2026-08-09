import { useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import type { ReactNode } from 'react';
import { Link, useNavigate, useParams } from 'react-router';
import * as Dialog from '@radix-ui/react-dialog';
import { ConflictError, getWithETag, putWithETag } from '../api/client';
import { enqueueJob, useUniverseFile, useVoices } from '../api/queries';
import type { SessionJson } from '../api/types';
import type { Strings } from '../i18n';
import { useStrings } from '../i18n';

/* Lokalne aliasy — całość tekstu mieszka w src/i18n. */
const aliases = (strings: Strings) => ({
  back: strings.backToStory,
  variantLabel: strings.variantLabel,
  presetGiven: strings.builderPresetGiven,
  presetInvent: strings.builderPresetInvent,
  presetGivenTitle: strings.builderPresetGivenTitle,
  presetGivenBody: strings.builderPresetGivenBody,
  presetInventTitle: strings.builderPresetInventTitle,
  presetInventBody: strings.builderPresetInventBody,
  confirm: strings.confirm,
  cancel: strings.cancel,
  universe: strings.universeLabel,
  cast: strings.builderCastLabel,
  castCustomTitle: strings.builderCastCustomTitle,
  castName: strings.builderCastName,
  castAbout: strings.builderCastAbout,
  add: strings.add,
  removeCast: strings.builderCastRemove,
  pov: strings.builderPovLabel,
  povEmpty: strings.builderPovEmpty,
  stakes: strings.builderStakesLabel,
  outcome: strings.builderOutcomeLabel,
  skipTitle: strings.builderSkipLabel,
  skipPlaceholder: strings.builderSkipPlaceholder,
  removeSkip: strings.builderSkipRemove,
  targetMinutes: strings.builderTargetMinutesLabel,
  voice: strings.builderVoiceLabel,
  voiceDefault: strings.builderVoiceDefault,
  language: strings.languageLabel,
  langPl: strings.languagePl,
  langEn: strings.languageEn,
  beats: strings.builderBeatsLabel,
  beatPlaceholder: strings.builderBeatPlaceholder,
  addBeat: strings.builderAddBeat,
  given: strings.builderGivenLabel,
  invent: strings.builderInventLabel,
  moveUp: strings.builderMoveUp,
  moveDown: strings.builderMoveDown,
  deleteBeat: strings.builderRemoveBeat,
  unsaved: strings.unsavedChanges,
  save: strings.save,
  saving: strings.saving,
  generate: strings.generate,
  generateHint: strings.builderGenerateHint,
  conflictTitle: strings.builderConflictTitle,
  conflictBody: strings.builderConflictBody,
  conflictReload: strings.builderConflictReload,
  conflictKeep: strings.builderConflictKeep,
  loading: strings.loading,
  loadError: strings.builderLoadError,
  sessionBroken: strings.builderSessionBroken,
});

/** Aliased copy for this route; re-resolves when the language changes. */
function useT() {
  const strings = useStrings();
  return useMemo(() => aliases(strings), [strings]);
}

type Json = Record<string, unknown>;
type BeatFlag = 'given' | 'invent';

const isObj = (v: unknown): v is Json => typeof v === 'object' && v !== null && !Array.isArray(v);
const asStr = (v: unknown): string => (typeof v === 'string' ? v : '');
const rawArr = (v: unknown): unknown[] => (Array.isArray(v) ? [...v] : []);

/** useUniverseFile data may be raw text or {text, etag}; accept both. */
function fileText(data: unknown): string | null {
  if (typeof data === 'string') return data;
  if (isObj(data) && typeof data.text === 'string') return data.text;
  return null;
}

/** characters.md: '## slug' headings; following paragraph = description. */
function parseCharacters(md: string): { slug: string; about: string }[] {
  const out: { slug: string; about: string }[] = [];
  for (const raw of md.split('\n')) {
    const line = raw.trimEnd();
    if (line.startsWith('## ')) {
      out.push({ slug: line.slice(3).trim(), about: '' });
    } else if (out.length > 0) {
      const trimmed = line.trim();
      if (trimmed !== '' && !trimmed.startsWith('#') && !trimmed.startsWith('<!--')) {
        const cur = out[out.length - 1]!;
        if (cur.about.length < 140) cur.about = cur.about === '' ? trimmed : `${cur.about} ${trimmed}`;
      }
    }
  }
  return out;
}

/** Next unused bN = max existing numeric suffix + 1; deleted ids are never reused, existing never renumbered. */
function nextBeatId(beats: unknown[]): string {
  let max = 0;
  for (const b of beats) {
    if (!isObj(b)) continue;
    const m = /^b(\d+)$/.exec(asStr(b.id));
    if (m) max = Math.max(max, Number(m[1]));
  }
  return `b${max + 1}`;
}

const AVATAR_COLORS = [
  'bg-rose-200 text-rose-900',
  'bg-sky-200 text-sky-900',
  'bg-emerald-200 text-emerald-900',
  'bg-orange-200 text-orange-900',
  'bg-indigo-200 text-indigo-900',
  'bg-teal-200 text-teal-900',
];
const avatarColor = (s: string): string =>
  AVATAR_COLORS[[...s].reduce((a, c) => a + c.charCodeAt(0), 0) % AVATAR_COLORS.length]!;

const inputCls =
  'h-11 w-full rounded-xl border border-neutral-300 bg-white px-3 text-base outline-none focus:border-neutral-500';

function LockIcon() {
  return (
    <svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" strokeWidth="2" aria-hidden="true">
      <rect x="5" y="11" width="14" height="9" rx="2" />
      <path d="M8 11V7a4 4 0 0 1 8 0v4" />
    </svg>
  );
}

function SparkleIcon() {
  return (
    <svg viewBox="0 0 24 24" width="16" height="16" fill="currentColor" aria-hidden="true">
      <path d="M12 3l1.9 5.1L19 10l-5.1 1.9L12 17l-1.9-5.1L5 10l5.1-1.9L12 3z" />
      <path d="M18.5 15.5l.8 2.2 2.2.8-2.2.8-.8 2.2-.8-2.2-2.2-.8 2.2-.8.8-2.2z" />
    </svg>
  );
}

function ChevronIcon({ up }: { up: boolean }) {
  return (
    <svg viewBox="0 0 24 24" width="20" height="20" fill="none" stroke="currentColor" strokeWidth="2.5" aria-hidden="true">
      <path d={up ? 'M6 15l6-6 6 6' : 'M6 9l6 6 6-6'} strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  );
}

function CrossIcon() {
  return (
    <svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" strokeWidth="2" aria-hidden="true">
      <path d="M6 6l12 12M18 6L6 18" strokeLinecap="round" />
    </svg>
  );
}

function Section({ title, children }: { title: string; children: ReactNode }) {
  return (
    <section className="space-y-3">
      <h2 className="text-xs font-semibold uppercase tracking-wider text-neutral-500">{title}</h2>
      {children}
    </section>
  );
}

function BeatCard({
  beat,
  index,
  count,
  onText,
  onFlag,
  onMove,
  onDelete,
}: {
  beat: Json;
  index: number;
  count: number;
  onText: (index: number, text: string) => void;
  onFlag: (index: number, flag: BeatFlag) => void;
  onMove: (index: number, delta: -1 | 1) => void;
  onDelete: (index: number) => void;
}) {
  const t = useT();
  const areaRef = useRef<HTMLTextAreaElement>(null);
  const text = asStr(beat.text);
  const flag: BeatFlag = asStr(beat.flag) === 'invent' ? 'invent' : 'given';

  useLayoutEffect(() => {
    const el = areaRef.current;
    if (el) {
      el.style.height = 'auto';
      el.style.height = `${el.scrollHeight}px`;
    }
  }, [text]);

  const borderCls = flag === 'given' ? 'border-amber-400' : 'border-violet-400';
  const segBase = 'flex h-11 items-center justify-center gap-2 text-sm font-semibold transition-colors';

  return (
    <div className={`overflow-hidden rounded-xl border-2 bg-white shadow-sm ${borderCls}`}>
      <div className="flex items-center gap-1 px-3 pt-2">
        <span className="font-mono text-xs text-neutral-400">{asStr(beat.id)}</span>
        <div className="ml-auto flex gap-1">
          <button
            type="button"
            onClick={() => onMove(index, -1)}
            disabled={index === 0}
            aria-label={t.moveUp}
            className="flex h-10 w-10 items-center justify-center rounded-lg text-neutral-500 hover:bg-neutral-100 disabled:opacity-30"
          >
            <ChevronIcon up />
          </button>
          <button
            type="button"
            onClick={() => onMove(index, 1)}
            disabled={index === count - 1}
            aria-label={t.moveDown}
            className="flex h-10 w-10 items-center justify-center rounded-lg text-neutral-500 hover:bg-neutral-100 disabled:opacity-30"
          >
            <ChevronIcon up={false} />
          </button>
          <button
            type="button"
            onClick={() => onDelete(index)}
            aria-label={t.deleteBeat}
            className="flex h-10 w-10 items-center justify-center rounded-lg text-neutral-400 hover:bg-red-50 hover:text-red-600"
          >
            <CrossIcon />
          </button>
        </div>
      </div>
      <div className="px-3 pb-3">
        <textarea
          ref={areaRef}
          rows={1}
          value={text}
          placeholder={t.beatPlaceholder}
          onChange={(e) => onText(index, e.target.value.replace(/\n/g, ' '))}
          onKeyDown={(e) => {
            if (e.key === 'Enter') e.preventDefault();
          }}
          className="w-full resize-none overflow-hidden bg-transparent text-base leading-6 outline-none placeholder:text-neutral-300"
        />
      </div>
      <div className="grid grid-cols-2 border-t border-neutral-100">
        <button
          type="button"
          aria-pressed={flag === 'given'}
          onClick={() => onFlag(index, 'given')}
          className={`${segBase} ${flag === 'given' ? 'bg-amber-500 text-white' : 'bg-neutral-50 text-neutral-400'}`}
        >
          <LockIcon />
          {t.given}
        </button>
        <button
          type="button"
          aria-pressed={flag === 'invent'}
          onClick={() => onFlag(index, 'invent')}
          className={`${segBase} ${flag === 'invent' ? 'bg-violet-500 text-white' : 'bg-neutral-50 text-neutral-400'}`}
        >
          <SparkleIcon />
          {t.invent}
        </button>
      </div>
    </div>
  );
}

export default function SessionBuilder() {
  const t = useT();
  const { id: storyId = '', variant = '' } = useParams();
  const navigate = useNavigate();

  const [doc, setDoc] = useState<SessionJson | null>(null);
  const [etag, setEtag] = useState('');
  const [dirty, setDirty] = useState(false);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [conflict, setConflict] = useState<{ text: string; etag: string } | null>(null);
  const [pendingPreset, setPendingPreset] = useState<BeatFlag | null>(null);
  const [generating, setGenerating] = useState(false);
  const [castName, setCastName] = useState('');
  const [castAbout, setCastAbout] = useState('');
  const [skipInput, setSkipInput] = useState('');

  // Route per docs/api.md: GET/PUT /api/stories/{sid}/session/{variant}.
  const sessionPath = `/api/stories/${storyId}/session/${variant}`;

  useEffect(() => {
    let cancelled = false;
    setDoc(null);
    setLoadError(null);
    setDirty(false);
    setSaveError(null);
    getWithETag(sessionPath)
      .then(({ text, etag: freshTag }) => {
        if (cancelled) return;
        try {
          const parsed: unknown = JSON.parse(text);
          if (!isObj(parsed)) throw new Error('not an object');
          setDoc(parsed as SessionJson);
          setEtag(freshTag);
        } catch {
          setLoadError(t.sessionBroken);
        }
      })
      .catch((e: unknown) => {
        if (!cancelled) setLoadError(`${t.loadError}: ${e instanceof Error ? e.message : String(e)}`);
      });
    return () => {
      cancelled = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [storyId, variant]);

  const universeId = doc ? asStr(doc.universe) : '';
  const language = doc ? asStr(doc.language) : '';
  const charsQuery = useUniverseFile(universeId, 'characters.md');
  const voicesQuery = useVoices();

  const characters = useMemo(() => {
    const text = fileText(charsQuery.data);
    return text === null ? [] : parseCharacters(text);
  }, [charsQuery.data]);

  // Katalog głosów jest globalny (library/voices.json). Pokazujemy głosy w języku
  // sesji, a gdy żaden nie pasuje — wszystkie, żeby lista nigdy nie była pusta.
  const voices = useMemo(() => {
    const all = voicesQuery.data ?? [];
    const matching = all.filter((v) => v.languages.includes(language));
    return matching.length > 0 ? matching : all;
  }, [voicesQuery.data, language]);

  /** Merge a patch of known keys into the parsed object; all unknown fields survive untouched. */
  const mutate = (fn: (d: SessionJson) => Json) => {
    setDoc((prev) => (prev ? { ...prev, ...fn(prev) } : prev));
    setDirty(true);
  };

  const setBeatText = (i: number, text: string) =>
    mutate((d) => {
      const beats = rawArr(d.beats);
      const b = beats[i];
      if (isObj(b)) beats[i] = { ...b, text };
      return { beats };
    });

  const setBeatFlag = (i: number, flag: BeatFlag) =>
    mutate((d) => {
      const beats = rawArr(d.beats);
      const b = beats[i];
      if (isObj(b)) beats[i] = { ...b, flag };
      return { beats };
    });

  const moveBeat = (i: number, delta: -1 | 1) =>
    mutate((d) => {
      const beats = rawArr(d.beats);
      const j = i + delta;
      if (j < 0 || j >= beats.length) return {};
      const tmp = beats[i];
      beats[i] = beats[j];
      beats[j] = tmp;
      return { beats };
    });

  const deleteBeat = (i: number) => mutate((d) => ({ beats: rawArr(d.beats).filter((_, ix) => ix !== i) }));

  const addBeat = () =>
    mutate((d) => {
      const beats = rawArr(d.beats);
      beats.push({ id: nextBeatId(beats), flag: 'given', text: '' });
      return { beats };
    });

  const applyPreset = (flag: BeatFlag) =>
    mutate((d) => ({ beats: rawArr(d.beats).map((b) => (isObj(b) ? { ...b, flag } : b)) }));

  const toggleCastRef = (slug: string) =>
    mutate((d) => {
      const cast = rawArr(d.cast);
      const has = cast.some((c) => isObj(c) && asStr(c.ref) === slug);
      const patch: Json = {
        cast: has ? cast.filter((c) => !(isObj(c) && asStr(c.ref) === slug)) : [...cast, { ref: slug }],
      };
      if (has && asStr(d.pov) === slug) patch.pov = '';
      return patch;
    });

  const removeCastAt = (index: number) => mutate((d) => ({ cast: rawArr(d.cast).filter((_, ix) => ix !== index) }));

  const save = async () => {
    if (!doc) return;
    setSaving(true);
    setSaveError(null);
    try {
      const body = JSON.stringify(doc, null, 2) + '\n';
      const { etag: freshTag } = await putWithETag(sessionPath, body, etag);
      setEtag(freshTag);
      setDirty(false);
    } catch (e) {
      if (e instanceof ConflictError) setConflict({ text: e.currentText, etag: e.currentETag });
      else setSaveError(e instanceof Error ? e.message : String(e));
    } finally {
      setSaving(false);
    }
  };

  const generate = async () => {
    setGenerating(true);
    setSaveError(null);
    try {
      await enqueueJob(storyId, { type: 'generate', variant });
      navigate(`/stories/${storyId}/v/${variant}/progress`);
    } catch (e) {
      setSaveError(e instanceof Error ? e.message : String(e));
      setGenerating(false);
    }
  };

  const conflictReload = () => {
    if (!conflict) return;
    try {
      const parsed: unknown = JSON.parse(conflict.text);
      if (isObj(parsed)) {
        setDoc(parsed as SessionJson);
        setEtag(conflict.etag);
        setDirty(false);
      } else {
        setSaveError(t.sessionBroken);
      }
    } catch {
      setSaveError(t.sessionBroken);
    }
    setConflict(null);
  };

  const conflictKeep = () => {
    if (!conflict) return;
    // Keep local edits; adopt the server ETag so the next explicit save overwrites the disk version.
    setEtag(conflict.etag);
    setConflict(null);
  };

  if (loadError) {
    return (
      <div className="mx-auto max-w-2xl px-4 py-8">
        <p className="text-red-600">{loadError}</p>
      </div>
    );
  }
  if (!doc) {
    return <div className="mx-auto max-w-2xl px-4 py-8 text-neutral-500">{t.loading}</div>;
  }

  const beatsRaw = Array.isArray(doc.beats) ? doc.beats : [];
  const castRaw = Array.isArray(doc.cast) ? doc.cast : [];
  const castRefs = castRaw.filter(isObj).map((c) => asStr(c.ref)).filter((s) => s !== '');
  const knownSlugs = characters.map((c) => c.slug);
  const checklist = [...characters, ...castRefs.filter((r) => !knownSlugs.includes(r)).map((slug) => ({ slug, about: '' }))];
  const customCast: { entry: Json; index: number }[] = [];
  castRaw.forEach((c, i) => {
    if (isObj(c) && asStr(c.ref) === '') customCast.push({ entry: c, index: i });
  });
  const pov = asStr(doc.pov);
  const povOptions = pov !== '' && !castRefs.includes(pov) ? [...castRefs, pov] : castRefs;
  const skipList = Array.isArray(doc.skip) ? doc.skip : [];
  const voiceId = asStr(doc.voice);

  return (
    <div className="mx-auto flex min-h-dvh max-w-2xl flex-col px-4 pt-4">
      <div className="flex-1 space-y-8 pb-6">
        <header className="space-y-3">
          <Link to={`/stories/${storyId}`} className="text-sm text-neutral-500 hover:text-neutral-800">
            ← {t.back}
          </Link>
          <div>
            <h1 className="text-2xl font-bold text-neutral-900">{asStr(doc.title) || storyId}</h1>
            <p className="text-sm text-neutral-500">
              {t.variantLabel}: <span className="font-medium text-neutral-700">{variant}</span>
            </p>
          </div>
          <div className="flex flex-wrap gap-2">
            <button
              type="button"
              onClick={() => setPendingPreset('given')}
              className="flex h-10 items-center gap-2 rounded-full border-2 border-amber-400 px-4 text-sm font-semibold text-amber-700 hover:bg-amber-50"
            >
              <LockIcon />
              {t.presetGiven}
            </button>
            <button
              type="button"
              onClick={() => setPendingPreset('invent')}
              className="flex h-10 items-center gap-2 rounded-full border-2 border-violet-400 px-4 text-sm font-semibold text-violet-700 hover:bg-violet-50"
            >
              <SparkleIcon />
              {t.presetInvent}
            </button>
          </div>
        </header>

        <Section title={t.universe}>
          <div className="flex h-11 items-center rounded-xl bg-neutral-100 px-3">
            <Link to={`/universes/${universeId}`} className="font-medium text-neutral-700 hover:underline">
              {universeId}
            </Link>
          </div>
        </Section>

        <Section title={t.cast}>
          <div className="space-y-2">
            {checklist.map(({ slug, about }) => {
              const checked = castRefs.includes(slug);
              return (
                <button
                  key={slug}
                  type="button"
                  role="checkbox"
                  aria-checked={checked}
                  onClick={() => toggleCastRef(slug)}
                  className={`flex w-full items-start gap-3 rounded-xl border p-3 text-left ${
                    checked ? 'border-neutral-400 bg-white' : 'border-neutral-200 bg-neutral-50'
                  }`}
                >
                  <span
                    aria-hidden="true"
                    className={`mt-0.5 flex h-6 w-6 shrink-0 items-center justify-center rounded-md border text-sm font-bold ${
                      checked ? 'border-neutral-900 bg-neutral-900 text-white' : 'border-neutral-300 bg-white text-transparent'
                    }`}
                  >
                    ✓
                  </span>
                  <span className="min-w-0">
                    <span className="block font-semibold text-neutral-900">{slug}</span>
                    {about !== '' && <span className="block truncate text-sm text-neutral-500">{about}</span>}
                  </span>
                </button>
              );
            })}
            {customCast.map(({ entry, index }) => (
              <div key={index} className="flex items-start gap-3 rounded-xl border border-neutral-200 bg-white p-3">
                <span className="min-w-0 flex-1">
                  <span className="block font-semibold text-neutral-900">{asStr(entry.name)}</span>
                  {asStr(entry.about) !== '' && (
                    <span className="block text-sm text-neutral-500">{asStr(entry.about)}</span>
                  )}
                </span>
                <button
                  type="button"
                  onClick={() => removeCastAt(index)}
                  aria-label={t.removeCast}
                  className="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg text-neutral-400 hover:bg-red-50 hover:text-red-600"
                >
                  <CrossIcon />
                </button>
              </div>
            ))}
            <form
              onSubmit={(e) => {
                e.preventDefault();
                const name = castName.trim();
                if (name === '') return;
                mutate((d) => ({ cast: [...rawArr(d.cast), { name, about: castAbout.trim() }] }));
                setCastName('');
                setCastAbout('');
              }}
              className="space-y-2 rounded-xl border border-dashed border-neutral-300 p-3"
            >
              <p className="text-sm font-medium text-neutral-500">{t.castCustomTitle}</p>
              <input value={castName} onChange={(e) => setCastName(e.target.value)} placeholder={t.castName} className={inputCls} />
              <input value={castAbout} onChange={(e) => setCastAbout(e.target.value)} placeholder={t.castAbout} className={inputCls} />
              <button
                type="submit"
                disabled={castName.trim() === ''}
                className="h-10 rounded-xl bg-neutral-200 px-4 text-sm font-semibold text-neutral-700 hover:bg-neutral-300 disabled:opacity-40"
              >
                {t.add}
              </button>
            </form>
          </div>
        </Section>

        <Section title={t.pov}>
          {povOptions.length === 0 ? (
            <p className="text-sm text-neutral-400">{t.povEmpty}</p>
          ) : (
            <div role="radiogroup" aria-label={t.pov} className="flex flex-wrap gap-3">
              {povOptions.map((slug) => {
                const active = pov === slug;
                return (
                  <button
                    key={slug}
                    type="button"
                    role="radio"
                    aria-checked={active}
                    onClick={() => mutate(() => ({ pov: slug }))}
                    className={`flex min-w-20 flex-col items-center gap-1.5 rounded-2xl border-2 px-4 py-3 ${
                      active ? 'border-neutral-900 bg-neutral-900' : 'border-neutral-200 bg-white'
                    }`}
                  >
                    <span
                      className={`flex h-12 w-12 items-center justify-center rounded-full text-lg font-bold ${avatarColor(slug)}`}
                    >
                      {(slug[0] ?? '?').toUpperCase()}
                    </span>
                    <span className={`text-sm font-semibold ${active ? 'text-white' : 'text-neutral-700'}`}>{slug}</span>
                  </button>
                );
              })}
            </div>
          )}
        </Section>

        <div className="grid gap-4 sm:grid-cols-2">
          <Section title={t.stakes}>
            <input value={asStr(doc.stakes)} onChange={(e) => mutate(() => ({ stakes: e.target.value }))} className={inputCls} />
          </Section>
          <Section title={t.outcome}>
            <input value={asStr(doc.outcome)} onChange={(e) => mutate(() => ({ outcome: e.target.value }))} className={inputCls} />
          </Section>
        </div>

        <Section title={t.skipTitle}>
          <div className="space-y-2">
            {skipList.length > 0 && (
              <div className="flex flex-wrap gap-2">
                {skipList.map((s, i) => (
                  <span
                    key={`${asStr(s)}-${i}`}
                    className="inline-flex items-center gap-1 rounded-full bg-neutral-100 py-1 pl-3 pr-1 text-sm text-neutral-700"
                  >
                    {asStr(s)}
                    <button
                      type="button"
                      onClick={() => mutate((d) => ({ skip: rawArr(d.skip).filter((_, ix) => ix !== i) }))}
                      aria-label={`${t.removeSkip}: ${asStr(s)}`}
                      className="flex h-7 w-7 items-center justify-center rounded-full text-neutral-400 hover:bg-neutral-200 hover:text-neutral-700"
                    >
                      <CrossIcon />
                    </button>
                  </span>
                ))}
              </div>
            )}
            <form
              onSubmit={(e) => {
                e.preventDefault();
                const v = skipInput.trim();
                if (v === '') return;
                mutate((d) => ({ skip: [...rawArr(d.skip), v] }));
                setSkipInput('');
              }}
              className="flex gap-2"
            >
              <input value={skipInput} onChange={(e) => setSkipInput(e.target.value)} placeholder={t.skipPlaceholder} className={inputCls} />
              <button
                type="submit"
                disabled={skipInput.trim() === ''}
                className="h-11 shrink-0 rounded-xl bg-neutral-200 px-4 text-sm font-semibold text-neutral-700 hover:bg-neutral-300 disabled:opacity-40"
              >
                {t.add}
              </button>
            </form>
          </div>
        </Section>

        <div className="grid gap-4 sm:grid-cols-3">
          <Section title={t.targetMinutes}>
            <input
              type="number"
              min={1}
              inputMode="numeric"
              value={typeof doc.targetMinutes === 'number' ? doc.targetMinutes : ''}
              onChange={(e) => {
                const v = e.target.value;
                const n = Number(v);
                mutate(() => ({ targetMinutes: v === '' || !Number.isFinite(n) ? null : n }));
              }}
              className={inputCls}
            />
          </Section>
          <Section title={t.voice}>
            <select value={voiceId} onChange={(e) => mutate(() => ({ voice: e.target.value }))} className={inputCls}>
              <option value="">{t.voiceDefault}</option>
              {voices.map((v) => (
                <option key={v.id} value={v.id}>
                  {v.languages.length > 0 ? `${v.id} (${v.languages.join(', ')})` : v.id}
                </option>
              ))}
              {voiceId !== '' && !voices.some((v) => v.id === voiceId) && <option value={voiceId}>{voiceId}</option>}
            </select>
          </Section>
          <Section title={t.language}>
            <select value={language} onChange={(e) => mutate(() => ({ language: e.target.value }))} className={inputCls}>
              <option value="pl">{t.langPl}</option>
              <option value="en">{t.langEn}</option>
              {language !== 'pl' && language !== 'en' && <option value={language}>{language}</option>}
            </select>
          </Section>
        </div>

        <Section title={t.beats}>
          <div className="space-y-3">
            {beatsRaw.map((b, i) =>
              isObj(b) ? (
                <BeatCard
                  key={asStr(b.id) !== '' ? asStr(b.id) : `beat-${i}`}
                  beat={b}
                  index={i}
                  count={beatsRaw.length}
                  onText={setBeatText}
                  onFlag={setBeatFlag}
                  onMove={moveBeat}
                  onDelete={deleteBeat}
                />
              ) : null,
            )}
            <button
              type="button"
              onClick={addBeat}
              className="h-12 w-full rounded-xl border-2 border-dashed border-neutral-300 text-sm font-semibold text-neutral-500 hover:border-neutral-400 hover:text-neutral-700"
            >
              + {t.addBeat}
            </button>
          </div>
        </Section>
      </div>

      <div className="sticky bottom-0 -mx-4 mt-auto border-t border-neutral-200 bg-white/95 px-4 py-3 backdrop-blur">
        {saveError && <p className="mb-2 text-sm text-red-600">{saveError}</p>}
        <div className="flex items-center gap-3">
          {dirty && (
            <span className="flex items-center gap-1.5 text-sm font-medium text-amber-600">
              <span className="h-2 w-2 rounded-full bg-amber-500" aria-hidden="true" />
              {t.unsaved}
            </span>
          )}
          <div className="ml-auto flex gap-2">
            <button
              type="button"
              onClick={() => void save()}
              disabled={!dirty || saving}
              className="h-11 rounded-xl bg-neutral-900 px-5 font-semibold text-white disabled:opacity-40"
            >
              {saving ? t.saving : t.save}
            </button>
            <button
              type="button"
              onClick={() => void generate()}
              disabled={dirty || saving || generating}
              title={dirty ? t.generateHint : undefined}
              className="h-11 rounded-xl bg-emerald-600 px-5 font-semibold text-white disabled:opacity-40"
            >
              {t.generate}
            </button>
          </div>
        </div>
      </div>

      <Dialog.Root open={pendingPreset !== null} onOpenChange={(open) => !open && setPendingPreset(null)}>
        <Dialog.Portal>
          <Dialog.Overlay className="fixed inset-0 z-40 bg-black/40" />
          <Dialog.Content className="fixed left-1/2 top-1/2 z-50 w-[calc(100vw-2rem)] max-w-sm -translate-x-1/2 -translate-y-1/2 rounded-2xl bg-white p-5 shadow-xl">
            <Dialog.Title className="text-base font-semibold text-neutral-900">
              {pendingPreset === 'invent' ? t.presetInventTitle : t.presetGivenTitle}
            </Dialog.Title>
            <Dialog.Description className="mt-1 text-sm text-neutral-600">
              {pendingPreset === 'invent' ? t.presetInventBody : t.presetGivenBody}
            </Dialog.Description>
            <div className="mt-4 flex justify-end gap-2">
              <button
                type="button"
                onClick={() => setPendingPreset(null)}
                className="h-11 rounded-xl border border-neutral-300 px-4 font-semibold text-neutral-700"
              >
                {t.cancel}
              </button>
              <button
                type="button"
                onClick={() => {
                  if (pendingPreset) applyPreset(pendingPreset);
                  setPendingPreset(null);
                }}
                className={`h-11 rounded-xl px-4 font-semibold text-white ${
                  pendingPreset === 'invent' ? 'bg-violet-500' : 'bg-amber-500'
                }`}
              >
                {t.confirm}
              </button>
            </div>
          </Dialog.Content>
        </Dialog.Portal>
      </Dialog.Root>

      <Dialog.Root open={conflict !== null} onOpenChange={(open) => !open && conflictKeep()}>
        <Dialog.Portal>
          <Dialog.Overlay className="fixed inset-0 z-40 bg-black/40" />
          <Dialog.Content className="fixed left-1/2 top-1/2 z-50 w-[calc(100vw-2rem)] max-w-sm -translate-x-1/2 -translate-y-1/2 rounded-2xl bg-white p-5 shadow-xl">
            <Dialog.Title className="text-base font-semibold text-neutral-900">{t.conflictTitle}</Dialog.Title>
            <Dialog.Description className="mt-1 text-sm text-neutral-600">{t.conflictBody}</Dialog.Description>
            <div className="mt-4 flex flex-col gap-2">
              <button
                type="button"
                onClick={conflictReload}
                className="h-11 rounded-xl bg-neutral-900 px-4 font-semibold text-white"
              >
                {t.conflictReload}
              </button>
              <button
                type="button"
                onClick={conflictKeep}
                className="h-11 rounded-xl border border-neutral-300 px-4 font-semibold text-neutral-700"
              >
                {t.conflictKeep}
              </button>
            </div>
          </Dialog.Content>
        </Dialog.Portal>
      </Dialog.Root>
    </div>
  );
}
