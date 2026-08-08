import { useEffect, useRef, useState } from 'react'
import type { ChangeEvent } from 'react'
import { Link, useParams } from 'react-router'
import * as Dialog from '@radix-ui/react-dialog'
import { useQueryClient } from '@tanstack/react-query'
import {
  ApiError,
  ConflictError,
  getWithETag,
  putWithETag,
  recollectionUrl,
  uploadRecollection,
} from '../api/client'
import { useJobs, useStory } from '../api/queries'
import type { JobDto } from '../api/types'
import { strings } from '../strings'

// ---------------------------------------------------------------------------
// Helpers (recollection filenames are <person>.<ext>; transcripts <person>.txt)
// ---------------------------------------------------------------------------

const AUDIO_EXTENSIONS = new Set(['m4a', 'mp3', 'wav', 'ogg', 'opus', 'webm', 'aac', 'flac', 'wma'])

function extensionOf(file: string): string {
  const dot = file.lastIndexOf('.')
  return dot < 0 ? '' : file.slice(dot + 1).toLowerCase()
}

function personOf(file: string): string {
  const dot = file.lastIndexOf('.')
  return dot < 0 ? file : file.slice(0, dot)
}

const PL_DIACRITICS: Record<string, string> = {
  ą: 'a', ć: 'c', ę: 'e', ł: 'l', ń: 'n', ó: 'o', ś: 's', ż: 'z', ź: 'z',
}

function sanitizePerson(name: string): string {
  return name
    .trim()
    .toLowerCase()
    .replace(/[ąćęłńóśżź]/g, (c) => PL_DIACRITICS[c] ?? c)
    .replace(/[^a-z0-9_-]+/g, '-')
    .replace(/^-+|-+$/g, '')
}

function isJobActive(job: JobDto): boolean {
  return job.state === 'queued' || job.state === 'running'
}

interface UploadRow {
  key: number
  person: string
  fileName: string
  pct: number
  status: 'uploading' | 'done' | 'error'
  error?: string
}

// Minimal local view of GET /api/stories/{sid}; see StoryDetail.tsx note.
interface StoryDetailData {
  id: string
  title: string
  recollections: string[]
}

// ---------------------------------------------------------------------------
// Transcript dialog: read-only first; editing requires an explicit
// "Edytuj mimo to" step — transcripts are deliberately left raw (HANDOVER.md:
// the messiness is the input).
// ---------------------------------------------------------------------------

function TranscriptDialog({
  storyId,
  file,
  onClose,
}: {
  storyId: string
  file: string | null
  onClose: () => void
}) {
  const [text, setText] = useState('')
  const [etag, setEtag] = useState('')
  const [phase, setPhase] = useState<'loading' | 'view' | 'confirm' | 'edit'>('loading')
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    if (file === null) return
    let cancelled = false
    setPhase('loading')
    setError(null)
    getWithETag(`/api/stories/${storyId}/recollections/${file}`)
      .then((res) => {
        if (cancelled) return
        setText(res.text)
        setEtag(res.etag)
        setPhase('view')
      })
      .catch((err: unknown) => {
        if (cancelled) return
        setError(err instanceof ApiError ? err.message : String(err))
        setPhase('view')
      })
    return () => {
      cancelled = true
    }
  }, [storyId, file])

  const save = async () => {
    if (file === null) return
    setSaving(true)
    setError(null)
    try {
      const res = await putWithETag(`/api/stories/${storyId}/recollections/${file}`, text, etag)
      setEtag(res.etag)
      setPhase('view')
    } catch (err) {
      if (err instanceof ConflictError) {
        setText(err.currentText)
        setEtag(err.currentETag)
        setPhase('view')
        setError(strings.transcriptReloaded)
      } else {
        setError(err instanceof ApiError ? `${err.message} (HTTP ${err.status})` : String(err))
      }
    } finally {
      setSaving(false)
    }
  }

  return (
    <Dialog.Root open={file !== null} onOpenChange={(open) => !open && onClose()}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 z-40 bg-black/40" />
        <Dialog.Content className="fixed left-1/2 top-1/2 z-50 flex max-h-[85vh] w-[calc(100vw-2rem)] max-w-2xl -translate-x-1/2 -translate-y-1/2 flex-col rounded-2xl bg-white p-6 shadow-xl">
          <Dialog.Title className="text-lg font-semibold text-stone-900">
            {file ?? ''}
          </Dialog.Title>
          <Dialog.Description className="mt-1 text-sm text-stone-500">
            {strings.transcriptRawHint}
          </Dialog.Description>

          {phase === 'loading' && (
            <p className="mt-4 text-stone-500">{strings.loading}</p>
          )}

          {phase !== 'loading' && phase !== 'edit' && (
            <pre className="mt-4 flex-1 overflow-y-auto whitespace-pre-wrap rounded-xl bg-stone-50 p-4 font-sans text-sm leading-relaxed text-stone-800">
              {text}
            </pre>
          )}
          {phase === 'edit' && (
            <textarea
              className="mt-4 min-h-56 flex-1 resize-y rounded-xl border border-stone-300 p-4 font-sans text-sm leading-relaxed text-stone-800 outline-none focus:border-amber-500"
              value={text}
              onChange={(e) => setText(e.target.value)}
            />
          )}

          {error !== null && (
            <p className="mt-3 rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700">{error}</p>
          )}

          {phase === 'confirm' && (
            <div className="mt-4 rounded-xl border border-amber-200 bg-amber-50 p-4">
              <p className="text-sm text-amber-900">
                {strings.transcriptEditWarning}
              </p>
              <div className="mt-3 flex justify-end gap-2">
                <button
                  type="button"
                  onClick={() => setPhase('view')}
                  className="rounded-xl px-4 py-2 text-sm text-stone-600 hover:bg-stone-100"
                >
                  {strings.goBack}
                </button>
                <button
                  type="button"
                  onClick={() => setPhase('edit')}
                  className="rounded-xl bg-amber-500 px-4 py-2 text-sm font-medium text-white active:bg-amber-600"
                >
                  {strings.editAnyway}
                </button>
              </div>
            </div>
          )}

          <div className="mt-4 flex justify-end gap-2">
            {phase === 'view' && (
              <button
                type="button"
                onClick={() => setPhase('confirm')}
                className="rounded-xl border border-stone-300 px-4 py-2 text-sm font-medium text-stone-700 active:bg-stone-100"
              >
                {strings.edit}
              </button>
            )}
            {phase === 'edit' && (
              <button
                type="button"
                onClick={save}
                disabled={saving}
                className="rounded-xl bg-stone-900 px-4 py-2 text-sm font-medium text-white disabled:opacity-50"
              >
                {saving ? strings.saving : strings.save}
              </button>
            )}
            <Dialog.Close asChild>
              <button
                type="button"
                className="rounded-xl px-4 py-2 text-sm text-stone-600 hover:bg-stone-100"
              >
                {strings.close}
              </button>
            </Dialog.Close>
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  )
}

