# Specification Quality Checklist: Blocks

**Purpose**: Validate specification completeness and quality before proceeding to planning
**Created**: 2026-10-15
**Feature**: [spec.md](../spec.md)

## Content Quality

- [x] No implementation details (languages, frameworks, APIs)
- [x] Focused on user value and business needs
- [x] Written for non-technical stakeholders
- [x] All mandatory sections completed

## Requirement Completeness

- [ ] No [NEEDS CLARIFICATION] markers remain — three remain on purpose, the input says "Decide in clarify"
- [x] Requirements are testable and unambiguous (except the three open points)
- [x] Success criteria are measurable
- [x] Success criteria are technology-agnostic (no implementation details)
- [x] All acceptance scenarios are defined
- [x] Edge cases are identified
- [x] Scope is clearly bounded
- [x] Dependencies and assumptions identified

## Feature Readiness

- [x] All functional requirements have clear acceptance criteria
- [x] User scenarios cover primary flows
- [x] Feature meets measurable outcomes defined in Success Criteria
- [x] No implementation details leak into specification

## Notes

- Open for `/speckit-clarify` (the input asks for these three to be decided there): US1 scenario 9 / FR-004 (does a
  user block of an actor send `Block`?), US2 scenario 6 / FR-005 (do domain blocks cover subdomains?), FR-012 (which
  webhooks when a block ends follows, including a remote `Block`?).
- Protocol words (`Block`, `Undo`, 403/404, signed fetch, shared inbox) are kept on purpose: for a protocol sidecar
  they are the product's observable behavior, as in specs 001 to 003. The endpoint path appears only because the
  input names it.
- One addition to the input, flagged in Assumptions: the instance call also accepts a single actor. Strike it in
  clarify if unwanted.
- The plan, research, data model, contracts and tasks in this folder were written for an earlier draft of this spec
  (no inbound `Block`, actor ids only, Mastodon not in the tests, the three open points decided). `/speckit-plan` and
  `/speckit-tasks` must regenerate them from this spec.
