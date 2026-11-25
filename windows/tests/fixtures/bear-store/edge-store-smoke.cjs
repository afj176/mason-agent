const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const assert = require('node:assert/strict');
const [project, evidence, installation] = process.argv.slice(2);
const {chromium} = require(path.join(installation, '.playwright/package'));
const markup = fs.readFileSync(path.join(project, 'index.html'));
const server = http.createServer((req,res) => {
    if (req.url === '/' || req.url === '/index.html') {
        res.writeHead(200, {'Content-Type':'text/html; charset=utf-8'});
        res.end(markup);
    } else { res.writeHead(404); res.end(); }
});
(async () => {
    await new Promise((resolve,reject) => {server.once('error',reject);server.listen(0,'127.0.0.1',resolve);});
    const origin = 'http://127.0.0.1:' + server.address().port;
    const run = [], errors = [];
    let browser;
    try {
        browser = await chromium.launch({channel:'msedge',headless:true});
        for (const width of [390,1280]) {
            const context = await browser.newContext({viewport:{width,height:860},serviceWorkers:'block'});
            await context.route('**/*', route => new URL(route.request().url()).origin === origin ? route.continue() : route.abort());
            const page = await context.newPage();
            page.on('pageerror', err => errors.push(err.message));
            await page.goto(origin);
            const report = {width, totals:[], shelf:[], receipts:[]};
            const tiles = new Map();
            for (const [n, item, price] of [[1,'Honey',10],[2,'Berries',5],[3,'Salmon',15]]) {
                const heading = page.getByRole('heading',{name:item,exact:true});
                assert.equal(await heading.count(),1);
                const tile = heading.locator('xpath=ancestor::*[.//button][1]');
                assert.equal(await tile.getByRole('button').count(),1);
                tiles.set(n,tile);
                assert.equal(Number((await tile.innerText()).match(/\$\s*([0-9.]+)/)[1]),price);
                report.shelf.push([item,price]);
            }
            async function total(expected) {
                const value = (await page.locator('#cart-total').innerText()).match(/-?\d+(?:\.\d+)?/g);
                assert.equal(value?.length,1);
                assert.equal(Number(value[0]),expected);
                report.totals.push(expected);
            }
            async function buy(n) {await tiles.get(n).getByRole('button').click();}
            async function checkout() {
                await page.locator('#checkout').click();
                await total(0);
                assert.equal((await page.locator('#cart-items').innerText()).trim(),'');
                const receipt=(await page.locator('#receipt').innerText()).match(/\bORD-[\w-]+/);
                assert.ok(receipt,'checkout must issue an order ID');
                await page.reload();
                assert.ok((await page.locator('#receipt').innerText()).includes(receipt[0]),'last order survives reload');
                return receipt[0];
            }
            for (const expected of [10,20,27,36]) {await buy(1);await total(expected);}
            await buy(2); await buy(2); await total(46);
            await buy(3); await buy(3); await total(76);
            await page.screenshot({path:path.join(evidence,'mixed-cart-'+width+'.png')});
            report.receipts.push(await checkout());
            await buy(3); await total(15);
            report.receipts.push(await checkout());
            assert.notEqual(report.receipts[0],report.receipts[1],'each order must get a fresh ID');
            await page.screenshot({path:path.join(evidence,'receipt-'+width+'.png')});
            await context.close();run.push(report);
        }
        assert.deepEqual(errors,[]);
        fs.writeFileSync(path.join(evidence,'edge-smoke.json'),JSON.stringify({edge:browser.version(),run},null,2));
        console.log('PASS two viewports, three shelf prices, discount boundary, mixed-cart total, distinct and persisted receipts.');
    } finally {if(browser) await browser.close();await new Promise(resolve=>server.close(resolve));}
})().catch(e=>{console.error(e.message);process.exitCode=1;});
