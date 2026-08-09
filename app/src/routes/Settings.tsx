// Settings: interface language, the voices you have, and environment status.
//
// Voices are installed artifacts here, not configuration. A row shows a name, a flag and a
// delivery — no engine, no file names, no numbers. Play is a static file, so it starts on the
// tap; anything that takes time (downloading, learning a voice) happens inside Add, behind a
// progress bar. The raw voices.json stays reachable under Advanced as the escape hatch.
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import type { ReactNode } from 'react'
import * as Dialog from '@radix-ui/react-dialog'
import { useQueryClient } from '@tanstack/react-query'
import { ConflictError, getWithETag, putWithETag } from '../api/client'
import {
  useDeleteVoice,
  useJob,
  useJobs,
  usePatchVoice,
  usePreviewVoice,
  useSetDefaultVoice,
  useStatus,
  useVoices,
  voicePreviewUrl,
} from '../api/queries'
import type { JobDto, VoiceDto } from '../api/types'
import AddVoiceDialog from '../components/AddVoiceDialog'
import Flag, { localeLabel } from '../components/Flag'
import { PlayButton, useSamplePlayer } from '../components/SamplePlayer'
import type { Lang, Strings } from '../i18n'
import { format, useLanguage, useStrings } from '../i18n'

const btnBase =
  'inline-flex min-h-11 items-center justify-center gap-2 rounded-lg px-4 py-2.5 text-sm font-medium transition-colors disabled:cursor-not-allowed disabled:opacity-40'
const btnPrimary = `${btnBase} bg-stone-900 text-white hover:bg-stone-700`
const btnGhost = `${btnBase} border border-stone-300 bg-white text-stone-700 hover:bg-stone-100`
const btnCard =
  'inline-flex min-h-11 cursor-pointer items-center justify-center gap-1.5 rounded-lg border border-stone-300 bg-white px-3 text-xs font-medium text-stone-700 transition-colors hover:bg-stone-100 disabled:cursor-not-allowed disabled:opacity-40'
// Overflow-menu rows: full width, 44 px minimum, so a thumb can hit them.
const menuItemCls =
  'flex min-h-11 w-full cursor-pointer items-center rounded-lg px-3 py-2.5 text-left text-sm font-medium text-stone-700 transition-colors hover:bg-stone-100'
const menuItemDangerCls =
  'flex min-h-11 w-full cursor-pointer items-center rounded-lg px-3 py-2.5 text-left text-sm font-medium text-red-700 transition-colors hover:bg-red-50'
const inputCls =
  'w-full rounded-xl border border-stone-300 bg-white px-3 py-2.5 text-base text-stone-900 outline-none focus:border-amber-500'
const labelCls = 'mb-1 block text-sm font-medium text-stone-700'
const dialogContentCls =
  'fixed left-1/2 top-1/2 z-50 max-h-[85vh] w-[calc(100vw-2rem)] max-w-md -translate-x-1/2 -translate-y-1/2 overflow-y-auto rounded-2xl bg-white p-6 shadow-xl'

const CATALOG_PATH = '/api/voices/catalog'

function Presence({ ok, okLabel, missingLabel }: { ok: boolean; okLabel?: string; missingLabel?: string }) {
  const strings = useStrings()

  return ok ? (
    <span className="inline-flex items-center gap-2 text-sm text-emerald-700">
      <span aria-hidden className="inline-flex h-6 w-6 items-center justify-center rounded-full bg-emerald-100 font-semibold">
        ✓
      </span>
      {okLabel ?? strings.statusFound}
    </span>
  ) : (
    <span className="inline-flex items-center gap-2 text-sm text-red-700">
      <span aria-hidden className="inline-flex h-6 w-6 items-center justify-center rounded-full bg-red-100 font-semibold">
        ✕
      </span>
      {missingLabel ?? strings.statusMissing}
    </span>
  )
}

function Row({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="flex flex-col gap-1 py-3 sm:flex-row sm:items-center sm:justify-between sm:gap-4">
      <span className="text-sm font-medium text-stone-600">{label}</span>
      <span className="text-sm text-stone-900">{children}</span>
    </div>
  )
}

