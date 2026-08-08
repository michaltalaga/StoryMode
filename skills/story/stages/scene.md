# Stage: scene

Write the prose for one scene, `sN`, of one variant.

## Read
- `outline.<v>.md` — your scene's block and the whole plan for context
- `session.<v>.json` — the beats your scene carries, with their flags, verbatim
- `draft.<v>.md` — everything written so far (may not exist for s1)
- `library/universes/<universe>/constraints.md`, `tones/<tone>.md` (if any), `bible.md`,
  `characters.md`
- `skills/story/style/register-sample.md`

## Write
`scene.sN.out.md` — exactly this shape, nothing else in the file:

```markdown
<!-- scene:sN -->
<the prose>
```

Never write into `draft.<v>.md` — the app splices your scratch file in.

## Rules
- The register sample is the craft bar: third person close, dry, character through
  behaviour. Match it in the story language.
- Given beats: the events occur exactly as the beat states them. Quote the beat text back
  to yourself before writing; when the scene is done, check the events against it again.
  Invent beats: free rein within constraints, bible, and tone.
- Continuity flows from the existing draft — timeline, injuries, objects, who knows what.
  The current draft text is truth even where it differs from what an earlier generation
  might have said (a human may have edited it).
- Hit `targetWords` ±20%. The `note` line is the scene's spine.
- No anachronisms from the constraints list. Naming per faction conventions.
- End the scene where the outline's next scene begins — no recaps, no previews.
