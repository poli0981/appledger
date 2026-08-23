## What & why

<!-- One concern per PR. Link the issue if one exists. -->

## Type
- [ ] Catalog entry (checklist from docs/13_CATALOG_RULES.md completed)
- [ ] Bug fix
- [ ] Feature
- [ ] Docs / CI

## Checklist
- [ ] `dotnet build` warning-free and `dotnet test` pass locally (`--filter Category!=Admin` on CI)
- [ ] `dotnet format --verify-no-changes` clean; XAML styled (XamlStyler)
- [ ] No new `OpenProcess` rights beyond `PROCESS_QUERY_LIMITED_INFORMATION`; no writes outside `%LOCALAPPDATA%\AppLedgerData` (docs/11_SAFETY_POLICY.md)
- [ ] Collector-path changes include a measured or reasoned budget note (CPU / RAM / events-per-second)
- [ ] New stored data classified in docs/12_PRIVACY_AND_RETENTION.md §Data inventory
- [ ] User-facing strings added to `Strings.resx` (en) + `vi` + `ja` (machine-draft `ja` marked `<!-- review -->`)
- [ ] docs/ and legal/THIRD_PARTY_NOTICES.md updated if dependencies or behavior changed
- [ ] Screenshots/GIF attached for UI changes
