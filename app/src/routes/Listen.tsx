import { useMemo, useState } from 'react';
import { useStories } from '../api/queries';
import { audioUrl } from '../api/client';
import type { StorySummary } from '../api/types';
import { usePlayerStore } from '../player/playerStore';
import type { Track } from '../player/playerStore';
import { useStrings } from '../i18n';

interface Row {
  track: Track;
  universe: string;
  pov: string;
}

function buildRows(stories: StorySummary[]): Row[] {
  const rows: Row[] = [];
  for (const story of stories) {
    const audioVariants = story.variants.filter((v) => v.stage === 'audio');
    for (const v of audioVariants) {
      rows.push({
        track: {
          storyId: story.id,
          variant: v.variant,
          title: audioVariants.length > 1 ? `${story.title} — ${v.variant}` : story.title,
          url: audioUrl(story.id, v.variant),
        },
        universe: story.universe,
        pov: v.pov,
      });
    }
  }
  return rows;
}

/** Car-primary listening screen: big controls, tap-to-play queue, mp3 downloads. */
export default function Listen() {
  const t = useStrings();
  const { data: stories, isLoading, isError } = useStories();

  const queue = usePlayerStore((s) => s.queue);
  const index = usePlayerStore((s) => s.index);
  const playing = usePlayerStore((s) => s.playing);
  const play = usePlayerStore((s) => s.play);
  const toggle = usePlayerStore((s) => s.toggle);
  const next = usePlayerStore((s) => s.next);
  const prev = usePlayerStore((s) => s.prev);

  const rows = useMemo(() => buildRows(stories ?? []), [stories]);
  const tracks = useMemo(() => rows.map((r) => r.track), [rows]);

  const current = index < queue.length ? queue[index] : undefined;

  const [downloadingAll, setDownloadingAll] = useState(false);

  const handleBigToggle = () => {
    if (queue.length > 0) {
      toggle();
    } else if (tracks.length > 0) {
      play(tracks, 0);
    }
  };

  const downloadAll = async () => {
    if (downloadingAll || tracks.length === 0) return;
    setDownloadingAll(true);
    try {
      for (const track of tracks) {
        const a = document.createElement('a');
        a.href = track.url;
        a.download = `${track.storyId}-${track.variant}.mp3`;
        document.body.appendChild(a);
        a.click();
        a.remove();
        // Give the browser a beat between downloads so none get dropped.
        await new Promise((resolve) => setTimeout(resolve, 500));
      }
    } finally {
      setDownloadingAll(false);
    }
  };

  return (
    <div className="mx-auto max-w-2xl pt-2 pb-4">
      <h1 className="text-2xl font-bold text-stone-900">{t.listenTitle}</h1>

      {/* Now playing + big car controls */}
      <section className="mt-6 rounded-2xl bg-stone-900 px-6 py-8 text-center text-white">
        {current && (
          <p className="text-xs font-medium tracking-wide text-stone-400 uppercase">
            {t.nowPlaying}
          </p>
        )}
        <p className="mt-1 min-h-14 text-lg leading-snug font-semibold">
          {current ? current.title : tracks.length > 0 ? t.playAll : ' '}
        </p>
        {current && <p className="mt-1 text-sm text-stone-400">{current.variant}</p>}
        <div className="mt-6 flex items-center justify-center gap-6">
          <button
            type="button"
            onClick={prev}
            disabled={queue.length === 0}
            aria-label={t.prevTrack}
            className="flex h-16 w-16 items-center justify-center rounded-full text-white active:bg-stone-700 disabled:opacity-30"
          >
            <svg viewBox="0 0 24 24" className="h-9 w-9" fill="currentColor" aria-hidden="true">
              <path d="M18 18L9.5 12 18 6v12zM6 6h2v12H6z" />
            </svg>
          </button>
          <button
            type="button"
            onClick={handleBigToggle}
            disabled={tracks.length === 0}
            aria-label={playing ? t.pause : t.play}
            className="flex h-24 w-24 items-center justify-center rounded-full bg-white text-stone-900 shadow-lg active:scale-95 disabled:opacity-30"
          >
            {playing ? (
              <svg viewBox="0 0 24 24" className="h-12 w-12" fill="currentColor" aria-hidden="true">
                <path d="M6 5h4v14H6zM14 5h4v14h-4z" />
              </svg>
            ) : (
              <svg viewBox="0 0 24 24" className="ml-1 h-12 w-12" fill="currentColor" aria-hidden="true">
                <path d="M8 5v14l11-7z" />
              </svg>
            )}
          </button>
          <button
            type="button"
            onClick={next}
            disabled={queue.length === 0}
            aria-label={t.nextTrack}
            className="flex h-16 w-16 items-center justify-center rounded-full text-white active:bg-stone-700 disabled:opacity-30"
          >
            <svg viewBox="0 0 24 24" className="h-9 w-9" fill="currentColor" aria-hidden="true">
              <path d="M6 18l8.5-6L6 6v12zM16 6h2v12h-2z" />
            </svg>
          </button>
        </div>
      </section>

      {/* Queue */}
      <section className="mt-8">
        <div className="flex items-center justify-between gap-3">
          <h2 className="text-lg font-semibold text-stone-900">{t.queueTitle}</h2>
          {tracks.length > 0 && (
            <button
              type="button"
              onClick={() => {
                void downloadAll();
              }}
              disabled={downloadingAll}
              className="rounded-full border border-stone-300 px-4 py-2.5 text-sm font-medium text-stone-700 active:bg-stone-100 disabled:opacity-50"
            >
              {downloadingAll ? t.downloadingAll : t.downloadAll}
            </button>
          )}
        </div>

        {isLoading && <p className="mt-6 text-stone-500">{t.loading}</p>}
        {isError && <p className="mt-6 text-red-700">{t.error}</p>}
        {!isLoading && !isError && rows.length === 0 && (
          <p className="mt-6 text-stone-500">{t.listenEmpty}</p>
        )}

        <ul className="mt-4 divide-y divide-stone-200">
          {rows.map((row, i) => {
            const isCurrent =
              current !== undefined &&
              current.storyId === row.track.storyId &&
              current.variant === row.track.variant;
            return (
              <li
                key={`${row.track.storyId}/${row.track.variant}`}
                className="flex items-stretch gap-2"
              >
                <button
                  type="button"
                  onClick={() => play(tracks, i)}
                  className={`flex min-h-16 min-w-0 flex-1 items-center gap-3 py-3 text-left active:bg-stone-100 ${
                    isCurrent ? 'text-amber-700' : 'text-stone-900'
                  }`}
                >
                  <span className="flex w-8 shrink-0 items-center justify-center">
                    {isCurrent && playing ? (
                      <svg
                        viewBox="0 0 24 24"
                        className="h-6 w-6"
                        fill="currentColor"
                        aria-hidden="true"
                      >
                        <path d="M4 10h3v8H4zM10.5 4h3v14h-3zM17 8h3v10h-3z" />
                      </svg>
                    ) : (
                      <span className={`text-sm ${isCurrent ? 'font-semibold' : 'text-stone-400'}`}>
                        {i + 1}
                      </span>
                    )}
                  </span>
                  <span className="min-w-0">
                    <span className={`block truncate ${isCurrent ? 'font-semibold' : 'font-medium'}`}>
                      {row.track.title}
                    </span>
                    <span className="block truncate text-sm text-stone-500">
                      {row.universe} · {row.pov}
                    </span>
                  </span>
                </button>
                <a
                  href={row.track.url}
                  download={`${row.track.storyId}-${row.track.variant}.mp3`}
                  aria-label={t.download}
                  className="flex w-14 shrink-0 items-center justify-center text-stone-500 active:bg-stone-100"
                >
                  <svg viewBox="0 0 24 24" className="h-6 w-6" fill="currentColor" aria-hidden="true">
                    <path d="M19 9h-4V3H9v6H5l7 7 7-7zM5 18v2h14v-2H5z" />
                  </svg>
                </a>
              </li>
            );
          })}
        </ul>
      </section>
    </div>
  );
}
