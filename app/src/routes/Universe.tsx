// Universe page (agent 7): constraints | bible | fakty | postacie | głosy.
// Markdown files are edited as plain text with ETag optimistic concurrency
// (409 -> reload dialog). "fakty" is the bible write-back approval queue.
import { useCallback, useEffect, useMemo, useState } from 'react'
import { useParams, useSearchParams } from 'react-router'
import * as Dialog from '@radix-ui/react-dialog'
import { useQueryClient } from '@tanstack/react-query'
import { ConflictError, getWithETag, postJson, putWithETag } from '../api/client'
import { usePendingFacts, useUniverses } from '../api/queries'
import type { UniversePendingFact } from '../api/types'
import { strings } from '../strings'

// ---------------------------------------------------------------------------
// Shared bits
// ---------------------------------------------------------------------------

const btnBase =
  'inline-flex min-h-11 items-center justify-center gap-2 rounded-lg px-4 py-2.5 text-sm font-medium transition-colors disabled:cursor-not-allowed disabled:opacity-40'
const btnPrimary = `${btnBase} bg-stone-900 text-white hover:bg-stone-700`
const btnGhost = `${btnBase} border border-stone-300 bg-white text-stone-700 hover:bg-stone-100`
const btnAmber = `${btnBase} bg-amber-600 text-white hover:bg-amber-700`

function isDesktopViewport(): boolean {
  return typeof window !== 'undefined' && window.matchMedia('(min-width: 1024px)').matches
}

interface ConflictState {
  currentText: string
  currentETag: string
}

/**
 * Local editable-file state over getWithETag/putWithETag. Deliberately not a
 * TanStack query: the textarea owns the text, and a 409 must surface as a
 * dialog rather than a background refetch.
 */
function useEditableUniverseFile(universeId: string, fileName: string) {
  const path = `/api/universes/${encodeURIComponent(universeId)}/files/${fileName
    .split('/')
    .map(encodeURIComponent)
    .join('/')}`
  const [text, setText] = useState('')
  const [savedText, setSavedText] = useState('')
  const [etag, setEtag] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)
  const [saveError, setSaveError] = useState<string | null>(null)
  const [conflict, setConflict] = useState<ConflictState | null>(null)

  const load = useCallback(async () => {
    setLoading(true)
    setLoadError(null)
    try {
      const file = await getWithETag(path)
      setText(file.text)
      setSavedText(file.text)
      setEtag(file.etag)
      setConflict(null)
      setSaveError(null)
    } catch (err) {
      setLoadError(err instanceof Error ? err.message : strings.error)
    } finally {
      setLoading(false)
    }
  }, [path])

  useEffect(() => {
    void load()
  }, [load])

  const save = useCallback(
    async (overrideEtag?: string) => {
      const ifMatch = overrideEtag ?? etag
      if (ifMatch === null) return
      setSaving(true)
      setSaveError(null)
      try {
        const result = await putWithETag(path, text, ifMatch)
        setEtag(result.etag)
        setSavedText(text)
        setConflict(null)
      } catch (err) {
        if (err instanceof ConflictError) {
          setConflict({ currentText: err.currentText, currentETag: err.currentETag })
        } else {
          setSaveError(err instanceof Error ? err.message : strings.error)
        }
      } finally {
        setSaving(false)
      }
    },
    [etag, path, text],
  )

  return {
    text,
    setText,
    dirty: text !== savedText,
    loading,
    loadError,
    saving,
    saveError,
    conflict,
    reload: load,
    save: () => void save(),
    /** Discard local edits and adopt the version currently on disk. */
    loadServerVersion: () => {
      if (conflict === null) return
      setText(conflict.currentText)
      setSavedText(conflict.currentText)
      setEtag(conflict.currentETag)
      setConflict(null)
    },
    /** Re-save local text on top of the current on-disk version. */
    overwriteServer: () => {
      if (conflict !== null) void save(conflict.currentETag)
    },
    dismissConflict: () => setConflict(null),
  }
}

type EditableFile = ReturnType<typeof useEditableUniverseFile>