/** Language picker: two segmented buttons, applied instantly (no reload). */
function LanguageSection() {
  const strings = useStrings()
  const { lang, setLang } = useLanguage()

  const options: { id: Lang; label: string }[] = [
    { id: 'en', label: strings.languageNameEn },
    { id: 'pl', label: strings.languageNamePl },
  ]

  return (
    <section className="rounded-xl border border-stone-200 bg-white px-4">
      <h2 className="border-b border-stone-100 py-3 text-sm font-semibold uppercase tracking-wide text-stone-400">
        {strings.settingsLanguageHeading}
      </h2>
      <div className="py-4">
        <div
          role="group"
          aria-label={strings.settingsLanguageHeading}
          className="inline-flex overflow-hidden rounded-lg border border-stone-300"
        >
          {options.map((option) => (
            <button
              key={option.id}
              type="button"
              lang={option.id}
              aria-pressed={lang === option.id}
              onClick={() => setLang(option.id)}
              className={`min-h-11 px-5 text-sm font-medium transition-colors ${
                lang === option.id
                  ? 'bg-stone-900 text-white'
                  : 'bg-white text-stone-600 hover:bg-stone-100'
              }`}
            >
              {option.label}
            </button>
          ))}
        </div>
        <p className="mt-2 text-xs text-stone-500">{strings.settingsLanguageHint}</p>
      </div>
    </section>
  )
}

// ---------------------------------------------------------------------------
// Voices
// ---------------------------------------------------------------------------

/** Style ids come from the backend; the label is ours. Unknown ids show as-is. */
function styleLabel(style: string, strings: Strings): string {
  switch (style) {
    case 'calm':
      return strings.voiceStyleCalm
    case 'natural':
      return strings.voiceStyleNatural
    case 'lively':
      return strings.voiceStyleLively
    default:
      return style
  }
}

function Spinner() {
  return (
    <span
      aria-hidden
      className="inline-block h-4 w-4 shrink-0 animate-spin rounded-full border-2 border-stone-300 border-t-violet-600"
    />
  )
}

function DotsIcon() {
  return (
    <svg viewBox="0 0 24 24" className="h-5 w-5" fill="currentColor" aria-hidden>
      <circle cx="5" cy="12" r="1.75" />
      <circle cx="12" cy="12" r="1.75" />
      <circle cx="19" cy="12" r="1.75" />
    </svg>
  )
}

/**
 * The row's ⋯ sheet: delivery, plus the three things you can do to a voice. A bottom sheet on
 * a phone, a small centred panel from sm up (built on the Radix Dialog already in the project).
 */
function VoiceMenu({
  voice,
  onSetDefault,
  onSetStyle,
  onRename,
  onRerecord,
  onDelete,
}: {
  voice: VoiceDto
  onSetDefault: () => void
  onSetStyle: (style: string) => void
  onRename: () => void
  onRerecord: () => void
  onDelete: () => void
}) {
  const strings = useStrings()
  const [open, setOpen] = useState(false)

  const run = (action: () => void) => {
    setOpen(false)
    action()
  }

  return (
    <Dialog.Root open={open} onOpenChange={setOpen}>
      <Dialog.Trigger asChild>
        <button
          type="button"
          aria-label={format(strings.voicesMenuOpen, { name: voice.name })}
          className="-mr-1.5 -mt-1.5 flex h-11 w-11 shrink-0 items-center justify-center rounded-lg text-stone-500 transition-colors hover:bg-stone-100 hover:text-stone-900"
        >
          <DotsIcon />
        </button>
      </Dialog.Trigger>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 z-40 bg-black/40" />
        <Dialog.Content className="fixed inset-x-0 bottom-0 z-50 rounded-t-2xl bg-white p-4 pb-6 shadow-xl sm:inset-x-auto sm:bottom-auto sm:left-1/2 sm:top-1/2 sm:w-80 sm:-translate-x-1/2 sm:-translate-y-1/2 sm:rounded-2xl sm:pb-4">
          <Dialog.Title className="px-3 text-xs font-semibold uppercase tracking-wide text-stone-400">
            {strings.voicesMenuTitle}
          </Dialog.Title>
          <Dialog.Description className="mt-1 px-3 text-sm font-medium text-stone-800">
            {voice.name}
          </Dialog.Description>

          {voice.styles.length > 1 && (
            <div className="mt-4 px-3">
              <span className={labelCls}>{strings.voicesStyleLabel}</span>
              <div role="group" className="inline-flex w-full overflow-hidden rounded-lg border border-stone-300">
                {voice.styles.map((style) => (
                  <button
                    key={style}
                    type="button"
                    aria-pressed={voice.style === style}
                    onClick={() => onSetStyle(style)}
                    className={`min-h-11 flex-1 px-2 text-sm font-medium transition-colors ${
                      voice.style === style
                        ? 'bg-stone-900 text-white'
                        : 'bg-white text-stone-600 hover:bg-stone-100'
                    }`}
                  >
                    {styleLabel(style, strings)}
                  </button>
                ))}
              </div>
            </div>
          )}

          <div className="mt-3 space-y-1">
            {!voice.isDefault && (
              <button type="button" className={menuItemCls} onClick={() => run(onSetDefault)}>
                {strings.voicesSetDefault}
              </button>
            )}
            <button type="button" className={menuItemCls} onClick={() => run(onRename)}>
              {strings.voicesRename}
            </button>
            <button type="button" className={menuItemCls} onClick={() => run(onRerecord)}>
              {strings.voicesRerecord}
            </button>
            <button type="button" className={menuItemDangerCls} onClick={() => run(onDelete)}>
              {strings.delete}
            </button>
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  )
}

