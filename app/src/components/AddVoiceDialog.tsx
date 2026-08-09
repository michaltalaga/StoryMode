// "Add a voice" — language, then how, then do it.
//
// The reader never chooses an engine, a format or a number. Which engine can serve a given
// language, and what installing one actually involves, is decided by the backend from the
// language and the source; this file only asks the two questions a person can answer.

import { useEffect, useRef, useState } from 'react'
import type { ChangeEvent, ReactNode } from 'react'
import * as Dialog from '@radix-ui/react-dialog'
import {
  useInstallVoice,
  useUploadVoiceRecording,
  useVoiceLanguages,
  useVoiceOffers,
  voiceOfferSampleUrl,
} from '../api/queries'
import type { VoiceLanguageDto, VoiceOfferDto } from '../api/types'
import { format, useStrings } from '../i18n'
import Flag, { formatBytes, localeLabel } from './Flag'
import { PlayButton, useSamplePlayer } from './SamplePlayer'
import { MAX_SECONDS, canRecord, startRecording, toWav } from './recordWav'
import type { Recording } from './recordWav'

const btnBase =
  'inline-flex min-h-11 items-center justify-center gap-2 rounded-lg px-4 py-2.5 text-sm font-medium transition-colors disabled:cursor-not-allowed disabled:opacity-40'
const btnPrimary = `${btnBase} bg-stone-900 text-white hover:bg-stone-700`
const btnGhost = `${btnBase} border border-stone-300 bg-white text-stone-700 hover:bg-stone-100`
const inputCls =
  'w-full rounded-xl border border-stone-300 bg-white px-3 py-2.5 text-base text-stone-900 outline-none focus:border-amber-500'
const labelCls = 'mb-1 block text-sm font-medium text-stone-700'

/**
 * Where to go when the microphone is unavailable. The block is always the same cause — the browser
 * hides the microphone on an insecure origin — so the fix is always the same address, on the host
 * you already reached the app on.
 */
function httpsUrl(): string {
  return `https://${window.location.hostname}:5212${window.location.pathname}`
}

function Spinner() {
  return (
    <span
      aria-hidden
      className="inline-block h-4 w-4 shrink-0 animate-spin rounded-full border-2 border-stone-300 border-t-violet-600"
    />
  )
}

/** A big tappable card — used for both the language step and the how step. */
function ChoiceCard({
  onClick,
  disabled = false,
  leading,
  title,
  subtitle,
  note,
}: {
  onClick?: () => void
  disabled?: boolean
  leading?: ReactNode
  title: string
  subtitle?: string
  note?: string
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      disabled={disabled}
      className={`flex w-full items-start gap-3 rounded-xl border p-4 text-left transition-colors ${
        disabled
          ? 'cursor-not-allowed border-stone-200 bg-stone-50'
          : 'border-stone-300 bg-white hover:border-stone-400 hover:bg-stone-50'
      }`}
    >
      {leading !== undefined && <span className="mt-0.5 shrink-0">{leading}</span>}
      <span className="min-w-0 flex-1">
        <span className={`block font-medium ${disabled ? 'text-stone-400' : 'text-stone-900'}`}>{title}</span>
        {subtitle !== undefined && (
          <span className={`mt-0.5 block text-sm ${disabled ? 'text-stone-400' : 'text-stone-500'}`}>
            {subtitle}
          </span>
        )}
        {note !== undefined && <span className="mt-1 block text-xs text-amber-700">{note}</span>}
      </span>
    </button>
  )
}

function LanguageStep({
  languages,
  loading,
  onPick,
}: {
  languages: VoiceLanguageDto[]
  loading: boolean
  onPick: (language: VoiceLanguageDto) => void
}) {
  const strings = useStrings()

  if (loading) return <p className="py-6 text-center text-sm text-stone-500">{strings.loading}</p>

  return (
    <div className="space-y-2">
      {languages.map((language) => (
        <ChoiceCard
          key={language.locale}
          onClick={() => onPick(language)}
          leading={<Flag locale={language.locale} className="mt-1" />}
          title={localeLabel(language.locale, strings)}
          subtitle={format(strings.voicesAddOfferCount, { count: String(language.offerCount) })}
        />
      ))}
    </div>
  )
}