function ConflictDialog({ file }: { file: EditableFile }) {
  return (
    <Dialog.Root open={file.conflict !== null} onOpenChange={(open) => !open && file.dismissConflict()}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 z-40 bg-black/40" />
        <Dialog.Content className="fixed left-1/2 top-1/2 z-50 w-[min(92vw,30rem)] -translate-x-1/2 -translate-y-1/2 rounded-xl bg-white p-6 shadow-xl">
          <Dialog.Title className="text-lg font-semibold text-stone-900">
            {strings.universeConflictTitle}
          </Dialog.Title>
          <Dialog.Description className="mt-2 text-sm text-stone-600">
            {strings.universeFileConflict}
          </Dialog.Description>
          {file.conflict !== null && (
            <pre className="mt-3 max-h-40 overflow-auto whitespace-pre-wrap rounded-lg bg-stone-100 p-3 text-xs leading-relaxed text-stone-700">
              {file.conflict.currentText}
            </pre>
          )}
          <div className="mt-5 flex flex-col gap-2 sm:flex-row sm:justify-end">
            <button type="button" className={btnGhost} onClick={file.dismissConflict}>
              {strings.cancel}
            </button>
            <button type="button" className={btnGhost} onClick={file.overwriteServer}>
              {strings.draftConflictOverwrite}
            </button>
            <button type="button" className={btnPrimary} onClick={file.loadServerVersion}>
              {strings.draftConflictReload}
            </button>
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  )
}

function FileStates({ file }: { file: EditableFile }) {
  if (file.loading) {
    return <p className="py-8 text-center text-sm text-stone-500">{strings.loading}</p>
  }
  if (file.loadError !== null) {
    return (
      <div className="rounded-xl border border-red-200 bg-red-50 p-4 text-sm text-red-800">
        <p>{file.loadError}</p>
        <button type="button" className={`${btnGhost} mt-3`} onClick={() => void file.reload()}>
          {strings.retry}
        </button>
      </div>
    )
  }
  return null
}

// ---------------------------------------------------------------------------
// Markdown file tab (constraints.md / bible.md / characters.md)
// ---------------------------------------------------------------------------

function MarkdownFileTab({ universeId, fileName }: { universeId: string; fileName: string }) {
  const file = useEditableUniverseFile(universeId, fileName)
  // Desktop is edit-focused, phone is read-focused (edit behind a button).
  const [editing, setEditing] = useState(isDesktopViewport)
  const [mono, setMono] = useState(false)

  const states = <FileStates file={file} />
  if (file.loading || file.loadError !== null) return states

  if (!editing) {
    return (
      <div>
        <div className="mb-3 flex items-center justify-between gap-3">
          <span className="text-xs text-stone-400">{fileName}</span>
          <button type="button" className={btnGhost} onClick={() => setEditing(true)}>
            {strings.edit}
          </button>
        </div>
        <article className="whitespace-pre-wrap rounded-xl border border-stone-200 bg-white p-4 font-serif leading-relaxed text-stone-800 sm:p-6">
          {file.text.length > 0 ? file.text : <span className="text-stone-400">{strings.emptyFile}</span>}
        </article>
      </div>
    )
  }

  return (
    <div>
      <div className="mb-3 flex flex-wrap items-center gap-2">
        <span className="mr-auto text-xs text-stone-400">{fileName}</span>
        <div className="inline-flex overflow-hidden rounded-lg border border-stone-300" role="group" aria-label={strings.fontToggleAria}>
          <button
            type="button"
            className={`min-h-11 px-3 text-sm ${mono ? 'bg-white text-stone-500' : 'bg-stone-900 text-white'}`}
            onClick={() => setMono(false)}
          >
            {strings.fontSerif}
          </button>
          <button
            type="button"
            className={`min-h-11 px-3 font-mono text-sm ${mono ? 'bg-stone-900 text-white' : 'bg-white text-stone-500'}`}
            onClick={() => setMono(true)}
          >
            {strings.fontMono}
          </button>
        </div>
        <button type="button" className={btnGhost} disabled={file.dirty} onClick={() => void file.reload()}>
          {strings.refresh}
        </button>
        <button type="button" className={btnGhost + ' lg:hidden'} onClick={() => setEditing(false)}>
          {strings.preview}
        </button>
        <button type="button" className={btnPrimary} disabled={!file.dirty || file.saving} onClick={file.save}>
          {file.saving ? strings.saving : strings.save}
        </button>
      </div>
      {file.dirty && (
        <p className="mb-2 text-xs text-amber-700">{strings.unsavedChanges}</p>
      )}
      {file.saveError !== null && (
        <p className="mb-2 rounded-lg border border-red-200 bg-red-50 p-2 text-xs text-red-800">{file.saveError}</p>
      )}
      <textarea
        value={file.text}
        onChange={(e) => file.setText(e.target.value)}
        spellCheck={false}
        className={`min-h-[60vh] w-full resize-y rounded-xl border border-stone-300 bg-white p-4 leading-relaxed text-stone-900 focus:border-stone-500 focus:outline-none ${
          mono ? 'font-mono text-sm' : 'font-serif text-base'
        }`}
      />
      <ConflictDialog file={file} />
    </div>
  )
}