/** Install step key → what a person reads. Unknown keys fall back to a neutral "working". */
function stepLabel(job: JobDto, strings: Strings): string {
  if (job.state === 'queued') return strings.voicesStepQueued
  if (job.state === 'failed') return strings.voicesInstallFailed
  switch (job.stage) {
    case 'download':
      return strings.voicesStepDownload
    case 'unpack':
      return strings.voicesStepUnpack
    case 'convert':
      return strings.voicesStepConvert
    case 'learn':
      return strings.voicesStepLearn
    case 'sample':
      return strings.voicesStepSample
    default:
      return strings.voicesStepWorking
  }
}

/**
 * A voice that is being installed. It occupies the same row shape as a finished one so the list
 * does not jump when it lands — plain-language status first, and the actual log a tap away for
 * when "learning the voice" for four minutes stops feeling like progress.
 */
function PendingVoiceRow({ job, onDismiss }: { job: JobDto; onDismiss?: () => void }) {
  const strings = useStrings()
  const [open, setOpen] = useState(false)
  // Only the detail route carries the log tail, and only while this row is expanded.
  const detail = useJob(open ? job.id : '')
  const failed = job.state === 'failed'
  const log = detail.data?.logTail ?? []

  return (
    <li
      className={`flex items-start gap-3 rounded-xl border p-3 ${
        failed ? 'border-red-200 bg-red-50' : 'border-violet-200 bg-violet-50/40'
      }`}
    >
      {/* Same 44 px footprint as the play button, so the row keeps its shape when it completes. */}
      <span className="flex h-11 w-11 shrink-0 items-center justify-center rounded-full bg-white">
        {failed ? <span className="text-lg text-red-600">!</span> : <Spinner />}
      </span>

      <div className="min-w-0 flex-1">
        <div className="flex items-start gap-2">
          <h3 className="min-w-0 flex-1 break-words font-semibold text-stone-900">
            {job.voiceName ?? job.voiceId}
          </h3>
          {failed && onDismiss !== undefined && (
            <button
              type="button"
              aria-label={strings.close}
              className="-mr-1.5 -mt-1.5 flex h-11 w-11 shrink-0 items-center justify-center rounded-lg text-stone-500 hover:bg-white hover:text-stone-900"
              onClick={onDismiss}
            >
              ✕
            </button>
          )}
        </div>

        <p className="mt-0.5 flex flex-wrap items-center gap-x-2 gap-y-1 text-sm text-stone-500">
          {job.voiceLocale != null && job.voiceLocale !== '' && (
            <>
              <Flag locale={job.voiceLocale} />
              <span>{localeLabel(job.voiceLocale, strings)}</span>
              <span aria-hidden>·</span>
            </>
          )}
          <span className={failed ? 'font-medium text-red-700' : 'font-medium text-violet-700'}>
            {stepLabel(job, strings)}
          </span>
        </p>

        {failed && job.error != null && job.error !== '' && (
          <p className="mt-1 text-sm leading-snug text-red-800">{job.error}</p>
        )}

        {!failed && (
          <>
            {job.percent != null ? (
              <div className="mt-2 h-1.5 w-full overflow-hidden rounded-full bg-white">
                <div
                  className="h-full bg-violet-500 transition-all"
                  style={{ width: `${job.percent}%` }}
                />
              </div>
            ) : (
              // No honest fraction to report: an indeterminate bar beats a made-up number.
              <div className="mt-2 h-1.5 w-full overflow-hidden rounded-full bg-white">
                <div className="h-full w-1/3 animate-pulse rounded-full bg-violet-400" />
              </div>
            )}
            <p className="mt-1 text-xs text-stone-500">{strings.voicesInstallKeepsGoing}</p>
          </>
        )}

        <button
          type="button"
          aria-expanded={open}
          className="mt-2 text-xs font-medium text-stone-500 underline underline-offset-2 hover:text-stone-800"
          onClick={() => setOpen((current) => !current)}
        >
          {open ? strings.voicesInstallHideDetails : strings.voicesInstallShowDetails}
        </button>

        {open && (
          <pre className="mt-2 max-h-40 overflow-auto whitespace-pre-wrap rounded-lg bg-stone-900 p-3 text-[11px] leading-relaxed text-stone-100">
            {log.length > 0 ? log.join('\n') : strings.loading}
          </pre>
        )}
      </div>
    </li>
  )
}

