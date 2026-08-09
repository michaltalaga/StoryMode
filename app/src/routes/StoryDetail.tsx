import { useState } from 'react'
import type { FormEvent } from 'react'
import { Link, useNavigate, useParams } from 'react-router'
import * as Dialog from '@radix-ui/react-dialog'
import { useQueryClient } from '@tanstack/react-query'
import { ApiError, audioUrl, getWithETag, postJson, putWithETag, recollectionUrl } from '../api/client'
import { useJobs, useStory } from '../api/queries'
import type { JobDto, VariantSummary } from '../api/types'
import type { Strings } from '../i18n'
import { useStrings } from '../i18n'
import { usePlayerStore } from '../player/playerStore'

// ---------------------------------------------------------------------------
// Local view of GET /api/stories/{sid} (see Program.cs) — the pinned
// StorySummary does not carry recollections/artifacts, so the detail payload
// is described here and the integrator can hoist it into api/types.ts.
// ---------------------------------------------------------------------------

interface VariantArtifacts {
  session: boolean
  outline: boolean
  draft: boolean
  verify: boolean
  pendingFacts: boolean
  audio: boolean
}

type DetailVariant = VariantSummary & { artifacts?: VariantArtifacts }

interface StoryDetailData {
  id: string
  title: string
  universe: string
  recollections: string[]
  variants: DetailVariant[]
}

const AUDIO_EXTENSIONS = new Set(['m4a', 'mp3', 'wav', 'ogg', 'opus', 'webm', 'aac', 'flac', 'wma'])

function extensionOf(file: string): string {
  const dot = file.lastIndexOf('.')
  return dot < 0 ? '' : file.slice(dot + 1).toLowerCase()
}

function personOf(file: string): string {
  const dot = file.lastIndexOf('.')
  return dot < 0 ? file : file.slice(0, dot)
}

function isJobActive(job: JobDto): boolean {
  return job.state === 'queued' || job.state === 'running'
}

// ---------------------------------------------------------------------------
// Stage tracker (duplicated from StoryList — integrator may hoist)
// ---------------------------------------------------------------------------

const STAGES = ['spec', 'outline', 'draft', 'audio'] as const

function stageLabels(strings: Strings): Record<VariantSummary['stage'], string> {
  return {
    spec: strings.stageSpec,
    outline: strings.stageOutline,
    draft: strings.stageDraft,
    audio: strings.stageAudio,
  }
}

function StageDots({ stage }: { stage: VariantSummary['stage'] }) {
  const label = stageLabels(useStrings())[stage]

  const reached = STAGES.indexOf(stage)
  return (
    <span className="inline-flex items-center gap-1" title={label} aria-label={label}>
      {STAGES.map((s, i) => (
        <span
          key={s}
          className={`h-2 w-2 rounded-full ${i <= reached ? 'bg-amber-500' : 'bg-stone-200'}`}
        />
      ))}
    </span>
  )
}

// ---------------------------------------------------------------------------
// Add-variant dialog: POST /api/stories creates session.<new>.json skeleton
// for an existing story folder (no dedicated create-variant route in api.md),
// then the source session json is cloned over it via GET + ETag-guarded PUT.
// ---------------------------------------------------------------------------

const inputCls =
  'w-full rounded-xl border border-stone-300 bg-white px-3 py-2.5 text-base text-stone-900 outline-none focus:border-amber-500'
const labelCls = 'block text-sm font-medium text-stone-700 mb-1'