// ---------------------------------------------------------------------------
// Capture route — phone-primary upload screen
// ---------------------------------------------------------------------------

export default function Capture() {
  const { id = '' } = useParams()
  const queryClient = useQueryClient()
  const storyQuery = useStory(id)
  const story = storyQuery.data as unknown as StoryDetailData | undefined
  const jobs = (useJobs().data ?? []) as JobDto[]

  const [uploads, setUploads] = useState<UploadRow[]>([])
  const [newPerson, setNewPerson] = useState('')
  const [openTranscript, setOpenTranscript] = useState<string | null>(null)
  const keyRef = useRef(0)

  const recollections = story?.recollections ?? []
  const people = [...new Set(recollections.map(personOf))].sort((a, b) => a.localeCompare(b))
  const transcriptPeople = new Set(
    recollections.filter((f) => extensionOf(f) === 'txt').map(personOf),
  )
  const transcribeActive = jobs.some(
    (j) => j.storyId === id && j.type === 'transcribe' && isJobActive(j),
  )

  const startUpload = async (person: string, file: File) => {
    keyRef.current += 1
    const key = keyRef.current
    setUploads((rows) => [
      ...rows,
      { key, person, fileName: file.name, pct: 0, status: 'uploading' },
    ])
    const patch = (change: Partial<UploadRow>) =>
      setUploads((rows) => rows.map((r) => (r.key === key ? { ...r, ...change } : r)))
    try {
      await uploadRecollection(id, person, file, (pct) => patch({ pct }))
      patch({ pct: 100, status: 'done' })
      // Refresh recollection list and pick up the auto-enqueued transcribe job.
      await queryClient.invalidateQueries()
    } catch (err) {
      patch({
        status: 'error',
        error: err instanceof ApiError ? `${err.message} (HTTP ${err.status})` : String(err),
      })
    }
  }

  const onPickFile = (person: string) => (e: ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0]
    e.target.value = ''
    if (file === undefined) return
    const safe = sanitizePerson(person)
    if (safe === '') return
    void startUpload(safe, file)
  }

  const uploadButtonCls =
    'flex w-full cursor-pointer items-center justify-center gap-2 rounded-2xl border-2 border-dashed border-stone-300 bg-white px-4 py-5 text-lg font-medium text-stone-800 shadow-sm active:border-amber-500 active:bg-amber-50'

  return (
    <div className="mx-auto max-w-2xl px-4 py-6">
      <Link to={`/stories/${id}`} className="text-sm text-stone-500 hover:text-stone-700">
        ← {story?.title ?? id}
      </Link>
      <h1 className="mt-2 text-2xl font-bold text-stone-900">
        {strings.captureHeading}
      </h1>
      <p className="mt-1 text-sm text-stone-500">
        {strings.captureSharedHint}
      </p>

      {/* Per-person upload buttons */}
      <section className="mt-6 space-y-3">
        {people.map((person) => (
          <label key={person} className={uploadButtonCls}>
            <input
              type="file"
              accept="audio/*,.txt,.md"
              className="hidden"
              onChange={onPickFile(person)}
            />
            <span>
              {strings.addFile} — <span className="font-semibold">{person}</span>
            </span>
          </label>
        ))}

        <div className="rounded-2xl border border-stone-200 bg-white p-4 shadow-sm">
          <label className="block text-sm font-medium text-stone-700" htmlFor="capture-new-person">
            {strings.captureNewPersonLabel}
          </label>
          <div className="mt-2 flex gap-2">
            <input
              id="capture-new-person"
              className="w-full min-w-0 flex-1 rounded-xl border border-stone-300 px-3 py-2.5 text-base outline-none focus:border-amber-500"
              value={newPerson}
              onChange={(e) => setNewPerson(e.target.value)}
              placeholder={strings.captureNewPersonPlaceholder}
            />
            <label
              className={`flex shrink-0 items-center rounded-xl px-4 py-2.5 text-base font-medium ${
                sanitizePerson(newPerson) === ''
                  ? 'cursor-not-allowed bg-stone-100 text-stone-400'
                  : 'cursor-pointer bg-stone-900 text-white active:bg-stone-700'
              }`}
            >
              <input
                type="file"
                accept="audio/*,.txt,.md"
                className="hidden"
                disabled={sanitizePerson(newPerson) === ''}
                onChange={onPickFile(newPerson)}
              />
              {strings.addFile}
            </label>
          </div>
        </div>
      </section>

      {/* In-flight / finished uploads */}
      {uploads.length > 0 && (
        <section className="mt-6 space-y-2">
          {uploads.map((u) => (
            <div key={u.key} className="rounded-xl border border-stone-200 bg-white p-3 shadow-sm">
              <div className="flex items-center justify-between gap-2 text-sm">
                <span className="truncate text-stone-800">
                  <span className="font-medium">{u.person}</span>
                  <span className="mx-1.5 text-stone-300">·</span>
                  {u.fileName}
                </span>
                <span className="shrink-0 text-xs text-stone-500">
                  {u.status === 'uploading' && `${Math.round(u.pct)}%`}
                  {u.status === 'done' && strings.uploadedShort}
                  {u.status === 'error' && strings.errorShort}
                </span>
              </div>
              {u.status === 'uploading' && (
                <div className="mt-2 h-2 w-full overflow-hidden rounded-full bg-stone-200">
                  <div
                    className="h-full rounded-full bg-amber-500 transition-all"
                    style={{ width: `${u.pct}%` }}
                  />
                </div>
              )}
              {u.status === 'error' && u.error !== undefined && (
                <p className="mt-2 rounded-lg bg-red-50 px-3 py-1.5 text-xs text-red-700">
                  {u.error}
                </p>
              )}
            </div>
          ))}
        </section>
      )}

      {/* Existing recollections */}
      <section className="mt-8">
        <h2 className="text-lg font-semibold text-stone-900">{strings.captureCollectedTitle}</h2>
        {storyQuery.isLoading && (
          <p className="mt-3 text-sm text-stone-500">{strings.loading}</p>
        )}
        {!storyQuery.isLoading && recollections.length === 0 && (
          <p className="mt-3 text-sm text-stone-500">
            {strings.captureEmptyList}
          </p>
        )}
        <ul className="mt-3 space-y-3">
          {[...recollections].sort((a, b) => a.localeCompare(b)).map((file) => {
            const ext = extensionOf(file)
            const isAudio = AUDIO_EXTENSIONS.has(ext)
            const person = personOf(file)
            const hasTranscript = transcriptPeople.has(person)
            return (
              <li key={file} className="rounded-xl border border-stone-200 bg-white p-3 shadow-sm">
                <div className="flex items-center justify-between gap-2">
                  <span className="truncate text-sm font-medium text-stone-800">
                    {person}
                    <span className="ml-1 font-normal text-stone-400">.{ext}</span>
                  </span>
                  {isAudio ? (
                    hasTranscript ? (
                      <button
                        type="button"
                        onClick={() => setOpenTranscript(`${person}.txt`)}
                        className="shrink-0 rounded-full bg-stone-100 px-3 py-1 text-xs font-medium text-stone-700 active:bg-stone-200"
                      >
                        {strings.transcriptChip}
                      </button>
                    ) : transcribeActive ? (
                      <span className="shrink-0 animate-pulse rounded-full bg-amber-100 px-3 py-1 text-xs text-amber-800">
                        {strings.transcriptPendingChip}
                      </span>
                    ) : null
                  ) : (
                    <button
                      type="button"
                      onClick={() => setOpenTranscript(file)}
                      className="shrink-0 rounded-full bg-stone-100 px-3 py-1 text-xs font-medium text-stone-700 active:bg-stone-200"
                    >
                      {strings.openText}
                    </button>
                  )}
                </div>
                {isAudio && (
                  <audio
                    controls
                    preload="none"
                    src={recollectionUrl(id, file)}
                    className="mt-2 h-9 w-full"
                  />
                )}
              </li>
            )
          })}
        </ul>
      </section>

      <TranscriptDialog storyId={id} file={openTranscript} onClose={() => setOpenTranscript(null)} />
    </div>
  )
}
