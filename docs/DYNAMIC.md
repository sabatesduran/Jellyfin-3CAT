# 3Cat a la carta (experimental)

Native virtual channel for Jellyfin 10.11, based on Lowitle/Jellyfin-3CAT. The original mirror remains available. The fork keeps the original plugin GUID so do not install both distributions at the same time.

## Setup after a successful build

1. Back up Jellyfin's config and use a test instance first.
2. Install the verified package for your Jellyfin server version. Remove the upstream repository from automatic updates if using this fork.
3. Open Dashboard → Plugins → 3Cat, enable **3Cat a la carta**, save, and restart. The switch suggests disabling the old mirror; it does not delete existing files. No destination folder or programme selection is necessary.
4. Run **Actualitza el catàleg virtual de 3Cat** in Scheduled Tasks, then open the new channel.

The channel indexes programmes and loads seasons/episodes on demand; the playback callback obtains available streams, probes them with Jellyfin's FFmpeg and caches published WebVTT subtitles when enabled. The periodic task refreshes the root and the union of users' favourite programmes. It does not download video files. Existing STRM entries have different Jellyfin IDs, so their viewing history and favourites do not automatically migrate. Keep the original library until you have verified the new one.

## Verification

- CI: existing Build Plugin workflow compiles, runs the upstream and new unit tests, and packages the plugin. Target: .NET 9, Jellyfin.Controller/Model 10.11.6.
- The existing Release workflow can publish a tagged, tested main commit and generate the manifest checksum from the real zip; no fake entry is included. Do not run Release from a feature branch.
- On a disposable server, verify the channel appears and search returns a programme after indexing; open a season and play an available video; check subtitles and resume.
- Mark a programme favourite as user A, ensure user B does not inherit that favourite, then restart and refresh. Confirm newly published episodes appear.
- Test incomplete catalogue responses, expiry, blocked channel permissions, cancellation and server restart. No region or access restrictions should be bypassed.
- Test on Web and native TV clients. Global search of episodes not yet materialized is not guaranteed; the native client UX varies.
- Standalone films outside the programme catalogue, separate child-safe SX3 channel, permanent reconciliation of withdrawn programmes, and a tested build for Jellyfin 12 are not yet implemented. Genre labels alone do not make content child-safe. The full channel is rated R conservatively; configure access via Jellyfin's user permissions. Disabled mode stops browsing/playback but previously indexed entries may remain visible until removed by Jellyfin.
- Root and episode caches retain known entries during the process lifetime, and reject incomplete page sets; expired episodes are filtered. Withdrawn programmes may remain until restart; existing history is not deliberately erased.
- No production install or successful CI run has yet been verified. Keep the PR in draft until compilation and smoke tests pass.

## Rollback

Disable the dynamic catalogue and re-enable the legacy mirror; if necessary stop Jellyfin and restore the previous plugin binary/config backup. Do not delete the existing media or reset the Jellyfin database.