function AddVariantDialog({ story }: { story: StoryDetailData }) {
  const strings = useStrings()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const [open, setOpen] = useState(false)
  const [source, setSource] = useState('')
  const [name, setName] = useState('')
  const [pov, setPov] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const sourceVariant =
    story.variants.find((v) => v.variant === source) ?? story.variants[0]

  const onOpenChange = (next: boolean) => {
    setOpen(next)
    if (next) {
      setSource(story.variants[0]?.variant ?? '')
      setName('')
      setPov('')
      setError(null)
    }
  }

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    const newName = name.trim().toLowerCase()
    if (sourceVariant === undefined) return
    if (!/^[a-z0-9][a-z0-9-]*$/.test(newName)) {
      setError(strings.variantNameInvalid)
      return
    }
    if (story.variants.some((v) => v.variant === newName)) {
      setError(strings.variantExists)
      return
    }
    setBusy(true)
    setError(null)
    try {
      // 1. Create the skeleton file for the new variant.
      await postJson<{ id: string }>('/api/stories', {
        slug: story.id,
        universe: story.universe,
        variant: newName,
        title: story.title,
        language: sourceVariant.language,
      })
      // 2. Clone the source session json, tweak pov, PUT over the skeleton.
      const src = await getWithETag(`/api/stories/${story.id}/session/${sourceVariant.variant}`)
      const skeleton = await getWithETag(`/api/stories/${story.id}/session/${newName}`)
      const clone = JSON.parse(src.text) as Record<string, unknown>
      clone.pov = pov.trim()
      await putWithETag(
        `/api/stories/${story.id}/session/${newName}`,
        JSON.stringify(clone, null, 2) + '\n',
        skeleton.etag,
      )
      await queryClient.invalidateQueries()
      setOpen(false)
      navigate(`/stories/${story.id}/v/${newName}/builder`)
    } catch (err) {
      setError(err instanceof ApiError ? `${err.message} (HTTP ${err.status})` : String(err))
    } finally {
      setBusy(false)
    }
  }

  return (
    <Dialog.Root open={open} onOpenChange={onOpenChange}>
      <Dialog.Trigger asChild>
        <button
          type="button"
          className="rounded-xl border border-stone-300 bg-white px-4 py-2.5 text-base font-medium text-stone-700 shadow-sm active:bg-stone-100"
        >
          {strings.addVariant}
        </button>
      </Dialog.Trigger>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 z-40 bg-black/40" />
        <Dialog.Content className="fixed left-1/2 top-1/2 z-50 max-h-[85vh] w-[calc(100vw-2rem)] max-w-md -translate-x-1/2 -translate-y-1/2 overflow-y-auto rounded-2xl bg-white p-6 shadow-xl">
          <Dialog.Title className="text-lg font-semibold text-stone-900">
            {strings.cloneTitle}
          </Dialog.Title>
          <Dialog.Description className="mt-1 text-sm text-stone-500">
            {strings.cloneDescription}
          </Dialog.Description>
          <form onSubmit={submit} className="mt-4 space-y-4">
            <div>
              <label className={labelCls} htmlFor="clone-source">
                {strings.cloneSourceLabel}
              </label>
              <select
                id="clone-source"
                className={inputCls}
                value={sourceVariant?.variant ?? ''}
                onChange={(e) => setSource(e.target.value)}
              >
                {story.variants.map((v) => (
                  <option key={v.variant} value={v.variant}>
                    {v.variant}
                    {v.pov !== '' ? ` (POV: ${v.pov})` : ''}
                  </option>
                ))}
              </select>
            </div>
            <div>
              <label className={labelCls} htmlFor="clone-name">
                {strings.cloneNameLabel}
              </label>
              <input
                id="clone-name"
                className={inputCls}
                value={name}
                onChange={(e) => setName(e.target.value)}
                placeholder={strings.newStoryVariantPlaceholder}
                autoFocus
              />
            </div>
            <div>
              <label className={labelCls} htmlFor="clone-pov">
                {strings.clonePovLabel}
              </label>
              <input
                id="clone-pov"
                className={inputCls}
                value={pov}
                onChange={(e) => setPov(e.target.value)}
                placeholder={strings.clonePovPlaceholder}
              />
            </div>
            {error !== null && (
              <p className="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700">{error}</p>
            )}
            <div className="flex justify-end gap-2 pt-1">
              <Dialog.Close asChild>
                <button
                  type="button"
                  className="rounded-xl px-4 py-2.5 text-base text-stone-600 hover:bg-stone-100"
                >
                  {strings.cancel}
                </button>
              </Dialog.Close>
              <button
                type="submit"
                disabled={busy}
                className="rounded-xl bg-stone-900 px-4 py-2.5 text-base font-medium text-white disabled:opacity-50"
              >
                {busy ? strings.creating : strings.createVariant}
              </button>
            </div>
          </form>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  )
}

// ---------------------------------------------------------------------------
// Story detail route
// ---------------------------------------------------------------------------

