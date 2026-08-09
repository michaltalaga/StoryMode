// Settings page (agent 7): environment status from GET /api/status plus the
// files-on-disk "how to bypass the panel" note (HANDOVER.md philosophy).
// Głosy są globalne (library/voices.json), więc ich katalog mieszka tutaj —
// nie w uniwersum. Karty mówią po ludzku (jeden przycisk „posłuchaj”, język
// słowem, „głos klonowany”), a surowy plik jest tylko wyjściem awaryjnym.
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import type { ChangeEvent, FormEvent, ReactNode } from 'react'
import * as Dialog from '@radix-ui/react-dialog'
import { useQueryClient } from '@tanstack/react-query'
import { ConflictError, getWithETag, putWithETag } from '../api/client'
import {
  useDeleteVoice,
  useJobs,
  usePreviewVoice,
  useSaveVoice,
  useSetDefaultVoice,
  useStatus,
  useUploadVoiceReference,
  useVoiceProviders,
  useVoices,
  voicePreviewUrl,
} from '../api/queries'
import type { VoiceDto, VoiceProviderDto } from '../api/types'
import type { Lang, Strings } from '../i18n'
import { LANGUAGES, format, useLanguage, useStrings } from '../i18n'

const btnBase =
  'inline-flex min-h-11 items-center justify-center gap-2 rounded-lg px-4 py-2.5 text-sm font-medium transition-colors disabled:cursor-not-allowed disabled:opacity-40'
const btnPrimary = `${btnBase} bg-stone-900 text-white hover:bg-stone-700`
const btnGhost = `${btnBase} border border-stone-300 bg-white text-stone-700 hover:bg-stone-100`
// Card actions: compact, but still a 44 px touch target on a phone.
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
// Głosy — globalny katalog: karty z odsłuchem, edycją i dodawaniem
// ---------------------------------------------------------------------------

const VOICE_ID_PATTERN = /^[a-z0-9][a-z0-9-]*$/
/** Providers we know exist even if the endpoint is unreachable (see /api/voices/providers). */
const KNOWN_PROVIDERS: VoiceProviderDto[] = [
  { id: 'chatterbox-onnx', supportsCloning: true },
  { id: 'piper-onnx', supportsCloning: false },
]

/** Language code → a word the reader knows; unknown codes stay as their code. */
function languageLabel(code: string, strings: Strings): string {
  const normalized = code.trim().toLowerCase()
  if (normalized.startsWith('pl')) return strings.languagePl
  if (normalized.startsWith('en')) return strings.languageEn
  return code.toUpperCase()
}

function Spinner({ light = false }: { light?: boolean }) {
  return (
    <span
      aria-hidden
      className={`inline-block h-4 w-4 shrink-0 animate-spin rounded-full border-2 ${
        light ? 'border-white/40 border-t-white' : 'border-stone-300 border-t-violet-600'
      }`}
    />
  )
}

function Chip({ children, tone = 'neutral' }: { children: ReactNode; tone?: 'neutral' | 'warn' }) {
  const toneCls = tone === 'warn' ? 'bg-amber-100 text-amber-900' : 'bg-stone-100 text-stone-600'
  return <span className={`rounded-full px-2 py-0.5 text-xs ${toneCls}`}>{children}</span>
}

function PlayIcon() {
  return (
    <svg viewBox="0 0 24 24" className="h-6 w-6" fill="currentColor" aria-hidden>
      <path d="M8 5.5v13l11-6.5z" />
    </svg>
  )
}

