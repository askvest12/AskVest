# ADR 0023: Signup profile and onboarding personalization

Status: accepted

## Context

The product UI defines a signup and onboarding flow that the Identity
module did not fully support:

- Signup collects full name, email and password. `SignUpRequest` carried
  only email and password, and `UserAccount` stored no name at all.
- Post-signup onboarding asks the user's investing familiarity, investment
  goals, country of residence and preferred currency. These answers must be
  persisted so the product can personalize Learning content, surface
  practice mode, pick display currency and pre-fill funding options.
- Login supports biometric unlock on the client. This is a local
  convenience, not a new authentication factor: the app keeps the refresh
  token in platform secure storage (Keychain, Keystore, Windows Hello
  backed store) and gates its use behind biometric. The backend contract
  is unchanged.

Country of residence already has a home: `PUT /api/v1/compliance/country`
writes `ComplianceProfile.DeclaredCountry` and feeds the eligibility
gate. Duplicating it inside Identity would create two sources of truth
for a compliance fact.

## Decision

1. **Full name is collected at signup, stored on the user account.**
   `SignUpRequest` gains an optional `fullName` (trimmed, max 120 chars).
   It is optional rather than required so existing clients and the
   enumeration-safe uniform signup response keep working; an authenticated
   user can set or change it later through the onboarding endpoint.
   `UserAccount.FullName` is a display name only: legal name for KYC is
   collected later by the compliance flow and is intentionally not the
   same field.
2. **Onboarding answers live in a new `OnboardingProfile` table owned by
   the Identity module**, one row per user, created lazily on first write:
   - `investing_familiarity`: `newcomer`, `some` or `experienced`.
   - `goal_flags`: a bitmask over six goals (`learn_investing`,
     `build_wealth`, `discover_opportunities`, `research_companies`,
     `diversify`, `practice_first`). Flags keep the set queryable without
     a join table or JSON parsing.
   - `preferred_currency`: ISO 4217 code, nullable.
   - `completed_at`: set on the first successful save so clients can tell
     finished onboarding from a partial draft.
3. **One endpoint writes the profile**: `PUT /api/v1/account/onboarding`,
   authenticated, idempotent, all fields optional so partial updates and
   replays are safe. It also accepts `fullName` to cover accounts created
   before signup collected a name.
4. **`GET /api/v1/account/me` returns the profile**: `fullName` plus an
   `onboarding` object (`familiarity`, `goals` as wire strings,
   `preferredCurrency`, `completedAt`), `null` when untouched. The client
   uses this to decide whether to show onboarding and to resume it.
5. **Country stays in Compliance.** The client calls
   `PUT /api/v1/compliance/country` during onboarding in addition to the
   Identity endpoint. No cross-module write is introduced.
6. **Biometric unlock needs no backend change.** Documented as a client
   responsibility: store the refresh token in platform secure storage and
   unlock it with biometrics. If passkey-grade biometric authentication is
   ever required, that is a separate ADR.

## Consequences

- New migration adds `users.full_name` and `identity.onboarding_profiles`.
- Learning, assistant and funding modules can later read the profile
  through a contract if personalization moves server-side; until then the
  client reads `account/me` and drives UI decisions.
- Goals are stored as flags; adding a seventh goal later is a new enum
  value plus a check-free bit, no schema change beyond documentation.
