// Microphone capture that always produces the one shape a cloning engine wants: mono 24 kHz PCM wav.
//
// The conversion happens here, in the browser, rather than on the server. Phones and desktops record
// in whatever container their browser prefers — Android Chrome gives webm/opus, iOS Safari gives
// mp4/aac — and Windows Media Foundation, which the server decodes with, cannot read webm/opus at
// all. Re-encoding client-side sidesteps the whole codec question: the browser can always decode
// what it just recorded, and the server receives a plain wav.

/** What the chatterbox speech encoder conditions on; resampling later would be lossy twice. */
const SAMPLE_RATE = 24_000

/** Recording stops itself here — well past the 40 s the instructions ask for. */
export const MAX_SECONDS = 90

// First supported wins. iOS Safari only offers audio/mp4.
const CANDIDATE_TYPES = [
  'audio/webm;codecs=opus',
  'audio/webm',
  'audio/mp4',
  'audio/ogg;codecs=opus',
]

type AudioContextCtor = typeof AudioContext

function audioContextCtor(): AudioContextCtor | undefined {
  const w = window as unknown as { AudioContext?: AudioContextCtor; webkitAudioContext?: AudioContextCtor }
  return w.AudioContext ?? w.webkitAudioContext
}

/**
 * Whether recording is possible at all. `isSecureContext` is the one that bites in practice: the
 * microphone is unavailable over plain http, which is how the phone reaches this app by default.
 */
export function canRecord(): boolean {
  return (
    typeof window !== 'undefined' &&
    window.isSecureContext &&
    navigator.mediaDevices?.getUserMedia !== undefined &&
    typeof MediaRecorder !== 'undefined' &&
    audioContextCtor() !== undefined
  )
}

export interface Recording {
  /** Resolves with the raw recorded blob, in whatever container the browser chose. */
  stop(): Promise<Blob>
  /** Abandon it and release the microphone. */
  cancel(): void
}

/** Must be called straight from a tap: iOS only grants the microphone on a real user gesture. */
export async function startRecording(): Promise<Recording> {
  const stream = await navigator.mediaDevices.getUserMedia({
    audio: {
      channelCount: 1,
      // Echo cancellation and noise suppression are tuned for phone calls: they smear exactly the
      // timbre a clone copies. Gain control stays on so a quiet room still gives usable level.
      echoCancellation: false,
      noiseSuppression: false,
      autoGainControl: true,
    },
  })

  const mimeType = CANDIDATE_TYPES.find((type) => MediaRecorder.isTypeSupported(type))
  const recorder = new MediaRecorder(stream, mimeType === undefined ? undefined : { mimeType })
  const chunks: BlobPart[] = []
  recorder.ondataavailable = (event) => {
    if (event.data.size > 0) chunks.push(event.data)
  }
  recorder.start()

  // Releasing the tracks is what turns the browser's recording indicator off.
  const release = () => stream.getTracks().forEach((track) => track.stop())

  return {
    stop: () =>
      new Promise<Blob>((resolve, reject) => {
        recorder.onerror = () => {
          release()
          reject(new Error('recording failed'))
        }
        recorder.onstop = () => {
          release()
          resolve(new Blob(chunks, { type: recorder.mimeType || 'audio/webm' }))
        }
        recorder.stop()
      }),
    cancel: () => {
      try {
        if (recorder.state !== 'inactive') recorder.stop()
      } catch {
        // already stopped — releasing the tracks is what matters
      }
      release()
    },
  }
}

/** Decodes whatever the browser recorded and re-encodes it as mono 24 kHz PCM wav. */
export async function toWav(recorded: Blob): Promise<Blob> {
  const Ctor = audioContextCtor()
  if (Ctor === undefined) throw new Error('no audio support')

  const bytes = await recorded.arrayBuffer()
  const context = new Ctor()
  let decoded: AudioBuffer
  try {
    decoded = await context.decodeAudioData(bytes)
  } finally {
    void context.close()
  }

  // OfflineAudioContext does the downmix to mono and the resample to 24 kHz in one pass.
  const frames = Math.max(1, Math.ceil(decoded.duration * SAMPLE_RATE))
  const offline = new OfflineAudioContext(1, frames, SAMPLE_RATE)
  const source = offline.createBufferSource()
  source.buffer = decoded
  source.connect(offline.destination)
  source.start()
  const rendered = await offline.startRendering()

  return encodeWav(rendered.getChannelData(0), SAMPLE_RATE)
}

function encodeWav(samples: Float32Array, sampleRate: number): Blob {
  const buffer = new ArrayBuffer(44 + samples.length * 2)
  const view = new DataView(buffer)
  const ascii = (offset: number, text: string) => {
    for (let i = 0; i < text.length; i++) view.setUint8(offset + i, text.charCodeAt(i))
  }

  ascii(0, 'RIFF')
  view.setUint32(4, 36 + samples.length * 2, true)
  ascii(8, 'WAVE')
  ascii(12, 'fmt ')
  view.setUint32(16, 16, true) // fmt chunk size
  view.setUint16(20, 1, true) // PCM
  view.setUint16(22, 1, true) // mono
  view.setUint32(24, sampleRate, true)
  view.setUint32(28, sampleRate * 2, true) // byte rate
  view.setUint16(32, 2, true) // block align
  view.setUint16(34, 16, true) // bits per sample
  ascii(36, 'data')
  view.setUint32(40, samples.length * 2, true)

  let offset = 44
  for (let i = 0; i < samples.length; i++, offset += 2) {
    const clipped = Math.max(-1, Math.min(1, samples[i]))
    view.setInt16(offset, clipped < 0 ? clipped * 0x8000 : clipped * 0x7fff, true)
  }

  return new Blob([buffer], { type: 'audio/wav' })
}
