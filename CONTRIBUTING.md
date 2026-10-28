# Contributing to Yojana

Thank you for helping. Issues, ideas and pull requests are all welcome.

## Before you start

- For anything larger than a small fix, open an issue first so we can agree the approach. New behaviour, schema
  changes and new or changed contracts (APIs, messages, events) are designed in a short decision record before code.
- Set up the environment with [DEVELOPING.md](DEVELOPING.md).

## Making a change

1. Branch from `main`.
2. Keep modules loosely coupled: a module talks to another only through its `.Contracts` project, commands and
   events, never through its tables or internals.
3. Add tests that fail without your change and pass with it. Database behaviour belongs in the database test
   projects.
4. Run the local gate before you push:
   ```bash
   make test        # database-free tests
   make test-db     # needs YOJANA_TEST_SQL pointing at a disposable database
   make web-test
   make gate        # licences, notices, external-name audit, bundle budget, client rules
   ```
5. Use [Conventional Commits](https://www.conventionalcommits.org/): `feat:`, `fix:`, `docs:`, `refactor:`,
   `test:`, `chore:`.

## Dependencies

Every new package, font, icon set, model or data file needs, in the same pull request:

- a licence the policy in `tools/gates/licence-policy.json` allows (copyleft and unclear licences need a
  maintainer decision first);
- the regenerated third-party notices (`make notices`), committed with the change.

## Names

Source, tests, comments and user-visible text describe behaviour, not other products. External product names are
allowed only where they are protocol facts inside a connector (`make names` checks this).

## Licence of contributions

Yojana is licensed under the [Apache License 2.0](LICENSE). By submitting a contribution you agree that it is
licensed under the same terms (Apache-2.0, section 5).