function HowStep({
  language,
  onChoose,
  onUpload,
  onRecord,
}: {
  language: VoiceLanguageDto
  onChoose: () => void
  onUpload: () => void
  onRecord: () => void
}) {
  const strings = useStrings()
  // Evaluated per render rather than at module load: the same build is served over both
  // http and https, and only one of those can reach a microphone.
  const recordable = canRecord() && language.canUpload

  return (
    <div className="space-y-2">
      <ChoiceCard
        onClick={onChoose}
        title={strings.voicesAddChoose}
        subtitle={strings.voicesAddChooseHint}
        disabled={language.offerCount === 0}
      />
      <ChoiceCard
        onClick={onRecord}
        title={strings.voicesAddRecord}
        subtitle={strings.voicesAddRecordHint}
        disabled={!recordable}
        note={recordable ? undefined : format(strings.voicesAddRecordBlocked, { url: httpsUrl() })}
      />
      <ChoiceCard
        onClick={onUpload}
        title={strings.voicesAddUpload}
        subtitle={strings.voicesAddUploadHint}
        disabled={!language.canUpload}
      />
    </div>
  )
}

function OfferRow({
  offer,
  playing,
  installing,
  onPlay,
  onAdd,
}: {
  offer: VoiceOfferDto
  playing: boolean
  installing: boolean
  onPlay: () => void
  onAdd: () => void
}) {
  const strings = useStrings()

  return (
    <li className="flex items-start gap-3 rounded-xl border border-stone-200 bg-white p-3">
      <PlayButton playing={playing} label={offer.name} onClick={onPlay} />
      <div className="min-w-0 flex-1">
        <p className="font-medium text-stone-900">{offer.name}</p>
        <p className="mt-0.5 text-sm leading-snug text-stone-500">{offer.description}</p>
        <p className="mt-1 text-xs text-stone-400">
          {offer.downloadBytes > 0
            ? format(strings.voicesDownloadSize, { size: formatBytes(offer.downloadBytes) })
            : strings.voicesDownloadNone}
        </p>
      </div>
      <button type="button" className={`${btnGhost} shrink-0`} disabled={installing} onClick={onAdd}>
        {installing ? <Spinner /> : null}
        {installing ? strings.voicesAddInstalling : strings.voicesAddInstall}
      </button>
    </li>
  )
}

function ChooseStep({
  locale,
  onQueued,
}: {
  locale: string
  onQueued: (voiceId: string, jobId: string) => void
}) {
  const strings = useStrings()
  const offersQuery = useVoiceOffers(locale)
  const install = useInstallVoice()
  const player = useSamplePlayer()
  const [error, setError] = useState<string | null>(null)
  const [installingKey, setInstallingKey] = useState<string | null>(null)

  const add = async (offer: VoiceOfferDto) => {
    setError(null)
    setInstallingKey(offer.key)
    try {
      const { jobId, voiceId } = await install.mutateAsync({ key: offer.key })
      player.stop()
      onQueued(voiceId, jobId)
    } catch (err) {
      setError(err instanceof Error ? err.message : strings.voicesAddInstallError)
    } finally {
      setInstallingKey(null)
    }
  }

  if (offersQuery.isLoading)
    return <p className="py-6 text-center text-sm text-stone-500">{strings.loading}</p>
  if (offersQuery.isError)
    return <p className="py-6 text-center text-sm text-red-700">{strings.voicesOffersLoadError}</p>

  const offers = offersQuery.data ?? []
  if (offers.length === 0)
    return <p className="py-6 text-center text-sm text-stone-500">{strings.voicesOffersEmpty}</p>

  return (
    <div>
      <ul className="space-y-2">
        {offers.map((offer) => {
          const url = voiceOfferSampleUrl(offer.key)
          return (
            <OfferRow
              key={offer.key}
              offer={offer}
              playing={player.playingUrl === url}
              installing={installingKey === offer.key}
              onPlay={() => player.toggle(url)}
              onAdd={() => void add(offer)}
            />
          )
        })}
      </ul>
      {error !== null && (
        <p className="mt-3 rounded-lg border border-red-200 bg-red-50 p-2 text-xs text-red-800">{error}</p>
      )}
    </div>
  )
}

