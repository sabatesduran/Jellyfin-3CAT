# 3Cat 2.0 development

Development branch only. Do not merge into the public preview or publish an update until the release gates pass.

## Implemented in this branch

- Build and package against Jellyfin 10.11/.NET 9 and 12.2/.NET 10, using isolated runtime smoke containers.
- Atomic bounded JSON catalogue and episode snapshots; per-programme locking, retry backoff and three complete observations before removing missing entries.
- Playback membership/expiry checks; SX3 episode filtering based on explicit audience information. SX3 remains conservative and is **not** a parental-control guarantee.
- Multiple real HTTPS playback variants, preferred quality first, bounded parallel media probing, distinct published subtitle tracks.
- Same-origin responsive web portal at `/ThreeCat/Explore/`, with Jellyfin login, channel access, search, favourites, programme/season detail, resume and next-up rails when returned by Jellyfin, recommendations based on favourite genres and links to the native player.
- Optional Web menu link: adds an in-memory entry to the public configuration; never writes the installed web client.
- Assisted, explicitly confirmed M3U/XMLTV setup; checks existing provider URLs and falls back to manual setup if the server API cannot be verified.
- Administrator-only, whitelisted diagnostics export.
- Unit tests, static JS checks, Playwright fixture and screenshot artifacts. Fixtures are not evidence of live playback.

## Remaining release blockers

- All development CI jobs must complete successfully for the same commit, including real runtime smoke for both server versions. Earlier builds and unit tests passed; startup smoke returned HTTP 503 during initial server setup. The readiness check has been revised.
- Exercise a normal user and restricted user on real Jellyfin, including permission isolation for the portal, native channel playback and the optional menu link; check real client behaviour (web, TV, mobile).
- Verify catalogue and film classification against actual current 3Cat responses, including trailers, short films, missing metadata, and pagination. No claim of a complete cinema catalogue until verified.
- True incremental catalogue updates, cross-library deduplication and richer artwork/age ratings require additional validated source data and implementation. The persistent store is bounded JSON, not SQLite.
- Verify actual audio-description availability, subtitles and quality negotiation on real media, and the native Live TV/EPG mapping. The portal links to the native Jellyfin details/player and does not implement an independent player. Popularity/expiring-soon rails are not yet verified or delivered.
- Test install/upgrade/rollback and package manifest with the intended public version. Do not use the current development ZIP as a replacement for the installed preview.

## Privacy and scope

The portal keeps its token in memory, never places it in a URL or persistent browser storage, and communicates only with the same Jellyfin origin. It does not copy media or delete local library files. The standalone portal may require a separate login to the native Jellyfin client; it does not transfer its session token.

The existing `feature/dynamic-catalogue` preview is unchanged.
