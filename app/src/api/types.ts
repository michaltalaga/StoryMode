// Wire types for the Session Stories API.
// Shapes verified against src/SessionStories.Api/Program.cs and
// src/SessionStories.Core (System.Text.Json Web defaults: camelCase properties,
// enums via JsonStringEnumConverter(camelCase), nulls serialized — not omitted).

// ---------------------------------------------------------------------------
// Enums (C# JobType / JobState / pipeline stage, camelCase string form)
// ---------------------------------------------------------------------------

export type JobType = 'transcribe' | 'generate' | 'regenScene' | 'verify' | 'renderTts';

export type JobState = 'queued' | 'running' | 'succeeded' | 'failed' | 'cancelled';

/** Furthest artifact present for a variant. */
export type PipelineStage = 'spec' | 'outline' | 'draft' | 'audio';

// ---------------------------------------------------------------------------
// Stories
// ---------------------------------------------------------------------------

export interface VariantSummary {
  variant: string;
  pov: string;
  language: string;
  stage: PipelineStage;
}

export interface StorySummary {
  id: string;
  title: string;
  universe: string;
  variants: VariantSummary[];
}

/** GET /api/stories/{sid} — artifact presence per variant. */
export interface VariantArtifacts {
  session: boolean;
  outline: boolean;
  draft: boolean;
  verify: boolean;
  pendingFacts: boolean;
  audio: boolean;
}

export interface VariantDetail extends VariantSummary {
  artifacts: VariantArtifacts;
}

/** GET /api/stories/{sid}. */
export interface StoryDetail {
  id: string;
  title: string;
  universe: string;
  recollections: string[];
  variants: VariantDetail[];
}

// ---------------------------------------------------------------------------
// Draft / verify
// ---------------------------------------------------------------------------

export interface VerifyFlag {
  rule: string;
  detail: string;
}

export interface SceneDto {
  sceneId: string;
  title: string;
  text: string;
  flags: VerifyFlag[];
}

/** GET /api/stories/{sid}/verify/{variant} — scope is 'sN' or 'global'. */
export interface VerifyItem {
  scope: string;
  rule: string;
  detail: string;
}

// ---------------------------------------------------------------------------
// Jobs
// ---------------------------------------------------------------------------

export interface JobDto {
  id: string;
  type: JobType;
  storyId: string;
  variant: string;
  sceneId?: string | null;
  state: JobState;
  stage: string;
  createdUtc: string;
  startedUtc?: string | null;
  finishedUtc?: string | null;
  costUsd?: number | null;
  error?: string | null;
  /** Last ~100 log lines. Only GET /api/jobs/{id} carries it; empty [] on list items. */
  logTail: string[];
  /** Recollection file (transcribe jobs). Detail route only. */
  file?: string | null;
  /** Seconds since start (or total when finished). Detail route only. */
  elapsedSeconds?: number | null;
}

// ---------------------------------------------------------------------------
// Facts / universes
// ---------------------------------------------------------------------------

/** Per-story pending bible fact (Core PendingFact record). */
export interface PendingFact {
  factId: string;
  text: string;
  sceneId?: string | null;
}

/** GET /api/universes/{uid}/pending-facts — aggregate across the universe's stories. */
export interface UniversePendingFact {
  storyId: string;
  variant: string;
  lineId: string;
  text: string;
  sceneId?: string | null;
}

/** GET /api/universes. */
export interface UniverseListItem {
  id: string;
  title: string;
}

// ---------------------------------------------------------------------------
// Status
// ---------------------------------------------------------------------------

export interface StatusDto {
  claude: { found: boolean; version?: string | null };
  gpu?: string | null;
  models: { whisper: boolean; chatterbox: boolean };
  libraryRoot: string;
}

// ---------------------------------------------------------------------------
// session.<variant>.json
// ---------------------------------------------------------------------------

/**
 * Raw session file content. Hand-editable on disk; unknown fields must be
 * preserved, so the SPA always round-trips the full object and only the
 * typed view below is used for reading/binding known fields.
 */
export type SessionJson = Record<string, unknown>;

export type BeatFlag = 'given' | 'invent';

export interface SessionBeat {
  id: string;
  flag: BeatFlag;
  text: string;
}

export interface SessionCastMember {
  /** Reference into the universe's characters.md recurring cast. */
  ref?: string;
  /** One-off character: inline name + description instead of a ref. */
  name?: string;
  about?: string;
}

export interface SessionSources {
  primary?: string[];
  background?: string[];
}

/**
 * Typed view of the known skeleton fields (see FileStoryStore.CreateStory and
 * library/stories/2026-08-08-marrowfield/session.michal.json). All optional:
 * files are hand-editable and older files may miss fields.
 */
export interface SessionView {
  schema?: number;
  inputType?: string;
  universe?: string;
  language?: string;
  title?: string;
  pov?: string;
  tone?: string;
  targetMinutes?: number | null;
  voice?: string;
  voiceOverrides?: Record<string, number>;
  sources?: SessionSources;
  cast?: SessionCastMember[];
  stakes?: string;
  beats?: SessionBeat[];
  outcome?: string;
  skip?: string[];
}

/** Typed read-only lens over a raw session object (no copy, no validation). */
export function viewSession(session: SessionJson): SessionView {
  return session as SessionView;
}