// ---------------------------------------------------------------------------
// Fakty — pending bible facts approval
// ---------------------------------------------------------------------------

function factKey(f: UniversePendingFact): string {
  return `${f.storyId}|${f.variant}|${f.lineId}`
}

function PendingFactsTab({ universeId }: { universeId: string }) {
  const query = usePendingFacts(universeId)
  const queryClient = useQueryClient()
  const [selected, setSelected] = useState<ReadonlySet<string>>(() => new Set<string>())
  const [approving, setApproving] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const facts = useMemo(() => query.data ?? [], [query.data])

  const groups = useMemo(() => {
    const map = new Map<string, { storyId: string; variant: string; facts: UniversePendingFact[] }>()
    for (const fact of facts) {
      const key = `${fact.storyId}/${fact.variant}`
      let group = map.get(key)
      if (group === undefined) {
        group = { storyId: fact.storyId, variant: fact.variant, facts: [] }
        map.set(key, group)
      }
      group.facts.push(fact)
    }
    return [...map.values()]
  }, [facts])

  const toggle = (key: string) => {
    setSelected((prev) => {
      const next = new Set(prev)
      if (next.has(key)) next.delete(key)
      else next.add(key)
      return next
    })
  }

  const selectGroup = (groupFacts: UniversePendingFact[], on: boolean) => {
    setSelected((prev) => {
      const next = new Set(prev)
      for (const fact of groupFacts) {
        if (on) next.add(factKey(fact))
        else next.delete(factKey(fact))
      }
      return next
    })
  }

  const approve = async () => {
    setApproving(true)
    setError(null)
    try {
      for (const group of groups) {
        const acceptedLineIds = group.facts.filter((f) => selected.has(factKey(f))).map((f) => f.lineId)
        if (acceptedLineIds.length > 0) {
          await postJson<{ appended: number }>(
            `/api/stories/${encodeURIComponent(group.storyId)}/bible/${encodeURIComponent(group.variant)}/approve`,
            { acceptedLineIds },
          )
        }
      }
      setSelected(new Set<string>())
    } catch (err) {
      setError(err instanceof Error ? err.message : strings.error)
    } finally {
      setApproving(false)
      await queryClient.invalidateQueries()
    }
  }

  if (query.isLoading) {
    return <p className="py-8 text-center text-sm text-stone-500">{strings.loading}</p>
  }
  if (query.isError) {
    return (
      <div className="rounded-xl border border-red-200 bg-red-50 p-4 text-sm text-red-800">
        <p>{strings.error}</p>
        <button type="button" className={`${btnGhost} mt-3`} onClick={() => void query.refetch()}>
          {strings.retry}
        </button>
      </div>
    )
  }
  if (facts.length === 0) {
    return (
      <p className="rounded-xl border border-dashed border-stone-300 py-10 text-center text-sm text-stone-500">
        {strings.pendingFactsEmpty}
      </p>
    )
  }

  const selectedCount = facts.filter((f) => selected.has(factKey(f))).length

  return (
    <div className="space-y-4">
      <p className="text-sm text-stone-600">{strings.pendingFactsHint}</p>
      {error !== null && (
        <p className="rounded-lg border border-red-200 bg-red-50 p-2 text-xs text-red-800">{error}</p>
      )}
      {groups.map((group) => {
        const allOn = group.facts.every((f) => selected.has(factKey(f)))
        return (
          <section key={`${group.storyId}/${group.variant}`} className="rounded-xl border border-stone-200 bg-white">
            <header className="flex items-center justify-between gap-3 border-b border-stone-100 px-4 py-3">
              <h3 className="text-sm font-semibold text-stone-800">
                {group.storyId} <span className="font-normal text-stone-400">· {group.variant}</span>
              </h3>
              <button
                type="button"
                className="min-h-11 px-2 text-xs font-medium text-stone-500 hover:text-stone-800"
                onClick={() => selectGroup(group.facts, !allOn)}
              >
                {allOn ? strings.deselectAll : strings.selectAll}
              </button>
            </header>
            <ul className="divide-y divide-stone-100">
              {group.facts.map((fact) => {
                const key = factKey(fact)
                return (
                  <li key={key}>
                    <label className="flex min-h-11 cursor-pointer items-start gap-3 px-4 py-3 hover:bg-stone-50">
                      <input
                        type="checkbox"
                        checked={selected.has(key)}
                        onChange={() => toggle(key)}
                        className="mt-1 h-5 w-5 shrink-0 accent-amber-600"
                      />
                      <span className="text-sm leading-relaxed text-stone-800">
                        {fact.text}
                        {fact.sceneId != null && fact.sceneId !== '' && (
                          <span className="ml-2 rounded bg-stone-100 px-1.5 py-0.5 align-middle text-xs text-stone-500">
                            {fact.sceneId}
                          </span>
                        )}
                      </span>
                    </label>
                  </li>
                )
              })}
            </ul>
          </section>
        )
      })}
      <div className="sticky bottom-4 flex justify-end">
        <button
          type="button"
          className={`${btnAmber} shadow-lg`}
          disabled={selectedCount === 0 || approving}
          onClick={() => void approve()}
        >
          {approving ? strings.saving : `${strings.approveSelected} (${selectedCount})`}
        </button>
      </div>
    </div>
  )
}