function StopIcon() {
  return (
    <svg viewBox="0 0 24 24" className="h-6 w-6" fill="currentColor" aria-hidden>
      <rect x="7" y="7" width="10" height="10" rx="1.5" />
    </svg>
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
 * One shared <audio> for the whole section: starting a voice stops whatever was
 * playing. `bump()` busts the browser cache after a preview has been re-rendered —
 * the mp3 URL never changes, only its content.
 */
function useVoicePlayer() {
  const audioRef = useRef<HTMLAudioElement | null>(null)
  const versionRef = useRef(0)
  const [playingId, setPlayingId] = useState<string | null>(null)
  // A voice whose preview is rendered and loaded, but which the browser refused to
  // start on its own. Rendering takes seconds to minutes, so by the time we call
  // play() the click's transient user activation is long gone and autoplay policy
  // blocks it — the card must say "ready, tap again" instead of going quiet.
  const [blockedId, setBlockedId] = useState<string | null>(null)

  const stop = useCallback(() => {
    audioRef.current?.pause()
    setPlayingId(null)
  }, [])

  const play = useCallback((voiceId: string) => {
    let audio = audioRef.current
    if (audio === null) {
      audio = new Audio()
      audio.addEventListener('ended', () => setPlayingId(null))
      audio.addEventListener('error', () => setPlayingId(null))
      audioRef.current = audio
    }
    audio.pause()
    const version = versionRef.current
    audio.src = version === 0 ? voicePreviewUrl(voiceId) : `${voicePreviewUrl(voiceId)}?v=${version}`
    setPlayingId(voiceId)
    setBlockedId(null)
    void audio.play().catch(() => {
      setPlayingId(null)
      // Keep the source loaded so the follow-up tap starts instantly.
      audio.load()
      setBlockedId(voiceId)
    })
  }, [])

  const bump = useCallback(() => {
    versionRef.current += 1
  }, [])

  useEffect(() => () => audioRef.current?.pause(), [])

  // Stable identity: the autoplay effect below depends on it.
  return useMemo(
    () => ({ playingId, blockedId, play, stop, bump }),
    [playingId, blockedId, play, stop, bump],
  )
}

/**
 * The card's ⋯ menu. Four full-width buttons per card was ~160 px of chrome around
 * ~40 px of content on a phone, so everything but Play now lives behind this.
 * Built on the Radix Dialog already in the project (no dropdown/popover package is
 * installed): a bottom sheet on a phone, a small centred panel from sm up.
 */
function VoiceMenu({
  voice,
  onSetDefault,
  onReplaceReference,
  onEdit,
  onDelete,
}: {
  voice: VoiceDto
  onSetDefault: () => void
  onReplaceReference: (file: File) => void
  onEdit: () => void
  onDelete: () => void
}) {
  const strings = useStrings()
  const [open, setOpen] = useState(false)

  const run = (action: () => void) => {
    setOpen(false)
    action()
  }

  const pickFile = (event: ChangeEvent<HTMLInputElement>) => {
    const file = event.target.files?.[0]
    event.target.value = ''
    if (file !== undefined) run(() => onReplaceReference(file))
  }

  return (
    <Dialog.Root open={open} onOpenChange={setOpen}>
      <Dialog.Trigger asChild>
        <button
          type="button"
          aria-label={format(strings.voicesMenuOpen, { name: voice.id })}
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
          <Dialog.Description className="mt-1 break-all px-3 font-mono text-sm text-stone-700">
            {voice.id}
          </Dialog.Description>

          <div className="mt-3 space-y-1">
            {!voice.isDefault && (
              <button type="button" className={menuItemCls} onClick={() => run(onSetDefault)}>
                {strings.voicesSetDefault}
              </button>
            )}
            {voice.supportsCloning && (
              <label className={menuItemCls}>
                <input type="file" accept=".wav,audio/wav" className="hidden" onChange={pickFile} />
                {strings.voicesReplaceReference}
              </label>
            )}
            <button type="button" className={menuItemCls} onClick={() => run(onEdit)}>
              {strings.edit}
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

interface VoiceCardProps {
  voice: VoiceDto
  playing: boolean
  preparing: boolean
  /** Preview is rendered and loaded, but the browser blocked the automatic start. */
  readyToPlay: boolean
  uploadPct: number | null
  onPlay: () => void
  onRerecord: () => void
  onSetDefault: () => void
  onReplaceReference: (file: File) => void
  onEdit: () => void
  onDelete: () => void
}

function VoiceCard({
  voice,
  playing,
  preparing,
  readyToPlay,
  uploadPct,
  onPlay,
  onRerecord,
  onSetDefault,
  onReplaceReference,
  onEdit,
  onDelete,
}: VoiceCardProps) {
  const strings = useStrings()
  const languages = voice.languages.map((code) => languageLabel(code, strings)).join(', ')
  // Cloning is a property of the engine, not of whether a wav is listed in the catalog.
  const capability = voice.supportsCloning ? strings.voicesCloned : strings.voicesBuiltIn
  // A fixed-voice engine needs no wav, so a missing one is only a problem for cloning voices.
  const referenceBroken =
    voice.supportsCloning && (voice.referenceWav ?? '') !== '' && !voice.hasReferenceWav

  return (
    <article className="flex h-full flex-col rounded-xl border border-stone-200 bg-white p-4">
      <div className="flex items-start gap-3">
        <button
          type="button"
          onClick={onPlay}
          disabled={preparing}
          aria-label={`${playing ? strings.voicesStop : strings.voicesPlay} — ${voice.id}`}
          className={`flex h-12 w-12 shrink-0 items-center justify-center rounded-full bg-stone-900 text-white transition-colors hover:bg-stone-700 disabled:cursor-not-allowed disabled:bg-stone-400 ${
            readyToPlay ? 'ring-2 ring-violet-500 ring-offset-2' : ''
          }`}
        >
          {preparing ? <Spinner light /> : playing ? <StopIcon /> : <PlayIcon />}
        </button>

        <div className="min-w-0 flex-1">
          <div className="flex items-start gap-2">
            {/* break-words, not break-all: a voice id should wrap at its hyphens, not mid-word. */}
            <h3 className="min-w-0 flex-1 break-words font-semibold text-stone-900">{voice.id}</h3>
            {voice.isDefault && (
              <span className="mt-0.5 shrink-0 rounded-full bg-emerald-100 px-2 py-0.5 text-xs font-medium text-emerald-800">
                {strings.voicesDefaultBadge}
              </span>
            )}
            <VoiceMenu
              voice={voice}
              onSetDefault={onSetDefault}
              onReplaceReference={onReplaceReference}
              onEdit={onEdit}
              onDelete={onDelete}
            />
          </div>
          <p className="mt-0.5 text-sm text-stone-500">
            {languages.length > 0 ? `${languages} · ${capability}` : capability}
          </p>
          {preparing && (
            <p className="mt-1 inline-flex items-center gap-2 text-xs text-violet-700">
              <Spinner />
              {strings.voicesPreparing}
            </p>
          )}
          {!preparing && readyToPlay && (
            <p className="mt-1 text-xs font-medium text-violet-700">{strings.voicesReadyToPlay}</p>
          )}
          {referenceBroken && (
            <p className="mt-1">
              <Chip tone="warn">⚠ {strings.voicesNoReferenceWav}</Chip>
            </p>
          )}
        </div>
      </div>

      {uploadPct !== null && (
        <div className="mt-3">
          <div className="h-1.5 w-full overflow-hidden rounded-full bg-stone-100">
            <div className="h-full bg-violet-500 transition-all" style={{ width: `${uploadPct}%` }} />
          </div>
          <p className="mt-1 text-xs text-stone-500">{strings.captureUploading}</p>
        </div>
      )}

      <div className="mt-auto pt-3">
        <details className="border-t border-stone-100 pt-3">
          <summary className="cursor-pointer list-none text-xs font-medium text-stone-500 hover:text-stone-800">
            {strings.voicesDetails} ▾
          </summary>
          <dl className="mt-2 space-y-1 text-xs text-stone-600">
            <div className="flex gap-2">
              <dt className="shrink-0 text-stone-400">{strings.voicesEngineLabel}</dt>
              <dd className="break-all font-mono">{voice.provider}</dd>
            </div>
            {voice.knobs.exaggeration !== null && (
              <div className="flex gap-2">
                <dt className="shrink-0 text-stone-400">{strings.voicesExaggerationLabel}</dt>
                <dd className="font-mono">{voice.knobs.exaggeration}</dd>
              </div>
            )}
            {voice.knobs.cfg !== null && (
              <div className="flex gap-2">
                <dt className="shrink-0 text-stone-400">{strings.voicesCfgLabel}</dt>
                <dd className="font-mono">{voice.knobs.cfg}</dd>
              </div>
            )}
            {(voice.referenceWav ?? '') !== '' && (
              <div className="flex gap-2">
                <dt className="shrink-0 text-stone-400">{strings.voicesReferenceFileLabel}</dt>
                <dd className="break-all font-mono">{voice.referenceWav}</dd>
              </div>
            )}
          </dl>
          <button type="button" className={`${btnCard} mt-2`} disabled={preparing} onClick={onRerecord}>
            {strings.voicesRerecord}
          </button>
        </details>
      </div>
    </article>
  )
}

interface VoiceFormValues {
  id: string
  provider: string
  languages: string[]
  exaggeration: number | null
  cfg: number | null
  referenceFile: File | null
}

/**
 * Add and edit share one dialog: the only difference is that the name and the
 * engine are fixed once a voice exists (stories refer to a voice by its name).
 * Mounted only while open, so the fields seed themselves once and a background
 * refetch can never wipe what is being typed.
 */
function VoiceFormDialog({
  voice,
  takenIds,
  providers,
  busy,
  error,
  onSubmit,
  onClose,
}: {
  /** null = add mode. */
  voice: VoiceDto | null
  takenIds: string[]
  providers: VoiceProviderDto[]
  busy: boolean
  error: string | null
  onSubmit: (values: VoiceFormValues) => void
  onClose: () => void
}) {
  const strings = useStrings()
  const editing = voice !== null

  const [id, setId] = useState(voice?.id ?? '')
  const [provider, setProvider] = useState(voice?.provider ?? providers[0]?.id ?? '')
  const [languages, setLanguages] = useState<string[]>(voice?.languages ?? ['en'])
  const [exaggeration, setExaggeration] = useState(
    voice?.knobs.exaggeration != null ? String(voice.knobs.exaggeration) : '',
  )
  const [cfg, setCfg] = useState(voice?.knobs.cfg != null ? String(voice.knobs.cfg) : '')
  const [referenceFile, setReferenceFile] = useState<File | null>(null)
  const [localError, setLocalError] = useState<string | null>(null)

  const languageOptions = useMemo(
    () => [...new Set<string>([...LANGUAGES, ...(voice?.languages ?? [])])],
    [voice],
  )

  const supportsCloning = (providerId: string) =>
    providers.find((option) => option.id === providerId)?.supportsCloning ?? false
  // Only a cloning engine can do anything with a reference wav.
  const cloningChosen = voice !== null ? voice.supportsCloning : supportsCloning(provider)

  const toggleLanguage = (code: string) =>
    setLanguages((current) =>
      current.includes(code) ? current.filter((c) => c !== code) : [...current, code],
    )

  const parseKnob = (raw: string): number | null | undefined => {
    const trimmed = raw.trim().replace(',', '.')
    if (trimmed === '') return null
    const value = Number(trimmed)
    return Number.isFinite(value) ? value : undefined
  }

  const submit = (event: FormEvent) => {
    event.preventDefault()
    const trimmedId = id.trim()
    if (!editing && !VOICE_ID_PATTERN.test(trimmedId)) {
      setLocalError(strings.voicesNameInvalid)
      return
    }
    if (!editing && takenIds.includes(trimmedId)) {
      setLocalError(strings.voicesNameTaken)
      return
    }
    if (languages.length === 0) {
      setLocalError(strings.voicesLanguagesRequired)
      return
    }
    const parsedExaggeration = parseKnob(exaggeration)
    const parsedCfg = parseKnob(cfg)
    if (parsedExaggeration === undefined || parsedCfg === undefined) {
      setLocalError(strings.voicesKnobInvalid)
      return
    }
    if (referenceFile !== null && !referenceFile.name.toLowerCase().endsWith('.wav')) {
      setLocalError(strings.voicesWavOnly)
      return
    }
    setLocalError(null)
    onSubmit({
      id: trimmedId,
      provider,
      languages,
      exaggeration: parsedExaggeration,
      cfg: parsedCfg,
      referenceFile,
    })
  }

  return (
    <Dialog.Root open onOpenChange={(next) => !next && onClose()}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 z-40 bg-black/40" />
        <Dialog.Content className={dialogContentCls}>
          <Dialog.Title className="text-lg font-semibold text-stone-900">
            {editing ? strings.voicesEditTitle : strings.voicesAddTitle}
          </Dialog.Title>
          <Dialog.Description className="mt-1 text-sm text-stone-500">
            {editing ? strings.voicesEditDescription : strings.voicesAddDescription}
          </Dialog.Description>

          <form onSubmit={submit} className="mt-4 space-y-4">
            <div>
              <label className={labelCls} htmlFor="voice-id">
                {strings.voicesNameLabel}
              </label>
              {editing ? (
                <p className="break-all rounded-xl bg-stone-100 px-3 py-2.5 font-mono text-sm text-stone-700">
                  {voice.id}
                </p>
              ) : (
                <input
                  id="voice-id"
                  className={`${inputCls} font-mono text-sm`}
                  value={id}
                  onChange={(e) => setId(e.target.value)}
                  placeholder={strings.voicesNamePlaceholder}
                  autoComplete="off"
                />
              )}
            </div>

            <div>
              <label className={labelCls} htmlFor="voice-provider">
                {strings.voicesEngineLabel}
              </label>
              {editing ? (
                <p className="break-all rounded-xl bg-stone-100 px-3 py-2.5 font-mono text-sm text-stone-700">
                  {voice.provider}
                </p>
              ) : (
                <>
                  <select
                    id="voice-provider"
                    className={inputCls}
                    value={provider}
                    onChange={(e) => {
                      setProvider(e.target.value)
                      // A fixed-voice engine ignores a wav; drop one that was already picked.
                      if (!supportsCloning(e.target.value)) setReferenceFile(null)
                    }}
                  >
                    {providers.map((option) => (
                      <option key={option.id} value={option.id}>
                        {option.id}
                      </option>
                    ))}
                  </select>
                  <p className="mt-1 text-xs text-stone-500">{strings.voicesEngineHint}</p>
                </>
              )}
            </div>

            <div>
              <span className={labelCls}>{strings.voicesLanguagesLabel}</span>
              <div className="flex flex-wrap gap-2">
                {languageOptions.map((code) => {
                  const on = languages.includes(code)
                  return (
                    <button
                      key={code}
                      type="button"
                      aria-pressed={on}
                      onClick={() => toggleLanguage(code)}
                      className={`min-h-11 rounded-lg border px-4 text-sm font-medium transition-colors ${
                        on
                          ? 'border-stone-900 bg-stone-900 text-white'
                          : 'border-stone-300 bg-white text-stone-600 hover:bg-stone-100'
                      }`}
                    >
                      {languageLabel(code, strings)}
                    </button>
                  )
                })}
              </div>
            </div>

            <div className="grid grid-cols-2 gap-3">
              <div>
                <label className={labelCls} htmlFor="voice-exaggeration">
                  {strings.voicesExaggerationLabel}
                </label>
                <input
                  id="voice-exaggeration"
                  className={inputCls}
                  inputMode="decimal"
                  value={exaggeration}
                  onChange={(e) => setExaggeration(e.target.value)}
                  placeholder={strings.optional}
                />
              </div>
              <div>
                <label className={labelCls} htmlFor="voice-cfg">
                  {strings.voicesCfgLabel}
                </label>
                <input
                  id="voice-cfg"
                  className={inputCls}
                  inputMode="decimal"
                  value={cfg}
                  onChange={(e) => setCfg(e.target.value)}
                  placeholder={strings.optional}
                />
              </div>
            </div>
            <p className="-mt-2 text-xs text-stone-500">{strings.voicesKnobsHint}</p>

            {!editing &&
              (cloningChosen ? (
                <div>
                  <span className={labelCls}>{strings.voicesReferenceLabel}</span>
                  <label className={`${btnGhost} w-full cursor-pointer`}>
                    <input
                      type="file"
                      accept=".wav,audio/wav"
                      className="hidden"
                      onChange={(e) => setReferenceFile(e.target.files?.[0] ?? null)}
                    />
                    {referenceFile?.name ?? strings.addFile}
                  </label>
                  <p className="mt-1 text-xs text-stone-500">{strings.voicesReferenceHint}</p>
                </div>
              ) : (
                <p className="text-xs text-stone-500">{strings.voicesReferenceUnsupported}</p>
              ))}

            {(localError ?? error) !== null && (
              <p className="rounded-lg border border-red-200 bg-red-50 p-2 text-xs text-red-800">
                {localError ?? error}
              </p>
            )}

            <div className="flex flex-col gap-2 sm:flex-row sm:justify-end">
              <button type="button" className={btnGhost} onClick={onClose}>
                {strings.cancel}
              </button>
              <button type="submit" className={btnPrimary} disabled={busy}>
                {busy ? strings.saving : strings.save}
              </button>
            </div>
          </form>
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
            {format(strings.voicesDeleteBody, { name: voice?.id ?? '' })}
          </Dialog.Description>

          {voice?.hasReferenceWav === true && (
            <label className="mt-4 flex min-h-11 cursor-pointer items-center gap-3 text-sm text-stone-700">
              <input
                type="checkbox"
                className="h-5 w-5"
                checked={deleteWav}
                onChange={(e) => setDeleteWav(e.target.checked)}
              />
              {strings.voicesDeleteWav}
            </label>
          )}

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
 * Surowy voices.json — ten sam protokół ETag co pliki uniwersum: GET z ETagiem,
 * PUT z If-Match, a 409 wraca z aktualną treścią i trafia do dialogu.
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
      // Karty czytają /api/voices — po zapisie muszą zobaczyć nowy katalog.
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
  const providersQuery = useVoiceProviders()
  const preview = usePreviewVoice()
  const saveVoice = useSaveVoice()
  const deleteVoice = useDeleteVoice()
  const setDefaultVoice = useSetDefaultVoice()
  const uploadReference = useUploadVoiceReference()
  const player = useVoicePlayer()

  const [showCatalog, setShowCatalog] = useState(false)
  const [formVoice, setFormVoice] = useState<VoiceDto | null>(null)
  const [formOpen, setFormOpen] = useState(false)
  const [deleting, setDeleting] = useState<VoiceDto | null>(null)
  const [actionError, setActionError] = useState<string | null>(null)
  const [upload, setUpload] = useState<{ id: string; pct: number } | null>(null)
  /** voiceId → the preview job we are waiting to autoplay. */
  const [awaiting, setAwaiting] = useState<ReadonlyMap<string, string>>(new Map())

  const voices = useMemo(() => voicesQuery.data ?? [], [voicesQuery.data])
  const jobs = jobsQuery.data

  const jobsById = useMemo(
    () => new Map((jobs ?? []).map((job) => [job.id, job])),
    [jobs],
  )

  // Previews already running (this tab or another device) still show a spinner.
  const runningPreviews = useMemo(() => {
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

  // The job we queued has finished: the mp3 is on disk, so refresh the cards and play it.
  useEffect(() => {
    if (awaiting.size === 0) return
    const settled: string[] = []
    let toPlay: string | null = null
    let failed = false
    for (const [voiceId, jobId] of awaiting) {
      const job = jobsById.get(jobId)
      if (job === undefined || job.state === 'queued' || job.state === 'running') continue
      settled.push(voiceId)
      if (job.state === 'succeeded') toPlay = voiceId
      else failed = true
    }
    if (settled.length === 0) return

    setAwaiting((current) => {
      const next = new Map(current)
      for (const voiceId of settled) next.delete(voiceId)
      return next
    })
    void queryClient.invalidateQueries({ queryKey: ['voices'] })
    if (failed) setActionError(strings.voicesPreviewError)
    if (toPlay !== null) {
      player.bump()
      player.play(toPlay)
    }
  }, [awaiting, jobsById, queryClient, player, strings.voicesPreviewError])

  const requestPreview = useCallback(
    async (voiceId: string) => {
      setActionError(null)
      try {
        const { jobId } = await preview.mutateAsync(voiceId)
        setAwaiting((current) => new Map(current).set(voiceId, jobId))
      } catch {
        setActionError(strings.voicesPreviewError)
      }
    },
    [preview, strings.voicesPreviewError],
  )

  // One button, one mental model: play what is there, otherwise make it and play it.
  const onPlay = useCallback(
    (voice: VoiceDto) => {
      if (player.playingId === voice.id) {
        player.stop()
        return
      }
      setActionError(null)
      if (voice.hasPreview) {
        player.play(voice.id)
        return
      }
      void requestPreview(voice.id)
    },
    [player, requestPreview],
  )

  const onSubmitForm = async (values: {
    id: string
    provider: string
    languages: string[]
    exaggeration: number | null
    cfg: number | null
    referenceFile: File | null
  }) => {
    setActionError(null)
    try {
      await saveVoice.mutateAsync({
        id: values.id,
        provider: values.provider,
        languages: values.languages,
        exaggeration: values.exaggeration,
        cfg: values.cfg,
      })
      if (values.referenceFile !== null) {
        setUpload({ id: values.id, pct: 0 })
        await uploadReference.mutateAsync({
          id: values.id,
          file: values.referenceFile,
          onProgress: (pct) => setUpload({ id: values.id, pct }),
        })
        setUpload(null)
      }
      setFormOpen(false)
      setFormVoice(null)
    } catch (err) {
      setUpload(null)
      setActionError(err instanceof Error ? err.message : strings.voicesSaveError)
    }
  }

  const onReplaceReference = async (voice: VoiceDto, file: File) => {
    setActionError(null)
    if (!file.name.toLowerCase().endsWith('.wav')) {
      setActionError(strings.voicesWavOnly)
      return
    }
    setUpload({ id: voice.id, pct: 0 })
    try {
      await uploadReference.mutateAsync({
        id: voice.id,
        file,
        onProgress: (pct) => setUpload({ id: voice.id, pct }),
      })
      // The old sample was rendered from the old wav; the server has thrown it away.
      player.bump()
    } catch (err) {
      setActionError(err instanceof Error ? err.message : strings.voicesSaveError)
    } finally {
      setUpload(null)
    }
  }

  const onDelete = async (deleteWav: boolean) => {
    if (deleting === null) return
    setActionError(null)
    try {
      if (player.playingId === deleting.id) player.stop()
      await deleteVoice.mutateAsync({ id: deleting.id, deleteWav })
      setDeleting(null)
    } catch (err) {
      setActionError(err instanceof Error ? err.message : strings.voicesSaveError)
    }
  }

  const onSetDefault = async (voice: VoiceDto) => {
    setActionError(null)
    try {
      await setDefaultVoice.mutateAsync(voice.id)
    } catch (err) {
      setActionError(err instanceof Error ? err.message : strings.voicesSaveError)
    }
  }

  const providers = useMemo(() => {
    const reported = providersQuery.data
    if (reported !== undefined && reported.length > 0) return reported
    // No endpoint (older API): fall back to what the catalog already uses.
    const byId = new Map<string, VoiceProviderDto>()
    for (const voice of voices)
      if (voice.provider.length > 0)
        byId.set(voice.provider, { id: voice.provider, supportsCloning: voice.supportsCloning })
    for (const known of KNOWN_PROVIDERS) if (!byId.has(known.id)) byId.set(known.id, known)
    return [...byId.values()]
  }, [providersQuery.data, voices])

  return (
    <section className="rounded-xl border border-stone-200 bg-white px-4">
      <h2 className="border-b border-stone-100 py-3 text-sm font-semibold uppercase tracking-wide text-stone-400">
        {strings.settingsVoicesHeading}
      </h2>
      <div className="space-y-3 py-4">
        <div className="flex flex-wrap items-start justify-between gap-3">
          <div className="min-w-0 flex-1">
            <p className="text-sm text-stone-600">{strings.settingsVoicesHint}</p>
            <p className="mt-1 text-xs text-stone-500">{strings.voicesSampleHint}</p>
          </div>
          <button
            type="button"
            className={btnPrimary}
            onClick={() => {
              setFormVoice(null)
              setFormOpen(true)
            }}
          >
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

        {voicesQuery.isSuccess && voices.length === 0 && (
          <div className="rounded-xl border border-dashed border-stone-300 py-8 text-center">
            <p className="text-sm text-stone-500">{strings.voicesEmpty}</p>
            <p className="mt-1 text-xs text-stone-400">{strings.voicesEmptyHint}</p>
          </div>
        )}

        {voices.length > 0 && (
          <div className="grid items-stretch gap-3 sm:grid-cols-2">
            {voices.map((voice) => (
              <VoiceCard
                key={voice.id}
                voice={voice}
                playing={player.playingId === voice.id}
                preparing={
                  awaiting.has(voice.id) ||
                  runningPreviews.has(voice.id) ||
                  (preview.isPending && preview.variables === voice.id)
                }
                readyToPlay={player.blockedId === voice.id}
                uploadPct={upload?.id === voice.id ? upload.pct : null}
                onPlay={() => onPlay(voice)}
                onRerecord={() => void requestPreview(voice.id)}
                onSetDefault={() => void onSetDefault(voice)}
                onReplaceReference={(file) => void onReplaceReference(voice, file)}
                onEdit={() => {
                  setFormVoice(voice)
                  setFormOpen(true)
                }}
                onDelete={() => setDeleting(voice)}
              />
            ))}
          </div>
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

      {formOpen && (
        <VoiceFormDialog
          voice={formVoice}
          takenIds={voices.map((voice) => voice.id)}
          providers={providers}
          busy={saveVoice.isPending || uploadReference.isPending}
          error={actionError}
          onSubmit={(values) => void onSubmitForm(values)}
          onClose={() => {
            setFormOpen(false)
            setFormVoice(null)
          }}
        />
      )}

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
