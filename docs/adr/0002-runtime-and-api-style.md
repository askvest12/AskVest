# ADR 0002: Runtime and API style

- Status: Accepted

## Context

The API is consumed by a mobile client and later a back office. It needs strong typing, minimal ceremony, and easy testing.

## Decision

.NET 10 with ASP.NET Core minimal APIs grouped into endpoint groups. Handlers are plain classes. Validation uses FluentValidation. No MediatR, no AutoMapper.

## Consequences

Request pipeline stays visible in `Program.cs` and extension classes. Handlers are directly unit testable without a mediator. Mapping is explicit and reviewable.

## Alternatives rejected

- MediatR: adds a pipeline abstraction for little gain; handlers can be called directly.
- AutoMapper: hides mapping bugs; explicit mapping is safer for money fields.
- Controllers: more ceremony than minimal APIs for this shape.
