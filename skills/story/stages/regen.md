# Stage: regen

Rewrite one existing scene, `sN`, incorporating human feedback. This always runs in a
fresh session: the current files are the only truth.

## Read
- the `Feedback:` note in the invocation prompt — this is the human telling you what was
  wrong; it outranks everything except the invariants
- `draft.<v>.md` — the current draft **including the current text of `sN`** and every other
  scene exactly as a human may have edited them
- `outline.<v>.md`, `session.<v>.json` (beat flags!), constraints, tone pack, bible,
  characters, `skills/story/style/register-sample.md`
- `verify.<v>.md` if present — outstanding flags on `sN` should also be fixed

## Write
`scene.sN.out.md` — same shape as the scene stage:

```markdown
<!-- scene:sN -->
<the rewritten prose>
```

Never write into `draft.<v>.md`.

## Rules
- Rewrite only `sN`. The surrounding scenes are fixed context — the new text must join
  seamlessly to the scene before and after as they currently read.
- Feedback interprets, it does not override: if the note asks for something that would
  contradict a `given` beat, satisfy the intent as far as the beat allows and keep the
  events intact.
- Keep what worked. This is a revision, not a fresh idea — preserve lines and structure
  from the current `sN` that the feedback does not touch.
