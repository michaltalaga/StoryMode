/**
 * Polish UI copy. Values moved over verbatim from the former src/strings.ts.
 *
 * Typed as `Strings` (= `typeof en`), so a missing or misspelled key here is a
 * compile error. Keep the key order in step with en.ts.
 */
import type { Strings } from './en'

export const pl: Strings = {
  // --- App / navigation ---
  /** Product name — deliberately identical in both dictionaries (and in index.html). */
  appName: 'Story Mode',
  /** Browser tab title; index.html ships the same name until React boots. */
  appTitle: 'Story Mode',
  navHome: 'Historie',
  navListen: 'Słuchaj',
  navSettings: 'Ustawienia',

  /** BCP-47 tag for Intl formatting (dates, currency). */
  locale: 'pl-PL',

  // --- General ---
  save: 'Zapisz',
  cancel: 'Anuluj',
  delete: 'Usuń',
  close: 'Zamknij',
  back: 'Wstecz',
  edit: 'Edytuj',
  create: 'Utwórz',
  confirm: 'Potwierdź',
  yes: 'Tak',
  no: 'Nie',
  loading: 'Wczytywanie…',
  saving: 'Zapisywanie…',
  saved: 'Zapisano',
  error: 'Coś poszło nie tak',
  errorNetwork: 'Brak połączenia z serwerem',
  errorNotFound: 'Nie znaleziono',
  retry: 'Spróbuj ponownie',
  optional: '(opcjonalnie)',

  // --- Story list ---
  storiesTitle: 'Historie',
  storiesEmpty: 'Nie ma jeszcze żadnych historii. Stwórzcie pierwszą!',
  newStory: 'Nowa historia',
  universeLabel: 'Świat',
  variantsLabel: 'Wersje',
  stageSpec: 'Założenia',
  stageOutline: 'Plan scen',
  stageDraft: 'Tekst',
  stageAudio: 'Audio',
  newStoryTitleHeading: 'Nowa historia',
  newStorySlugLabel: 'Identyfikator folderu',
  newStorySlugPlaceholder: 'np. 2026-08-08-jaskinia',
  newStoryTitleLabel: 'Tytuł',
  newStoryTitlePlaceholder: 'np. Klejnot z Marrowfield',
  newStoryUniverseLabel: 'Świat',
  newStoryVariantLabel: 'Pierwsza wersja (czyja opowieść?)',
  newStoryVariantPlaceholder: 'np. michal',
  newStoryLanguageLabel: 'Język',
  newStoryCreated: 'Historia utworzona',

  // --- Story detail ---
  storyDetailTitle: 'Historia',
  recollectionsTitle: 'Wspomnienia',
  recollectionsEmpty: 'Nie ma jeszcze wspomnień. Nagrajcie, co pamiętacie z sesji.',
  recollectionsShared: 'Wspomnienia są wspólne dla wszystkich wersji.',
  addRecollection: 'Dodaj wspomnienie',
  variantLabel: 'Wersja',
  povLabel: 'Bohater',
  languageLabel: 'Język',
  artifactSession: 'Założenia',
  artifactOutline: 'Plan scen',
  artifactDraft: 'Tekst',
  artifactVerify: 'Weryfikacja',
  artifactPendingFacts: 'Nowe fakty',
  artifactAudio: 'Audio',
  goCapture: 'Nagraj wspomnienie',
  goBuilder: 'Ustawienia opowieści',
  goDraft: 'Czytaj i poprawiaj',
  goProgress: 'Postęp',
  goListen: 'Słuchaj',
  startGeneration: 'Generuj historię',
  startVerify: 'Sprawdź zgodność',
  startRenderTts: 'Nagraj lektora',
  addVariant: 'Dodaj wersję',

  // --- Capture ---
  captureTitle: 'Opowiedz, co pamiętasz',
  captureHint:
    'Mów swobodnie, po swojemu. Nie musi być po kolei — najważniejsze jest to, co było najciekawsze.',
  capturePersonLabel: 'Kto opowiada?',
  capturePersonPlaceholder: 'np. michal, kuba…',
  captureStart: 'Nagrywaj',
  captureStop: 'Zatrzymaj',
  captureRecording: 'Nagrywanie…',
  capturePickFile: 'Wybierz plik',
  captureUpload: 'Wyślij',
  captureUploading: 'Wysyłanie…',
  captureUploadDone: 'Wysłano! Nagranie zostanie spisane automatycznie.',
  captureTextMode: 'Wolisz napisać?',
  captureTextPlaceholder: 'Zapisz tu swoje wspomnienie…',
  captureExists: 'Wspomnienie tej osoby już istnieje — wspomnień nie można nadpisywać.',
  captureMicDenied: 'Brak dostępu do mikrofonu. Sprawdź uprawnienia.',
  captureTranscribing: 'Spisywanie nagrania…',
  captureListenBack: 'Odsłuchaj przed wysłaniem',

  // --- Session builder ---
  builderTitle: 'Ustawienia opowieści',
  builderGivenLabel: 'TAK BYŁO',
  builderInventLabel: 'WYMYŚL',
  builderGivenHint: 'Tak się wydarzyło — przebiegu nie wolno zmieniać.',
  builderInventHint: 'Pole do fantazji — niech historia sama się ułoży.',
  builderBeatsLabel: 'Wydarzenia (po kolei)',
  builderBeatPlaceholder: 'Co się wydarzyło?',
  builderAddBeat: 'Dodaj wydarzenie',
  builderRemoveBeat: 'Usuń wydarzenie',
  builderCastLabel: 'Postacie',
  builderStakesLabel: 'Stawka',
  builderStakesPlaceholder: 'O co toczyła się gra?',
  builderPovLabel: 'Czyimi oczami?',
  builderToneLabel: 'Ton opowieści',
  builderVoiceLabel: 'Głos lektora',
  builderTargetMinutesLabel: 'Docelowa długość (minuty)',
  builderSkipLabel: 'Co pominąć',
  builderSkipPlaceholder: 'np. kłótnie o zasady, przerwy…',
  builderPresetLabel: 'Zacznij od szablonu',
  builderPresetBattleReport: 'Raport z bitwy — wszystko tak było',
  builderPresetRpgSession: 'Sesja RPG — trochę było, trochę wymyśl',
  builderPresetPremise: 'Sam pomysł — wymyśl resztę',
  builderSaveAndGenerate: 'Zapisz i generuj',
  builderConflict:
    'Plik założeń zmienił się w międzyczasie. Wczytaj aktualną wersję i nanieś zmiany jeszcze raz.',

  // --- Progress / jobs ---
  progressTitle: 'Postęp',
  jobsTitle: 'Zadania',
  jobsEmpty: 'Brak zadań',
  jobQueued: 'W kolejce',
  jobRunning: 'W toku',
  jobSucceeded: 'Gotowe',
  jobFailed: 'Nie udało się',
  jobCancelled: 'Przerwano',
  jobCancel: 'Przerwij',
  jobCost: 'Koszt',
  jobElapsed: 'Czas',
  jobStage: 'Etap',
  jobLog: 'Dziennik',
  jobTypeTranscribe: 'Spisywanie nagrania',
  jobTypeGenerate: 'Generowanie historii',
  jobTypeRegenScene: 'Nowa wersja sceny',
  jobTypeVerify: 'Sprawdzanie zgodności',
  jobTypeRenderTts: 'Nagrywanie lektora',
  jobTypePreviewVoice: 'Próbka głosu',
  jobEnqueued: 'Dodano do kolejki',
  jobSerialNote: 'Zadania wykonują się po kolei, jedno naraz.',

  // --- Draft review ---
  draftTitle: 'Wersja robocza',
  draftEmpty: 'Nie ma jeszcze tekstu. Najpierw wygeneruj historię.',
  sceneLabel: 'Scena',
  sceneEdit: 'Edytuj scenę',
  sceneSave: 'Zapisz scenę',
  sceneRegen: 'Wygeneruj na nowo',
  sceneRestore: 'Przywróć poprzednią wersję',
  sceneFeedbackLabel: 'Wskazówka dla nowej wersji',
  sceneFeedbackPlaceholder: 'Co poprawić w tej scenie? (opcjonalnie)',
  sceneRegenQueued: 'Scena trafiła do kolejki',
  verifyFlagsTitle: 'Uwagi weryfikacji',
  verifyClean: 'Bez zastrzeżeń',
  draftConflictTitle: 'Tekst się zmienił',
  draftConflictBody:
    'Ten fragment został w międzyczasie zmieniony (przez generator albo na innym urządzeniu). Możesz wczytać nową wersję albo nadpisać ją swoją.',
  draftConflictReload: 'Wczytaj nową wersję',
  draftConflictOverwrite: 'Nadpisz moją wersją',
  draftSaved: 'Zapisano scenę',

  // --- Listening / player ---
  listenTitle: 'Słuchanie',
  listenEmpty: 'Nie ma jeszcze żadnych nagrań. Wygeneruj historię i nagraj lektora.',
  playAll: 'Odtwórz wszystko',
  play: 'Odtwórz',
  pause: 'Pauza',
  nextTrack: 'Następna',
  prevTrack: 'Poprzednia',
  nowPlaying: 'Teraz gra',
  queueTitle: 'Kolejka',
  download: 'Pobierz',

  // --- Universe ---
  universeTitle: 'Świat',
  tabConstraints: 'Zasady świata',
  tabBible: 'Kronika',
  tabCharacters: 'Postacie',
  tabTones: 'Tony',
  universeFileSaved: 'Zapisano plik',
  universeFileConflict:
    'Plik zmienił się w międzyczasie. Wczytaj aktualną wersję i nanieś zmiany jeszcze raz.',
  pendingFactsTitle: 'Nowe fakty do kroniki',
  pendingFactsEmpty: 'Brak nowych faktów do zatwierdzenia.',
  pendingFactsHint:
    'Fakty z ukończonych historii — zatwierdzone trafią na stałe do kroniki świata.',
  approveSelected: 'Zatwierdź zaznaczone',
  factsApproved: 'Dodano do kroniki',
  factSource: 'Źródło',

  // --- Settings / system status ---
  settingsTitle: 'Ustawienia',
  /** Page subtitle: what this page actually holds. */
  settingsSubtitle: 'Język, głosy i stan systemu',
  statusTitle: 'Stan systemu',
  statusClaude: 'Claude CLI',
  statusGpu: 'Karta graficzna',
  statusModels: 'Modele',
  /** Named by what they do, not what they are — the brand helps nobody decide anything. */
  statusWhisper: 'Model spisywania',
  statusChatterbox: 'Model lektora',
  statusLibraryRoot: 'Folder biblioteki',
  statusFound: 'Dostępny',
  statusMissing: 'Brak',
  statusVersion: 'Wersja',

  // --- General (second pass: former inline literals) ---
  add: 'Dodaj',
  preview: 'Podgląd',
  refresh: 'Odśwież',
  generate: 'Generuj',
  creating: 'Tworzenie…',
  goBack: 'Wróć',
  backToStory: 'Wróć do opowieści',
  backToList: 'Wróć do listy',
  unsavedChanges: 'Niezapisane zmiany',
  untitled: 'Bez tytułu',
  selectAll: 'Zaznacz wszystko',
  deselectAll: 'Odznacz wszystko',
  emptyFile: 'Pusty plik',
  languagePl: 'polski',
  languageEn: 'angielski',
  discardConfirm: 'Odrzucić niezapisane zmiany?',
  moreActions: 'Więcej działań',
  errorShort: 'błąd',
  titleLabel: 'Tytuł',

  // --- HTTP client ---
  errorFileChangedOnDisk: 'Plik zmienił się na dysku od czasu odczytu.',
  uploadNetworkError: 'Błąd sieci podczas wysyłania.',
  uploadAborted: 'Wysyłanie przerwane.',

  // --- Listening ---
  downloadAll: 'Pobierz wszystkie',
  downloadingAll: 'Pobieranie…',

  // --- Story list ---
  newStoryDescription: 'Utworzy folder historii i pierwszą wersję.',
  newStoryValidationMissing: 'Podaj tytuł i nazwę wersji.',
  newStorySlugInvalid: 'Slug może zawierać tylko małe litery, cyfry i myślniki.',
  newStoryNoUniverses: 'Brak uniwersów — najpierw dodaj folder uniwersum w bibliotece.',
  universesEmptyOption: 'brak uniwersów',
  storiesLoadError: 'Nie udało się pobrać historii.',
  storiesEmptyHint: 'Zacznij od „Nowa historia”, potem nagrajcie wspomnienia.',

  // --- Story detail ---
  storyNotFound: 'Nie znaleziono historii.',
  variantNameInvalid: 'Nazwa wersji: małe litery, cyfry i myślniki.',
  variantExists: 'Taka wersja już istnieje.',
  cloneTitle: 'Nowa wersja historii',
  cloneDescription: 'Skopiuje ustawienia istniejącej wersji — wspomnienia są wspólne.',
  cloneSourceLabel: 'Kopiuj z wersji',
  cloneNameLabel: 'Nazwa nowej wersji',
  clonePovLabel: 'POV (czyja to opowieść)',
  clonePovPlaceholder: 'np. Kuba — łucznik',
  createVariant: 'Utwórz wersję',
  actionProgress: 'Zobacz postęp',
  actionBuild: 'Zbuduj sesję',
  actionCapture: 'Nagraj wspomnienia',
  actionReadDraft: 'Czytaj szkic',
  sharedAcrossVariants: 'wspólne dla wszystkich wersji',
  textNote: 'notatka tekstowa',
  transcriptChip: 'transkrypcja',
  transcriptPendingChip: 'transkrypcja…',
  povUnset: 'POV nieustawione',
  linkSession: 'sesja',
  linkProgress: 'postęp',
  linkDraft: 'szkic',
  linkAudio: 'audio',
  /** Chip on a variant card: "pracuje: <stage>". */
  jobWorkingPrefix: 'pracuje',

  // --- Capture ---
  captureHeading: 'Wspomnienia z sesji',
  captureSharedHint: 'Wspólne dla wszystkich wersji. Nagrania audio transkrybują się same.',
  addFile: 'Dodaj plik',
  captureNewPersonLabel: 'Ktoś nowy?',
  captureNewPersonPlaceholder: 'imię, np. zosia',
  uploadedShort: 'wysłano',
  captureCollectedTitle: 'Już zebrane',
  captureEmptyList: 'Jeszcze nic tu nie ma — nagraj pierwsze wspomnienie powyżej.',
  openText: 'otwórz tekst',
  transcriptReloaded: 'Plik zmienił się w międzyczasie — wczytano aktualną wersję.',
  transcriptRawHint: 'Surowa transkrypcja — bałagan jest częścią wspomnienia.',
  transcriptEditWarning:
    'Transkrypcje celowo zostają nieuczesane — tak zapamiętało dziecko i tak ma zostać. Edytuj tylko, gdy Whisper przekręcił słowa.',
  editAnyway: 'Edytuj mimo to',

  // --- Session builder ---
  builderPresetGiven: 'Raport bitewny',
  builderPresetInvent: 'Pomysł',
  builderPresetGivenTitle: 'Zastosować szablon „Raport bitewny”?',
  builderPresetGivenBody: 'Wszystkie beaty zostaną oznaczone jako „TAK BYŁO”.',
  builderPresetInventTitle: 'Zastosować szablon „Pomysł”?',
  builderPresetInventBody: 'Wszystkie beaty zostaną oznaczone jako „WYMYŚL”.',
  builderCastCustomTitle: 'Dodaj własną postać',
  builderCastName: 'Imię',
  builderCastAbout: 'Kim jest (krótko)',
  builderCastRemove: 'Usuń postać',
  builderPovEmpty: 'Zaznacz postacie w obsadzie, aby wybrać POV.',
  builderOutcomeLabel: 'Finał',
  builderSkipRemove: 'Usuń z listy pominięć',
  builderVoiceDefault: '(domyślny z katalogu)',
  builderMoveUp: 'Przenieś wyżej',
  builderMoveDown: 'Przenieś niżej',
  builderGenerateHint: 'Zapisz zmiany przed generowaniem',
  builderConflictTitle: 'Plik zmieniony na dysku — przeładować?',
  builderConflictBody:
    'Plik session.json zmienił się od ostatniego wczytania (może edycja ręczna). Przeładowanie odrzuci lokalne zmiany. Dalsza edycja sprawi, że następny zapis nadpisze wersję z dysku.',
  builderConflictReload: 'Przeładuj z serwera',
  builderConflictKeep: 'Edytuj dalej',
  builderLoadError: 'Nie udało się wczytać sesji',
  builderSessionBroken: 'Plik sesji nie jest poprawnym obiektem JSON.',

  // --- Progress ---
  progressLoadingJobs: 'Wczytywanie zadań…',
  progressNoJobs: 'Brak zadań dla tego wariantu.',
  jobsLoadError: 'Nie udało się pobrać listy zadań.',
  jobQueuedNote: 'Zadanie czeka w kolejce — zadania wykonują się pojedynczo.',
  jobCancelConfirm: 'Na pewno przerwać to zadanie?',
  jobCancelAction: 'Przerwij zadanie',
  jobFailedNote: 'Zadanie zakończyło się błędem.',
  stageExtract: 'Ekstrakcja faktów',
  stageOutlineStep: 'Szkielet scen',
  stageScenes: 'Sceny',
  stageVerify: 'Weryfikacja',
  stageBible: 'Fakty do biblii',
  /** Scene counter under the "Sceny" step; {n} and {total} are substituted. */
  stageSceneOf: 'scena {n} z {total}',
  openBuilder: 'Otwórz kreator',
  viewDraft: 'Zobacz szkic',
  reviewDraft: 'Przejrzyj szkic',

  // --- Draft review ---
  draftReviewTitle: 'Przegląd szkicu',
  draftLoading: 'Wczytywanie szkicu…',
  draftEmptyForVariant: 'Nie ma jeszcze szkicu dla tego wariantu.',
  sceneRestoreConfirm: 'Przywrócić poprzednią wersję tej sceny?',
  sceneRegenInProgress: 'Nowa wersja sceny w przygotowaniu…',
  sceneRegenTitle: 'Wygeneruj scenę ponownie',
  sceneRegenHint: 'Możesz dodać wskazówkę — co poprawić w nowej wersji.',
  sceneRegenPlaceholder: 'Np. mniej opisów, więcej dialogu…',
  enqueueError: 'Nie udało się dodać zadania.',
  renderTtsEnqueueError: 'Nie udało się dodać zadania renderowania.',
  verifyAgain: 'Weryfikuj ponownie',
  verifyRunning: 'Weryfikacja…',
  sceneRegenOwnNotesLabel: 'Własne uwagi (opcjonalnie)',
  sceneRegenNothingSelectedHint: 'Zaznacz uwagi albo napisz własne',
  regenerate: 'Regeneruj',
  sceneRestoreShort: 'Przywróć poprzednią',
  discardChanges: 'Odrzuć zmiany',
  renderAudio: 'Renderuj audio',
  conflictLoadFromDisk: 'Wczytaj wersję z dysku',
  conflictKeepMine: 'Zachowaj moją i zapisz ponownie',
  /** Heading of the note handed to the generator when regenerating a scene. */
  feedbackNoteHeader: 'Zastosuj następujące uwagi weryfikacji:',
  /** Label before the free-text part of that note. */
  feedbackNoteExtra: 'Dodatkowo',

  // --- Verification notes: rule labels (rule slug → plain language) ---
  verifyRuleGivenDrift: 'Zmienione fakty — tak nie było',
  verifyRuleRegister: 'Styl narracji',
  verifyRuleAnachronism: 'Anachronizm',
  verifyRuleNaming: 'Nazewnictwo',
  verifyRuleHardRules: 'Zasady świata',
  verifyRuleCanon: 'Sprzeczne z kroniką',
  verifyRuleTone: 'Ton opowieści',
  draftConflictDiskTitle: 'Szkic zmienił się na dysku',
  draftConflictDiskBody:
    'plik szkicu został w międzyczasie zmieniony (np. przez regenerację albo ręczną edycję). Co zrobić z Twoją wersją?',

  // --- Universe ---
  universeConflictTitle: 'Plik zmienił się na dysku',
  fontToggleAria: 'Krój pisma',
  fontSerif: 'Szeryf',
  fontMono: 'Mono',
  tabFacts: 'Fakty',

  // --- Settings ---
  settingsClaudeMissingTitle: 'Brak Claude CLI',
  settingsClaudeMissingBody1: 'Nie znaleziono polecenia',
  settingsClaudeMissingBody2:
    '. Generowanie historii nie będzie działać. Zainstaluj Claude Code i zaloguj się — uruchom',
  settingsClaudeMissingBody3: 'w terminalu i przejdź logowanie subskrypcją.',
  settingsModelsHintPrefix: 'Brakujące modele pobierze',
  settingsBypassBody:
    'Ta aplikacja to tylko widok na zwykłe pliki — bez bazy danych i bez blokad. Każdy plik możesz edytować ręcznie (albo podejrzeć, co zrobił generator), a panel zobaczy zmiany od razu. Jeśli panel kiedyś przeszkadza, edytuj pliki bezpośrednio:',
  settingsGeneratorHeading: 'Generator',
  settingsLibraryHeading: 'Biblioteka',
  settingsBypassTitle: 'Panel można pominąć',
  /** Annotated library tree; rendered under the library root path. */
  settingsLibraryTree: `├─ stories/
│  └─ <data-slug historii>/
│     ├─ session.<wariant>.json   ← spec: obsada, beaty, given|invent
│     ├─ recollections/           ← nagrania i notatki (niezmienne)
│     ├─ outline.<wariant>.md
│     ├─ draft.<wariant>.md       ← tekst opowieści
│     ├─ verify.<wariant>.md
│     └─ audio/<wariant>.mp3
├─ universes/
│  └─ <uniwersum>/
│     ├─ constraints.md           ← zasady świata
│     ├─ bible.md                 ← fakty kanoniczne (kronika)
│     └─ characters.md
├─ voices.json                    ← katalog głosów (wspólny dla wszystkich światów)
├─ voices/                        ← nagrania wzorcowe
└─ voice-previews/                ← wyrenderowane próbki głosów`,

  // --- Settings: language picker ---
  settingsLanguageHeading: 'Język interfejsu',
  settingsLanguageHint: 'Wybór zapamiętuje ta przeglądarka. Tekst opowieści zostaje bez zmian.',
  /** Endonyms — deliberately identical in both dictionaries. */
  languageNameEn: 'English',
  languageNamePl: 'Polski',

  // --- Settings: voices ---
  settingsVoicesHeading: 'Głosy',
  settingsVoicesHint: 'Każdą opowieść czyta jeden z nich. Naciśnij odtwarzanie, żeby posłuchać.',
  voicesEmpty: 'Nie masz jeszcze głosów',
  voicesEmptyHint: 'Dodaj pierwszy — narrator wbudowany jest na liście.',
  voicesLoadError: 'Nie udało się pobrać Twoich głosów.',
  voicesDefaultBadge: 'domyślny',
  voicesPlay: 'Odtwórz',
  voicesStop: 'Zatrzymaj',
  voicesNoSample: 'brak próbki',
  voicesSampleError: 'Nie udało się odtworzyć próbki.',
  /** Locale names. The flag carries the country, so these name the language. */
  localeEnUs: 'angielski (USA)',
  localeEnGb: 'angielski (Wielka Brytania)',
  localePlPl: 'polski',
  /** Named deliveries. The reader picks one of these; the numbers behind them never surface. */
  voiceStyleCalm: 'Spokojnie',
  voiceStyleNatural: 'Naturalnie',
  voiceStyleLively: 'Żywo',
  voicesStyleLabel: 'Sposób czytania',
  voicesRerecord: 'Nagraj próbkę jeszcze raz',
  voicesSetDefault: 'Ustaw jako domyślny',
  voicesRename: 'Zmień nazwę',
  /** The ⋯ button on a row, and the sheet it opens. {name} is the voice name. */
  voicesMenuOpen: 'Więcej działań — {name}',
  voicesMenuTitle: 'Głos',
  voicesRenameTitle: 'Zmień nazwę głosu',
  voicesRenameHint: 'Zmienia się tylko wyświetlana nazwa — opowieści działają dalej.',
  voicesNameLabel: 'Nazwa',
  voicesNameRequired: 'Nadaj głosowi nazwę.',
  voicesDeleteTitle: 'Usunąć ten głos?',
  /** {name} is the voice name. */
  voicesDeleteBody: 'Usuwa „{name}” i jego próbkę. Opowieści ustawione na ten głos będą potrzebowały innego.',
  voicesDeleteWav: 'Usuń też jego nagranie',
  voicesSaveError: 'Nie udało się zapisać zmiany.',
  voicesAdvancedShow: 'Zaawansowane: edytuj plik voices.json',
  voicesAdvancedHide: 'Ukryj edytor pliku',
  voicesAdvancedHint:
    'Katalog to zwykły plik. Wszystko powyżej edytuje go za Ciebie — to tylko na wyjątkowe przypadki.',
  voicesInvalidJson: 'Nieprawidłowy JSON — plik i tak można zapisać',

  // --- Add a voice: language → how → do it ---
  voicesAdd: 'Dodaj głos',
  voicesAddStepLanguage: 'W jakim języku ma czytać?',
  voicesAddStepHow: 'Skąd ma pochodzić głos?',
  voicesAddBack: 'Wstecz',
  /** {count} is how many voices the shelf has for that language. */
  voicesAddOfferCount: '{count} do wyboru',
  voicesAddChoose: 'Wybierz głos',
  voicesAddChooseHint: 'Wybierz z gotowej listy. Każdego możesz posłuchać przed dodaniem.',
  voicesAddUpload: 'Wgraj nagranie',
  voicesAddUploadHint: 'Ktoś czyta przez pół minuty, a opowieści są potem czytane jego głosem.',
  voicesAddRecord: 'Nagraj teraz',
  voicesAddRecordHint: 'Przeczytaj fragment na głos. Tworzenie głosu zajmie potem kilka minut.',
  /** Browsers hide the microphone on an insecure origin; {url} is the same page over https. */
  voicesAddRecordBlocked:
    'Mikrofon działa tylko przez bezpieczne połączenie. Otwórz {url} — przeglądarka raz ostrzeże o certyfikacie, przejdź dalej.',
  voicesRecordStart: 'Zacznij nagrywać',
  voicesRecordStop: 'Zatrzymaj',
  voicesRecordAgain: 'Nagraj jeszcze raz',
  voicesRecordHint: 'Naciśnij start, przeczytaj fragment powyżej, potem naciśnij stop.',
  voicesRecordKeepGoing: 'Czytaj dalej — potrzeba co najmniej 10 sekund.',
  voicesRecordEnough: 'Wystarczająco długo. Możesz zatrzymać, kiedy chcesz.',
  voicesRecordListen: 'Odsłuchaj',
  voicesRecordDenied: 'Przeglądarka zablokowała mikrofon. Zezwól na dostęp dla tej strony i spróbuj ponownie.',
  voicesRecordFailed: 'Nagranie się nie udało. Spróbuj jeszcze raz.',
  voicesAddInstall: 'Dodaj',
  voicesAddInstalling: 'Dodaję…',
  voicesAddInstallError: 'Nie udało się dodać tego głosu.',
  voicesOffersEmpty: 'Nie ma jeszcze gotowych głosów w tym języku — wgraj nagranie.',
  voicesOffersLoadError: 'Nie udało się pobrać listy głosów.',
  /** Shown on the Add button; {size} is like "64 MB". */
  voicesDownloadSize: 'pobiera {size}',
  voicesDownloadNone: 'nic do pobrania',

  // --- Add a voice: the upload instructions ---
  voicesUploadTitle: 'Wgraj nagranie',
  voicesUploadRules: 'Co zadziała',
  voicesUploadRule1: 'Od 20 do 40 sekund mówienia.',
  voicesUploadRule2: 'Tylko jedna osoba — bez muzyki, telewizora i innych głosów.',
  voicesUploadRule3: 'Czytaj zwyczajnie, jak bajkę na dobranoc. Aktorstwo tylko szkodzi.',
  voicesUploadRule4: 'Cichy pokój. Telefon trzymany na szerokość dłoni wystarczy.',
  voicesUploadFormats: 'wav, mp3 lub m4a — cokolwiek nagrywa dyktafon w telefonie.',
  voicesUploadScript: 'Przeczytaj to',
  voicesUploadScriptHint: 'Każdy tekst zadziała, ale ten ma odpowiednią długość i ton.',
  /** ~40 s read aloud, in narration register so the copy is conditioned on storytelling. */
  voicesUploadPassageEn:
    'The elder of Marrowfield did not offer them chairs. He spoke about the gem the way a man speaks about a debt he has decided not to pay. Outside, the rain had stopped, and the road out of town was already turning to mud. They had until morning to decide, and neither of them wanted to be the one who said it first.',
  voicesUploadPassagePl:
    'Starszy z Marrowfield nie zaproponował im krzeseł. Mówił o klejnocie tak, jak człowiek mówi o długu, którego postanowił nie spłacić. Na zewnątrz deszcz ustał, a droga z miasta zamieniała się już w błoto. Mieli czas do rana, żeby zdecydować, i żadne z nich nie chciało powiedzieć tego pierwsze.',
  voicesUploadPick: 'Wybierz nagranie',
  voicesUploadNameLabel: 'Czyj to głos?',
  voicesUploadNamePlaceholder: 'np. Babcia',
  voicesUploadSubmit: 'Dodaj ten głos',
  voicesUploading: 'Wysyłam…',
  /** Measured at ~8 minutes on an RTX 4060 Ti: conditioning, then rendering the first sample. */
  voicesUploadPreparing: 'Uczę się głosu — kilka minut. Możesz zamknąć tę stronę, praca trwa dalej.',
  voicesUploadError: 'Nie udało się użyć tego nagrania.',

  // --- Samples (temporary TTS shelf) ---
  samplesTitle: 'Próbki audio (tymczasowe)',
  samplesHint:
    'Porównania silników TTS i głosów referencyjnych. Strona zniknie po wyborze silnika.',
  samplesLoadError: 'Nie udało się pobrać listy próbek.',
}
