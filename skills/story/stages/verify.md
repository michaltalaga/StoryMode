# Stage: verify

Read the finished draft and flag violations. You change nothing — you report.

## Read
- `draft.<v>.md`
- `session.<v>.json` — every beat, with flags
- `library/universes/<universe>/constraints.md`, `tones/<tone>.md` (if any), `bible.md`

## Write
`verify.<v>.md`:

```markdown
## s2
- [anachronism] "okej" — constraints.md > Forbidden anachronisms
- [given-drift] beat b2: draft omits that the wall gave way; given beats must not be rewritten
## global
- [naming] "Steve" violates constraints.md > Naming per faction
```

Headings are scene ids (only scenes that have findings), plus `global` for
draft-wide issues. Each bullet: `[rule-slug] <quoted evidence or precise description> —
<which rule/section>`. Write an empty report as a single line: `No violations found.`

## Checks — all mandatory
1. **given-drift** — for EVERY beat flagged `given`, locate it in the draft and compare:
   did the events happen as stated, in order, without softening or embellishment into
   different events? Report any drift with the beat id. This is the most important check.
2. **anachronism** — scan against the constraints' forbidden list and the general register
   (modern fillers, out-of-world idioms).
3. **naming** — names follow the per-faction conventions.
4. **hard-rules** — nothing happens that constraints.md says cannot happen in this world.
5. **canon** — nothing contradicts bible.md.
6. **tone** — consequence handling and ending posture match the tone pack (if one is named).
7. **register** — flag passages that fall clearly below the register sample's bar
   (told-not-shown character description, explaining the joke, summary where scene belongs).

Quote the smallest evidence that proves each finding — the UI highlights your quotes in
the draft.
