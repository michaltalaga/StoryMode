// "Render audio": pick who reads it and how, then go.
//
// This is where voice and delivery belong. Neither changes a word of the story — they only
// describe one recording of it — so asking here means you can render the same draft in two
// voices without editing the spec. The story remembers what you picked last, so the usual
// case is opening this and pressing the button.

import { useEffect, useState } from 'react'
import * as Dialog from '@radix-ui/react-dialog'
import { useRenderChoice, useVoices } from '../api/queries'
import Flag, { localeLabel } from './Flag'
import { format, useStrings } from '../i18n'

const btnBase =
  'inline-flex min-h-11 items-center justify-center gap-2 rounded-lg px-4 py-2.5 text-sm font-medium transition-colors disabled:cursor-not-allowed disabled:opacity-40'
const btnPrimary = `${btnBase} bg-amber-600 text-white hover:bg-amber-700`
const btnGhost = `${btnBase} border border-stone-300 bg-white text-stone-700 hover:bg-stone-100`
const inputCls =
  'w-full rounded-xl border border-stone-300 bg-white px-3 py-2.5 text-base text-stone-900 outline-none focus:border-amber-500'
const labelCls = 'mb-1 block text-sm font-medium text-stone-700'

const DELIVERIES = ['calm', 'natural', 'lively']

export default function RenderAudioDialog({
  open,
  storyId,
  variant,
  busy,
  onClose,
  onRender,
}: {
  open: boolean
  storyId: string
  variant: string
  busy: boolean
  onClose: () => void
  onRender: (choice: { voiceId: string; delivery: string }) => void
}) {
  const strings = useStrings()
  const voicesQuery = useVoices()
  const remembered = useRenderChoice(storyId, open ? variant : '')
  const [voiceId, setVoiceId] = useState('')
  const [delivery, setDelivery] = useState('natural')

  const voices = voicesQuery.data ?? []

  // Seed from what this story was rendered with last; fall back to the catalog default.
  useEffect(() => {
    if (!open || remembered.data === undefined) return
    const previous = remembered.data.voice
    const known = voices.some((voice) => voice.id === previous)
    setVoiceId(known ? previous : (voices.find((voice) => voice.isDefault)?.id ?? voices[0]?.id ?? ''))
    setDelivery(remembered.data.delivery)
  }, [open, remembered.data, voices])

  const chosen = voices.find((voice) => voice.id === voiceId)
  const options = chosen?.styles ?? DELIVERIES

  const deliveryLabel = (id: string) =>
    id === 'calm' ? strings.voiceStyleCalm : id === 'lively' ? strings.voiceStyleLively : strings.voiceStyleNatural

  return (
    <Dialog.Root open={open} onOpenChange={(next) => !next && onClose()}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 z-40 bg-black/40" />
        <Dialog.Content className="fixed left-1/2 top-1/2 z-50 max-h-[85vh] w-[calc(100vw-2rem)] max-w-md -translate-x-1/2 -translate-y-1/2 overflow-y-auto rounded-2xl bg-white p-6 shadow-xl">
          <Dialog.Title className="text-lg font-semibold text-stone-900">{strings.renderAudio}</Dialog.Title>
          <Dialog.Description className="mt-1 text-sm text-stone-500">
            {strings.renderAudioHint}
          </Dialog.Description>

          <div className="mt-4 space-y-4">
            <div>
              <label className={labelCls} htmlFor="render-voice">
                {strings.builderVoiceLabel}
              </label>
              {voices.length === 0 ? (
                <p className="text-sm text-stone-500">{strings.voicesEmpty}</p>
              ) : (
                <select
                  id="render-voice"
                  className={inputCls}
                  value={voiceId}
                  onChange={(e) => setVoiceId(e.target.value)}
                >
                  {voices.map((voice) => (
                    <option key={voice.id} value={voice.id}>
                      {`${voice.name} — ${localeLabel(voice.locale, strings)}`}
                    </option>
                  ))}
                </select>
              )}
              {chosen !== undefined && (
                <p className="mt-1 flex items-center gap-2 text-xs text-stone-500">
                  <Flag locale={chosen.locale} />
                  {chosen.description}
                </p>
              )}
            </div>

            <div>
              <span className={labelCls}>{strings.voicesStyleLabel}</span>
              <div role="group" className="inline-flex w-full overflow-hidden rounded-lg border border-stone-300">
                {options.map((option) => (
                  <button
                    key={option}
                    type="button"
                    aria-pressed={delivery === option}
                    onClick={() => setDelivery(option)}
                    className={`min-h-11 flex-1 px-2 text-sm font-medium transition-colors ${
                      delivery === option
                        ? 'bg-stone-900 text-white'
                        : 'bg-white text-stone-600 hover:bg-stone-100'
                    }`}
                  >
                    {deliveryLabel(option)}
                  </button>
                ))}
              </div>
              <p className="mt-1 text-xs text-stone-500">{strings.renderDeliveryHint}</p>
            </div>
          </div>

          <div className="mt-6 flex flex-col gap-2 sm:flex-row sm:justify-end">
            <button type="button" className={btnGhost} onClick={onClose}>
              {strings.cancel}
            </button>
            <button
              type="button"
              className={btnPrimary}
              disabled={busy || voiceId === ''}
              onClick={() => onRender({ voiceId, delivery })}
            >
              {format(strings.renderAudioStart, { name: chosen?.name ?? '' })}
            </button>
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  )
}
