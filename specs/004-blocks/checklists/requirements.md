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

- [x] No [NEEDS CLARIFICATION] markers remain
- [x] Requirements are testable and unambiguous
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

- Protocol words (`Block`, `Reject`, `Undo`, 403/404, signed fetch, shared inbox) are kept on purpose: for a protocol
  sidecar they are the product's observable behavior, as in specs 001 to 003. Tables, modules and endpoint paths
  live in plan.md, data-model.md and contracts/.
- The task's "instance-wide domain block" is extended to instance blocks of single actors, at no extra cost, since the
  vision rule names "that actor or domain" for both kinds of block.
- Decisions taken without a clarification round (the task gave the scope): actor ids only, no handles (Assumptions);
  403 for instance blocks and silent 202 for user blocks (R-43); `Block` sent for user actor blocks (R-40); no
  inbound `Block` handling (proposed as a follow-up).
