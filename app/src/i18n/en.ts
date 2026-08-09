/**
 * English UI copy — the source of truth for the key set.
 *
 * `Strings` is `typeof en`, and pl.ts declares its object as `const pl: Strings`,
 * so a key that is missing from (or misspelled in) either dictionary is a
 * compile error. Keep both files in the same order and add keys to both.
 */
export const en = {
  // --- App / navigation ---
  /** Product name — deliberately identical in both dictionaries (and in index.html). */
  appName: 'Story Mode',
  /** Browser tab title; index.html ships the same name until React boots. */
  appTitle: 'Story Mode',
  navHome: 'Stories',
  navListen: 'Listen',
  navSettings: 'Settings',

  /** BCP-47 tag for Intl formatting (dates, currency). */
  locale: 'en-US',

  // --- General ---
  save: 'Save',
  cancel: 'Cancel',
  delete: 'Delete',
  close: 'Close',
  back: 'Back',
  edit: 'Edit',
  create: 'Create',
  confirm: 'Confirm',
  yes: 'Yes',
  no: 'No',
  loading: 'Loading…',
  saving: 'Saving…',
  saved: 'Saved',
  error: 'Something went wrong',
  errorNetwork: 'No connection to the server',
  errorNotFound: 'Not found',
  retry: 'Try again',
  optional: '(optional)',

  // --- Story list ---
  storiesTitle: 'Stories',
  storiesEmpty: 'No stories yet. Create the first one!',
  newStory: 'New story',
  universeLabel: 'World',
  variantsLabel: 'Versions',
  stageSpec: 'Setup',
  stageOutline: 'Scene plan',
  stageDraft: 'Text',
  stageAudio: 'Audio',
  newStoryTitleHeading: 'New story',
  newStorySlugLabel: 'Folder identifier',
  newStorySlugPlaceholder: 'e.g. 2026-08-08-the-cave',
  newStoryTitleLabel: 'Title',
  newStoryTitlePlaceholder: 'e.g. The Marrowfield Gem',
  newStoryUniverseLabel: 'World',
  newStoryVariantLabel: 'First version (whose story is it?)',
  newStoryVariantPlaceholder: 'e.g. michal',
  newStoryLanguageLabel: 'Language',
  newStoryCreated: 'Story created',

  // --- Story detail ---
  storyDetailTitle: 'Story',
  recollectionsTitle: 'Recollections',
  recollectionsEmpty: 'No recollections yet. Record what you remember from the session.',
  recollectionsShared: 'Recollections are shared by every version.',
  addRecollection: 'Add a recollection',
  variantLabel: 'Version',
  povLabel: 'Character',
  languageLabel: 'Language',
  artifactSession: 'Setup',
  artifactOutline: 'Scene plan',
  artifactDraft: 'Text',
  artifactVerify: 'Verification',
  artifactPendingFacts: 'New facts',
  artifactAudio: 'Audio',
  goCapture: 'Record a recollection',
  goBuilder: 'Story setup',
  goDraft: 'Read and fix up',
  goProgress: 'Progress',
  goListen: 'Listen',
  startGeneration: 'Generate the story',
  startVerify: 'Check consistency',
  startRenderTts: 'Record the narration',
  addVariant: 'Add a version',

  // --- Capture ---
  captureTitle: 'Tell us what you remember',
  captureHint:
    'Speak freely, in your own words. It does not have to be in order — what matters is the good bits.',
  capturePersonLabel: 'Who is telling it?',
  capturePersonPlaceholder: 'e.g. michal, kuba…',
  captureStart: 'Record',
  captureStop: 'Stop',
  captureRecording: 'Recording…',
  capturePickFile: 'Choose a file',
  captureUpload: 'Send',
  captureUploading: 'Sending…',
  captureUploadDone: 'Sent! The recording will be transcribed automatically.',
  captureTextMode: 'Rather write it?',
  captureTextPlaceholder: 'Write your recollection here…',
  captureExists: 'This person already has a recollection — recollections cannot be overwritten.',
  captureMicDenied: 'No access to the microphone. Check the permissions.',
  captureTranscribing: 'Transcribing the recording…',
  captureListenBack: 'Listen back before sending',

  // --- Session builder ---
  builderTitle: 'Story setup',
  builderGivenLabel: 'REALLY HAPPENED',
  builderInventLabel: 'MAKE IT UP',
  builderGivenHint: 'This is what happened — the course of events must not change.',
  builderInventHint: 'Room for imagination — let the story find its own shape.',
  builderBeatsLabel: 'Events (in order)',
  builderBeatPlaceholder: 'What happened?',
  builderAddBeat: 'Add an event',
  builderRemoveBeat: 'Remove event',
  builderCastLabel: 'Characters',
  builderStakesLabel: 'Stakes',
  builderStakesPlaceholder: 'What was at stake?',
  builderPovLabel: 'Through whose eyes?',
  builderToneLabel: 'Story tone',
  builderVoiceLabel: 'Narrator voice',
  builderTargetMinutesLabel: 'Target length (minutes)',
  builderSkipLabel: 'What to skip',
  builderSkipPlaceholder: 'e.g. arguments about the rules, breaks…',
  builderPresetLabel: 'Start from a template',
  builderPresetBattleReport: 'Battle report — all of it really happened',
  builderPresetRpgSession: 'RPG session — some real, some made up',
  builderPresetPremise: 'Just the premise — make up the rest',
  builderSaveAndGenerate: 'Save and generate',
  builderConflict:
    'The setup file changed in the meantime. Load the current version and make your changes again.',

  // --- Progress / jobs ---
  progressTitle: 'Progress',
  jobsTitle: 'Jobs',
  jobsEmpty: 'No jobs',
  jobQueued: 'Queued',
  jobRunning: 'In progress',
  jobSucceeded: 'Done',
  jobFailed: 'Failed',
  jobCancelled: 'Stopped',
  jobCancel: 'Stop',
  jobCost: 'Cost',
  jobElapsed: 'Time',
  jobStage: 'Stage',
  jobLog: 'Log',
  jobTypeTranscribe: 'Transcribing the recording',
  jobTypeGenerate: 'Generating the story',
  jobTypeRegenScene: 'New version of the scene',
  jobTypeVerify: 'Checking consistency',
  jobTypeRenderTts: 'Recording the narration',
  jobTypePreviewVoice: 'Voice preview',
  jobEnqueued: 'Added to the queue',
  jobSerialNote: 'Jobs run one at a time, in order.',

  // --- Draft review ---
  draftTitle: 'Draft',
  draftEmpty: 'No text yet. Generate the story first.',
  sceneLabel: 'Scene',
  sceneEdit: 'Edit scene',
  sceneSave: 'Save scene',
  sceneRegen: 'Generate again',
  sceneRestore: 'Restore the previous version',
  sceneFeedbackLabel: 'A hint for the new version',
  sceneFeedbackPlaceholder: 'What should change in this scene? (optional)',
  sceneRegenQueued: 'The scene went into the queue',
  verifyFlagsTitle: 'Verification notes',
  verifyClean: 'Nothing to flag',
  draftConflictTitle: 'The text changed',
  draftConflictBody:
    'This fragment was changed in the meantime (by the generator or on another device). You can load the new version or overwrite it with yours.',
  draftConflictReload: 'Load the new version',
  draftConflictOverwrite: 'Overwrite with mine',
  draftSaved: 'Scene saved',

  // --- Listening / player ---
  listenTitle: 'Listening',
  listenEmpty: 'No recordings yet. Generate a story and record the narration.',
  playAll: 'Play everything',
  play: 'Play',
  pause: 'Pause',
  nextTrack: 'Next',
  prevTrack: 'Previous',
  nowPlaying: 'Now playing',
  queueTitle: 'Queue',
  download: 'Download',

  // --- Universe ---
  universeTitle: 'World',
  tabConstraints: 'World rules',
  tabBible: 'Chronicle',
  tabCharacters: 'Characters',
  tabTones: 'Tones',
  universeFileSaved: 'File saved',
  universeFileConflict:
    'The file changed in the meantime. Load the current version and make your changes again.',
  pendingFactsTitle: 'New facts for the chronicle',
  pendingFactsEmpty: 'No new facts to approve.',
  pendingFactsHint:
    'Facts from finished stories — approved ones go into the world chronicle for good.',
  approveSelected: 'Approve selected',
  factsApproved: 'Added to the chronicle',
  factSource: 'Source',

  // --- Settings / system status ---
  settingsTitle: 'Settings',
  /** Page subtitle: what this page actually holds. */
  settingsSubtitle: 'Language, voices and system status',
  statusTitle: 'System status',
  statusClaude: 'Claude CLI',
  statusGpu: 'Graphics card',
  statusModels: 'Models',
  statusWhisper: 'Whisper (transcription)',
  statusChatterbox: 'Chatterbox (narration)',
  statusLibraryRoot: 'Library folder',
  statusFound: 'Available',
  statusMissing: 'Missing',
  statusVersion: 'Version',

  // --- General (second pass: former inline literals) ---
  add: 'Add',
  preview: 'Preview',
  refresh: 'Refresh',
  generate: 'Generate',
  creating: 'Creating…',
  goBack: 'Back',
  backToStory: 'Back to the story',
  backToList: 'Back to the list',
  unsavedChanges: 'Unsaved changes',
  untitled: 'Untitled',
  selectAll: 'Select all',
  deselectAll: 'Deselect all',
  emptyFile: 'Empty file',
  languagePl: 'Polish',
  languageEn: 'English',
  discardConfirm: 'Discard unsaved changes?',
  moreActions: 'More actions',
  errorShort: 'error',
  titleLabel: 'Title',

  // --- HTTP client ---
  errorFileChangedOnDisk: 'The file changed on disk since it was read.',
  uploadNetworkError: 'Network error while uploading.',
  uploadAborted: 'Upload stopped.',

  // --- Listening ---
  downloadAll: 'Download all',
  downloadingAll: 'Downloading…',

  // --- Story list ---
  newStoryDescription: 'Creates the story folder and the first version.',
  newStoryValidationMissing: 'Enter a title and a version name.',
  newStorySlugInvalid: 'The slug may only contain lowercase letters, digits and hyphens.',
  newStoryNoUniverses: 'No worlds — add a universe folder to the library first.',
  universesEmptyOption: 'no worlds',
  storiesLoadError: 'Could not load the stories.',
  storiesEmptyHint: 'Start with “New story”, then record your recollections.',

  // --- Story detail ---
  storyNotFound: 'Story not found.',
  variantNameInvalid: 'Version name: lowercase letters, digits and hyphens.',
  variantExists: 'That version already exists.',
  cloneTitle: 'New version of the story',
  cloneDescription: 'Copies the settings of an existing version — recollections are shared.',
  cloneSourceLabel: 'Copy from version',
  cloneNameLabel: 'Name of the new version',
  clonePovLabel: 'POV (whose story this is)',
  clonePovPlaceholder: 'e.g. Kuba — the archer',
  createVariant: 'Create version',
  actionProgress: 'See progress',
  actionBuild: 'Build the session',
  actionCapture: 'Record recollections',
  actionReadDraft: 'Read the draft',
  sharedAcrossVariants: 'shared by every version',
  textNote: 'text note',
  transcriptChip: 'transcript',
  transcriptPendingChip: 'transcript…',
  povUnset: 'POV not set',
  linkSession: 'session',
  linkProgress: 'progress',
  linkDraft: 'draft',
  linkAudio: 'audio',
  /** Chip on a variant card: "working: <stage>". */
  jobWorkingPrefix: 'working',

  // --- Capture ---
  captureHeading: 'Recollections from the session',
  captureSharedHint: 'Shared by every version. Audio recordings transcribe themselves.',
  addFile: 'Add a file',
  captureNewPersonLabel: 'Someone new?',
  captureNewPersonPlaceholder: 'name, e.g. zosia',
  uploadedShort: 'sent',
  captureCollectedTitle: 'Already collected',
  captureEmptyList: 'Nothing here yet — record the first recollection above.',
  openText: 'open text',
  transcriptReloaded: 'The file changed in the meantime — the current version has been loaded.',
  transcriptRawHint: 'Raw transcript — the mess is part of the memory.',
  transcriptEditWarning:
    'Transcripts are deliberately left rough — that is how the child remembered it and that is how it should stay. Edit only where Whisper got the words wrong.',
  editAnyway: 'Edit anyway',

  // --- Session builder ---
  builderPresetGiven: 'Battle report',
  builderPresetInvent: 'Premise',
  builderPresetGivenTitle: 'Apply the “Battle report” template?',
  builderPresetGivenBody: 'Every beat will be marked “REALLY HAPPENED”.',
  builderPresetInventTitle: 'Apply the “Premise” template?',
  builderPresetInventBody: 'Every beat will be marked “MAKE IT UP”.',
  builderCastCustomTitle: 'Add your own character',
  builderCastName: 'Name',
  builderCastAbout: 'Who they are (briefly)',
  builderCastRemove: 'Remove character',
  builderPovEmpty: 'Tick characters in the cast to pick a POV.',
  builderOutcomeLabel: 'Ending',
  builderSkipRemove: 'Remove from the skip list',
  builderVoiceDefault: '(default from the catalog)',
  builderMoveUp: 'Move up',
  builderMoveDown: 'Move down',
  builderGenerateHint: 'Save your changes before generating',
  builderConflictTitle: 'File changed on disk — reload?',
  builderConflictBody:
    'session.json has changed since it was last loaded (a manual edit, perhaps). Reloading discards your local changes. If you keep editing, the next save will overwrite the version on disk.',
  builderConflictReload: 'Reload from the server',
  builderConflictKeep: 'Keep editing',
  builderLoadError: 'Could not load the session',
  builderSessionBroken: 'The session file is not a valid JSON object.',

  // --- Progress ---
  progressLoadingJobs: 'Loading jobs…',
  progressNoJobs: 'No jobs for this variant.',
  jobsLoadError: 'Could not load the job list.',
  jobQueuedNote: 'The job is waiting in the queue — jobs run one at a time.',
  jobCancelConfirm: 'Really stop this job?',
  jobCancelAction: 'Stop the job',
  jobFailedNote: 'The job ended with an error.',
  stageExtract: 'Fact extraction',
  stageOutlineStep: 'Scene skeleton',
  stageScenes: 'Scenes',
  stageVerify: 'Verification',
  stageBible: 'Facts for the chronicle',
  /** Scene counter under the "Scenes" step; {n} and {total} are substituted. */
  stageSceneOf: 'scene {n} of {total}',
  openBuilder: 'Open the builder',
  viewDraft: 'See the draft',
  reviewDraft: 'Review the draft',

  // --- Draft review ---
  draftReviewTitle: 'Draft review',
  draftLoading: 'Loading the draft…',
  draftEmptyForVariant: 'No draft for this variant yet.',
  sceneRestoreConfirm: 'Restore the previous version of this scene?',
  sceneRegenInProgress: 'A new version of the scene is on the way…',
  sceneRegenTitle: 'Generate the scene again',
  sceneRegenHint: 'You can add a hint — what to improve in the new version.',
  sceneRegenPlaceholder: 'E.g. fewer descriptions, more dialogue…',
  enqueueError: 'Could not queue the job.',
  renderTtsEnqueueError: 'Could not queue the render job.',
  verifyAgain: 'Verify again',
  verifyRunning: 'Verifying…',
  sceneRegenOwnNotesLabel: 'Your own notes (optional)',
  sceneRegenNothingSelectedHint: 'Tick some notes or write your own',
  regenerate: 'Regenerate',
  sceneRestoreShort: 'Restore previous',
  discardChanges: 'Discard changes',
  renderAudio: 'Render audio',
  conflictLoadFromDisk: 'Load the version from disk',
  conflictKeepMine: 'Keep mine and save again',
  /** Heading of the note handed to the generator when regenerating a scene. */
  feedbackNoteHeader: 'Apply the following verification notes:',
  /** Label before the free-text part of that note. */
  feedbackNoteExtra: 'Additionally',

  // --- Verification notes: rule labels (rule slug → plain language) ---
  verifyRuleGivenDrift: 'Changed facts — that is not how it went',
  verifyRuleRegister: 'Narrative style',
  verifyRuleAnachronism: 'Anachronism',
  verifyRuleNaming: 'Naming',
  verifyRuleHardRules: 'World rules',
  verifyRuleCanon: 'Contradicts the chronicle',
  verifyRuleTone: 'Story tone',
  draftConflictDiskTitle: 'The draft changed on disk',
  draftConflictDiskBody:
    'the draft file was changed in the meantime (by a regeneration or a manual edit, say). What should happen to your version?',

  // --- Universe ---
  universeConflictTitle: 'The file changed on disk',
  fontToggleAria: 'Typeface',
  fontSerif: 'Serif',
  fontMono: 'Mono',
  tabFacts: 'Facts',

  // --- Settings ---
  settingsClaudeMissingTitle: 'Claude CLI is missing',
  settingsClaudeMissingBody1: 'Could not find the',
  settingsClaudeMissingBody2:
    ' command. Story generation will not work. Install Claude Code and sign in — run',
  settingsClaudeMissingBody3: 'in a terminal and go through the subscription login.',
  settingsModelsHintPrefix: 'Missing models are fetched by',
  settingsBypassBody:
    'This app is only a view onto ordinary files — no database, no locks. You can edit any file by hand (or look at what the generator did), and the panel picks the change up straight away. If the panel ever gets in the way, edit the files directly:',
  settingsGeneratorHeading: 'Generator',
  settingsLibraryHeading: 'Library',
  settingsBypassTitle: 'The panel can be bypassed',
  /** Annotated library tree; rendered under the library root path. */
  settingsLibraryTree: `├─ stories/
│  └─ <story-date-slug>/
│     ├─ session.<variant>.json   ← spec: cast, beats, given|invent
│     ├─ recollections/           ← recordings and notes (immutable)
│     ├─ outline.<variant>.md
│     ├─ draft.<variant>.md       ← the story text
│     ├─ verify.<variant>.md
│     └─ audio/<variant>.mp3
├─ universes/
│  └─ <universe>/
│     ├─ constraints.md           ← world rules
│     ├─ bible.md                 ← canon facts (the chronicle)
│     └─ characters.md
├─ voices.json                    ← voice catalog (shared by every world)
├─ voices/                        ← reference wavs
└─ voice-previews/                ← rendered voice previews`,

  // --- Settings: language picker ---
  settingsLanguageHeading: 'Interface language',
  settingsLanguageHint: 'The choice is remembered in this browser. Story text is unaffected.',
  /** Endonyms — deliberately identical in both dictionaries. */
  languageNameEn: 'English',
  languageNamePl: 'Polski',

  // --- Settings: voices (global catalog) ---
  settingsVoicesHeading: 'Voices',
  settingsVoicesHint:
    'The voice catalog is shared by every world. Listen to a voice before you hand a story to it.',
  /** What the play button actually reads out. */
  voicesSampleHint: 'A sample reads one fixed sentence in the voice’s own language.',
  voicesEmpty: 'No voices defined',
  voicesEmptyHint: 'Add the first one with “Add voice”.',
  voicesLoadError: 'Could not load the voice list.',
  voicesDefaultBadge: 'default',
  voicesNoReferenceWav: 'reference recording missing',
  /** Plain-language capability, derived from what the engine can do (supportsCloning). */
  voicesCloned: 'cloned voice',
  voicesBuiltIn: 'built-in voice',
  voicesPlay: 'Play the sample',
  voicesStop: 'Stop',
  voicesPreparing: 'preparing…',
  voicesReadyToPlay: 'Ready — tap to play',
  voicesPreviewError: 'Could not prepare the sample.',
  voicesDetails: 'Details',
  voicesEngineLabel: 'Engine',
  voicesExaggerationLabel: 'Expressiveness (exaggeration)',
  voicesCfgLabel: 'Guidance (cfg)',
  voicesReferenceFileLabel: 'Reference recording',
  voicesRerecord: 'Record the sample again',
  voicesSetDefault: 'Set as default',
  voicesReplaceReference: 'Replace reference audio',
  /** The ⋯ button on a card, and the sheet it opens. {name} is the voice id. */
  voicesMenuOpen: 'More actions — {name}',
  voicesMenuTitle: 'Voice actions',
  voicesAdd: 'Add voice',
  voicesAddTitle: 'New voice',
  voicesAddDescription:
    'A name, an engine and at least one language. The reference recording can come along right away.',
  voicesEditTitle: 'Edit voice',
  voicesEditDescription: 'The name stays as it is — stories refer to the voice by it.',
  voicesNameLabel: 'Name',
  voicesNamePlaceholder: 'e.g. narrator-pl-babcia',
  voicesNameInvalid: 'Name: lowercase letters, digits and hyphens.',
  voicesNameTaken: 'A voice with that name already exists.',
  voicesEngineHint: 'Cloning engines copy a reference recording; the others have fixed voices.',
  voicesLanguagesLabel: 'Languages',
  voicesLanguagesRequired: 'Pick at least one language.',
  voicesKnobsHint: 'Only some engines use these — leave them empty if unsure.',
  voicesKnobInvalid: 'Expressiveness and guidance must be numbers.',
  voicesReferenceLabel: 'Reference recording (.wav)',
  voicesReferenceHint: 'The engine copies this voice. A new one replaces the sample rendered from the old one.',
  /** Shown instead of the upload field when the chosen engine has fixed voices. */
  voicesReferenceUnsupported: 'This engine has fixed trained voices — it ignores a reference recording.',
  voicesWavOnly: 'Only .wav files are accepted.',
  voicesDeleteTitle: 'Delete this voice?',
  /** {name} is the voice id. */
  voicesDeleteBody: 'Removes “{name}” from the catalog, along with its sample and cached data.',
  voicesDeleteWav: 'Delete the reference recording too',
  voicesSaveError: 'Could not save the change.',
  voicesAdvancedShow: 'Advanced: edit voices.json by hand',
  voicesAdvancedHide: 'Hide the file editor',
  voicesAdvancedHint:
    'The catalog is a plain file. Everything above edits it for you — this is only for exotic cases.',
  voicesInvalidJson: 'Invalid JSON — the file can still be saved',

  // --- Samples (temporary TTS shelf) ---
  samplesTitle: 'Audio samples (temporary)',
  samplesHint:
    'Comparisons of TTS engines and reference voices. This page disappears once an engine is chosen.',
  samplesLoadError: 'Could not load the sample list.',
}

/** Shape every dictionary must match, key for key. */
export type Strings = typeof en