export default function StoryDetail() {
  const strings = useStrings()
  const STAGE_LABELS = stageLabels(strings)
  const { id = '' } = useParams()
  const navigate = useNavigate()
  const play = usePlayerStore((s) => s.play)
  const storyQuery = useStory(id)
  const story = storyQuery.data as unknown as StoryDetailData | undefined
  const jobs = (useJobs().data ?? []) as JobDto[]

  if (storyQuery.isLoading) {
    return <p className="mx-auto max-w-4xl px-4 py-8 text-stone-500">{strings.loading}</p>
  }
  if (storyQuery.isError || story === undefined) {
    return (
      <div className="mx-auto max-w-4xl px-4 py-8">
        <p className="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700">
          {strings.storyNotFound}
        </p>
        <Link to="/" className="mt-4 inline-block text-sm text-stone-600 underline">
          {strings.backToList}
        </Link>
      </div>
    )
  }

  const storyJobs = jobs.filter((j) => j.storyId === story.id && isJobActive(j))
  const transcribeActive = storyJobs.some((j) => j.type === 'transcribe')
  const transcriptFiles = new Set(
    story.recollections.filter((f) => extensionOf(f) === 'txt').map((f) => personOf(f)),
  )
  const recollectionRows = [...story.recollections].sort((a, b) => a.localeCompare(b))

  const activeJobFor = (variant: string) =>
    storyJobs.find((j) => j.variant === variant && j.type !== 'transcribe')

  const listenTo = (v: DetailVariant) => {
    play(
      [
        {
          storyId: story.id,
          variant: v.variant,
          title: `${story.title} — ${v.variant}`,
          url: audioUrl(story.id, v.variant),
        },
      ],
      0,
    )
    navigate('/listen')
  }

  // Next action by stage, with running-job awareness.
  const nextActionFor = (v: DetailVariant): { label: string; run: () => void } => {
    const base = `/stories/${story.id}/v/${v.variant}`
    if (activeJobFor(v.variant) !== undefined) {
      return {
        label: strings.actionProgress,
        run: () => navigate(`${base}/progress`),
      }
    }
    switch (v.stage) {
      case 'spec':
        return story.recollections.length > 0
          ? { label: strings.actionBuild, run: () => navigate(`${base}/builder`) }
          : {
              label: strings.actionCapture,
              run: () => navigate(`/stories/${story.id}/capture`),
            }
      case 'outline':
        return { label: strings.actionProgress, run: () => navigate(`${base}/progress`) }
      case 'draft':
        return { label: strings.actionReadDraft, run: () => navigate(`${base}/draft`) }
      case 'audio':
        return { label: strings.goListen, run: () => listenTo(v) }
    }
  }

  return (
    <div className="mx-auto max-w-4xl px-4 py-6">
      <Link to="/" className="text-sm text-stone-500 hover:text-stone-700">
        ← {strings.navHome}
      </Link>
      <header className="mt-2 flex flex-wrap items-center justify-between gap-3">
        <div>
          <h1 className="text-2xl font-bold text-stone-900">{story.title}</h1>
          <p className="mt-0.5 text-sm text-stone-500">
            <Link to={`/universes/${story.universe}`} className="underline decoration-stone-300 hover:text-stone-700">
              {story.universe}
            </Link>
            <span className="mx-2 text-stone-300">·</span>
            <span className="font-mono text-xs">{story.id}</span>
          </p>
        </div>
        <AddVariantDialog story={story} />
      </header>

      {/* Shared recollections */}
      <section className="mt-6 rounded-2xl border border-stone-200 bg-white p-4 shadow-sm">
        <div className="flex flex-wrap items-baseline justify-between gap-2">
          <h2 className="text-lg font-semibold text-stone-900">{strings.recollectionsTitle}</h2>
          <span className="rounded-full bg-amber-50 px-2.5 py-0.5 text-xs text-amber-800">
            {strings.sharedAcrossVariants}
          </span>
        </div>
        {recollectionRows.length === 0 ? (
          <p className="mt-3 text-sm text-stone-500">
            {strings.recollectionsEmpty}
          </p>
        ) : (
          <ul className="mt-3 divide-y divide-stone-100">
            {recollectionRows.map((file) => {
              const ext = extensionOf(file)
              const isAudio = AUDIO_EXTENSIONS.has(ext)
              const person = personOf(file)
              const hasTranscript = isAudio && transcriptFiles.has(person)
              return (
                <li key={file} className="flex flex-col gap-2 py-3 sm:flex-row sm:items-center">
                  <span className="w-32 shrink-0 truncate text-sm font-medium text-stone-800">
                    {person}
                    <span className="ml-1 font-normal text-stone-400">.{ext}</span>
                  </span>
                  {isAudio ? (
                    <audio
                      controls
                      preload="none"
                      src={recollectionUrl(story.id, file)}
                      className="h-9 w-full min-w-0 flex-1"
                    />
                  ) : (
                    <span className="flex-1 text-sm text-stone-500">{strings.textNote}</span>
                  )}
                  {isAudio &&
                    (hasTranscript ? (
                      <span className="shrink-0 self-start rounded-full bg-stone-100 px-2.5 py-0.5 text-xs text-stone-600 sm:self-auto">
                        {strings.transcriptChip}
                      </span>
                    ) : transcribeActive ? (
                      <span className="shrink-0 animate-pulse self-start rounded-full bg-amber-100 px-2.5 py-0.5 text-xs text-amber-800 sm:self-auto">
                        {strings.transcriptPendingChip}
                      </span>
                    ) : null)}
                </li>
              )
            })}
          </ul>
        )}
        <Link
          to={`/stories/${story.id}/capture`}
          className="mt-3 inline-block rounded-xl border border-stone-300 px-4 py-2 text-sm font-medium text-stone-700 active:bg-stone-100"
        >
          {strings.addRecollection}
        </Link>
      </section>

      {/* Variants */}
      <section className="mt-6">
        <h2 className="text-lg font-semibold text-stone-900">{strings.variantsLabel}</h2>
        <div className="mt-3 grid gap-4 sm:grid-cols-2">
          {story.variants.map((v) => {
            const action = nextActionFor(v)
            const busyJob = activeJobFor(v.variant)
            return (
              <div key={v.variant} className="rounded-2xl border border-stone-200 bg-white p-4 shadow-sm">
                <div className="flex items-start justify-between gap-2">
                  <div>
                    <h3 className="font-semibold text-stone-900">{v.variant}</h3>
                    <p className="mt-0.5 text-sm text-stone-500">
                      {v.pov !== '' ? `POV: ${v.pov}` : strings.povUnset}
                      <span className="mx-1.5 text-stone-300">·</span>
                      {v.language}
                    </p>
                  </div>
                  <span className="rounded-full bg-stone-100 px-2.5 py-0.5 text-xs text-stone-600">
                    {STAGE_LABELS[v.stage]}
                  </span>
                </div>
                <div className="mt-3 flex items-center gap-2">
                  <StageDots stage={v.stage} />
                  {busyJob !== undefined && (
                    <span className="animate-pulse rounded-full bg-amber-100 px-2.5 py-0.5 text-xs text-amber-800">
                      {busyJob.state === 'running'
                        ? `${strings.jobWorkingPrefix}: ${busyJob.stage !== '' ? busyJob.stage : busyJob.type}`
                        : strings.jobQueued}
                    </span>
                  )}
                </div>
                <button
                  type="button"
                  onClick={action.run}
                  className="mt-4 w-full rounded-xl bg-stone-900 px-4 py-3 text-base font-medium text-white active:bg-stone-700"
                >
                  {action.label}
                </button>
                <div className="mt-2 flex flex-wrap gap-x-4 gap-y-1 text-xs text-stone-500">
                  <Link className="hover:underline" to={`/stories/${story.id}/v/${v.variant}/builder`}>
                    {strings.linkSession}
                  </Link>
                  <Link className="hover:underline" to={`/stories/${story.id}/v/${v.variant}/progress`}>
                    {strings.linkProgress}
                  </Link>
                  {v.artifacts?.draft === true && (
                    <Link className="hover:underline" to={`/stories/${story.id}/v/${v.variant}/draft`}>
                      {strings.linkDraft}
                    </Link>
                  )}
                  {v.artifacts?.audio === true && (
                    <button type="button" className="hover:underline" onClick={() => listenTo(v)}>
                      {strings.linkAudio}
                    </button>
                  )}
                </div>
              </div>
            )
          })}
        </div>
      </section>
    </div>
  )
}