/** The same advice serves both routes in: a recording is a recording, however it arrives. */
function RulesPanel({ showFormats }: { showFormats: boolean }) {
  const strings = useStrings()

  return (
    <div className="rounded-xl border border-stone-200 bg-stone-50 p-3">
      <h4 className="text-sm font-semibold text-stone-800">{strings.voicesUploadRules}</h4>
      <ul className="mt-2 list-disc space-y-1 pl-5 text-sm leading-snug text-stone-600">
        <li>{strings.voicesUploadRule1}</li>
        <li>{strings.voicesUploadRule2}</li>
        <li>{strings.voicesUploadRule3}</li>
        <li>{strings.voicesUploadRule4}</li>
      </ul>
      {showFormats && <p className="mt-2 text-xs text-stone-500">{strings.voicesUploadFormats}</p>}
    </div>
  )
}

/** What to read, in the language the voice will speak — the thing that makes "record" actionable. */
function PassagePanel({ locale }: { locale: string }) {
  const strings = useStrings()
  const passage = locale.toLowerCase().startsWith('pl')
    ? strings.voicesUploadPassagePl
    : strings.voicesUploadPassageEn

  return (
    <div>
      <h4 className="text-sm font-semibold text-stone-800">{strings.voicesUploadScript}</h4>
      <p className="mt-1 text-xs text-stone-500">{strings.voicesUploadScriptHint}</p>
      <p
        lang={locale}
        className="mt-2 rounded-xl border border-stone-200 bg-white p-3 font-serif text-[15px] leading-relaxed text-stone-800"
      >
        {passage}
      </p>
    </div>
  )
}

function NameField({ value, onChange }: { value: string; onChange: (value: string) => void }) {
  const strings = useStrings()

  return (
    <div>
      <label className={labelCls} htmlFor="voice-name">
        {strings.voicesUploadNameLabel}
      </label>
      <input
        id="voice-name"
        className={inputCls}
        value={value}
        onChange={(e) => onChange(e.target.value)}
        placeholder={strings.voicesUploadNamePlaceholder}
        autoComplete="off"
      />
    </div>
  )
}

function UploadProgress({ pct }: { pct: number }) {
  const strings = useStrings()

  return (
    <div>
      <div className="h-1.5 w-full overflow-hidden rounded-full bg-stone-100">
        <div className="h-full bg-violet-500 transition-all" style={{ width: `${pct}%` }} />
      </div>
      <p className="mt-1 text-xs text-stone-500">
        {pct < 100 ? strings.voicesUploading : strings.voicesUploadPreparing}
      </p>
    </div>
  )
}

function UploadStep({
  locale,
  onQueued,
}: {
  locale: string
  onQueued: (voiceId: string, jobId: string) => void
}) {
  const strings = useStrings()
  const upload = useUploadVoiceRecording()
  const [file, setFile] = useState<File | null>(null)
  const [name, setName] = useState('')
  const [pct, setPct] = useState<number | null>(null)
  const [error, setError] = useState<string | null>(null)

  const pick = (event: ChangeEvent<HTMLInputElement>) => {
    setFile(event.target.files?.[0] ?? null)
    setError(null)
  }

  const submit = async () => {
    if (file === null) return
    if (name.trim().length === 0) {
      setError(strings.voicesNameRequired)
      return
    }
    setError(null)
    setPct(0)
    try {
      const { jobId, voiceId } = await upload.mutateAsync({
        name: name.trim(),
        locale,
        file,
        onProgress: setPct,
      })
      onQueued(voiceId, jobId)
    } catch (err) {
      setError(err instanceof Error ? err.message : strings.voicesUploadError)
    } finally {
      setPct(null)
    }
  }

  return (
    <div className="space-y-4">
      <RulesPanel showFormats />
      <PassagePanel locale={locale} />

      <label className={`${btnGhost} w-full cursor-pointer`}>
        <input type="file" accept="audio/*,.wav,.mp3,.m4a" className="hidden" onChange={pick} />
        {file?.name ?? strings.voicesUploadPick}
      </label>

      <NameField value={name} onChange={setName} />

      {pct !== null && <UploadProgress pct={pct} />}
      {error !== null && (
        <p className="rounded-lg border border-red-200 bg-red-50 p-2 text-xs text-red-800">{error}</p>
      )}

      <button
        type="button"
        className={`${btnPrimary} w-full`}
        disabled={file === null || pct !== null}
        onClick={() => void submit()}
      >
        {strings.voicesUploadSubmit}
      </button>
    </div>
  )
}