// ---------------------------------------------------------------------------
// Głosy — voices.json as cards + raw editor
// ---------------------------------------------------------------------------

interface VoiceCard {
  id: string
  languages: string[]
  exaggeration: number | null
  cfg: number | null
  provider: string | null
}

function toNumberOrNull(value: unknown): number | null {
  return typeof value === 'number' && Number.isFinite(value) ? value : null
}

function toStringArray(value: unknown): string[] {
  if (typeof value === 'string') return [value]
  if (Array.isArray(value)) return value.filter((item): item is string => typeof item === 'string')
  return []
}

/** Defensive parse: voices.json may be an array, an {id: voice} map, or {voices:[...]}. */
function parseVoices(text: string): VoiceCard[] | null {
  let parsed: unknown
  try {
    parsed = JSON.parse(text)
  } catch {
    return null
  }
  let entries: [string | null, unknown][]
  if (Array.isArray(parsed)) {
    entries = parsed.map((item): [string | null, unknown] => [null, item])
  } else if (typeof parsed === 'object' && parsed !== null) {
    const record = parsed as Record<string, unknown>
    entries = Array.isArray(record['voices'])
      ? (record['voices'] as unknown[]).map((item): [string | null, unknown] => [null, item])
      : Object.entries(record)
  } else {
    return null
  }
  const cards: VoiceCard[] = []
  for (const [key, value] of entries) {
    if (typeof value !== 'object' || value === null) continue
    const voice = value as Record<string, unknown>
    const id = typeof voice['id'] === 'string' ? voice['id'] : key
    if (id === null || id === '') continue
    cards.push({
      id,
      languages: toStringArray(voice['languages'] ?? voice['language']),
      exaggeration: toNumberOrNull(voice['exaggeration']),
      cfg: toNumberOrNull(voice['cfg'] ?? voice['cfgWeight'] ?? voice['cfg_weight']),
      provider: typeof voice['provider'] === 'string' ? voice['provider'] : null,
    })
  }
  return cards
}

