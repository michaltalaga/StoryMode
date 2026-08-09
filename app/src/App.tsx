import { Link, NavLink, Route, Routes, useNavigate } from 'react-router'
import { useJobs } from './api/queries'
import type { Strings } from './i18n'
import { useStrings } from './i18n'
import PlayerBar from './player/PlayerBar'
import Capture from './routes/Capture'
import DraftReview from './routes/DraftReview'
import Listen from './routes/Listen'
import Progress from './routes/Progress'
import SessionBuilder from './routes/SessionBuilder'
import Settings from './routes/Settings'
// TYMCZASOWE — patrz Samples.tsx
import Samples from './routes/Samples'
import StoryDetail from './routes/StoryDetail'
import StoryList from './routes/StoryList'
import Universe from './routes/Universe'

function jobTypeLabels(t: Strings): Record<string, string> {
  return {
    transcribe: t.jobTypeTranscribe,
    generate: t.jobTypeGenerate,
    regenScene: t.jobTypeRegenScene,
    verify: t.jobTypeVerify,
    renderTts: t.jobTypeRenderTts,
    previewVoice: t.jobTypePreviewVoice,
  }
}

/**
 * Global chip in the header: shows the running (or first queued) job with a
 * spinning dot and stage text; tapping it opens that job's progress page.
 */
function RunningJobChip() {
  const t = useStrings()
  const navigate = useNavigate()
  const { data: jobs } = useJobs()
  const active =
    jobs?.find((j) => j.state === 'running') ??
    jobs?.find((j) => j.state === 'queued')
  if (!active) return null

  const label =
    active.state === 'queued'
      ? t.jobQueued
      : active.stage || jobTypeLabels(t)[active.type] || active.type
  const target = active.variant
    ? `/stories/${active.storyId}/v/${active.variant}/progress`
    : `/stories/${active.storyId}`

  return (
    <button
      type="button"
      onClick={() => navigate(target)}
      className="flex min-w-0 items-center gap-2 rounded-full border border-amber-300 bg-amber-50 px-3 py-1.5 text-xs font-medium text-amber-900 transition-colors hover:bg-amber-100"
    >
      <span
        aria-hidden
        className={
          active.state === 'running'
            ? 'h-3 w-3 shrink-0 animate-spin rounded-full border-2 border-amber-600 border-t-transparent'
            : 'h-2.5 w-2.5 shrink-0 animate-pulse rounded-full bg-amber-500'
        }
      />
      <span className="max-w-40 truncate sm:max-w-64">{label}</span>
    </button>
  )
}

function HomeIcon() {
  return (
    <svg
      viewBox="0 0 24 24"
      className="h-6 w-6"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.8"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden
    >
      <path d="M3 10.5 12 3l9 7.5" />
      <path d="M5 9.5V21h14V9.5" />
    </svg>
  )
}

function HeadphonesIcon() {
  return (
    <svg
      viewBox="0 0 24 24"
      className="h-6 w-6"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.8"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden
    >
      <path d="M4 14a8 8 0 0 1 16 0" />
      <rect x="3" y="14" width="4" height="6" rx="1.5" />
      <rect x="17" y="14" width="4" height="6" rx="1.5" />
    </svg>
  )
}

function SlidersIcon() {
  return (
    <svg
      viewBox="0 0 24 24"
      className="h-6 w-6"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.8"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden
    >
      <path d="M3 6h11m4 0h3M3 12h5m4 0h9M3 18h13m4 0h1" />
      <path d="M14 4v4M8 10v4M16 16v4" />
    </svg>
  )
}

function desktopLinkClass({ isActive }: { isActive: boolean }) {
  return `rounded-md px-3 py-1.5 text-sm font-medium transition-colors ${
    isActive
      ? 'bg-amber-100 text-amber-900'
      : 'text-stone-600 hover:bg-stone-200 hover:text-stone-900'
  }`
}

function tabLinkClass({ isActive }: { isActive: boolean }) {
  return `flex flex-col items-center justify-center gap-0.5 py-2 text-[11px] font-medium ${
    isActive ? 'text-amber-700' : 'text-stone-500'
  }`
}

export default function App() {
  const t = useStrings()

  return (
    <div className="min-h-dvh">
      <header className="sticky top-0 z-40 border-b border-stone-200 bg-stone-50/90 backdrop-blur">
        <div className="mx-auto flex h-14 max-w-5xl items-center gap-3 px-4">
          <Link
            to="/"
            className="shrink-0 font-serif text-xl font-semibold tracking-tight text-stone-900"
          >
            {t.appName}
          </Link>
          <div className="min-w-0 flex-1">
            <RunningJobChip />
          </div>
          <nav className="hidden shrink-0 items-center gap-1 sm:flex">
            <NavLink to="/" end className={desktopLinkClass}>
              {t.navHome}
            </NavLink>
            <NavLink to="/listen" className={desktopLinkClass}>
              {t.navListen}
            </NavLink>
            <NavLink to="/settings" className={desktopLinkClass}>
              {t.navSettings}
            </NavLink>
          </nav>
        </div>
      </header>

      <main className="mx-auto w-full max-w-5xl px-4 pt-4 pb-44 sm:pb-28">
        <Routes>
          <Route path="/" element={<StoryList />} />
          <Route path="/stories/:id" element={<StoryDetail />} />
          <Route path="/stories/:id/capture" element={<Capture />} />
          <Route
            path="/stories/:id/v/:variant/builder"
            element={<SessionBuilder />}
          />
          <Route
            path="/stories/:id/v/:variant/progress"
            element={<Progress />}
          />
          <Route path="/stories/:id/v/:variant/draft" element={<DraftReview />} />
          <Route path="/listen" element={<Listen />} />
          <Route path="/universes/:id" element={<Universe />} />
          <Route path="/settings" element={<Settings />} />
          <Route path="/samples" element={<Samples />} />
        </Routes>
      </main>

      {/* Player slot: above the tab bar on phones, flush bottom on desktop. */}
      <div className="fixed inset-x-0 bottom-[calc(3.5rem+env(safe-area-inset-bottom))] z-30 sm:bottom-0">
        <PlayerBar />
      </div>

      {/* Phone-only bottom tab bar; desktop uses the header links. */}
      <nav className="fixed inset-x-0 bottom-0 z-40 border-t border-stone-200 bg-stone-50/95 pb-[env(safe-area-inset-bottom)] backdrop-blur sm:hidden">
        <div className="grid h-14 grid-cols-3">
          <NavLink to="/" end className={tabLinkClass}>
            <HomeIcon />
            <span>{t.navHome}</span>
          </NavLink>
          <NavLink to="/listen" className={tabLinkClass}>
            <HeadphonesIcon />
            <span>{t.navListen}</span>
          </NavLink>
          <NavLink to="/settings" className={tabLinkClass}>
            <SlidersIcon />
            <span>{t.navSettings}</span>
          </NavLink>
        </div>
      </nav>
    </div>
  )
}
