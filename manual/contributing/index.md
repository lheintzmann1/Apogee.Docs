# Writing documentation

This site is built with [DocFX](https://dotnet.github.io/docfx/) from the
[Apogee.Docs](https://github.com/lheintzmann1/Apogee.Docs) repository.

## What is written by hand, and what is not

Everything under `manual/` is hand-written markdown — edit it directly and open a pull request.

The three API references are **generated** and are not committed. Editing a page under `api/`,
`api-cpp/` or `api-lua/` has no effect: the next build overwrites it. To change what those pages
say, change the documentation comment in the engine sources. See
[Documenting the APIs](documenting-the-api.md).

## Building locally

```bash
git clone https://github.com/lheintzmann1/Apogee.Docs.git
cd Apogee.Docs
./build.sh serve
```

That fetches the engine at the pinned revision, generates all three references, builds the site
and serves it at http://localhost:8080. If you already have an engine checkout beside the docs
repo, it is used as-is.

For prose-only changes, `./build.sh site` skips the API steps and rebuilds in a few seconds.

## Style

- One sentence per idea; prefer short paragraphs over bullet soup.
- Say what something *is* before saying how to use it.
- Code samples should be runnable, not sketched.
- Link to the API reference rather than restating a signature, which will drift.
