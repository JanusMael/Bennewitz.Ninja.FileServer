# AGENTS.md — `plans/`

Numbered plans: `NNNNN-slug.md`, zero-padded to five digits, sequential, as
`00001-unlisted-files.md` is. List the directory before taking a number.

## Shape

| Part | What it holds |
|---|---|
| Title | `# NNNNN — ` and what the plan does |
| Status line | A blockquote: `Status: **approved YYYY-MM-DD**.` and `Supersedes` the plan it replaces, or `nothing` |
| `## Decisions` | A numbered table, Decision and Why, then the alternatives dismissed |
| `## Scope`, `## Test rule` | What is in and out, and what every assertion must also check |
| `## Steps` | Each step ends in **Verify:**, naming the tests or the run that proves it |

## Rules

| Rule | Why | Guarded by |
|---|---|---|
| An approved plan is committed on its own, as `docs(plans): approve NNNNN …`, before implementation | The agreement is a commit of its own, separate from the work | `git log -- plans` |
| An approved plan is never edited, not even where the build departed from it | It is the record of what was agreed | `PROGRESS.md` states "The plan stays as approved" |
| Where the build departed from the plan goes in `PROGRESS.md`, under "Decisions recorded during implementation", citing the decision number | The plan stays the promise; the work state says how it was kept | `PROGRESS.md` |
| A different approach is a new number whose status line names what it supersedes | The old plan stays as it was approved | the status line |
| A link inside an approved plan that stops resolving is left alone | Its links are specification; a dead one records drift | nothing |
