import { useEffect, useRef } from 'react';
import { Link } from 'react-router';
import { useStrings } from '../i18n';
import { usePlayerStore } from './playerStore';

function updatePositionState(el: HTMLAudioElement): void {
  if (!('mediaSession' in navigator)) return;
  if (typeof navigator.mediaSession.setPositionState !== 'function') return;
  if (!Number.isFinite(el.duration)) return;
  try {
    navigator.mediaSession.setPositionState({
      duration: el.duration,
      playbackRate: el.playbackRate,
      position: Math.min(el.currentTime, el.duration),
    });
  } catch {
    // Position state is best-effort only.
  }
}

/**
 * Owns the ONE <audio> element for the whole app. Mount once in the shell
 * (outside the routed area) so playback survives navigation. The shell wraps
 * this in a fixed slot pinned above the phone tab bar; the component itself
 * renders a plain bar (nothing when the queue is empty).
 */
export default function PlayerBar() {
  const t = useStrings();
  const audioRef = useRef<HTMLAudioElement>(null);
  const loadedUrlRef = useRef<string | null>(null);

  const queue = usePlayerStore((s) => s.queue);
  const index = usePlayerStore((s) => s.index);
  const playing = usePlayerStore((s) => s.playing);
  const toggle = usePlayerStore((s) => s.toggle);
  const next = usePlayerStore((s) => s.next);

  const track = index < queue.length ? queue[index] : undefined;

  // Load the current track and follow play/pause intent.
  useEffect(() => {
    const el = audioRef.current;
    if (!el) return;
    if (!track) {
      loadedUrlRef.current = null;
      el.removeAttribute('src');
      el.load();
      return;
    }
    if (loadedUrlRef.current !== track.url) {
      loadedUrlRef.current = track.url;
      el.src = track.url;
      el.load();
    }
    if (playing) {
      el.play().catch((err: unknown) => {
        // Autoplay refusals (no user gesture yet) must not leave the store lying.
        if (err instanceof DOMException && err.name === 'NotAllowedError') {
          usePlayerStore.setState({ playing: false });
        }
      });
    } else {
      el.pause();
    }
  }, [track, playing]);

  // Lock-screen / Bluetooth metadata.
  useEffect(() => {
    if (!('mediaSession' in navigator)) return;
    if (!track) {
      navigator.mediaSession.metadata = null;
      return;
    }
    navigator.mediaSession.metadata = new MediaMetadata({
      title: track.title,
      artist: t.appName,
      album: track.variant,
    });
  }, [track, t.appName]);

  useEffect(() => {
    if (!('mediaSession' in navigator)) return;
    navigator.mediaSession.playbackState = track ? (playing ? 'playing' : 'paused') : 'none';
  }, [track, playing]);

  // Media Session action handlers (registered once; they read live store state).
  useEffect(() => {
    if (!('mediaSession' in navigator)) return;
    const ms = navigator.mediaSession;
    const safeSet = (action: MediaSessionAction, handler: MediaSessionActionHandler | null) => {
      try {
        ms.setActionHandler(action, handler);
      } catch {
        // Action not supported by this browser.
      }
    };
    safeSet('play', () => {
      if (usePlayerStore.getState().queue.length > 0) usePlayerStore.setState({ playing: true });
    });
    safeSet('pause', () => usePlayerStore.setState({ playing: false }));
    safeSet('nexttrack', () => usePlayerStore.getState().next());
    safeSet('previoustrack', () => {
      const { index: i, prev } = usePlayerStore.getState();
      const el = audioRef.current;
      if (el && (el.currentTime > 5 || i === 0)) {
        el.currentTime = 0;
        usePlayerStore.setState({ playing: true });
      } else {
        prev();
      }
    });
    return () => {
      safeSet('play', null);
      safeSet('pause', null);
      safeSet('nexttrack', null);
      safeSet('previoustrack', null);
    };
  }, []);

  const handleEnded = () => {
    const el = audioRef.current;
    const { queue: q, index: i, next: advance } = usePlayerStore.getState();
    if (i + 1 < q.length) {
      advance();
    } else {
      usePlayerStore.setState({ playing: false });
      if (el) el.currentTime = 0; // replaying the last track starts from the top
    }
  };

  const handlePause = () => {
    const el = audioRef.current;
    if (!el || el.ended || el.seeking) return;
    // External pause (headphones unplugged, another app took audio focus).
    if (usePlayerStore.getState().playing) usePlayerStore.setState({ playing: false });
  };

  const handlePlay = () => {
    if (!usePlayerStore.getState().playing) usePlayerStore.setState({ playing: true });
  };

  return (
    <>
      <audio
        ref={audioRef}
        preload="metadata"
        className="hidden"
        onEnded={handleEnded}
        onPause={handlePause}
        onPlay={handlePlay}
        onLoadedMetadata={() => {
          const el = audioRef.current;
          if (el) updatePositionState(el);
        }}
        onSeeked={() => {
          const el = audioRef.current;
          if (el) updatePositionState(el);
        }}
      />
      {track && (
        <div className="border-t border-stone-200 bg-white/95 backdrop-blur">
          <div className="mx-auto flex max-w-3xl items-center gap-1 px-3 py-1.5">
            <Link to="/listen" className="min-w-0 flex-1 py-1.5">
              <p className="truncate text-sm font-medium text-stone-900">{track.title}</p>
              <p className="truncate text-xs text-stone-500">{track.variant}</p>
            </Link>
            <button
              type="button"
              onClick={toggle}
              aria-label={playing ? t.pause : t.play}
              className="flex h-12 w-12 shrink-0 items-center justify-center rounded-full text-stone-900 active:bg-stone-200"
            >
              {playing ? (
                <svg viewBox="0 0 24 24" className="h-7 w-7" fill="currentColor" aria-hidden="true">
                  <path d="M6 5h4v14H6zM14 5h4v14h-4z" />
                </svg>
              ) : (
                <svg viewBox="0 0 24 24" className="h-7 w-7" fill="currentColor" aria-hidden="true">
                  <path d="M8 5v14l11-7z" />
                </svg>
              )}
            </button>
            <button
              type="button"
              onClick={next}
              aria-label={t.nextTrack}
              className="flex h-12 w-12 shrink-0 items-center justify-center rounded-full text-stone-900 active:bg-stone-200"
            >
              <svg viewBox="0 0 24 24" className="h-7 w-7" fill="currentColor" aria-hidden="true">
                <path d="M6 18l8.5-6L6 6v12zM16 6h2v12h-2z" />
              </svg>
            </button>
          </div>
        </div>
      )}
    </>
  );
}
