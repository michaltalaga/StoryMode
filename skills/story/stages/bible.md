# Stage: bible

Propose new canon facts from the finished draft. A human approves them in the panel;
you only propose.

## Read
- `draft.<v>.md`
- `session.<v>.json`
- `library/universes/<universe>/bible.md` — what canon already exists
- `library/universes/<universe>/characters.md`

## Write
`bible.pending.<v>.md` — one fact per bullet, each with a stable id and scene attribution:

```markdown
- [f1] Corin nie ufa ścianom po upadku w jaskini. (s2)
- [f2] Starszy Marrowfield nie płaci, dopóki nie musi. (s1)
```

Ids `f1, f2…` are per-file. Write facts in the story language.

## Rules
- Propose only facts likely to matter in a future story: character traits revealed,
  standing relationships, places with established properties, debts, injuries with
  consequences, named items and where they ended up.
- One sentence per fact, present tense, standalone (readable without the story).
- Skip anything bible.md already records; skip one-off color that cannot recur.
- Facts from `invent` beats are proposed like any other — once told, they are canon
  candidates.
- Prefer few good facts over many. Three to eight per story is the expected range.
