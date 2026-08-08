// Settings page (agent 7): environment status from GET /api/status plus the
// files-on-disk "how to bypass the panel" note (HANDOVER.md philosophy).
import type { ReactNode } from 'react'
import { useStatus } from '../api/queries'
import { strings } from '../strings'

function Presence({ ok, okLabel, missingLabel }: { ok: boolean; okLabel?: string; missingLabel?: string }) {
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

export default function Settings() {
  const query = useStatus()
  const status = query.data

  return (
    <main className="mx-auto w-full max-w-2xl px-4 pb-24 pt-6 sm:px-6">
      <header className="mb-6">
        <h1 className="text-2xl font-semibold text-stone-900">{strings.settingsTitle}</h1>
        <p className="text-sm text-stone-500">{strings.statusTitle}</p>
      </header>

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
{`${status.libraryRoot}
├─ stories/
│  └─ <data-slug historii>/
│     ├─ session.<wariant>.json   ← spec: obsada, beaty, given|invent
│     ├─ recollections/           ← nagrania i notatki (niezmienne)
│     ├─ outline.<wariant>.md
│     ├─ draft.<wariant>.md       ← tekst opowieści
│     ├─ verify.<wariant>.md
│     └─ audio/<wariant>.mp3
└─ universes/
   └─ <uniwersum>/
      ├─ constraints.md           ← zasady świata
      ├─ bible.md                 ← fakty kanoniczne (kronika)
      ├─ characters.md
      └─ voices.json`}
              </pre>
            </div>
          </section>
        </div>
      )}
    </main>
  )
}