function VoiceRow({
  voice,
  playing,
  busy,
  onPlay,
  onSetDefault,
  onSetStyle,
  onRename,
  onRerecord,
  onDelete,
}: {
  voice: VoiceDto
  playing: boolean
  /** A sample is being re-recorded — the only time a voice row waits for anything. */
  busy: boolean
  onPlay: () => void
  onSetDefault: () => void
  onSetStyle: (style: string) => void
  onRename: () => void
  onRerecord: () => void
  onDelete: () => void
}) {
  const strings = useStrings()

  return (
    <li className="flex items-start gap-3 rounded-xl border border-stone-200 bg-white p-3">
      <PlayButton playing={playing} disabled={!voice.hasSample || busy} label={voice.name} onClick={onPlay} />
      <div className="min-w-0 flex-1">
        <div className="flex items-start gap-2">
          <h3 className="min-w-0 flex-1 break-words font-semibold text-stone-900">{voice.name}</h3>
          {voice.isDefault && (
            <span className="mt-0.5 shrink-0 rounded-full bg-emerald-100 px-2 py-0.5 text-xs font-medium text-emerald-800">
              {strings.voicesDefaultBadge}
            </span>
          )}
          <VoiceMenu
            voice={voice}
            onSetDefault={onSetDefault}
            onSetStyle={onSetStyle}
            onRename={onRename}
            onRerecord={onRerecord}
            onDelete={onDelete}
          />
        </div>
        <p className="mt-0.5 flex flex-wrap items-center gap-x-2 gap-y-1 text-sm text-stone-500">
          <Flag locale={voice.locale} />
          <span>{localeLabel(voice.locale, strings)}</span>
          <span aria-hidden>·</span>
          <span>{styleLabel(voice.style, strings)}</span>
        </p>
        {voice.description.length > 0 && (
          <p className="mt-1 text-sm leading-snug text-stone-500">{voice.description}</p>
        )}
        {busy && (
          <p className="mt-1 inline-flex items-center gap-2 text-xs text-violet-700">
            <Spinner />
            {strings.voicesAddInstalling}
          </p>
        )}
        {!voice.hasSample && !busy && (
          <p className="mt-1 text-xs text-amber-700">⚠ {strings.voicesNoSample}</p>
        )}
      </div>
    </li>
  )
}

function RenameDialog({
  voice,
  onSave,
  onClose,
}: {
  voice: VoiceDto | null
  onSave: (name: string) => void
  onClose: () => void
}) {
  const strings = useStrings()
  const [name, setName] = useState('')
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    setName(voice?.name ?? '')
    setError(null)
  }, [voice])

  const save = () => {
    if (name.trim().length === 0) {
      setError(strings.voicesNameRequired)
      return
    }
    onSave(name.trim())
  }

  return (
    <Dialog.Root open={voice !== null} onOpenChange={(next) => !next && onClose()}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 z-40 bg-black/40" />
        <Dialog.Content className={dialogContentCls}>
          <Dialog.Title className="text-lg font-semibold text-stone-900">{strings.voicesRenameTitle}</Dialog.Title>
          <Dialog.Description className="mt-1 text-sm text-stone-500">{strings.voicesRenameHint}</Dialog.Description>

          <div className="mt-4">
            <label className={labelCls} htmlFor="rename-voice">
              {strings.voicesNameLabel}
            </label>
            <input
              id="rename-voice"
              className={inputCls}
              value={name}
              onChange={(e) => setName(e.target.value)}
              autoComplete="off"
            />
          </div>
          {error !== null && <p className="mt-2 text-xs text-red-700">{error}</p>}

          <div className="mt-5 flex flex-col gap-2 sm:flex-row sm:justify-end">
            <button type="button" className={btnGhost} onClick={onClose}>
              {strings.cancel}
            </button>
            <button type="button" className={btnPrimary} onClick={save}>
              {strings.save}
            </button>
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  )
}

function DeleteVoiceDialog({
  voice,
  busy,
  onConfirm,
  onClose,
}: {
  voice: VoiceDto | null
  busy: boolean
  onConfirm: (deleteWav: boolean) => void
  onClose: () => void
}) {
  const strings = useStrings()
  const [deleteWav, setDeleteWav] = useState(false)

  useEffect(() => {
    if (voice !== null) setDeleteWav(false)
  }, [voice])

  return (
    <Dialog.Root open={voice !== null} onOpenChange={(next) => !next && onClose()}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 z-40 bg-black/40" />
        <Dialog.Content className={dialogContentCls}>
          <Dialog.Title className="text-lg font-semibold text-stone-900">
            {strings.voicesDeleteTitle}
          </Dialog.Title>
          <Dialog.Description className="mt-2 text-sm text-stone-600">
            {format(strings.voicesDeleteBody, { name: voice?.name ?? '' })}
          </Dialog.Description>

          <label className="mt-4 flex min-h-11 cursor-pointer items-center gap-3 text-sm text-stone-700">
            <input
              type="checkbox"
              className="h-5 w-5"
              checked={deleteWav}
              onChange={(e) => setDeleteWav(e.target.checked)}
            />
            {strings.voicesDeleteWav}
          </label>

          <div className="mt-5 flex flex-col gap-2 sm:flex-row sm:justify-end">
            <button type="button" className={btnGhost} onClick={onClose}>
              {strings.cancel}
            </button>
            <button
              type="button"
              disabled={busy}
              className={`${btnBase} bg-red-600 text-white hover:bg-red-700`}
              onClick={() => onConfirm(deleteWav)}
            >
              {strings.delete}
            </button>
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  )
}

