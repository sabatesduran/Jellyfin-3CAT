import fs from 'node:fs';
import vm from 'node:vm';
import assert from 'node:assert/strict';
import test from 'node:test';

test('browse page ships parseable script and real native Jellyfin API calls',()=>{
    const html=fs.readFileSync('Jellyfin.Plugin.ThreeCat/Configuration/browsePage.html','utf8');
    const script=html.match(/<script type="text\/javascript">([\s\S]*?)<\/script>/)[1];
    new vm.Script(script);
    for(const value of ['Continua mirant','Els teus preferits','Novetats','Pel·lícules','ApiClient.getCurrentUserId','/FavoriteItems/','/Items/Resume','#/details?id=','tc-hero','tc-strip'])
        assert.ok(html.includes(value),value);
    const ids=[...html.matchAll(/\bid="([^"]+)"/g)].map(x=>x[1]);
    assert.equal(new Set(ids).size,ids.length);
    assert.ok(!html.includes('innerHTML='));
});
