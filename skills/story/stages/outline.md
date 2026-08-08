# Stage: outline

Plan the scenes for one variant.

## Read
- `session.<v>.json`
- `library/universes/<universe>/constraints.md`
- `library/universes/<universe>/tones/<tone>.md` (if the session names a tone and the file exists)
- `library/universes/<universe>/bible.md`
- `library/universes/<universe>/characters.md`
- `skills/story/style/register-sample.md`

## Write
`outline.<v>.md` — one block per scene:

```markdown
## Scene s1: <title in the story language>
- pov: <character slug>
- beats: b1, b2
- targetWords: 450
- note: <one line: what this scene is really about>
```

## Rules
- Scene ids `s1, s2…` in reading order. Every beat id from the session appears in exactly
  one scene's `beats:` list; a scene may carry several consecutive beats.
- `pov` is the session's POV character for every scene (third person close on them);
  scenes where they are absent are told through what they later learn or find.
- Weighting follows the recollection, not dramatic convention: the beats the teller dwelt
  on get the scenes and the words; connective beats get little.
- Total `targetWords` ≈ `targetMinutes × 150`, distributed by that weighting.
- The `note` line carries the scene's engine — the thing underneath the events (per the
  register sample: "the trap is being pleased with yourself").
- Respect every hard rule in constraints.md and every established fact in bible.md.
  The tone pack shapes stakes framing and ending posture at outline level.
