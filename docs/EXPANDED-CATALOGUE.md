# 3Cat 1.14 preview: expanded native catalogue

## New features
- Native standalone movies from the public cinema facet, in addition to series. Clips under 60 minutes are excluded; metadata may be incomplete if 3Cat omits duration.
- Optional separate SX3 channel, with independent IDs. This is a browsing category, not a verified child-safe catalogue. Configure Jellyfin channel permissions; the conservative channel rating is retained.
- Prefer active stream quality (auto, 1080p, 720p, 480p). Direct Play is advertised only after actual FFmpeg probing; Jellyfin still chooses the supported client path.
- Synopsis when published by the source, programme/episode dates, anime/category labels and the existing bundled 3Cat channel artwork. Existing items may need a metadata refresh; upstream ChannelManager may preserve existing metadata.
- Cache catalogue metadata on disk, reject incomplete pagination or a sudden catalogue reduction, and refresh from the source when stale. A failed cinema refresh does not prevent programme browsing. Successful complete episode snapshots replace withdrawn entries.
- Administrator diagnostics show actual catalogue counts, last refresh and degraded status. No video files or private viewing data are exported.
- Public M3U and optional XMLTV guide for Jellyfin Live TV: https://raw.githubusercontent.com/sabatesduran/Jellyfin-3CAT/feature/dynamic-catalogue/live/3cat.m3u and https://www.tdtchannels.com/epg/TV.xml.gz . Add these in Dashboard > Live TV as an M3U tuner and XMLTV provider; verify the channel mapping. Sources: public 3Cat direct broadcasts and TDTChannels. Broadcasting rights and location restrictions remain in effect.

## Verification
The existing 10.11 build remains the default. CI additionally attempts .NET 10 / Jellyfin.Controller and Model 12.2.0 via MSBuild properties. A passing compilation is not a runtime guarantee. Confirm native search, favourites and resume, movie playback, SX3 permissions, live EPG mapping and image refresh on a disposable Jellyfin 12.2 instance before merging.
 
Pending: dedicated recommendation/popularity/expiring rails, automatic live tuner provisioning, full integration tests against a running server, safe local duplicate reconciliation, episode-level audiodescription verification, and a separately published 12.2 binary. Do not treat SX3 tags as an age rating.
