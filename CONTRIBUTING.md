# Contributing

Thank you for your interest. Issues and pull requests are welcome; for anything large, please open an issue first to
talk it through. [PLAN.md](PLAN.md) holds the design and the decisions made so far, with their reasons.

## Branches

- **`develop`**: where work happens. Open pull requests against `develop`.
- **`main`**: releases only. Merging `develop` into `main` makes a release (see below), so only the maintainer does it.

## Building and testing

Needs the .NET 10 SDK and GStreamer 1.24 or newer with the base, good and bad plugins and libnice (Ubuntu:
`scripts/setup-ubuntu.sh`).

```bash
dotnet build src
dotnet test src                        # the full suite, a few minutes: the media tests run once per mix engine
dotnet test src --filter "FullyQualifiedName~ControlApi"    # just the tests a change touches
```

The suite runs on GStreamer 1.28 and 1.24 in CI. If your change touches the media code (`Media/`, signalling),
please run the full suite before opening the pull request.

## Changes

- Keep a pull request to one change, with tests for new behaviour.
- **CHANGELOG.md:** a change to the server or the load tool adds one brief line under `## Unreleased`:
  `- Server: what changed, and why.` or `- Load tool: …`, written for the people who run them. Changes to CI,
  packaging, tests or documentation get no entry. These lines become the next release's notes.
- The admin UI and the test web client are plain HTML and JavaScript, with no build step; please keep them that way.

## Releases

Merging a pull request from `develop` into `main` releases: the next patch version (the labels `minor` and `major`
choose a bigger step), with the `## Unreleased` entries as its notes, and packages for Ubuntu/Debian, Fedora and
Windows. The details are in [PACKAGING-PLAN.md](PACKAGING-PLAN.md), step 7.

## Licence

By contributing you agree that your contribution is licensed under the project's licence, Apache-2.0
([LICENSE](LICENSE)), as section 5 of that licence describes.
