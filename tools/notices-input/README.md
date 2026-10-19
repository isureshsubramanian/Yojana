# Notices generator inputs

Everything in `THIRD-PARTY-NOTICES.txt` that cannot be read from a package comes from here, never
from an edit of the generated file.

- `repositories/*.json`: licence text for packages that ship only an SPDX identifier, keyed by the
  repository URL in their manifest. Use `matchPackages` when the text was read at exact package commits; an exact match
  takes precedence over the repository-wide fallback. Each entry records the URL the text was taken from and the date.
- `components/*.json`: components that are not NuGet packages (shared frameworks, vendored files).
- `spdx/*.txt`: SPDX standard texts, used only when neither the package nor its repository publishes
  a text; copyright placeholders are left unfilled on purpose.
- `texts/`: the texts themselves, byte-for-byte as downloaded.

Regenerate with `make notices`; `make gate` fails when the committed output drifts.
