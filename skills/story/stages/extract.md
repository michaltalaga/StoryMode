# Stage: extract

Turn raw recollections into the story spec — filling only the empty fields of
`session.<v>.json`.

## Read
- `session.<v>.json` (the file you will fill)
- every file listed in `session.sources.primary` and `session.sources.background`
  (paths relative to `recollections/`); if `sources` itself is empty, treat the POV
  person's files (`<pov-person>.*`) as primary and all other recollections as background
  and record that in `sources`
- `library/universes/<universe>/characters.md` for recurring-cast slugs

## Write
`session.<v>.json` — only fields that are currently empty (`""`, `[]`, missing):
`sources`, `cast`, `stakes`, `beats`, `outcome`, `skip`. Preserve every existing value and
every unknown field byte-for-byte. Never touch `universe`, `language`, `pov`, `tone`,
`voice`, `targetMinutes`.

## Rules
- Beats get ids `b1, b2…` in the order the **primary** source tells them — the teller's
  order, even when it is obviously not chronological. Order from background sources may fill
  gaps but never reorders the primary teller's sequence.
- Flag defaults by `inputType`:
  - `battle-report`: every event beat `given`; `invent` only for connective transition
    beats you insert (mark those clearly as transitions in their text)
  - `rpg`: events described in sources → `given`; gaps that need bridging → `invent`
  - `premise`: everything `invent` unless the source states it as having happened
- Beat text quotes the teller's emphasis — if the primary source lingers, the beat says so
  (e.g. "the goblin fight — the teller's favourite part, four sentences of it").
- `skip`: rules arguments, snack breaks, table logistics, out-of-character chatter found in
  the sources. When `inputType` is `rpg`, look for these actively.
- Cast entries: use `{"ref": "<slug>"}` for people matching a heading in `characters.md`,
  else `{"name": "...", "about": "<one line from the sources>"}`.
- Write `stakes` and `outcome` as single sentences in the story language.

Transcripts are read verbatim. You never write a cleaned or summarized copy of any
recollection anywhere.
