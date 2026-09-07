# Nomori Marketplace Backend AI Working Agreement

- Target .NET 10 (`net10.0`) and follow `global.json`.
- Read `docs/decisions.md` and the relevant module docs before changing code.
- Keep dependency direction: Core <- Data/Services; Api depends on abstractions, never the reverse.
- Keep controllers thin. Put business rules in services/domain code.
- Use `/api/v1`, dedicated request/response DTOs and `ProblemDetails` for API errors.
- Store and process timestamps in UTC.
- Enforce authorization in the backend; UI checks are not security.
- Do not edit generated API clients by hand.
- Never commit secrets, tokens, private keys, real connection strings or personal data.
- After changes, run the narrowest build/test, review the diff, and update the module report.
