---
name: organizing-documentation
description: >-
  Activate when the user asks to write, restructure, review, or file new
  documentation — a README, a guide, a reference doc, a wiki page, an ADR, or
  any markdown intended for other readers. Use this to decide what *kind* of
  document is being written (tutorial, how-to, reference, or explanation),
  keep that single purpose intact rather than mixing modes, and decide where
  it belongs in an existing docs tree. Also use when a user complains that
  documentation is "hard to navigate," "doesn't answer my question," or
  "has become a dumping ground" — that is usually a Diataxis mode mismatch,
  not a writing-quality problem.
license: MIT
metadata:
  category: documentation
  framework: diataxis (https://diataxis.fr)
---

# Organizing Documentation (Diataxis)

Documentation problems are usually structural, not stylistic: a doc trying to
be a tutorial, a reference, and an explanation at once ends up serving none
of its readers well. The [Diataxis](https://diataxis.fr) framework fixes this
by recognizing that documentation serves exactly four distinct needs, and
each needs a different shape.

## The four modes

Diataxis organizes documentation along two axes: whether the reader is
**studying** (acquiring knowledge) or **working** (applying it), and whether
the content is **practical** (steps, actions) or **theoretical**
(understanding, information).

|                | Practical (action)        | Theoretical (cognition) |
|----------------|----------------------------|--------------------------|
| **Studying**   | Tutorial                   | Explanation              |
| **Working**    | How-to guide                | Reference                |

- **Tutorial** — a lesson, taken by a *beginner*, led by the author.
  Goal: the reader succeeds at a concrete exercise and builds confidence.
  Written in the imperative ("do this, then this"), with a guaranteed
  outcome. No unnecessary choices, no alternatives, no "why" digressions.
- **How-to guide** — a recipe, followed by someone who *already knows the
  basics* and has a specific goal ("how do I configure X for Y"). Assumes
  competence, addresses a real-world problem, can offer branches/choices a
  tutorial can't.
- **Reference** — dry, structured, factual description of the machinery
  (API, config keys, CLI flags, schema). Organized to match the *structure
  of the thing itself*, not a narrative. Optimized for lookup, not reading
  start-to-end. No opinions, no instructions on how to use it.
- **Explanation** — discussion that deepens understanding: why something is
  designed the way it is, background, context, alternatives considered,
  connections between concepts. The only mode allowed to be discursive.

## Deciding which mode a document is

Ask what the reader is doing right now:

| Reader's situation | Mode |
|---|---|
| Learning the tool/system for the first time, no goal yet beyond "get it working" | Tutorial |
| Has a specific task, knows the basics, wants the steps | How-to guide |
| Needs to look up an exact name, signature, flag, or field | Reference |
| Wants to understand *why*, or how pieces relate | Explanation |

If a request doesn't cleanly fit one situation, it's a sign the underlying
ask should become two documents, not one document doing double duty.

## Applying this when writing

1. **Pick one mode before writing a word.** State it to yourself as the doc's
   job description (e.g. "this is a how-to for configuring X").
2. **Strip content that belongs to a different mode.** A "why we chose this
   default" aside inside a how-to guide is explanation smuggled in — cut it
   or link out to an explanation doc instead of inlining it.
3. **Match the tone to the mode**: tutorials and how-tos are imperative and
   procedural; reference is declarative and structured (tables, definition
   lists); explanation is prose that can digress and compare alternatives.
4. **Don't let reference docs teach, and don't let tutorials cover edge
   cases.** A tutorial with five different ways to do something isn't a
   tutorial anymore.

## Applying this when organizing an existing docs tree

1. **Sort existing docs into the four modes first**, before deciding on
   folders — a mode mismatch is the usual reason a doc feels "in the wrong
   place" even when the topic-based folder looks right.
2. **Group by mode at the top level if the collection is large enough to
   need navigation** (`tutorials/`, `how-to/`, `reference/`, `explanation/`);
   for a smaller topic-based repo, mode can instead be signaled per-document
   (front-matter, a heading convention, or just the doc's opening sentence
   stating its purpose) while folders stay organized by subject area.
3. **A single topic legitimately produces up to four documents** — e.g. for
   one tool: a getting-started tutorial, a handful of how-to guides for
   specific tasks, a flags/config reference, and an explanation of its
   design tradeoffs. Don't force them into one file because they're "about
   the same thing."
4. When reviewing a complaint that docs are hard to navigate, check first
   whether readers are being handed the wrong mode for their situation
   (e.g. a newcomer landed on a reference page and bounced) rather than
   assuming the content itself is unclear.

## Keeping an OKF bundle valid

If the docs tree is an OKF bundle (the root `index.md` has `okf_version`),
check it after filing documents, when `eru` is available: run `eru okf init
<root>` to create missing `index.md` files and `eru okf validate <root>` to
confirm conformance (`eru okf fix` repairs most violations). Remember that a
folder `index.md` has no frontmatter, and that `README.md` is not part of the
bundle.

A valid OKF v0.2 note needs only a non-empty `type` in its frontmatter. When
you also add trust or provenance keys, use the v0.2 shapes: `generated: { by,
at }`, `verified: [{ by, at }]` (never write it yourself, not even `false` or `unknown`; omit it and let `eru okf verify <file>` record it),
`sources: [{ resource: ... }]` (objects, not bare strings), and
`stale_after` as an ISO 8601 datetime with a UTC offset. Actors are
`<producer>/<version>`, `human:<id>` or `process:<id>`; only a person's
confirmation may use `human:`. Don't write v0.1 `timestamp` or a `# Citations`
section. `eru okf validate` prints `⚠` warnings for non-v0.2 shapes.

It also warns about links that point at something missing from the bundle
(`broken-link`, `broken-image`, `broken-wikilink`, `broken-anchor`). Keep notes
self-contained: link only to notes and files that exist in the docs tree,
and when a link target is missing, keep the text and drop the link.
`eru okf validate --strict-links` makes these failures.

## Anti-patterns to flag

- A README that opens with installation steps (how-to), drifts into "how it
  works internally" (explanation), then ends with a full flag reference
  (reference) — three documents wearing one hat.
- A tutorial with "if you're using version X instead, do Y" branches —
  that's a how-to's job; tutorials should have exactly one path.
- A reference doc written as flowing prose instead of scannable
  structure — readers looking something up have to read past what they
  don't need to find what they do.
- An explanation doc buried as a comment or aside inside a how-to guide,
  invisible to the reader who actually wants it and unwanted by the reader
  who doesn't.

## Resources

- [Diataxis framework](https://diataxis.fr)
- [Diataxis: a systematic approach to technical documentation authoring](https://diataxis.fr/start-here/)
