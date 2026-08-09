import { useState } from 'react'
import type { FormEvent } from 'react'
import { Link, useNavigate } from 'react-router'
import * as Dialog from '@radix-ui/react-dialog'
import { useQueryClient } from '@tanstack/react-query'
import { ApiError, audioUrl, postJson } from '../api/client'
import { useStories, useUniverses } from '../api/queries'
import type { StorySummary, VariantSummary } from '../api/types'
import type { Strings } from '../i18n'
import { useStrings } from '../i18n'
import { usePlayerStore } from '../player/playerStore'

// ---------------------------------------------------------------------------
// Stage tracker (spec -> outline -> draft -> audio)
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

function stageIndex(stage: VariantSummary['stage']): number {
  return STAGES.indexOf(stage)
}

function StageDots({ stage }: { stage: VariantSummary['stage'] }) {
  const label = stageLabels(useStrings())[stage]

  const reached = stageIndex(stage)
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

function audioTracks(story: StorySummary) {
  return story.variants
    .filter((v) => v.stage === 'audio')
    .map((v) => ({
      storyId: story.id,
      variant: v.variant,
      title: `${story.title} — ${v.variant}`,
      url: audioUrl(story.id, v.variant),
    }))
}

// ---------------------------------------------------------------------------
// New story dialog
// ---------------------------------------------------------------------------

const PL_DIACRITICS: Record<string, string> = {
  ą: 'a', ć: 'c', ę: 'e', ł: 'l', ń: 'n', ó: 'o', ś: 's', ż: 'z', ź: 'z',
}

function slugify(text: string): string {
  return text
    .toLowerCase()
    .replace(/[ąćęłńóśżź]/g, (c) => PL_DIACRITICS[c] ?? c)
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '')
}

function suggestSlug(title: string): string {
  const today = new Date().toISOString().slice(0, 10)
  const rest = slugify(title)
  return rest.length > 0 ? `${today}-${rest}` : today
}

const inputCls =
  'w-full rounded-xl border border-stone-300 bg-white px-3 py-2.5 text-base text-stone-900 outline-none focus:border-amber-500'
const labelCls = 'block text-sm font-medium text-stone-700 mb-1'

