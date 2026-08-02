# Because You Watched

Real "Because You Watched" recommendations for Jellyfin.

Jellyfin's built-in recommendations are broken. The "Because you watched X" row on the
home screen returns the **same alphabetical slice of your library for every title** ([jellyfin/jellyfin#16088](https://github.com/jellyfin/jellyfin/issues/16088)). It labels the row after something you watched, then shows you titles that have nothing to do with it.

This plugin fixes that. It builds the row from the movies you **actually watched**, using
Jellyfin's own working similarity engine (the same one behind the `/Items/Similar` API,
which returns genuinely good matches), instead of the broken recommendations endpoint.

## What it does

- **Uses the engine that works.** Jellyfin scores real similarity in `SimilarTo` queries; the plugin drives that directly, so a horror pick recommends horror, not the front of your library.
- **Blends your recent watches.** Instead of a single seed, it weights your last few plays so the row reflects what you've actually been into lately.
- **Hides what you've already seen.** Watched titles are filtered out by default.
- **Never ships a weak row.** If a title has thin similarity data, it backfills with same-genre, well-rated picks so you never get a one-movie "recommendation." This is the exact gap other recommendation rows leave open ([home-sections#243](https://github.com/IAmParadox27/jellyfin-plugin-home-sections/issues/243)).
- **Zero setup, no API keys, no external calls.** Everything runs on your server against your own data.

## Requirements

- Jellyfin **10.11** or later. That is the only requirement for the standalone mode below.

## Install

Two ways to run it. The first needs nothing but this plugin.

### Standalone, recommended (no other plugins)

1. In Jellyfin, **Dashboard → Plugins → Repositories**, add:

   ```
   https://raw.githubusercontent.com/cquest101/jellyfin-plugin-because-you-watched/main/manifest.json
   ```

2. **Catalog → Because You Watched → Install**, then restart Jellyfin.
3. Done. A per-user **Because You Watched** playlist is built automatically and refreshes every 12 hours. To see it right away, run **Dashboard → Scheduled Tasks → "Because You Watched: rebuild playlists" → Run**.

### Home-screen row, optional (premium)

To render it as a real row on the home screen instead of a playlist, you also need IAmParadox27's Home Screen Sections stack. Add his repository:

```
https://www.iamparadox.dev/jellyfin/plugins/manifest.json
```

Install **File Transformation**, **Plugin Pages**, and **Home Screen Sections** from the catalog, restart, then open the hamburger menu → **Modular Home**, enable it, and select the **Because You Watched** section. This plugin registers that section automatically; you just switch it on.

Either way, you can also grab the zip from [Releases](../../releases) and drop it in your Jellyfin `plugins` folder.

## Configuration

Dashboard → Plugins → Because You Watched:

| Setting | Default | What it does |
|---|---|---|
| Row title | Because You Watched | Heading shown above the row. |
| Items in the row | 16 | How many recommendations to show. |
| Recent watches to blend | 3 | 1 is classic single-title; higher blends your last few plays. |
| Backfill floor | 5 | If similarity is thinner than this, fill with same-genre picks. |
| Hide watched | on | Keep already-watched titles out of the row. |

## How it works

The Home Screen Sections plugin lets a server-side plugin register a section and hands it
a user id when the row is built. This plugin registers through that interface (by reflection,
so there is no fragile compile-time dependency), then for each request:

1. Pulls the user's most recently played movies as seeds.
2. Runs Jellyfin's `SimilarTo` query for each seed and scores results by seed recency and rank.
3. Merges and de-duplicates, dropping watched titles and the seeds themselves.
4. Backfills from shared genres if the result set is thin.
5. Returns the ranked list as the row's items.

The recommendation logic lives entirely in `BecauseYouWatchedResults.cs`. It depends only on
Jellyfin core services, so it is easy to read, change, and extend.

## Build

```
dotnet build Jellyfin.Plugin.BecauseYouWatched/Jellyfin.Plugin.BecauseYouWatched.csproj -c Release
```

CI (`.github/workflows/build.yml`) builds on every push and, on a `v*` tag, packages the
zip, prints its md5, and attaches it to a GitHub release. Update `manifest.json`'s `sourceUrl`
and `checksum` from that release.

## License

MIT. See [LICENSE](LICENSE).