/**
 * Raw voices.json — same ETag protocol as the universe files: GET with an ETag, PUT with
 * If-Match, and a 409 comes back carrying the current content for the conflict dialog.
 */
function CatalogEditor() {
  const strings = useStrings()
  const queryClient = useQueryClient()
  const [text, setText] = useState('')
  const [savedText, setSavedText] = useState('')
  const [etag, setEtag] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)
  const [saveError, setSaveError] = useState<string | null>(null)
  const [conflict, setConflict] = useState<{ text: string; etag: string } | null>(null)

  const dirty = text !== savedText
  const invalidJson = useMemo(() => {
    try {
      JSON.parse(text)
      return false
    } catch {
      return true
    }
  }, [text])

  const load = useCallback(async () => {
    setLoading(true)
    setLoadError(null)
    try {
      const file = await getWithETag(CATALOG_PATH)
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
  }, [strings.error])

  useEffect(() => {
    void load()
  }, [load])

  const save = async (overrideEtag?: string) => {
    const ifMatch = overrideEtag ?? etag
    if (ifMatch === null) return
    setSaving(true)
    setSaveError(null)
    try {
      const result = await putWithETag(CATALOG_PATH, text, ifMatch)
      setEtag(result.etag)
      setSavedText(text)
      setConflict(null)
      // The list reads /api/voices — after a save it must see the new catalog.
      void queryClient.invalidateQueries({ queryKey: ['voices'] })
    } catch (err) {
      if (err instanceof ConflictError) setConflict({ text: err.currentText, etag: err.currentETag })
      else setSaveError(err instanceof Error ? err.message : strings.error)
    } finally {
      setSaving(false)
    }
  }

  if (loading) return <p className="py-6 text-center text-sm text-stone-500">{strings.loading}</p>
  if (loadError !== null) {
    return (
      <div className="rounded-xl border border-red-200 bg-red-50 p-4 text-sm text-red-800">
        <p>{loadError}</p>
        <button type="button" className={`${btnGhost} mt-3`} onClick={() => void load()}>
          {strings.retry}
        </button>
      </div>
    )
  }

  return (
    <div>
      <div className="mb-2 flex flex-wrap items-center gap-2">
        <span className="mr-auto font-mono text-xs text-stone-400">voices.json</span>
        <button type="button" className={btnGhost} disabled={dirty} onClick={() => void load()}>
          {strings.refresh}
        </button>
        <button type="button" className={btnPrimary} disabled={!dirty || saving} onClick={() => void save()}>
          {saving ? strings.saving : strings.save}
        </button>
      </div>
      {dirty && invalidJson && <p className="mb-2 text-xs text-red-700">{strings.voicesInvalidJson}</p>}
      {saveError !== null && (
        <p className="mb-2 rounded-lg border border-red-200 bg-red-50 p-2 text-xs text-red-800">{saveError}</p>
      )}
      <textarea
        value={text}
        onChange={(e) => setText(e.target.value)}
        spellCheck={false}
        aria-label="voices.json"
        className="min-h-[40vh] w-full resize-y rounded-xl border border-stone-300 bg-white p-4 font-mono text-sm leading-relaxed text-stone-900 focus:border-stone-500 focus:outline-none"
      />

      <Dialog.Root open={conflict !== null} onOpenChange={(open) => !open && setConflict(null)}>
        <Dialog.Portal>
          <Dialog.Overlay className="fixed inset-0 z-40 bg-black/40" />
          <Dialog.Content className="fixed left-1/2 top-1/2 z-50 w-[min(92vw,30rem)] -translate-x-1/2 -translate-y-1/2 rounded-xl bg-white p-6 shadow-xl">
            <Dialog.Title className="text-lg font-semibold text-stone-900">
              {strings.universeConflictTitle}
            </Dialog.Title>
            <Dialog.Description className="mt-2 text-sm text-stone-600">
              {strings.universeFileConflict}
            </Dialog.Description>
            {conflict !== null && (
              <pre className="mt-3 max-h-40 overflow-auto whitespace-pre-wrap rounded-lg bg-stone-100 p-3 text-xs leading-relaxed text-stone-700">
                {conflict.text}
              </pre>
            )}
            <div className="mt-5 flex flex-col gap-2 sm:flex-row sm:justify-end">
              <button type="button" className={btnGhost} onClick={() => setConflict(null)}>
                {strings.cancel}
              </button>
              <button
                type="button"
                className={btnGhost}
                onClick={() => {
                  if (conflict !== null) void save(conflict.etag)
                }}
              >
                {strings.draftConflictOverwrite}
              </button>
              <button
                type="button"
                className={btnPrimary}
                onClick={() => {
                  if (conflict === null) return
                  setText(conflict.text)
                  setSavedText(conflict.text)
                  setEtag(conflict.etag)
                  setConflict(null)
                }}
              >
                {strings.draftConflictReload}
              </button>
            </div>
          </Dialog.Content>
        </Dialog.Portal>
      </Dialog.Root>
    </div>
  )
}

