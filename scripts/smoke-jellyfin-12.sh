#!/usr/bin/env bash
set -euo pipefail
root="$(mktemp -d)"
name="threecat-smoke"
base="http://127.0.0.1:18096"
auth='MediaBrowser Client="3Cat smoke", Device="CI", DeviceId="threecat-ci", Version="1.0"'
cleanup() {
  docker logs "$name" > "$root/server.log" 2>&1 || true
  cp "$root/server.log" ./threecat-smoke.log
  if [[ "${1:-0}" != 0 ]]; then tail -100 "$root/server.log" >&2; fi
  docker rm -f "$name" >/dev/null 2>&1 || true
  sudo rm -rf "$root"
}
trap 'cleanup "$?"' EXIT
version="${PLUGIN_VERSION:-$(sed -n 's/.*<Version>\([^<]*\)<\/Version>.*/\1/p' Directory.Build.props | head -1)}"
image="${JELLYFIN_IMAGE:-ghcr.io/jellyfin/jellyfin:12.2.20261005-225228}"
expected="${EXPECTED_SERVER_PREFIX:-12.2}"
mkdir -p "$root/config/plugins/3Cat_$version" "$root/cache"
unzip -q "dist/threecat_$version.zip" -d "$root/config/plugins/3Cat_$version"
test -s "$root/config/plugins/3Cat_$version/3cat.png"
# Match the manifest normally produced by Jellyfin when installing from a repository.
jq -nc --arg v "$version" --arg abi "$expected.0.0" '{guid:"d8b0f3a2-6c4e-4f0a-9e1b-2c3d4e5f6a7b",name:"3Cat",version:$v,targetAbi:$abi,status:"Active",autoUpdate:false,category:"General",imagePath:"3cat.png",assemblies:["Jellyfin.Plugin.ThreeCat.dll"]}' > "$root/config/plugins/3Cat_$version/meta.json"
docker run -d --name "$name" --user 0:0 -p 127.0.0.1:18096:8096 \
  -v "$root/config:/config" -v "$root/cache:/cache"  "$image" >/dev/null
for i in $(seq 1 90); do
  if curl -fsS "$base/System/Info/Public" > "$root/public.json" && curl -fsS -H "Authorization: $auth" "$base/Startup/User" >/dev/null; then break; fi
  sleep 2
done
jq -e --arg expected "$expected" '(.Version // .version // "") | startswith($expected)' "$root/public.json"
api() {
  curl --fail-with-body -sS -H "Authorization: $auth" -H 'Content-Type: application/json' "$@"
}
password="$(openssl rand -hex 24)"
api "$base/Startup/User" >/dev/null
api -X POST "$base/Startup/Configuration" -d '{"UICulture":"ca","MetadataCountryCode":"ES","PreferredMetadataLanguage":"ca"}' >/dev/null
api -X POST "$base/Startup/User" -d "$(jq -nc --arg p "$password" '{Name:"threecat-ci",Password:$p}')" >/dev/null
api -X POST "$base/Startup/Complete" >/dev/null
token="$(api -X POST "$base/Users/AuthenticateByName" -d "$(jq -nc --arg p "$password" '{Username:"threecat-ci",Pw:$p}')" | jq -er '.AccessToken // .accessToken')"
authenticated() {
  curl --fail-with-body -sS -H "Authorization: $auth, Token=\"$token\"" -H 'Content-Type: application/json' "$@"
}
authenticated "$base/Plugins" > "$root/plugins.json"
jq 'map({id:(.Id // .id),name:(.Name // .name),version:(.Version // .version),status:(.Status // .status)})' "$root/plugins.json"
jq -e --arg version "$version" 'any(.[]; ((.Id // .id // "")|ascii_downcase|gsub("-";""))=="d8b0f3a26c4e4f0a9e1b2c3d4e5f6a7b" and (.Version // .version)==$version)' "$root/plugins.json"
authenticated "$base/ThreeCat/Dynamic/Status" | jq -e '.State // .state'
authenticated "$base/ThreeCat/Dynamic/Diagnostics" | jq -e '(.schema // .Schema)==1'
# Check ordinary users cannot access configuration or diagnostics.
viewerPassword="$(openssl rand -hex 24)"
authenticated -X POST "$base/Users/New" -d "$(jq -nc --arg p "$viewerPassword" '{Name:"threecat-viewer",Password:$p}')" > "$root/viewer.json"
viewerToken="$(api -X POST "$base/Users/AuthenticateByName" -d "$(jq -nc --arg p "$viewerPassword" '{Username:"threecat-viewer",Pw:$p}')" | jq -er '.AccessToken // .accessToken')"
for endpoint in ThreeCat/Dynamic/Diagnostics ThreeCat/Programs/All ThreeCat/Libraries; do
  status="$(curl -sS -o /dev/null -w '%{http_code}' -H "Authorization: $auth, Token=\"$viewerToken\"" "$base/$endpoint")"
  [[ "$status" == 403 || "$status" == 401 ]]