function NewStoryDialog() {
  const strings = useStrings()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const universesQuery = useUniverses()
  const universes = (universesQuery.data ?? []) as { id: string; title: string }[]

  const [open, setOpen] = useState(false)
  const [title, setTitle] = useState('')
  const [slug, setSlug] = useState(suggestSlug(''))
  const [slugTouched, setSlugTouched] = useState(false)
  const [universe, setUniverse] = useState('')
  const [variant, setVariant] = useState('')
  const [language, setLanguage] = useState<'pl' | 'en'>('pl')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const selectedUniverse = universe !== '' ? universe : (universes[0]?.id ?? '')

  const reset = () => {
    setTitle('')
    setSlug(suggestSlug(''))
    setSlugTouched(false)
    setUniverse('')
    setVariant('')
    setLanguage('pl')
    setError(null)
  }

  const onOpenChange = (next: boolean) => {
    setOpen(next)
    if (next) reset()
  }

  const onTitleChange = (value: string) => {
    setTitle(value)
    if (!slugTouched) setSlug(suggestSlug(value))
  }

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    const variantName = slugify(variant)
    if (title.trim() === '' || variantName === '') {
      setError(strings.newStoryValidationMissing)
      return
    }
    if (!/^[a-z0-9][a-z0-9-]*$/.test(slug)) {
      setError(strings.newStorySlugInvalid)
      return
    }
    if (selectedUniverse === '') {
      setError(strings.newStoryNoUniverses)
      return
    }
    setBusy(true)
    setError(null)
    try {
      const created = await postJson<{ id: string }>('/api/stories', {
        slug,
        universe: selectedUniverse,
        variant: variantName,
        title: title.trim(),
        language,
      })
      await queryClient.invalidateQueries()
      setOpen(false)
      navigate(`/stories/${created.id}/capture`)
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
          className="rounded-xl bg-stone-900 px-4 py-2.5 text-base font-medium text-white shadow-sm active:bg-stone-700"
        >
          {strings.newStory}
        </button>
      </Dialog.Trigger>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 z-40 bg-black/40" />
        <Dialog.Content className="fixed left-1/2 top-1/2 z-50 max-h-[85vh] w-[calc(100vw-2rem)] max-w-md -translate-x-1/2 -translate-y-1/2 overflow-y-auto rounded-2xl bg-white p-6 shadow-xl">
          <Dialog.Title className="text-lg font-semibold text-stone-900">
            {strings.newStoryTitleHeading}
          </Dialog.Title>
          <Dialog.Description className="mt-1 text-sm text-stone-500">
            {strings.newStoryDescription}
          </Dialog.Description>
          <form onSubmit={submit} className="mt-4 space-y-4">
            <div>
              <label className={labelCls} htmlFor="new-story-title">
                {strings.newStoryTitleLabel}
              </label>
              <input
                id="new-story-title"
                className={inputCls}
                value={title}
                onChange={(e) => onTitleChange(e.target.value)}
                placeholder={strings.newStoryTitlePlaceholder}
                autoFocus
              />
            </div>
            <div>
              <label className={labelCls} htmlFor="new-story-slug">
                {strings.newStorySlugLabel}
              </label>
              <input
                id="new-story-slug"
                className={`${inputCls} font-mono text-sm`}
                value={slug}
                onChange={(e) => {
                  setSlugTouched(true)
                  setSlug(e.target.value)
                }}
              />
            </div>
            <div>
              <label className={labelCls} htmlFor="new-story-universe">
                {strings.newStoryUniverseLabel}
              </label>
              <select
                id="new-story-universe"
                className={inputCls}
                value={selectedUniverse}
                onChange={(e) => setUniverse(e.target.value)}
                disabled={universes.length === 0}
              >
                {universes.length === 0 && (
                  <option value="">{strings.universesEmptyOption}</option>
                )}
                {universes.map((u) => (
                  <option key={u.id} value={u.id}>
                    {u.title}
                  </option>
                ))}
              </select>
            </div>
            <div className="grid grid-cols-2 gap-3">
              <div>
                <label className={labelCls} htmlFor="new-story-variant">
                  {strings.newStoryVariantLabel}
                </label>
                <input
                  id="new-story-variant"
                  className={inputCls}
                  value={variant}
                  onChange={(e) => setVariant(e.target.value)}
                  placeholder={strings.newStoryVariantPlaceholder}
                />
              </div>
              <div>
                <label className={labelCls} htmlFor="new-story-language">
                  {strings.newStoryLanguageLabel}
                </label>
                <select
                  id="new-story-language"
                  className={inputCls}
                  value={language}
                  onChange={(e) => setLanguage(e.target.value === 'en' ? 'en' : 'pl')}
                >
                  <option value="pl">{strings.languagePl}</option>
                  <option value="en">{strings.languageEn}</option>
                </select>
              </div>
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
                {busy ? strings.creating : strings.create}
              </button>
            </div>
          </form>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  )
}

// ---------------------------------------------------------------------------
// Story list route
// ---------------------------------------------------------------------------

