---
name: review-code
description: Review Nomori backend AI-generated code for bugs, security, architecture violations, regressions, missing tests and technical debt.
---

Review the current diff and nearby tests. Report findings first, ordered by severity:

- `[P0]` Critical
- `[P1]` High
- `[P2]` Medium
- `[P3]` Low

Each finding must include file/line, impact and a concrete fix. Check authorization, validation, ProblemDetails, UTC, migration safety, transaction/idempotency, logging, dependency direction, performance and test coverage. Then include validation run, open questions/assumptions and change summary. If no findings exist, state that clearly and list residual risk.
