import {chromium} from 'playwright';
import {createServer} from 'node:http';
import {readFileSync,mkdirSync} from 'node:fs';
import assert from 'node:assert/strict';

const root='Jellyfin.Plugin.ThreeCat/Portal/';
const movie={Id:'movie1',Name:'Pel·lícula de prova',Type:'Movie',Genres:['Cinema'],Overview:'Una pel·lícula de ficció.',ChannelId:'main',UserData:{IsFavorite:false}};
const series={Id:'series1',Name:'Sèrie de prova',Type:'Series',IsFolder:true,Overview:'Una sèrie amb una temporada.',Genres:['Ficció'],ChannelId:'main',UserData:{IsFavorite:false}};
const child={Id:'child1',Name:'Programa SX3 de prova',Type:'Series',IsFolder:true,ChannelId:'child'};
let favorite=false;const requests=[];
const server=createServer((req,res)=>{
    const u=new URL(req.url,'http://localhost');requests.push({method:req.method,path:u.pathname,auth:!!req.headers.authorization});
    const json=(data,code=200)=>{res.writeHead(code,{'Content-Type':'application/json'});res.end(JSON.stringify(data))};
    const items=data=>json({Items:data,TotalRecordCount:data.length});
    const asset=u.pathname.match(/^\/jf\/ThreeCat\/Explore\/(index\.html|portal\.js|portal\.css)?$/);
    if(asset){const f=asset[1]||'index.html';res.writeHead(200,{'Content-Type':f.endsWith('.js')?'text/javascript':f.endsWith('.css')?'text/css':'text/html'});res.end(readFileSync(root+f));return}
    if(u.pathname==='/jf/Users/AuthenticateByName')return json({AccessToken:'fixture-token',User:{Id:'u1',Name:'Usuari de prova'},ServerId:'server1'});
    if(!req.headers.authorization?.includes('fixture-token'))return json({},401);
    if(u.pathname==='/jf/Channels')return items([{Id:'main',Name:'3Cat a la carta'},{Id:'child',Name:'SX3 a la carta'}]);
    if(u.pathname==='/jf/Channels/main/Items'){
        const folder=u.searchParams.get('folderId');
        if(folder==='series1')return items([{Id:'season1',Name:'Temporada 1',Type:'Season',IsFolder:true}]);
        if(folder==='season1')return items([{Id:'episode1',Name:'Primer episodi',Type:'Episode',ChannelId:'main',IndexNumber:1}]);
        if(folder==='category-series')return items([series]);
        if(folder==='category-cinema')return items([{...movie,UserData:{IsFavorite:favorite}}]);
        return items([{Id:'category-series',Name:'Sèries',Type:'Folder',IsFolder:true},{Id:'category-cinema',Name:'Cinema',Type:'Folder',IsFolder:true}]);
    }
    if(u.pathname==='/jf/Channels/child/Items')return items([child]);
    if(u.pathname==='/jf/UserFavoriteItems/movie1'){favorite=req.method==='POST';return json({IsFavorite:favorite})}
    if(u.pathname==='/jf/Sessions/Logout'){res.writeHead(204);res.end();return}
    if(['/jf/UserItems/Resume','/jf/Shows/NextUp','/jf/Items'].includes(u.pathname))return items([]);
    return json({},404);
});
await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
const url='http://127.0.0.1:'+server.address().port+'/jf/ThreeCat/Explore/';
const browser=await chromium.launch({headless:true});
try{
    const page=await browser.newPage({viewport:{width:1440,height:900}});
    const errors=[];page.on('pageerror',e=>errors.push(e.message));
    await page.goto(url);
    await page.getByLabel('Usuari',{exact:true}).fill('test');
    await page.getByLabel('Contrasenya').fill('fixture-password');
    await page.getByRole('button',{name:'Entra',exact:true}).click();
    await page.getByText('3 títols disponibles').waitFor();
    mkdirSync('artifacts',{recursive:true});
    await page.screenshot({path:'artifacts/portal-desktop.png',fullPage:true});
    await page.getByRole('button',{name:'Pel·lícules',exact:true}).click();
    await page.getByRole('button',{name:'Preferit: Pel·lícula de prova'}).click();
    await page.waitForFunction(()=>document.querySelector('[aria-label="Preferit: Pel·lícula de prova"]')?.getAttribute('aria-pressed')==='true');
    await page.getByRole('button',{name:'La meva llista',exact:true}).click();
    await page.getByText('Pel·lícula de prova',{exact:true}).waitFor();
    await page.getByRole('button',{name:'Sèries i programes',exact:true}).click();
    await page.getByRole('button',{name:/Sèrie de prova/}).first().click();
    await page.getByText('Primer episodi',{exact:false}).waitFor();
    const href=await page.getByRole('link',{name:'Reprodueix',exact:true}).getAttribute('href');
    assert.match(href,/\/jf\/web\/#\/details\?id=episode1/);
    assert.ok(!href.includes('fixture-token'));
    await page.setViewportSize({width:390,height:844});
    await page.screenshot({path:'artifacts/portal-mobile.png',fullPage:true});
    await page.getByRole('button',{name:'Tanca la sessió'}).click();
    await page.getByRole('button',{name:'Entra',exact:true}).waitFor();
    assert.equal(await page.evaluate(()=>localStorage.length+sessionStorage.length),0);
    assert.deepEqual(errors,[]);
    assert.ok(requests.some(r=>r.method==='POST'&&r.path==='/jf/UserFavoriteItems/movie1'&&r.auth));
    console.log('Portal fixture: login, browse, favourite, detail, native playback link, mobile and logout passed.');
}finally{await browser.close();await new Promise(resolve=>server.close(resolve))}