export default function StoryList() {
  const strings = useStrings()
  const STAGE_LABELS = stageLabels(strings)
  const storiesQuery = useStories()
  const navigate = useNavigate()
  const play = usePlayerStore((s) => s.play)

  const stories = [...(storiesQuery.data ?? [])].sort((a, b) => b.id.localeCompare(a.id))

  const listen = (story: StorySummary) => {
    const tracks = audioTracks(story)
    if (tracks.length === 0) return
    play(tracks, 0)
    navigate('/listen')
  }

  return (
    <div className="mx-auto max-w-5xl px-4 py-6">
      <header className="flex items-center justify-between gap-4">
        <h1 className="text-2xl font-bold text-stone-900">{strings.storiesTitle}</h1>
        <NewStoryDialog />
      </header>

      {storiesQuery.isLoading && (
        <p className="mt-8 text-stone-500">{strings.loading}</p>
      )}
      {storiesQuery.isError && (
        <p className="mt-8 rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700">
          {strings.storiesLoadError}
        </p>
      )}
      {!storiesQuery.isLoading && !storiesQuery.isError && stories.length === 0 && (
        <div className="mt-16 text-center text-stone-500">
          <p className="text-lg">{strings.storiesEmpty}</p>
          <p className="mt-1 text-sm">
            {strings.storiesEmptyHint}
          </p>
        </div>
      )}

      {/* Phone: cards */}
      <ul className="mt-6 space-y-4 md:hidden">
        {stories.map((story) => {
          const hasAudio = story.variants.some((v) => v.stage === 'audio')
          return (
            <li key={story.id} className="rounded-2xl border border-stone-200 bg-white p-4 shadow-sm">
              <Link to={`/stories/${story.id}`} className="block">
                <div className="flex items-start justify-between gap-3">
                  <h2 className="text-lg font-semibold text-stone-900">{story.title}</h2>
                  <span className="shrink-0 rounded-full bg-stone-100 px-2.5 py-0.5 text-xs text-stone-600">
                    {story.universe}
                  </span>
                </div>
              </Link>
              <ul className="mt-3 space-y-2">
                {story.variants.map((v) => (
                  <li
                    key={v.variant}
                    className="flex items-center justify-between gap-2 text-sm text-stone-700"
                  >
                    <span className="truncate font-medium">{v.variant}</span>
                    <span className="flex items-center gap-2">
                      <StageDots stage={v.stage} />
                      <span className="w-24 text-right text-xs text-stone-500">
                        {STAGE_LABELS[v.stage]}
                      </span>
                    </span>
                  </li>
                ))}
              </ul>
              {hasAudio && (
                <button
                  type="button"
                  onClick={() => listen(story)}
                  className="mt-4 w-full rounded-xl bg-stone-900 px-4 py-3 text-base font-medium text-white active:bg-stone-700"
                >
                  {strings.goListen}
                </button>
              )}
            </li>
          )
        })}
      </ul>

      {/* Desktop: denser table */}
      {stories.length > 0 && (
        <div className="mt-6 hidden overflow-x-auto rounded-2xl border border-stone-200 bg-white shadow-sm md:block">
          <table className="w-full text-left text-sm">
            <thead>
              <tr className="border-b border-stone-200 text-xs uppercase tracking-wide text-stone-500">
                <th className="px-4 py-3 font-medium">{strings.titleLabel}</th>
                <th className="px-4 py-3 font-medium">{strings.universeLabel}</th>
                <th className="px-4 py-3 font-medium">{strings.variantsLabel}</th>
                <th className="px-4 py-3" />
              </tr>
            </thead>
            <tbody>
              {stories.map((story) => (
                <tr key={story.id} className="border-b border-stone-100 last:border-b-0 hover:bg-stone-50">
                  <td className="px-4 py-3 align-top">
                    <Link
                      to={`/stories/${story.id}`}
                      className="font-medium text-stone-900 hover:underline"
                    >
                      {story.title}
                    </Link>
                    <div className="mt-0.5 font-mono text-xs text-stone-400">{story.id}</div>
                  </td>
                  <td className="px-4 py-3 align-top text-stone-600">{story.universe}</td>
                  <td className="px-4 py-3 align-top">
                    <ul className="space-y-1.5">
                      {story.variants.map((v) => (
                        <li key={v.variant} className="flex items-center gap-3">
                          <span className="w-28 truncate font-medium text-stone-700">
                            {v.variant}
                          </span>
                          <StageDots stage={v.stage} />
                          <span className="text-xs text-stone-500">{STAGE_LABELS[v.stage]}</span>
                        </li>
                      ))}
                    </ul>
                  </td>
                  <td className="px-4 py-3 align-top text-right">
                    {story.variants.some((v) => v.stage === 'audio') && (
                      <button
                        type="button"
                        onClick={() => listen(story)}
                        className="rounded-lg bg-stone-900 px-3 py-1.5 text-sm font-medium text-white active:bg-stone-700"
                      >
                        {strings.goListen}
                      </button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  )
}