function VoicesSection() {
  const strings = useStrings()
  const queryClient = useQueryClient()
  const voicesQuery = useVoices()
  const jobsQuery = useJobs()
  const patchVoice = usePatchVoice()
  const deleteVoice = useDeleteVoice()
  const setDefaultVoice = useSetDefaultVoice()
  const rerecord = usePreviewVoice()
  const player = useSamplePlayer()

  const [showCatalog, setShowCatalog] = useState(false)
  const [adding, setAdding] = useState(false)
  const [renaming, setRenaming] = useState<VoiceDto | null>(null)
  const [deleting, setDeleting] = useState<VoiceDto | null>(null)
  const [actionError, setActionError] = useState<string | null>(null)
  /** Failed install rows the reader has waved away. */
  const [dismissedJobs, setDismissedJobs] = useState<ReadonlySet<string>>(new Set())

  const voices = useMemo(() => voicesQuery.data ?? [], [voicesQuery.data])
  const jobs = jobsQuery.data

  const installJobs = useMemo(
    () => (jobs ?? []).filter((job) => job.type === 'installVoice' && (job.voiceId ?? '') !== ''),
    [jobs],
  )

  // Derived from the jobs, not from what this tab happens to remember: a pending voice survives a
  // reload and shows up on the other devices in the house too.
  const pendingInstalls = useMemo(
    () => installJobs.filter((job) => job.state === 'queued' || job.state === 'running'),
    [installJobs],
  )

  const installingIds = useMemo(
    () => new Set(pendingInstalls.map((job) => job.voiceId as string)),
    [pendingInstalls],
  )

  // A failed install leaves nothing behind to explain itself, so its row stays until dismissed.
  const failedInstalls = useMemo(
    () =>
      installJobs.filter(
        (job) =>
          job.state === 'failed' &&
          !dismissedJobs.has(job.id) &&
          !voices.some((voice) => voice.id === job.voiceId),
      ),
    [installJobs, dismissedJobs, voices],
  )

  // Re-recording a sample keeps the finished row in place but disables its play button.
  const busySamples = useMemo(() => {
    const ids = new Set<string>()
    for (const job of jobs ?? [])
      if (
        job.type === 'previewVoice' &&
        (job.state === 'queued' || job.state === 'running') &&
        job.voiceId != null &&
        job.voiceId !== ''
      )
        ids.add(job.voiceId)
    return ids
  }, [jobs])

  /** Voice jobs still in flight, as a stable key — install and re-record alike. */
  const activeVoiceJobs = useMemo(
    () =>
      (jobs ?? [])
        .filter(
          (job) =>
            (job.type === 'installVoice' || job.type === 'previewVoice') &&
            (job.state === 'queued' || job.state === 'running'),
        )
        .map((job) => job.id)
        .sort()
        .join(','),
    [jobs],
  )
  const previousActiveJobs = useRef<string | null>(null)

  // One of them finished, so the voice and its sample are now on disk. Derived from the jobs
  // rather than from what this tab queued, so a reload mid-install still updates when it lands.
  useEffect(() => {
    const previous = previousActiveJobs.current
    previousActiveJobs.current = activeVoiceJobs
    if (previous === null || previous === '' || previous === activeVoiceJobs) return

    const stillRunning = new Set(activeVoiceJobs === '' ? [] : activeVoiceJobs.split(','))
    if (previous.split(',').every((id: string) => stillRunning.has(id))) return

    // A re-recorded sample has the same URL and different bytes.
    player.bump()
    void queryClient.invalidateQueries({ queryKey: ['voices'] })
  }, [activeVoiceJobs, player, queryClient])

  const run = async (action: Promise<unknown>) => {
    setActionError(null)
    try {
      await action
    } catch (err) {
      setActionError(err instanceof Error ? err.message : strings.voicesSaveError)
    }
  }

  const onRerecord = async (voice: VoiceDto) => {
    setActionError(null)
    try {
      // The mutation invalidates ['jobs'], so the row picks up its busy state on the next poll.
      await rerecord.mutateAsync(voice.id)
    } catch (err) {
      setActionError(err instanceof Error ? err.message : strings.voicesSaveError)
    }
  }

  const onDelete = async (deleteWav: boolean) => {
    if (deleting === null) return
    if (player.playingUrl === voicePreviewUrl(deleting.id)) player.stop()
    await run(deleteVoice.mutateAsync({ id: deleting.id, deleteWav }))
    setDeleting(null)
  }

  return (
    <section className="rounded-xl border border-stone-200 bg-white px-4">
      <h2 className="border-b border-stone-100 py-3 text-sm font-semibold uppercase tracking-wide text-stone-400">
        {strings.settingsVoicesHeading}
      </h2>
      <div className="space-y-3 py-4">
        <div className="flex flex-wrap items-start justify-between gap-3">
          <p className="min-w-0 flex-1 text-sm text-stone-600">{strings.settingsVoicesHint}</p>
          <button type="button" className={btnPrimary} onClick={() => setAdding(true)}>
            + {strings.voicesAdd}
          </button>
        </div>

        {voicesQuery.isLoading && <p className="py-6 text-center text-sm text-stone-500">{strings.loading}</p>}

        {voicesQuery.isError && (
          <div className="rounded-xl border border-red-200 bg-red-50 p-4 text-sm text-red-800">
            <p>{strings.voicesLoadError}</p>
            <button type="button" className={`${btnGhost} mt-3`} onClick={() => void voicesQuery.refetch()}>
              {strings.retry}
            </button>
          </div>
        )}

        {voicesQuery.isSuccess &&
          voices.length === 0 &&
          pendingInstalls.length === 0 &&
          failedInstalls.length === 0 && (
            <div className="rounded-xl border border-dashed border-stone-300 py-8 text-center">
              <p className="text-sm text-stone-500">{strings.voicesEmpty}</p>
              <p className="mt-1 text-xs text-stone-400">{strings.voicesEmptyHint}</p>
            </div>
          )}

        {(voices.length > 0 || pendingInstalls.length > 0 || failedInstalls.length > 0) && (
          <ul className="space-y-2">
            {/* In-flight and failed installs sit at the top: they are what just changed. */}
            {pendingInstalls.map((job) => (
              <PendingVoiceRow key={job.id} job={job} />
            ))}
            {failedInstalls.map((job) => (
              <PendingVoiceRow
                key={job.id}
                job={job}
                onDismiss={() => setDismissedJobs((current) => new Set(current).add(job.id))}
              />
            ))}
            {voices
              .filter((voice) => !installingIds.has(voice.id))
              .map((voice) => {
                const url = voicePreviewUrl(voice.id)
                return (
                  <VoiceRow
                    key={voice.id}
                    voice={voice}
                    playing={player.playingUrl === url}
                    busy={busySamples.has(voice.id)}
                    onPlay={() => player.toggle(url)}
                    onSetDefault={() => void run(setDefaultVoice.mutateAsync(voice.id))}
                    onSetStyle={(style) =>
                      void run(patchVoice.mutateAsync({ id: voice.id, patch: { style } }))
                    }
                    onRename={() => setRenaming(voice)}
                    onRerecord={() => void onRerecord(voice)}
                    onDelete={() => setDeleting(voice)}
                  />
                )
              })}
          </ul>
        )}

        {player.failedUrl !== null && (
          <p className="rounded-lg border border-amber-200 bg-amber-50 p-2 text-xs text-amber-800">
            {strings.voicesSampleError}
          </p>
        )}

        {actionError !== null && (
          <p className="rounded-lg border border-red-200 bg-red-50 p-2 text-xs text-red-800">{actionError}</p>
        )}

        <div className="border-t border-stone-100 pt-3">
          <button
            type="button"
            className={`${btnCard} text-stone-500`}
            aria-expanded={showCatalog}
            onClick={() => setShowCatalog((open) => !open)}
          >
            {showCatalog ? strings.voicesAdvancedHide : strings.voicesAdvancedShow}
          </button>
          {showCatalog && (
            <div className="mt-3">
              <p className="mb-2 text-xs text-stone-500">{strings.voicesAdvancedHint}</p>
              <CatalogEditor />
            </div>
          )}
        </div>
      </div>

      <AddVoiceDialog
        open={adding}
        onClose={() => setAdding(false)}
        // The pending row comes from the job list, so closing is all this has to do.
        onQueued={() => setAdding(false)}
      />

      <RenameDialog
        voice={renaming}
        onSave={(name) => {
          if (renaming !== null) void run(patchVoice.mutateAsync({ id: renaming.id, patch: { name } }))
          setRenaming(null)
        }}
        onClose={() => setRenaming(null)}
      />

      <DeleteVoiceDialog
        voice={deleting}
        busy={deleteVoice.isPending}
        onConfirm={(deleteWav) => void onDelete(deleteWav)}
        onClose={() => setDeleting(null)}
      />
    </section>
  )
}

