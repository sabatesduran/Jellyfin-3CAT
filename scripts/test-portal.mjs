import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';

const html=fs.readFileSync('Jellyfin.Plugin.ThreeCat/Portal/index.html','utf8');
const js=fs.readFileSync('Jellyfin.Plugin.ThreeCat/Portal/portal.js','utf8');

test('portal script parses and has no persistent token storage',()=>{
    new vm.Script(js);
    assert.doesNotMatch(js,/localStorage|sessionStorage|document\.cookie|innerHTML\s*=/);
    assert.match(js,/Users\/AuthenticateByName/);
    assert.match(js,/Sessions\/Logout/);
    assert.match(js,/ChannelId/);
});
test('all static DOM references exist',()=>{
    const ids=new Set([...html.matchAll(/\bid="([^"]+)"/g)].map(x=>x[1]));
    for(const m of js.matchAll(/\$\("([^"]+)"\)/g))assert.ok(ids.has(m[1]),m[1]);
    assert.match(html,/src="portal.js"/);
    assert.match(html,/href="portal.css"/);
});
