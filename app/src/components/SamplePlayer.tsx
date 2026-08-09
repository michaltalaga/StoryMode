// One shared player for every voice sample in the app, and the button that drives it.
//
// The load-bearing property: play() is called straight out of a click on a URL that is
// already a file on disk. Nothing is rendered, nothing is queued, so there is no wait and
// no autoplay problem — the browser only blocks playback that starts long after the tap.

import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useStrings } from '../i18n'

export function useSamplePlayer() {
  const audioRef = useRef<HTMLAudioElement | null>(null)
  const versionRef = useRef(0)
  const [playingUrl, setPlayingUrl] = useState<string | null>(null)
  const [failedUrl, setFailedUrl] = useState<string | null>(null)

  const stop = useCallback(() => {
    audioRef.current?.pause()
    setPlayingUrl(null)
  }, [])

  const play = useCallback((url: string) => {
    let audio = audioRef.current
    if (audio === null) {
      audio = new Audio()
      audio.addEventListener('ended', () => setPlayingUrl(null))
      audioRef.current = audio
    }
    audio.pause()
    // The sample URL never changes, only its bytes (re-recording a sample), so bust the cache.
    audio.src = versionRef.current === 0 ? url : `${url}?v=${versionRef.current}`
    setPlayingUrl(url)
    setFailedUrl(null)
    void audio.play().catch(() => {
      setPlayingUrl(null)
      setFailedUrl(url)
    })
  }, [])

  const toggle = useCallback(
    (url: string) => (playingUrl === url ? stop() : play(url)),
    [playingUrl, play, stop],
  )

  /** Call after a sample has been re-rendered so the browser fetches the new bytes. */
  const bump = useCallback(() => {
    versionRef.current += 1
  }, [])

  useEffect(() => () => audioRef.current?.pause(), [])

  return useMemo(
    () => ({ playingUrl, failedUrl, play, stop, toggle, bump }),
    [playingUrl, failedUrl, play, stop, toggle, bump],
  )
}

function PlayIcon() {
  return (
    <svg viewBox="0 0 24 24" className="h-5 w-5" fill="currentColor" aria-hidden>
      <path d="M8 5.5v13l11-6.5z" />
    </svg>
  )
}

function StopIcon() {
  return (
    <svg viewBox="0 0 24 24" className="h-5 w-5" fill="currentColor" aria-hidden>
      <rect x="7" y="7" width="10" height="10" rx="1.5" />
    </svg>
  )
}

/** 44 px round button — the whole "listen to this voice" interaction. */
export function PlayButton({
  playing,
  disabled = false,
  label,
  onClick,
}: {
  playing: boolean
  disabled?: boolean
  label: string
  onClick: () => void
}) {
  const strings = useStrings()

  return (
    <button
      type="button"
      onClick={onClick}
      disabled={disabled}
      aria-label={`${playing ? strings.voicesStop : strings.voicesPlay} — ${label}`}
      className="flex h-11 w-11 shrink-0 items-center justify-center rounded-full bg-stone-900 text-white transition-colors hover:bg-stone-700 disabled:cursor-not-allowed disabled:bg-stone-300"
    >
      {playing ? <StopIcon /> : <PlayIcon />}
    </button>
  )
}
