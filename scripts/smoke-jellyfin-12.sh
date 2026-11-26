#!/usr/bin/env bash
set -euo pipefail
root="$(mktemp -d)"
name="threecat-smoke"
base="http://127.0.0.1:18096"
auth='MediaBrowser Client="3Cat smoke", Device="CI", DeviceId="threecat-ci", Version="1.0"'
cleanup() {
  docker logs "$name" > "$root/server.log" 2>&1 || true
  cp "$root/server.log" ./threecat-smoke.log
  docker rm -f "$name" >/dev/null 2>&1 || true
  sudo rm -rf "$root"
}
trap cleanup EXIT
version="${PLUGIN_VERSION:-$(sed -n 's/.*<Version>\([^<]*\)<\/Version>.*/\1/p' Directory.Build.props | head -1)}"
image="${JELLYFIN_IMAGE:-ghcr.io/jellyfin/jellyfin:12.2.20261005-225228}"
expected="${EXPECTED_SERVER_PREFIX:-12.2}"
mkdir -p "$root/config/plugins/3Cat_$version" "$root/cache"
unzip -q "dist/threecat_$version.zip" -d "$root/config/plugins/3Cat_$version"
test -s "$root/config/plugins/3Cat_$version/3cat.png"
docker run -d --name "$name" --user 0:0 -p 127.0.0.1:18096:8096 \
  -v "$root/config:/config" -v "$root/cache:/cache"  "$image" >/dev/null
for i in $(seq 1 90); do
  if curl -fsS "$base/System/Info/Public" > "$root/public.json"; then break; fi
  sleep 2
done
jq -e --arg expected "$expected" '(.Version // .version // "") | startswith($expected)' "$root/public.json"
api() {
  curl --fail-with-body -sS -H "Authorization: $auth" -H 'Content-Type: application/json' "$@"
}
password="$(openssl rand -hex 24)"
api "$base/Startup/User" >/dev/null
api -X POST "$base/Startup/User" -d "$(jq -nc --arg p "$password" '{Name:"threecat-ci",Password:$p}')" >/dev/null
api -X POST "$base/Startup/Complete" >/dev/null
token="$(api -X POST "$base/Users/AuthenticateByName" -d "$(jq -nc --arg p "$password" '{Username:"threecat-ci",Pw:$p}')" | jq -er '.AccessToken // .accessToken')"
authenticated() {
  curl --fail-with-body -sS -H "Authorization: $auth, Token=\"$token\"" -H 'Content-Type: application/json' "$@"
}
authenticated "$base/Plugins" | jq -e --arg version "$version" 'any(.[]; ((.Id // .id // "")|ascii_downcase)=="d8b0f3a2-6c4e-4f0a-9e1b-2c3d4e5f6a7b" and (.Version // .version)==$version)'
authenticated "$base/ThreeCat/Dynamic/Status" | jq -e '.State // .state'
echo "Plugin loads and administrator status endpoint responds."
