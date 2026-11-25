// Offline outcome check for --evaluate-empty-project. Supply Playwright and an installed Chromium browser.
const fs = require("node:fs/promises");
const path = require("node:path");
const { chromium } = require("playwright");

async function main() {
    const [browserExe, ...trials] = process.argv.slice(2);
    if (!browserExe || !trials.length) throw new Error("Expected a Chromium executable and one or more evaluation directories.");
    const browser = await chromium.launch({ executablePath: browserExe, headless: true, chromiumSandbox: true });
    const results = [];
    try {
        for (const trial of trials) {
            const report = JSON.parse(await fs.readFile(path.join(trial, "report.json"), "utf8"));
            const root = await fs.realpath(path.join(trial, "food-bear"));
            const origin = "https://mason-eval.invalid";
            const contentTypes = { ".html": "text/html", ".htm": "text/html", ".js": "text/javascript",
                ".css": "text/css", ".png": "image/png", ".jpg": "image/jpeg", ".svg": "image/svg+xml", ".webp": "image/webp" };
            const entry = report.pages.find(p => /^index\.html?$/i.test(p)) ?? report.pages[0];
            if (!entry) { results.push({ trial, passed: false, reason: "No root-level HTML entry point" }); continue; }
            for (const width of [390, 1280]) {
                const context = await browser.newContext({ viewport: { width, height: 900 }, serviceWorkers: "block", acceptDownloads: false });
                try {
                    await context.route("**/*", async route => {
                        const url = new URL(route.request().url());
                        if (url.origin !== origin || route.request().method() !== "GET") return route.abort();
                        try {
                            const file = await fs.realpath(path.resolve(root, "." + decodeURIComponent(url.pathname)));
                            const relative = path.relative(root, file);
                            const type = contentTypes[path.extname(file).toLowerCase()];
                            if (!type || path.isAbsolute(relative) || relative.split(path.sep).some(p => p.startsWith("."))) return route.abort();
                            return route.fulfill({ body: await fs.readFile(file), contentType: type, headers: {
                                "Content-Security-Policy": "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'none'; frame-src 'none'; object-src 'none'; form-action 'none'; base-uri 'none'"
                            } });
                        } catch { return route.abort(); }
                    });
                    const page = await context.newPage();
                    const errors = [];
                    page.on("pageerror", error => errors.push(error.message));
                    await page.goto(origin + "/" + encodeURIComponent(entry), { waitUntil: "load", timeout: 15000 });
                    const text = await page.locator("body").innerText({ timeout: 5000 });
                    const headings = await page.locator("h1:visible,h2:visible,[role=heading]:visible").allTextContents();
                    const passed = report.created && report.State === "Completed" && errors.length === 0 &&
                        headings.length > 0 && text.trim().length >= 40 && /bear/i.test(text) && /shop|store|cart|buy|product|\$\d/i.test(text);
                    results.push({ trial: path.basename(trial), entry, width, title: await page.title(), headings: headings.length,
                        visibleCharacters: text.length, errors, passed });
                } finally { await context.close(); }
            }
        }
    } finally { await browser.close(); }
    console.log(JSON.stringify(results, null, 2));
    if (results.some(result => !result.passed)) process.exitCode = 1;
}
main().catch(error => { console.error(error); process.exitCode = 1; });
