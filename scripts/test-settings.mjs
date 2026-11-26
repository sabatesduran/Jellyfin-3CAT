import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';

const html = fs.readFileSync('Jellyfin.Plugin.ThreeCat/Configuration/configPage.html', 'utf8');
const source = html.match(/<script type="text\/javascript">([\s\S]*?)<\/script>/)[1];

function harness(config = {}) {
    const elements = new Map();
    const el = id => {
        if (!elements.has(id)) elements.set(id, {
            checked: false, value: '', hidden: false, open: false,
            disabled: false, textContent: '', addEventListener() {},
            classList: { toggle() {}, add() {} }
        });
        return elements.get(id);
    };
    let saved;
    const context = vm.createContext({
        document: { querySelector: el }, setTimeout() {},
        ApiClient: {
            getPluginConfiguration: async () => structuredClone(config),
            updatePluginConfiguration: async (_, data) => { saved = JSON.parse(JSON.stringify(data)); },
            ajax: async () => {},
            getJSON: async () => ({state:'ready',programmes:12,films:3,sx3:2}),
            getUrl: value => value
        }
    });
    vm.runInContext(source, context);
    context.ThreeCatConfig.loaded = true;
    return { el, context, saved: () => saved };
}

test('settings script parses and all element IDs are unique', () => {
    new vm.Script(source);
    const ids = [...html.matchAll(/\bid="([^"]+)"/g)].map(m => m[1]);
    assert.equal(new Set(ids).size, ids.length);
});

test('four legacy flag combinations round-trip through the mode selector', () => {
    const {el, context} = harness();
    for (const [mode, dynamic, disableMirror] of [
        ['dynamic', true, true], ['legacy', false, false],
        ['both', true, false], ['off', false, true]
    ]) {
        assert.equal(context.threeCatModeFromFlags(dynamic, disableMirror), mode);
        el('#ThreeCatMode').value = mode;
        context.threeCatApplyMode();
        assert.equal(el('#ThreeCatDynamicCatalogue').checked, dynamic);
        assert.equal(el('#ThreeCatDisableLegacyMirror').checked, disableMirror);
        assert.equal(el('#ThreeCatLegacySettings').hidden, false);
        assert.equal(el('#ThreeCatManualModeHint').hidden, !disableMirror);
        assert.equal(el('#ThreeCatCreateLibrary').disabled, disableMirror);
        assert.equal(el('#ThreeCatRunNow').disabled, mode === 'off');
    }
});

test('dynamic save preserves existing manual settings and avoids duplicate JSON keys', async () => {
    const old = {programs:[{Id:'12345', Title:'Example'}], destinationPath:'/config/3cat', tmdbApiKey:'fixture', kindLibraries:[{Category:'Anime',Library:'Anime'}], dynamicCatalogue:false, disableLegacyMirror:false, writeSubtitles:false, classifyPrograms:true};
    const {el, context, saved} = harness(old);
    el('#ThreeCatMode').value = 'dynamic';
    el('#ThreeCatWriteSubtitles').checked = true;
    el('#ThreeCatClassifyPrograms').checked = false;
    context.threeCatApplyMode();
    await context.threeCatSave();
    const result = saved();
    for (const key of ['programs','destinationPath','tmdbApiKey','kindLibraries']) assert.deepEqual(result[key], old[key]);
    assert.equal(result.DynamicCatalogue,true);
    assert.equal(result.DisableLegacyMirror,true);
    assert.equal(result.WriteSubtitles,true);
    assert.equal(result.ClassifyPrograms,false);
    assert.equal('dynamicCatalogue' in result,false);
});


test('advanced settings remain accessible and can be saved in dynamic mode', async () => {
    const {el, context, saved} = harness({dynamicCatalogue:true,disableLegacyMirror:true});
    el('#ThreeCatMode').value = 'dynamic';
    el('#ThreeCatLegacySettings').open = true;
    context.threeCatApplyMode();
    el('#ThreeCatDestinationPath').value = '/config/3cat';
    el('#ThreeCatSeparateAnime').checked = true;
    el('#ThreeCatSeparateChildren').checked = true;
    await context.threeCatSave();
    const result = saved();
    assert.equal(result.DynamicCatalogue,true);
    assert.equal(result.DisableLegacyMirror,true);
    assert.equal(result.DestinationPath,'/config/3cat');
    assert.equal(result.SeparateAnime,true);
    assert.equal(result.SeparateChildren,true);
});


test('dynamic controls persist independently of the manual library', async () => {
    const {el, context, saved} = harness({dynamicCatalogue:true,disableLegacyMirror:true});
    el('#ThreeCatMode').value = 'dynamic';
    context.threeCatApplyMode();
    el('#ThreeCatDynamicSx3').checked = true;
    el('#ThreeCatDynamicQuality').value = '720';
    await context.threeCatSave();
    assert.equal(saved().DynamicSx3,true);
    assert.equal(saved().DynamicQuality,'720');
    assert.equal(el('#ThreeCatSx3Row').hidden,false);
});