function clock(seconds: number): string {
  return `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`
}

function RecordStep({
  locale,
  onQueued,
}: {
  locale: string
  onQueued: (voiceId: string, jobId: string) => void
}) {
  const strings = useStrings()
  const upload = useUploadVoiceRecording()
  const [phase, setPhase] = useState<'idle' | 'recording' | 'ready'>('idle')
  const [seconds, setSeconds] = useState(0)
  const [wavUrl, setWavUrl] = useState<string | null>(null)
  const [name, setName] = useState('')
  const [pct, setPct] = useState<number | null>(null)
  const [error, setError] = useState<string | null>(null)
  const recordingRef = useRef<Recording | null>(null)
  const wavRef = useRef<Blob | null>(null)

  // Leaving the step mid-recording must release the microphone, not leave it live.
  useEffect(
    () => () => {
      recordingRef.current?.cancel()
      if (wavUrl !== null) URL.revokeObjectURL(wavUrl)
    },
    [wavUrl],
  )

  const finish = async () => {
    const recording = recordingRef.current
    if (recording === null) return
    recordingRef.current = null
    try {
      const raw = await recording.stop()
      const wav = await toWav(raw)
      wavRef.current = wav
      setWavUrl((previous) => {
        if (previous !== null) URL.revokeObjectURL(previous)
        return URL.createObjectURL(wav)
      })
      setPhase('ready')
    } catch {
      setError(strings.voicesRecordFailed)
      setPhase('idle')
    }
  }

  // Tick the clock while recording, and stop by itself at the cap.
  useEffect(() => {
    if (phase !== 'recording') return
    const timer = window.setInterval(() => {
      setSeconds((current) => {
        const next = current + 1
        if (next >= MAX_SECONDS) void finish()
        return next
      })
    }, 1000)
    return () => window.clearInterval(timer)
    // finish() is stable enough for this: it only reads refs and setState.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [phase])

  const begin = async () => {
    setError(null)
    setSeconds(0)
    try {
      // Started straight from the tap — iOS only grants the microphone on a real gesture.
      recordingRef.current = await startRecording()
      setPhase('recording')
    } catch (err) {
      const denied = err instanceof DOMException && (err.name === 'NotAllowedError' || err.name === 'SecurityError')
      setError(denied ? strings.voicesRecordDenied : strings.voicesRecordFailed)
    }
  }

  const submit = async () => {
    const wav = wavRef.current
    if (wav === null) return
    if (name.trim().length === 0) {
      setError(strings.voicesNameRequired)
      return
    }
    setError(null)
    setPct(0)
    try {
      const file = new File([wav], 'recording.wav', { type: 'audio/wav' })
      const { jobId, voiceId } = await upload.mutateAsync({
        name: name.trim(),
        locale,
        file,
        onProgress: setPct,
      })
      onQueued(voiceId, jobId)
    } catch (err) {
      setError(err instanceof Error ? err.message : strings.voicesUploadError)
    } finally {
      setPct(null)
    }
  }

  const tooShort = seconds < 10

  return (
    <div className="space-y-4">
      <RulesPanel showFormats={false} />
      <PassagePanel locale={locale} />

      <div className="rounded-xl border border-stone-200 p-4 text-center">
        <p
          className={`font-mono text-3xl tabular-nums ${
            phase === 'recording' ? 'text-red-600' : 'text-stone-800'
          }`}
        >
          {clock(seconds)}
        </p>

        {phase === 'recording' ? (
          <>
            <button type="button" className={`${btnPrimary} mt-3 w-full`} onClick={() => void finish()}>
              ■ {strings.voicesRecordStop}
            </button>
            <p className="mt-2 text-xs text-stone-500">
              {tooShort ? strings.voicesRecordKeepGoing : strings.voicesRecordEnough}
            </p>
          </>
        ) : (
          <button
            type="button"
            className={`${btnPrimary} mt-3 w-full`}
            disabled={pct !== null}
            onClick={() => void begin()}
          >
            ● {phase === 'ready' ? strings.voicesRecordAgain : strings.voicesRecordStart}
          </button>
        )}

        {phase === 'idle' && seconds === 0 && (
          <p className="mt-2 text-xs text-stone-500">{strings.voicesRecordHint}</p>
        )}
      </div>

      {phase === 'ready' && wavUrl !== null && (
        <div>
          <h4 className="mb-1 text-sm font-semibold text-stone-800">{strings.voicesRecordListen}</h4>
          {/* Plain controls on purpose: scrubbing back over your own reading is the point. */}
          <audio src={wavUrl} controls className="w-full" />
        </div>
      )}

      {phase === 'ready' && <NameField value={name} onChange={setName} />}

      {pct !== null && <UploadProgress pct={pct} />}
      {error !== null && (
        <p className="rounded-lg border border-red-200 bg-red-50 p-2 text-xs text-red-800">{error}</p>
      )}

      {phase === 'ready' && (
        <button
          type="button"
          className={`${btnPrimary} w-full`}
          disabled={pct !== null || tooShort}
          onClick={() => void submit()}
        >
          {strings.voicesUploadSubmit}
        </button>
      )}
    </div>
  )
}

export default function AddVoiceDialog({
  open,
  onClose,
  onQueued,
}: {
  open: boolean
  onClose: () => void
  /** An install job is running; the caller watches it and closes the dialog. */
  onQueued: (voiceId: string, jobId: string) => void
}) {
  const strings = useStrings()
  const languagesQuery = useVoiceLanguages()
  const [language, setLanguage] = useState<VoiceLanguageDto | null>(null)
  const [mode, setMode] = useState<'choose' | 'upload' | 'record' | null>(null)

  const reset = () => {
    setLanguage(null)
    setMode(null)
  }

  const close = () => {
    reset()
    onClose()
  }

  const back = () => {
    if (mode !== null) setMode(null)
    else setLanguage(null)
  }

  const title =
    language === null
      ? strings.voicesAddStepLanguage
      : mode === null
        ? strings.voicesAddStepHow
        : mode === 'choose'
          ? strings.voicesAddChoose
          : mode === 'record'
            ? strings.voicesAddRecord
            : strings.voicesUploadTitle

  return (
    <Dialog.Root open={open} onOpenChange={(next) => !next && close()}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 z-40 bg-black/40" />
        <Dialog.Content className="fixed left-1/2 top-1/2 z-50 max-h-[88vh] w-[calc(100vw-2rem)] max-w-lg -translate-x-1/2 -translate-y-1/2 overflow-y-auto rounded-2xl bg-white p-5 shadow-xl sm:p-6">
          <Dialog.Title className="text-lg font-semibold text-stone-900">{strings.voicesAdd}</Dialog.Title>
          <Dialog.Description className="mt-1 flex items-center gap-2 text-sm text-stone-500">
            {language !== null && <Flag locale={language.locale} />}
            {title}
          </Dialog.Description>

          <div className="mt-4">
            {language === null ? (
              <LanguageStep
                languages={languagesQuery.data ?? []}
                loading={languagesQuery.isLoading}
                onPick={setLanguage}
              />
            ) : mode === null ? (
              <HowStep
                language={language}
                onChoose={() => setMode('choose')}
                onUpload={() => setMode('upload')}
                onRecord={() => setMode('record')}
              />
            ) : mode === 'choose' ? (
              <ChooseStep locale={language.locale} onQueued={onQueued} />
            ) : mode === 'record' ? (
              <RecordStep locale={language.locale} onQueued={onQueued} />
            ) : (
              <UploadStep locale={language.locale} onQueued={onQueued} />
            )}
          </div>

          <div className="mt-5 flex justify-between gap-2">
            {language !== null ? (
              <button type="button" className={btnGhost} onClick={back}>
                ← {strings.voicesAddBack}
              </button>
            ) : (
              <span />
            )}
            <button type="button" className={btnGhost} onClick={close}>
              {strings.cancel}
            </button>
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  )
}