function VoicesTab({ universeId }: { universeId: string }) {
  const file = useEditableUniverseFile(universeId, 'voices.json')
  const [rawMode, setRawMode] = useState(false)

  const voices = useMemo(() => parseVoices(file.text), [file.text])

  const states = <FileStates file={file} />
  if (file.loading || file.loadError !== null) return states

  return (
    <div>
      <div className="mb-3 flex flex-wrap items-center gap-2">
        <span className="mr-auto text-xs text-stone-400">voices.json</span>
        <button type="button" className={btnGhost} onClick={() => setRawMode((v) => !v)}>
          {rawMode ? strings.voicesShowCards : strings.builderRawJson}
        </button>
        {rawMode && (
          <>
            <button type="button" className={btnGhost} disabled={file.dirty} onClick={() => void file.reload()}>
              {strings.refresh}
            </button>
            <button type="button" className={btnPrimary} disabled={!file.dirty || file.saving} onClick={file.save}>
              {file.saving ? strings.saving : strings.save}
            </button>
          </>
        )}
      </div>

      {rawMode ? (
        <>
          {file.dirty && voices === null && (
            <p className="mb-2 text-xs text-red-700">{strings.voicesInvalidJson}</p>
          )}
          {file.saveError !== null && (
            <p className="mb-2 rounded-lg border border-red-200 bg-red-50 p-2 text-xs text-red-800">{file.saveError}</p>
          )}
          <textarea
            value={file.text}
            onChange={(e) => file.setText(e.target.value)}
            spellCheck={false}
            className="min-h-[60vh] w-full resize-y rounded-xl border border-stone-300 bg-white p-4 font-mono text-sm leading-relaxed text-stone-900 focus:border-stone-500 focus:outline-none"
          />
        </>
      ) : voices === null ? (
        <p className="rounded-xl border border-red-200 bg-red-50 p-4 text-sm text-red-800">
          {strings.voicesParseError}
        </p>
      ) : voices.length === 0 ? (
        <p className="rounded-xl border border-dashed border-stone-300 py-10 text-center text-sm text-stone-500">
          {strings.voicesEmpty}
        </p>
      ) : (
        <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
          {voices.map((voice) => (
            <article key={voice.id} className="rounded-xl border border-stone-200 bg-white p-4">
              <header className="flex items-start justify-between gap-2">
                <h3 className="break-all font-semibold text-stone-900">{voice.id}</h3>
                {voice.provider !== null && (
                  <span className="shrink-0 rounded-full bg-violet-100 px-2 py-0.5 text-xs text-violet-800">
                    {voice.provider}
                  </span>
                )}
              </header>
              {voice.languages.length > 0 && (
                <div className="mt-2 flex flex-wrap gap-1">
                  {voice.languages.map((lang) => (
                    <span key={lang} className="rounded bg-stone-100 px-1.5 py-0.5 text-xs uppercase text-stone-600">
                      {lang}
                    </span>
                  ))}
                </div>
              )}
              <dl className="mt-3 grid grid-cols-2 gap-2 text-sm">
                <div>
                  <dt className="text-xs text-stone-400">exaggeration</dt>
                  <dd className="text-stone-800">{voice.exaggeration ?? '—'}</dd>
                </div>
                <div>
                  <dt className="text-xs text-stone-400">cfg</dt>
                  <dd className="text-stone-800">{voice.cfg ?? '—'}</dd>
                </div>
              </dl>
            </article>
          ))}
        </div>
      )}
      <ConflictDialog file={file} />
    </div>
  )
}

// ---------------------------------------------------------------------------
// Page
// ---------------------------------------------------------------------------

const TABS = [
  { id: 'constraints', label: strings.tabConstraints, file: 'constraints.md' },
  { id: 'bible', label: strings.tabBible, file: 'bible.md' },
  { id: 'fakty', label: strings.tabFacts, file: null },
  { id: 'postacie', label: strings.tabCharacters, file: 'characters.md' },
  { id: 'glosy', label: strings.tabVoices, file: null },
] as const

type TabId = (typeof TABS)[number]['id']

export default function Universe() {
  const { id } = useParams()
  const [searchParams, setSearchParams] = useSearchParams()
  const universesQuery = useUniverses()

  const universeId = id ?? ''
  const rawTab = searchParams.get('tab')
  const tab: TabId = TABS.some((t) => t.id === rawTab) ? (rawTab as TabId) : 'constraints'

  const title = universesQuery.data?.find((u) => u.id === universeId)?.title ?? universeId

  if (universeId === '') return null

  const activeTab = TABS.find((t) => t.id === tab) ?? TABS[0]

  return (
    <main className="mx-auto w-full max-w-4xl px-4 pb-24 pt-6 sm:px-6">
      <header className="mb-4">
        <p className="text-xs font-medium uppercase tracking-wide text-stone-400">{strings.universeTitle}</p>
        <h1 className="text-2xl font-semibold text-stone-900">{title}</h1>
        <p className="text-sm text-stone-400">{universeId}</p>
      </header>

      <nav className="mb-6 flex gap-1 overflow-x-auto rounded-xl bg-stone-100 p-1" aria-label={strings.universeTitle}>
        {TABS.map((t) => (
          <button
            key={t.id}
            type="button"
            aria-current={t.id === tab ? 'page' : undefined}
            className={`min-h-11 shrink-0 rounded-lg px-4 text-sm font-medium transition-colors ${
              t.id === tab ? 'bg-white text-stone-900 shadow-sm' : 'text-stone-500 hover:text-stone-800'
            }`}
            onClick={() => setSearchParams({ tab: t.id }, { replace: true })}
          >
            {t.label}
          </button>
        ))}
      </nav>

      {activeTab.file !== null ? (
        <MarkdownFileTab key={activeTab.file} universeId={universeId} fileName={activeTab.file} />
      ) : activeTab.id === 'fakty' ? (
        <PendingFactsTab universeId={universeId} />
      ) : (
        <VoicesTab universeId={universeId} />
      )}
    </main>
  )
}
