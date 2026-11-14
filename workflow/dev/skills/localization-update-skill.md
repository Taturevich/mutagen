# Localization Update Skill

Use this skill when adding, editing, deleting, or reviewing localization keys in the repo.

## Catalog Ownership

- Frontend UI copy lives in `src/VeronikaStudio.Client/src/areas/localization/locales/{cs,en,ru,uk}.json`.
- Backend salon/domain copy lives in `src/VeronikaStudio.Infrastructure/Localization/{cs,en,ru,uk}.json`.
- Keep all supported cultures aligned for the same catalog. Do not add a key to only one language unless the change is explicitly an intentional fallback.
- Prefer stable semantic keys over English phrases as keys.
- Use the native script for each language. Never use transliterated Latin text for languages written in Cyrillic, including Russian and Ukrainian.

## Update Command

Use the repo-owned script for JSON edits so files stay valid UTF-8, sorted, and consistently formatted:

```powershell
scripts\tool.cmd localization set <file> <key> <value>
scripts\tool.cmd localization delete <file> <key>
scripts\tool.cmd localization validate <file>
```

Examples:

```powershell
scripts\tool.cmd localization set src/VeronikaStudio.Client/src/areas/localization/locales/en.json admin.save "Save"
scripts\tool.cmd localization set src/VeronikaStudio.Infrastructure/Localization/en.json status.booked "Booked"
```

## Change Procedure

1. Identify whether the key belongs to the frontend catalog or backend catalog.
2. Update every supported language file in that catalog using `scripts\tool.cmd localization`.
3. Validate each changed JSON file with `scripts\tool.cmd localization validate <file>`.
4. If code references are added or renamed, update the call sites that use `t(...)`, `t("...", { defaultValue: ... })`, or backend `Translate(...)`.
5. Run the localization regression coverage before considering the change done.

## Verification

Validate every changed catalog with `scripts\tool.cmd localization validate <file>`. When regression tests are in scope, run them through `test.cmd` using its documented filters; do not invoke `dotnet test` directly.

## Review Checklist

- Are the same keys present across `cs`, `en`, `ru`, and `uk` for the affected catalog?
- Did the change avoid relying on fallback text for production UI copy?
- Are user-facing strings absent from React/backend code where a catalog key should be used?
- Are Russian and Ukrainian values written in Cyrillic rather than transliterated Latin text?
- Are JSON files sorted and UTF-8 encoded by the script?
- Do translation regression tests pass?