done
curl -fsS "$base/ThreeCat/Explore/" | grep -q "loginForm"
curl -fsS "$base/ThreeCat/Explore/portal.js" > "$root/portal.js"
grep -q "Users/AuthenticateByName" "$root/portal.js"
status="$(curl -sS -o /dev/null -w '%{http_code}' "$base/ThreeCat/Dynamic/Status")"
[[ "$status" == 401 || "$status" == 403 ]]
echo "Plugin loads, protected administrator API responds, and public portal assets are served."

# Exercise native navigation with a deterministic cached catalogue (no broadcaster calls).
python3 - "$root/cache/3cat-dynamic-catalogue.json" <<'PY'
import datetime, json, sys
now = datetime.datetime.now(datetime.timezone.utc)
items = [
    {"Id":"p:10001","Name":"Native fixture series","Type":1,"FolderType":4,"MediaType":1,"Genres":["Sèrie"],"ProviderIds":{"threecat":"10001"}},
    {"Id":"m:10002","Name":"Native fixture movie","Type":0,"ContentType":3,"MediaType":1,"Genres":["Cinema"],"ProviderIds":{"threecat":"10002"}}
]
json.dump({"Expires":(now+datetime.timedelta(hours=2)).isoformat(),"Items":items,"Signature":"False:False:False","LastFull":now.isoformat()},open(sys.argv[1],"w"))
PY
pluginId="d8b0f3a2-6c4e-4f0a-9e1b-2c3d4e5f6a7b"
authenticated "$base/Plugins/$pluginId/Configuration" | jq 'del(.DynamicCatalogue,.DisableLegacyMirror,.ClassifyPrograms,.SeparateAnime,.SeparateChildren,.DynamicSx3) | .dynamicCatalogue=true | .disableLegacyMirror=true | .classifyPrograms=false | .separateAnime=false | .separateChildren=false | .dynamicSx3=false' > "$root/plugin-config.json"
authenticated -X POST "$base/Plugins/$pluginId/Configuration" --data-binary "@$root/plugin-config.json"
viewerId="$(jq -er '.Id // .id' "$root/viewer.json")"
# A newly created user may default to restricted channels. Explicitly grant channel access.
authenticated "$base/Users/$viewerId" | jq '(.Policy // .policy) | del(.EnableAllChannels,.EnabledChannels,.BlockedChannels,.MaxParentalRating,.BlockUnratedItems) | .enableAllChannels=true | .enabledChannels=[] | .blockedChannels=[] | .maxParentalRating=null | .blockUnratedItems=[]' > "$root/viewer-policy.json"
authenticated -X POST "$base/Users/$viewerId/Policy" --data-binary "@$root/viewer-policy.json"
authenticated "$base/Plugins/$pluginId/Configuration" | jq -e '(.DynamicCatalogue // .dynamicCatalogue)==true'
echo "Native test: dynamic catalogue enabled and viewer policy granted."
viewer() {
  curl --fail-with-body -sS -H "Authorization: $auth, Token=\"$viewerToken\"" -H 'Content-Type: application/json' "$@"
}
viewer "$base/Channels?userId=$viewerId" > "$root/channels.json"
jq '(.Items // .items)|map({name:(.Name // .name),id:(.Id // .id)})' "$root/channels.json"
channelId="$(jq -er '(.Items // .items)[]|select((.Name // .name)=="3Cat a la carta")|(.Id // .id)' "$root/channels.json")"
viewer "$base/Channels/$channelId/Items?userId=$viewerId" > "$root/native-root.json"
jq -e '(.Items // .items)|length==6' "$root/native-root.json"
seriesFolder="$(jq -er '(.Items // .items)[]|select((.Name // .name)=="Sèries")|(.Id // .id)' "$root/native-root.json")"
cinemaFolder="$(jq -er '(.Items // .items)[]|select((.Name // .name)=="Cinema")|(.Id // .id)' "$root/native-root.json")"
viewer "$base/Channels/$channelId/Items?userId=$viewerId&folderId=$seriesFolder&fields=ProviderIds" > "$root/native-series.json"
viewer "$base/Channels/$channelId/Items?userId=$viewerId&folderId=$cinemaFolder&fields=ProviderIds" > "$root/native-movies.json"
jq -e '(.Items // .items)|length==1 and (.[0].Name // .[0].name)=="Native fixture series"' "$root/native-series.json"
jq -e '(.Items // .items)|length==1 and (.[0].Name // .[0].name)=="Native fixture movie"' "$root/native-movies.json"
seriesId="$(jq -er '(.Items // .items)[0]|(.Id // .id)' "$root/native-series.json")"
viewer -X POST "$base/UserFavoriteItems/$seriesId?userId=$viewerId" >/dev/null || viewer -X POST "$base/Users/$viewerId/FavoriteItems/$seriesId" >/dev/null
viewer "$base/Channels/$channelId/Items?userId=$viewerId&folderId=$seriesFolder" | jq -e --arg id "$seriesId" '(.Items // .items)[0]|(.Id // .id)==$id and ((.UserData // .userData)|(.IsFavorite // .isFavorite))==true'
echo "Native hierarchy and ordinary-user favourites verified."