export default function Settings() {
  const strings = useStrings()
  const query = useStatus()
  const status = query.data

  return (
    <main className="mx-auto w-full max-w-2xl px-4 pb-24 pt-6 sm:px-6">
      <header className="mb-6">
        <h1 className="text-2xl font-semibold text-stone-900">{strings.settingsTitle}</h1>
        <p className="text-sm text-stone-500">{strings.settingsSubtitle}</p>
      </header>

      {/* Above the status block on purpose: reachable even when /api/status fails. */}
      <div className="mb-6 space-y-6">
        <LanguageSection />
        <VoicesSection />
      </div>

      {query.isLoading && <p className="py-8 text-center text-sm text-stone-500">{strings.loading}</p>}

      {query.isError && (
        <div className="rounded-xl border border-red-200 bg-red-50 p-4 text-sm text-red-800">
          <p>{strings.errorNetwork}</p>
          <button
            type="button"
            className="mt-3 inline-flex min-h-11 items-center rounded-lg border border-red-300 bg-white px-4 py-2.5 text-sm font-medium text-red-800 hover:bg-red-100"
            onClick={() => void query.refetch()}
          >
            {strings.retry}
          </button>
        </div>
      )}

      {status !== undefined && (
        <div className="space-y-6">
          {!status.claude.found && (
            <div className="rounded-xl border border-amber-300 bg-amber-50 p-4 text-amber-900">
              <p className="font-semibold">{strings.settingsClaudeMissingTitle}</p>
              <p className="mt-1 text-sm leading-relaxed">
                {strings.settingsClaudeMissingBody1}{' '}
                <code className="rounded bg-amber-100 px-1 font-mono">claude</code>
                {strings.settingsClaudeMissingBody2}{' '}
                <code className="rounded bg-amber-100 px-1 font-mono">claude</code>{' '}
                {strings.settingsClaudeMissingBody3}
              </p>
            </div>
          )}

          <section className="rounded-xl border border-stone-200 bg-white px-4">
            <h2 className="border-b border-stone-100 py-3 text-sm font-semibold uppercase tracking-wide text-stone-400">
              {strings.settingsGeneratorHeading}
            </h2>
            <div className="divide-y divide-stone-100">
              <Row label={strings.statusClaude}>
                <Presence ok={status.claude.found} okLabel={status.claude.version ?? undefined} />
              </Row>
            </div>
          </section>

          <section className="rounded-xl border border-stone-200 bg-white px-4">
            <h2 className="border-b border-stone-100 py-3 text-sm font-semibold uppercase tracking-wide text-stone-400">
              {strings.statusModels}
            </h2>
            <div className="divide-y divide-stone-100">
              <Row label={strings.statusGpu}>{status.gpu ?? strings.statusMissing}</Row>
              <Row label={strings.statusWhisper}>
                <Presence ok={status.models.whisper} />
              </Row>
              <Row label={strings.statusChatterbox}>
                <Presence ok={status.models.chatterbox} />
              </Row>
            </div>
            {(!status.models.whisper || !status.models.chatterbox) && (
              <p className="border-t border-stone-100 py-3 text-xs text-stone-500">
                {strings.settingsModelsHintPrefix}{' '}
                <code className="rounded bg-stone-100 px-1 font-mono">scripts/download-models.ps1</code>.
              </p>
            )}
          </section>

          <section className="rounded-xl border border-stone-200 bg-white px-4">
            <h2 className="border-b border-stone-100 py-3 text-sm font-semibold uppercase tracking-wide text-stone-400">
              {strings.settingsLibraryHeading}
            </h2>
            <div className="divide-y divide-stone-100">
              <Row label={strings.statusLibraryRoot}>
                <code className="break-all rounded bg-stone-100 px-1.5 py-0.5 font-mono text-xs">{status.libraryRoot}</code>
              </Row>
            </div>
            <div className="border-t border-stone-100 py-4">
              <h3 className="text-sm font-semibold text-stone-800">{strings.settingsBypassTitle}</h3>
              <p className="mt-1 text-sm leading-relaxed text-stone-600">
                {strings.settingsBypassBody}
              </p>
              <pre className="mt-3 overflow-x-auto rounded-lg bg-stone-900 p-4 text-xs leading-relaxed text-stone-100">
                {`${status.libraryRoot}\n${strings.settingsLibraryTree}`}
              </pre>
            </div>
          </section>
        </div>
      )}
    </main>
  )
}
