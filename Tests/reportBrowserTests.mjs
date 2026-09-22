import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { existsSync } from "node:fs";
import { mkdtemp, readFile, readdir, rm, mkdir, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { inflateRawSync } from "node:zlib";
import { createHash } from "node:crypto";
import { pathToFileURL } from "node:url";

const url = process.argv[2] ?? "http://127.0.0.1:5187/";
const fixture = process.argv[3] ?? "gitCommits";
const collectedIncidentFixture = fixture === "incident-available-export" || fixture === "incident-unkeyed-export";
const incidentFixture = fixture === "incidentManagement" || collectedIncidentFixture;
const roleIconFixture = fixture === "git-role-icons";
const artifacts = process.env.REPORT_TEST_ARTIFACTS;
if (artifacts) await mkdir(artifacts, { recursive: true });
const browserPath = [
    process.env.REPORT_TEST_BROWSER,
    "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe",
    "C:\\Program Files (x86)\\Google\\Chrome\\Application\\chrome.exe",
    "C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe",
    "C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe"
].find(path => path && existsSync(path));
assert(browserPath, "Chrome/Edge is required; set REPORT_TEST_BROWSER to its executable.");
const profile = await mkdtemp(join(tmpdir(), "productivity-report-browser-"));
const downloads = join(profile, "downloads");
await mkdir(downloads);
const browser = spawn(browserPath, ["--headless=new", "--no-first-run", "--no-default-browser-check",
    "--disable-gpu", "--remote-debugging-port=0", `--user-data-dir=${profile}`, "about:blank"],
    { stdio: "ignore" });
let socket;
let pageSession;
let nextId = 0;
let checks = 0;
const pending = new Map();
const completedDownloads = [];
const moduleRequests = [];
const interceptionErrors = [];
const sleep = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));
async function until(test, label, timeout = 120000) {
    const end = Date.now() + timeout;
    while (Date.now() < end) {
        const result = await test();
        if (result) return result;
        await sleep(100);
    }
    throw new Error(`Timed out: ${label}`);
}
async function cdp(method, params = {}, sessionId) {
    const id = ++nextId;
    return await new Promise((resolve, reject) => {
        const timeout = setTimeout(() => { pending.delete(id); reject(new Error(`CDP timeout: ${method}`)); }, 120000);
        pending.set(id, {
            resolve: value => { clearTimeout(timeout); resolve(value); },
            reject: error => { clearTimeout(timeout); reject(error); }
        });
        socket.send(JSON.stringify({ id, method, params, ...(sessionId ? { sessionId } : {}) }));
    });
}
async function evaluate(expression) {
    const result = await cdp("Runtime.evaluate", { expression, awaitPromise: true, returnByValue: true }, pageSession);
    assert(!result.exceptionDetails, JSON.stringify(result.exceptionDetails));
    return result.result.value;
}
async function clickExport() {
    await until(async () => {
        const state = await evaluate(`({
            ready:document.querySelector('.analysis-export-btn')?.disabled === false,
            error:document.querySelector('[role=alert].report-export-status')?.textContent ?? ''
        })`);
        assert(!state.error.includes("could not be initialized"), state.error);
        return state.ready;
    }, "save action ready");
    const point = await evaluate(`(() => {
        const button = document.querySelector('.analysis-export-btn');
        button.scrollIntoView({block:'center'});
        const rect = button.getBoundingClientRect();
        return {x:rect.x + rect.width / 2,y:rect.y + rect.height / 2};
    })()`);
    await cdp("Input.dispatchMouseEvent", { type: "mousePressed", ...point, button: "left", clickCount: 1 }, pageSession);
    await cdp("Input.dispatchMouseEvent", { type: "mouseReleased", ...point, button: "left", clickCount: 1 }, pageSession);
}
function unzip(bytes) {
    const eocd = bytes.lastIndexOf(Buffer.from([0x50, 0x4b, 0x05, 0x06]));
    assert(eocd >= 0, "ZIP end-of-central-directory missing");
    const entries = bytes.readUInt16LE(eocd + 10);
    let position = bytes.readUInt32LE(eocd + 16);
    const result = new Map();
    for (let index = 0; index < entries; index++) {
        assert.equal(bytes.readUInt32LE(position), 0x02014b50);
        const method = bytes.readUInt16LE(position + 10);
        const compressedSize = bytes.readUInt32LE(position + 20);
        const size = bytes.readUInt32LE(position + 24);
        const nameLength = bytes.readUInt16LE(position + 28);
        const extraLength = bytes.readUInt16LE(position + 30);
        const commentLength = bytes.readUInt16LE(position + 32);
        const localOffset = bytes.readUInt32LE(position + 42);
        const name = bytes.subarray(position + 46, position + 46 + nameLength).toString("utf8");
        const dataStart = localOffset + 30 + bytes.readUInt16LE(localOffset + 26) + bytes.readUInt16LE(localOffset + 28);
        const data = bytes.subarray(dataStart, dataStart + compressedSize);
        const inflated = method === 8 ? inflateRawSync(data) : data;
        assert.equal(inflated.length, size, "ZIP entry uncompressed length mismatch");
        assert(!result.has(name), "Duplicate ZIP entry");
        result.set(name, inflated.toString("utf8"));
        position += 46 + nameLength + extraLength + commentLength;
    }
    return result;
}
try {
    const activePortFile = join(profile, "DevToolsActivePort");
    await until(() => existsSync(activePortFile), "browser debugging port", 30000);
    const [port, endpoint] = (await readFile(activePortFile, "utf8")).trim().split(/\r?\n/);
    socket = new WebSocket(`ws://127.0.0.1:${port}${endpoint}`);
    await new Promise((resolve, reject) => { socket.addEventListener("open", resolve, { once: true }); socket.addEventListener("error", reject, { once: true }); });
    socket.addEventListener("message", event => {
        const data = JSON.parse(event.data);
        if (data.method === "Browser.downloadProgress" && data.params.state === "completed")
            completedDownloads.push(data.params.guid);
        if (data.method === "Fetch.requestPaused") {
            const requestUrl = new URL(data.params.request.url);
            moduleRequests.push(requestUrl);
            const legacy = !requestUrl.search;
            const command = legacy ? "Fetch.fulfillRequest" : "Fetch.continueRequest";
            const params = legacy ? {
                requestId: data.params.requestId, responseCode: 200,
                responseHeaders: [
                    { name: "Content-Type", value: "text/javascript" },
                    { name: "Cache-Control", value: "public, max-age=31536000" }
                ],
                body: Buffer.from("export function beginDownload() {} export function cancelDownload() {} export async function downloadReport() { return false; }").toString("base64")
            } : { requestId: data.params.requestId };
            void cdp(command, params, data.sessionId).catch(error => interceptionErrors.push(error.message));
        }
        if (data.id && pending.has(data.id)) {
            const request = pending.get(data.id);
            pending.delete(data.id);
            if (data.error) request.reject(new Error(JSON.stringify(data.error)));
            else request.resolve(data.result);
        }
    });
    await cdp("Browser.setDownloadBehavior", { behavior: "allow", downloadPath: downloads, eventsEnabled: true });
    const { targetId } = await cdp("Target.createTarget", { url: "about:blank" });
    ({ sessionId: pageSession } = await cdp("Target.attachToTarget", { targetId, flatten: true }));
    await cdp("Page.enable", {}, pageSession);
    if (fixture === "stale-module") {
        await cdp("Fetch.enable", { patterns: [{ urlPattern: "*reportExport.module.js*", requestStage: "Request" }] }, pageSession);
        await cdp("Page.addScriptToEvaluateOnNewDocument", {
            source: `window.legacyReportModule = import('./reportExport.module.js');`
        }, pageSession);
    }
    await cdp("Page.navigate", { url }, pageSession);
    await until(() => evaluate("!!document.querySelector('.analysis-export-btn') && !!window.Blazor"), "prepared application");
    assert.equal(await evaluate("typeof window.Radzen"), "object", "Radzen static assets did not load");
    // A server circuit event proves hydration, rather than clicking a prerender-only button.
    await until(() => evaluate("!!document.querySelector('.commit-summary-card,.work-item-entry') || (document.querySelector('.analysis-summary-table button')?.click(), false)"), "interactive contributor expansion");
    assert.equal(await evaluate("document.querySelector('.analysis-export-btn').textContent.trim()"), '\u{1F4E5} Export Reports',
        "The export button must retain its icon and show only Export Reports without file extensions");
    const before = await evaluate("({html:document.querySelector('.analysis-summary-table').innerHTML,heading:document.querySelector('.table-title-header').textContent})");
    const onlineWorkFields = fixture === "workItems" ? await evaluate(`Object.fromEntries([...document.querySelectorAll('.work-item-info strong')]
        .map(node => { const match = /^(\\S+)\\s+(.+:)$/.exec(node.textContent);
            return [match[2],{icon:match[1],value:node.parentElement.textContent.slice(node.textContent.length).trim()}]; }))`) : {};
    const onlineWorkSummary = fixture === "workItems"
        ? await evaluate("[...document.querySelectorAll('.contributor-summary > div')].map(node => node.textContent.trim())") : [];
    const onlineRoleIcons = await evaluate(`Object.fromEntries([...document.querySelectorAll('.commit-entry div')]
        .map(node => /^(\\S+)\\s+(Author|Committer):/.exec(node.textContent.trim()))
        .filter(Boolean).map(match => [match[2] + ':',match[1]]))`);
    const onlineHierarchyExpression = `(() => {
        const nodes = [...document.querySelectorAll('.org-tree-container li.org-tree-node')];
        return nodes.map(node => ({
            name:node.querySelector(':scope > .org-tree-row .fw-bold').textContent,
            parent:nodes.indexOf(node.parentElement.closest('li.org-tree-node'))
        }));
    })()`;
    const onlineHierarchy = await evaluate(onlineHierarchyExpression);
    assert(before.heading.includes("Example Manager"), "Teams heading lost its manager");
    assert(before.heading.includes(incidentFixture ? "1 owning team" : "1 selection"), "Teams heading lost its source count");
    if (fixture === "stale-module") {
        assert.equal(await evaluate("legacyReportModule.then(module => typeof module.bindExportButton)"), "undefined",
            "The regression requires an older module already cached in this document");
    }
    assert.equal(await evaluate("isSecureContext && typeof showSaveFilePicker === 'function'"), true,
        "This browser does not expose the native Save As API in a secure context");
    // Headless cannot operate a native OS Save As dialog. A picker/writer shim captures real streamed
    // bytes and uses the test profile's download directory only after the simulated writer commits.
    await evaluate(`(() => {
        const state = window.reportSaveFixture = {calls:0, opens:0, closes:0, aborts:0, mode:'save', names:[]};
        window.reportFixturePicker = window.showSaveFilePicker = options => {
            if (!navigator.userActivation.isActive) throw new Error('Save As lost the native click user activation');
            state.calls++;
            state.names.push(options.suggestedName);
            const mode = state.mode;
            if (mode === 'cancel') return Promise.reject(new DOMException('Cancelled', 'AbortError'));
            const handle = {
                name: 'Chosen report.zip',
                async createWritable() {
                    state.opens++;
                    let bytes;
                    return {
                        async write(value) {
                            if (mode === 'disk-error') throw new DOMException('Fixture disk write denied', 'NotAllowedError');
                            bytes = value;
                        },
                        async close() {
                            const url = URL.createObjectURL(new Blob([bytes], {type:'application/zip'}));
                            const anchor = document.createElement('a');
                            try {
                                anchor.href = url;
                                anchor.download = handle.name;
                                document.body.appendChild(anchor);
                                anchor.click();
                                await new Promise(resolve => setTimeout(resolve, 0));
                                state.closes++;
                            } finally {
                                anchor.remove();
                                URL.revokeObjectURL(url);
                            }
                        },
                        async abort() { state.aborts++; }
                    };
                }
            };
            return mode === 'pending'
                ? new Promise(resolve => { state.select = () => resolve(handle); })
                : Promise.resolve(handle);
        };
    })()`);
    if (fixture === "skipped-export") {
        await evaluate("reportSaveFixture.mode = 'cancel'");
        await clickExport();
        await until(() => evaluate("reportSaveFixture.calls === 1 && !document.querySelector('.analysis-export-btn').disabled"), "cancelled picker cleanup");
        assert.equal(await evaluate("document.querySelector('[role=alert].report-export-status')?.textContent ?? ''"), "");
        assert.equal(completedDownloads.length, 0);
        await evaluate("window.showSaveFilePicker = undefined");
        await clickExport();
        await until(() => evaluate("document.querySelector('[role=alert].report-export-status')?.textContent.includes('requires Microsoft Edge or Google Chrome')"), "unsupported browser error");
        await evaluate("window.showSaveFilePicker = window.reportFixturePicker; reportSaveFixture.mode = 'pending'");
    }
    if (fixture === "picker-stale") await evaluate("reportSaveFixture.mode = 'pending'");
    await clickExport();
    if (fixture === "skipped-export" || fixture === "picker-stale") {
        await until(() => evaluate("document.querySelector('[role=status].report-export-status')?.textContent.includes('Save As dialog')"), "picker before collection");
        assert.equal(completedDownloads.length, 0);
        assert.equal(await evaluate("reportSaveFixture.opens"), 0);
        if (fixture === "skipped-export") {
            assert.equal(await evaluate("document.querySelector('.analysis-summary-table').innerHTML"), before.html);
            await evaluate("reportSaveFixture.select(); reportSaveFixture.mode = 'save'");
        }
    }
    if (fixture === "failed-export") {
        await until(() => evaluate("document.querySelector('[role=alert].report-export-status')?.textContent.includes('incomplete changed files')"), "explicit export failure");
        assert.equal(await evaluate("document.querySelector('.analysis-summary-table').innerHTML"), before.html);
        assert.equal((await readdir(downloads)).filter(name => name.endsWith(".zip")).length, 0);
    } else if (fixture === "slow-export" || fixture === "picker-stale") {
        if (fixture === "slow-export")
            await until(() => evaluate("document.querySelector('[role=status].report-export-status')?.textContent.includes('synthetic export fixture')"), "collection started");
        await evaluate("[...document.querySelectorAll('.nav-tabs-vertical button')].find(button => button.textContent.trim()==='Details').click()");
        if (fixture === "picker-stale") await evaluate("reportSaveFixture.select()");
        await sleep(5500);
        assert.equal((await readdir(downloads)).filter(name => name.endsWith(".zip")).length, 0, "Superseded export downloaded");
        assert.equal(await evaluate("reportSaveFixture.opens"), 0, "Stale operation opened the selected file");
    } else {
        if (fixture === "large") {
            await evaluate("document.querySelector('.analysis-summary-table button').click()");
            await until(() => evaluate("!document.querySelector('.commit-summary-card')"), "responsive Blazor collapse during export", 5000);
            await evaluate("document.querySelector('.analysis-summary-table button').click()");
            await until(() => evaluate("!!document.querySelector('.commit-summary-card')"), "responsive Blazor expansion during export", 5000);
        }
        await until(async () => {
            checks++;
            const time = Date.now();
            assert.equal(await evaluate("document.querySelector('.table-title-header').textContent.includes('Example Manager')"), true);
            assert(Date.now() - time < 2000, "Browser became unresponsive during export");
            const failure = await evaluate("document.querySelector('[role=alert].report-export-status')?.textContent ?? ''");
            assert(!failure, failure);
            assert.equal(await evaluate("document.querySelector('#blazor-error-ui')?.style.display === 'block'"), false, "The Blazor circuit failed");
            return completedDownloads.length > 0 && (await readdir(downloads)).find(name => name.endsWith(".zip"));
        }, "complete ZIP download");
        const name = (await readdir(downloads)).find(name => name.endsWith(".zip"));
        assert.equal(name, "Chosen report.zip", "The user's selected filename was not used");
        assert.equal(await evaluate("reportSaveFixture.names.every(name => name.endsWith('.zip') && name !== 'Chosen report.zip')"), true);
        const entries = unzip(await readFile(join(downloads, name)));
        assert.equal(entries.size, 2);
        assert([...entries.keys()].every(name => name.endsWith(".html")), "Markdown was not replaced by detailed HTML");
        const [detailedName, detailed] = [...entries].find(([name]) => name.endsWith("_Detailed.html"));
        const [htmlName, html] = [...entries].find(([name]) => !name.endsWith("_Detailed.html"));
        assert.equal(detailedName, htmlName.slice(0, -5) + "_Detailed.html");
        assert(html.includes(detailedName));
        for (const content of [html, detailed]) {
            assert(!/<script\b|<img\b|<iframe\b/i.test(content), "Export contains external/script/image content");
        }
        const extracted = join(profile, "extracted");
        await mkdir(extracted);
        await Promise.all([...entries].map(([file, content]) => writeFile(join(extracted, file), content)));
        const { targetId: detailTarget } = await cdp("Target.createTarget", {
            url: pathToFileURL(join(extracted, htmlName)).href
        });
        let detailView;
        try {
            const { sessionId: detailSession } = await cdp("Target.attachToTarget", { targetId: detailTarget, flatten: true });
            await cdp("Emulation.setDeviceMetricsOverride", {
                width: 1440, height: 1000, deviceScaleFactor: 1, mobile: false
            }, detailSession);
            await until(async () => {
                const result = await cdp("Runtime.evaluate", {
                    expression: `(() => { const link = document.querySelector('a[href$="_Detailed.html"]'); if (!link) return false; link.click(); return true; })()`,
                    returnByValue: true
                }, detailSession);
                return result.result.value;
            }, "summary link to detailed HTML", 10000);
            const initialFolds = await until(async () => {
                const result = await cdp("Runtime.evaluate", {
                    expression: `document.readyState === 'complete' && document.querySelector('details.report-person-details') && (() => {
                        const sections = [...document.querySelectorAll('details')];
                        return {collapsed:sections.every(section => !section.open && !section.hasAttribute('open')
                                && !section.querySelector(':scope > div').checkVisibility()),
                            headingsVisible:[...document.querySelectorAll('details.report-person-details > summary')].every(summary => summary.checkVisibility())};
                    })()`, returnByValue:true
                }, detailSession);
                assert(!result.exceptionDetails, JSON.stringify(result.exceptionDetails));
                return result.result.value;
            }, "initially collapsed detailed HTML", 30000);
            assert.deepEqual(initialFolds, {collapsed:true,headingsVisible:true},
                "Every person and item must start collapsed with the person headings available for navigation");
            const initialItems = await cdp("Runtime.evaluate", {
                expression: `(() => {
                    const person = document.querySelector('details.report-person-details:has(details.report-item-details)');
                    if (!person) return {items:false};
                    person.querySelector(':scope > summary').click();
                    const items = [...person.querySelectorAll('details.report-item-details')];
                    return {items:true,personOpen:person.open,collapsed:items.every(item => !item.open
                            && !item.querySelector(':scope > .report-item-content').checkVisibility()),
                        headingsVisible:items.every(item => item.querySelector(':scope > summary').checkVisibility())};
                })()`, returnByValue:true
            }, detailSession);
            assert.deepEqual(initialItems.result.value, fixture === "skipped-export"
                ? {items:false} : {items:true,personOpen:true,collapsed:true,headingsVisible:true},
                "Expanding a person must reveal the item headings without expanding any item");
            await cdp("Runtime.evaluate", {
                expression: "document.querySelectorAll('details:not([open])').forEach(section => section.querySelector(':scope > summary').click())"
            }, detailSession);
            detailView = await until(async () => {
                const result = await cdp("Runtime.evaluate", {
                    expression: `document.readyState === 'complete' && document.fonts.status === 'loaded' && document.querySelector('main h1') && ({
                        text:document.querySelector('main').innerText,
                        cells:[...document.querySelectorAll('td')].map(cell => cell.textContent),
                        tables:document.querySelectorAll('table').length,
                        headings:document.querySelectorAll('main h1,main h2,main h3').length,
                        unsafe:document.querySelectorAll('script,img,iframe,object,embed').length,
                        font:getComputedStyle(document.body).fontFamily,
                        fontSize:getComputedStyle(document.body).fontSize,
                        embeddedFontLoaded:[...document.fonts].some(font => font.family.includes('Montserrat') && font.status === 'loaded'),
                        icons:document.querySelectorAll('.report-icon').length,
                        sections:document.querySelectorAll('.report-section-heading').length,
                        tableWrappers:document.querySelectorAll('.report-table-scroll').length,
                        numericFont:getComputedStyle(document.querySelector('table')).fontVariantNumeric,
                        palette:{
                            scheme:getComputedStyle(document.documentElement).colorScheme,
                            body:getComputedStyle(document.body).backgroundColor,
                            main:getComputedStyle(document.querySelector('main')).backgroundColor,
                            text:getComputedStyle(document.querySelector('main')).color
                        },
                        roster:document.querySelectorAll('.report-roster-member').length,
                        rosterLinks:document.querySelectorAll('.report-roster-member a.report-member-link').length,
                        rosterNames:[...document.querySelectorAll('.report-roster-member a.report-member-link')].map(link => link.textContent),
                        detailLines:[...document.querySelectorAll('div.report-line')].map(node => node.textContent),
                        workItemSummaries:[...document.querySelectorAll('h3.report-person-heading')].map(heading => {
                            const section = heading.closest('details.report-person-details')?.querySelector(':scope > .report-person-content > .report-lines');
                            return section?.matches('.report-lines') && section.textContent.trim().startsWith('\u{1F4CA} Summary for ')
                                ? {person:heading.textContent,lines:[...section.children].map(node => node.textContent.trim())} : null;
                        }).filter(Boolean),
                        workItemEntries:[...document.querySelectorAll('h4.report-record-heading')]
                            .filter(node => node.textContent.startsWith('\u{1F4CB} Work Item #'))
                            .map(node => ({heading:node.textContent,fields:[...node.closest('details.report-item-details').querySelectorAll(':scope > .report-item-content > .report-lines > div.report-line')]
                                .map(line => {
                                    const label = line.querySelector(':scope > .report-label');
                                    return {label:label?.textContent,icon:line.querySelector(':scope > .report-icon')?.textContent,
                                        value:label?.nextSibling?.textContent.trim()};
                                }).filter(field => field.label)})),
                        workItemHistories:[...document.querySelectorAll('h5')]
                            .filter(heading => /State Change History$/i.test(heading.textContent)).map(heading => {
                                let text = '';
                                for (let node = heading.nextElementSibling; node && !/^H[1-6]$/.test(node.tagName); node = node.nextElementSibling) {
                                    if (node.querySelector('h1,h2,h3,h4,h5,h6')) break;
                                    text += node.textContent;
                                }
                                return text;
                            }),
                        commitMetadataFields:[...document.querySelectorAll('div.report-line')].map(node => ({
                            label:node.querySelector(':scope > .report-label')?.textContent,
                            icon:node.querySelector(':scope > .report-icon')?.textContent
                        })).filter(field => ['Commit ID:', 'Organization:', 'Repository ID:', 'Author date:', 'Committer date:'].includes(field.label)),
                        commitRoleFields:[...document.querySelectorAll('div.report-line')].map(node => ({
                            label:node.querySelector(':scope > .report-label')?.textContent,
                            icon:node.querySelector(':scope > .report-icon')?.textContent
                        })).filter(field => field.label === 'Author:' || field.label === 'Committer:'),
                        personHeadings:[...document.querySelectorAll('h3.report-person-heading')].map(node => node.textContent),
                        rosterHierarchy:(() => {
                            const nodes = [...document.querySelectorAll('.report-roster-hierarchy li')];
                            return nodes.map(node => ({
                                name:node.querySelector(':scope > .report-roster-member a').textContent,
                                parent:nodes.indexOf(node.parentElement.closest('li')),
                                left:node.querySelector(':scope > .report-roster-member').getBoundingClientRect().left
                            }));
                        })(),
                        memberIncidentDetails:${incidentFixture} ? [...document.querySelectorAll('.report-roster-member a.report-member-link')].map(link => {
                            const heading = document.getElementById(link.hash.slice(1));
                            const buckets = [];
                            let bucket;
                            for (let node = heading.closest('details.report-person-details')?.querySelector(':scope > .report-person-content')?.firstElementChild; node && !/^H[23]$/.test(node.tagName); node = node.nextElementSibling) {
                                const match = node.tagName === 'H4' && /^(Created|Contributed) ICMs \\(([0-9]+)\\)$/.exec(node.textContent);
                                if (match) {
                                    bucket = {kind:match[1],count:Number(match[2]),incidents:[],actions:0};
                                    buckets.push(bucket);
                                } else if (bucket && node.matches('details.report-item-details')) {
                                    const title = node.querySelector(':scope > summary > h5');
                                    if (title?.textContent.startsWith('Incident ')) {
                                        bucket.incidents.push(title.textContent);
                                        bucket.actions += [...node.querySelectorAll('table')].filter(table =>
                                            [...table.querySelectorAll('th')].some(cell => cell.textContent === 'Action ID'))
                                            .reduce((count, table) => count + table.querySelectorAll('tbody tr').length, 0);
                                    }
                                }
                            }
                            return {member:link.textContent,buckets};
                        }) : [],
                        icmLinks:document.querySelectorAll('a.report-icm-link').length,
                        recordedUrls:[...document.querySelectorAll('li')].filter(item =>
                            item.querySelector(':scope > strong')?.textContent === 'Recorded URL:').map(item => ({
                                text:item.querySelector(':scope > a')?.textContent,
                                href:item.querySelector(':scope > a')?.getAttribute('href')
                            })),
                        viewIncidentLinks:[...document.querySelectorAll('a')].filter(link => link.textContent === 'View Incident').length,
                        viewWorkItemLinks:[...document.querySelectorAll('a')].filter(link => link.textContent.trim().endsWith('View Work Item')).length,
                        commitUrlLinks:[...document.querySelectorAll('a')].filter(link => link.textContent.trim() === '\u{1F310} URL').length,
                        brokenLinks:[...document.querySelectorAll('a[href^="#"]')].filter(link => {
                            const target = document.getElementById(link.hash.slice(1));
                            return !target || !target.matches('h3,h4,tr') || !target.textContent.trim();
                        }).length,
                        duplicateIds:[...document.querySelectorAll('[id]')].length - new Set([...document.querySelectorAll('[id]')].map(node => node.id)).size,
                        pageOverflow:document.documentElement.scrollWidth > innerWidth + 1,
                        remoteResources:performance.getEntriesByType('resource').filter(resource => /^https?:/.test(resource.name)).length
                    })`,
                    returnByValue: true
                }, detailSession);
                assert(!result.exceptionDetails, JSON.stringify(result.exceptionDetails));
                return result.result.value;
            }, "readable detailed HTML", 30000);
            assert(detailView.tables > 0 && detailView.headings > 1, "Details were not rendered into readable HTML structure");
            assert.equal(detailView.unsafe, 0);
            assert(detailView.font.includes("Montserrat") && detailView.embeddedFontLoaded, "The offline report font did not load");
            assert.equal(detailView.fontSize, "14px");
            assert(detailView.icons > 0 && detailView.sections > 0, "Report icons or section hierarchy are missing");
            assert.equal(detailView.tableWrappers, detailView.tables);
            assert.equal(detailView.numericFont, "tabular-nums");
            assert.equal(detailView.pageOverflow, false, "Desktop report content overflows the page");
            assert.equal(detailView.remoteResources, 0, "Detailed presentation must remain self-contained");
            assert.deepEqual(detailView.palette, {
                scheme: "dark", body: "rgb(11, 18, 32)", main: "rgb(17, 28, 46)", text: "rgb(230, 237, 247)"
            }, "Only detailed screen presentation should use the dark palette");
            assert(detailView.roster > 0);
            assert.equal(detailView.rosterLinks, detailView.roster, "Every roster member needs an actionable detail link");
            assert.equal(detailView.brokenLinks, 0, "Offline report links must resolve to real detail headings or evidence rows");
            assert.equal(detailView.duplicateIds, 0, "Each anchor ID must appear only once");
            if (fixture === "gitCommits" || roleIconFixture) {
                for (const label of ['Source-qualified key:', 'Source memberships:', 'Author identity:', 'Committer identity:'])
                    assert(!detailView.detailLines.some(line => line.startsWith(label)),
                        "Redundant commit footer field must be absent: " + label);
                for (const [label, icon] of Object.entries({
                    'Commit ID:':'\u{1F511}', 'Organization:':'\u{1F3E2}', 'Repository ID:':'\u{1F5C2}\uFE0F',
                    'Author date:':'\u{1F5D3}\uFE0F', 'Committer date:':'\u{1F4C5}'
                })) {
                    const fields = detailView.commitMetadataFields.filter(field => field.label === label);
                    assert.equal(fields.length, roleIconFixture ? 3 : 2, "Remaining commit footer field must be retained: " + label);
                    assert(fields.every(field => field.icon === icon), "Each commit must use the metadata icon for " + label);
                }
                assert.equal(detailView.commitUrlLinks, 0, "Detailed Git must omit the redundant URL link");
                const commitUrls = detailView.recordedUrls.filter(link => link.href?.includes("/commits/"));
                assert.equal(commitUrls.length, roleIconFixture ? 3 : 2);
                assert(commitUrls.every(link => link.text === link.href), "Each commit's Recorded URL value must itself be clickable");
                assert.deepEqual(onlineRoleIcons, { 'Author:': '\u{1F464}', 'Committer:': '\u270D\uFE0F' });
                assert.equal(detailView.commitRoleFields.length, (roleIconFixture ? 3 : 2) * 2);
                for (const field of detailView.commitRoleFields)
                    assert.equal(field.icon, onlineRoleIcons[field.label], "Each exported commit role must use its on-screen icon");
                assert(detailView.personHeadings.some(heading => heading.startsWith('\u270D\uFE0F Committer:')),
                    "Committer detail headings must use the writing-hand icon without losing their h3 navigation target");
            }
            if (roleIconFixture) {
                const pair = '\u{1F464} Author: Example Contributor / \u270D\uFE0F Committer: Zoe Lead';
                assert.equal(detailView.cells.filter(cell => cell === pair).length, 2,
                    "Both detailed summary tables must distinguish the author/committer pair");
                assert(detailView.personHeadings.some(heading => heading.includes(pair)),
                    "The shared contribution heading must preserve both role icons");
            }
            if (incidentFixture) {
                assert(detailView.icmLinks > 0, "ICM paths must link to their report details");
                assert.equal(detailView.memberIncidentDetails.length, detailView.roster);
                for (const member of detailView.memberIncidentDetails) {
                    const expectedCount = member.member.includes("Example Contributor") ? 1 : 0;
                    assert.deepEqual(member.buckets.map(bucket => [bucket.kind, bucket.count]),
                        [["Created", expectedCount], ["Contributed", expectedCount]],
                        "Every member's Created and Contributed sections must match existing incident credits");
                    for (const bucket of member.buckets) {
                        assert.equal(bucket.incidents.length, expectedCount, "Every credited incident needs full member-scoped details");
                        assert.equal(new Set(bucket.incidents).size, expectedCount, "Member buckets must deduplicate incidents");
                        if (bucket.kind === "Contributed") assert.equal(bucket.actions > 0, expectedCount > 0,
                            "Contributed incidents need the member's recorded action evidence");
                    }
                }
                assert.equal(detailView.viewIncidentLinks, 0, "Redundant View Incident links must be removed");
                assert(detailView.recordedUrls.length > 0 && detailView.recordedUrls.every(link =>
                    /^https?:/.test(link.href ?? "") && link.text === link.href),
                    "Recorded incident URLs must be clickable at the field itself");
            }
            assert.equal(detailView.rosterHierarchy.length, 4, "Hierarchy fixture must include the manager, both branches and an indirect report");
            assert.deepEqual(detailView.rosterHierarchy.map(node => node.parent), onlineHierarchy.map(node => node.parent),
                "Detailed roster parent/report relationships must match the online hierarchy");
            detailView.rosterHierarchy.forEach((node, index, nodes) => {
                assert(node.name.includes(onlineHierarchy[index].name), "Export changed captured hierarchy order or member identity");
                if (node.parent >= 0) assert(node.left > nodes[node.parent].left, "Reports must be visually nested beneath their actual manager");
            });
            if (artifacts) {
                const shot = await cdp("Page.captureScreenshot", { format: "png" }, detailSession);
                await writeFile(join(artifacts, `${fixture}-detailed-desktop.png`), Buffer.from(shot.data, "base64"));
                await cdp("Runtime.evaluate", { expression: "document.querySelector('table').scrollIntoView({block:'start'})" }, detailSession);
                const tableShot = await cdp("Page.captureScreenshot", { format: "png" }, detailSession);
                await writeFile(join(artifacts, `${fixture}-detailed-table.png`), Buffer.from(tableShot.data, "base64"));
            }
            const folding = await cdp("Runtime.evaluate", {
                expression: `(() => {
                    const sections = [...document.querySelectorAll('details.report-person-details')];
                    const first = sections[0];
                    const summary = first?.querySelector(':scope > summary');
                    const content = first?.querySelector(':scope > .report-person-content');
                    const expanded = sections.length > 1 && sections.every(section => section.open && section.querySelector(':scope > .report-person-content').checkVisibility());
                    summary?.click();
                    summary?.focus();
                    return {expanded,collapsed:!first?.open && !content?.checkVisibility(),
                        independent:sections.slice(1).every(section => section.open),
                        headingVisible:summary?.checkVisibility(),focused:document.activeElement === summary,
                        noNestedSections:sections.every(section => !section.querySelector('details.report-person-details'))};
                })()`, returnByValue: true
            }, detailSession);
            assert.deepEqual(folding.result.value, { expanded:true,collapsed:true,independent:true,
                headingVisible:true,focused:true,noNestedSections:true },
                "Clicking one user's heading must fold only that complete section, keeping the heading visible");
            await cdp("Page.bringToFront", {}, detailSession);
            const checkDisclosureKeys = async selector => {
                for (const [key, code, expected] of [["Enter", "Enter", true], [" ", "Space", false]]) {
                    await cdp("Input.dispatchKeyEvent", { type:"keyDown", key, code, text:key === "Enter" ? "\r" : " ",
                        windowsVirtualKeyCode:key === "Enter" ? 13 : 32 }, detailSession);
                    await cdp("Input.dispatchKeyEvent", { type:"keyUp", key, code, windowsVirtualKeyCode:key === "Enter" ? 13 : 32 }, detailSession);
                    const keyboard = await cdp("Runtime.evaluate", {
                        expression: `document.querySelector(${JSON.stringify(selector)}).open`, returnByValue:true
                    }, detailSession);
                    assert.equal(keyboard.result.value, expected, "Enter and Space must toggle the focused disclosure offline");
                }
            };
            await checkDisclosureKeys('details.report-person-details');
            const itemFolding = await cdp("Runtime.evaluate", {
                expression: `(() => {
                    document.querySelectorAll('details.report-person-details:not([open])').forEach(section => section.querySelector(':scope > summary').click());
                    const items = [...document.querySelectorAll('details.report-item-details')];
                    if (items.length === 0) return {count:0};
                    const first = items[0];
                    const summary = first?.querySelector(':scope > summary');
                    const content = first?.querySelector(':scope > .report-item-content');
                    const parent = first?.parentElement.closest('details.report-person-details');
                    const expanded = items.length > 0 && items.every(item => item.open && item.querySelector(':scope > .report-item-content').checkVisibility());
                    const completeHeadings = [...document.querySelectorAll('details.report-person-details h4,details.report-person-details h5')]
                        .filter(heading => /^(?:\\u{1F4CB} Work Item #|Work item |\\u{1F504} Commit:|Incident )/.test(heading.textContent))
                        .every(heading => heading.parentElement.matches('summary') && heading.closest('details').matches('.report-item-details'));
                    summary?.click();
                    const independent = parent?.open && items.slice(1).every(item => item.open);
                    parent?.querySelector(':scope > summary').click();
                    parent?.querySelector(':scope > summary').click();
                    summary?.focus();
                    return {expanded,completeHeadings,independent,preserved:parent?.open && !first?.open,
                        hidden:!content?.checkVisibility(),headingVisible:summary?.checkVisibility(),focused:document.activeElement === summary,
                        nesting:items.every(item => item.parentElement.closest('details')?.matches('.report-person-details') && !item.querySelector('details'))};
                })()`, returnByValue:true
            }, detailSession);
            if (fixture === "skipped-export") {
                assert.deepEqual(itemFolding.result.value, {count:0}, "Unavailable sources must not fabricate item folds");
            } else {
                assert.deepEqual(itemFolding.result.value, { expanded:true,completeHeadings:true,independent:true,preserved:true,
                    hidden:true,headingVisible:true,focused:true,nesting:true },
                    "Each record must fold independently within its person, retaining item state when the person is reopened");
                await checkDisclosureKeys('details.report-item-details');
            }
            await cdp("Runtime.evaluate", {
                expression: "document.querySelectorAll('details.report-item-details[open]').forEach(section => section.querySelector(':scope > summary').click())"
            }, detailSession);
            await cdp("Runtime.evaluate", {
                expression: "document.querySelectorAll('details.report-person-details[open]').forEach(section => section.querySelector(':scope > summary').click())"
            }, detailSession);
            for (const selector of [
                ".report-roster-member a.report-member-link",
                ...(incidentFixture ? ["a.report-icm-link"] : [])
            ]) {
                const navigation = await cdp("Runtime.evaluate", {
                    expression: `(() => {
                        const link = document.querySelector(${JSON.stringify(selector)});
                        link.click();
                        const target = document.getElementById(link.hash.slice(1));
                        const rect = target.getBoundingClientRect();
                        return {hash:location.hash === link.hash, visible:rect.top < innerHeight && rect.bottom > 0,
                            focused:document.activeElement === target, highlighted:getComputedStyle(target).outlineStyle};
                    })()`,
                    returnByValue: true
                }, detailSession);
                assert.deepEqual(navigation.result.value, { hash: true, visible: true, focused: true, highlighted: "solid" },
                    "Roster and ICM links must navigate, focus and highlight their detail destination offline");
            }
            await cdp("Runtime.evaluate", {
                expression: "document.querySelectorAll('details:not([open])').forEach(section => section.querySelector(':scope > summary').click())"
            }, detailSession);
            await cdp("Emulation.setDeviceMetricsOverride", {
                width: 390, height: 844, deviceScaleFactor: 1, mobile: true
            }, detailSession);
            const mobile = await cdp("Runtime.evaluate", {
                expression: "document.documentElement.scrollWidth <= innerWidth + 1", returnByValue: true
            }, detailSession);
            assert.equal(mobile.result.value, true, "Narrow-screen report overflows instead of wrapping or scrolling its tables");
            if (artifacts) {
                await cdp("Runtime.evaluate", { expression: "scrollTo(0,0)" }, detailSession);
                const shot = await cdp("Page.captureScreenshot", { format: "png" }, detailSession);
                await writeFile(join(artifacts, `${fixture}-detailed-mobile.png`), Buffer.from(shot.data, "base64"));
            }
            await cdp("Runtime.evaluate", {
                expression: "[...document.querySelectorAll('details[open]')].reverse().forEach(section => section.querySelector(':scope > summary').click())"
            }, detailSession);
            await cdp("Emulation.setEmulatedMedia", { media: "print" }, detailSession);
            const print = await cdp("Runtime.evaluate", {
                expression: "({background:getComputedStyle(document.body).backgroundColor,shadow:getComputedStyle(document.querySelector('main')).boxShadow,overflow:getComputedStyle(document.querySelector('.report-table-scroll')).overflowX,scheme:getComputedStyle(document.documentElement).colorScheme,heading:getComputedStyle(document.querySelector('h3')).color})",
                returnByValue: true
            }, detailSession);
            assert.deepEqual(print.result.value, { background: "rgb(255, 255, 255)", shadow: "none", overflow: "visible",
                scheme: "light", heading: "rgb(13, 27, 63)" },
                "Print presentation must remove page decoration without clipping tables");
            const printedDetails = await cdp("Runtime.evaluate", {
                expression: `([...document.querySelectorAll('details')].every(section =>
                    !section.open && section.querySelector(':scope > div').checkVisibility()
                    && [...section.querySelector(':scope > div').children].every(child => child.checkVisibility())))`,
                returnByValue: true
            }, detailSession);
            assert.equal(printedDetails.result.value, true, "Printing must include all records and evidence even when both person and item levels are folded");
            await cdp("Emulation.setEmulatedMedia", { media: "" }, detailSession);
            const restoredFolds = await cdp("Runtime.evaluate", {
                expression: "[...document.querySelectorAll('details')].every(section => !section.open && !section.querySelector(':scope > div').checkVisibility())",
                returnByValue: true
            }, detailSession);
            assert.equal(restoredFolds.result.value, true, "Printing must preserve the user's screen folding choices");
        } finally {
            await cdp("Target.closeTarget", { targetId: detailTarget });
        }
        const detailText = detailView.text;
        assert(detailText.includes("Example Contributor"));
        if (fixture === "gitCommits" || roleIconFixture || fixture === "large" || fixture === "workItems" || incidentFixture) {
            const workItems = fixture === "workItems";
            const incidents = incidentFixture;
            const incidentCounts = collectedIncidentFixture ? ["0", "1", "0", "1", "1"] : ["0", "1", "1", "1", "1"];
            if (workItems) {
                assert.equal(detailView.workItemSummaries.length, 4, "Every roster member needs a per-person summary");
                const summary = detailView.workItemSummaries.find(summary => summary.person.includes('Example Contributor'));
                assert(summary && onlineWorkSummary.length > 0);
                for (const line of onlineWorkSummary)
                    assert(summary.lines.includes(line), "The per-person summary must match the on-screen value: " + line);
                for (const zero of detailView.workItemSummaries.filter(summary => !summary.person.includes('Example Contributor')))
                    assert(zero.lines.includes('Total Work Items Completed: 0') && zero.lines.includes('Work Items with Active Work Time: 0 of 0'),
                        "Zero-activity members must remain explicit without invented work");
                assert.equal(detailView.workItemHistories.length, 2, "Both work-item history locations must remain present");
                assert(detailView.workItemHistories.every(history => history.includes('Example Contributor')
                    && history.includes('email: dev@example.test') && !history.includes('principal:') && !history.includes('ID:')),
                    "State histories must retain names and emails without Principal or ID annotations");
                assert.equal(detailView.viewWorkItemLinks, 0, "Work-item details must omit separate View Work Item links");
                assert.equal(detailView.recordedUrls.length, 2, "Recorded URL must remain in both member details and the audit appendix");
                assert(detailView.recordedUrls.every(link => link.href === 'https://example.test/item/42' && link.text === link.href),
                    "Each work item's Recorded URL value must itself be clickable in both detail locations");
                assert.equal(detailView.workItemEntries.length, 1, "Each work item needs a detailed entry under its member");
                assert.equal(detailView.workItemEntries[0].heading, '\u{1F4CB} Work Item #42 - Task');
                for (const [label, expected] of Object.entries(onlineWorkFields)) {
                    const field = detailView.workItemEntries[0].fields.find(field => field.label === label);
                    assert(field, "Detailed work item is missing " + label);
                    assert.deepEqual({icon:field.icon,value:field.value}, expected,
                        "Work-item details must match the on-screen field value and icon: " + label);
                }
                assert(detailView.workItemEntries[0].fields.some(field => field.label === 'Assignee:' && field.value.includes('dev@example.test')));
                assert(detailText.includes("\u{1F3C6} TEAM WORK ITEM COMPLETION DETAILS BY PERSON"));
                assert(detailText.includes("2026-07-09 12:00:00 - Example Contributor")
                    && detailText.includes("New \u2192 Active (spent 1d in 'New')"));
                assert(detailText.includes("State change history") && detailText.includes("Total time by state"));
                assert(detailText.indexOf("TEAM WORK ITEM SUMMARY BY AREA PATH") < detailText.indexOf("Captured inputs and export query context"));
            } else if (incidents) {
                assert(detailText.includes("TEAM INCIDENT DETAILS BY ICM PATH") && detailText.includes("Incident Details:"));
                assert(detailText.includes("Contributors with recorded in-period actions"));
                assert(detailText.includes("TTD by severity"));
                if (collectedIncidentFixture) {
                    assert(detailText.includes("Synthetic mitigation"));
                    assert(detailView.cells.includes(fixture === "incident-unkeyed-export" ? "Not provided" : "100"));
                    for (const text of [detailText, html]) {
                        assert(text.includes("Available warehouse data"), "Available-data coverage disclosure missing");
                        assert(!text.includes("Verified history window"), "Export invented coverage verification");
                        if (fixture === "incident-unkeyed-export") {
                            assert(text.includes("no HistoryId is invented"), "Missing-ID handling was not disclosed");
                        }
                    }
                } else {
                    assert(detailText.includes("Example mitigation") && detailText.includes("action-1"));
                }
                assert(detailText.indexOf("TEAM INCIDENT SUMMARY BY ICM PATH") < detailText.indexOf("Captured inputs and export query context"));
            } else {
                assert(detailText.includes("file_11.cs"), "Full changed-file collection missing");
                assert(detailText.includes(fixture === "large" ? "Example change 4999" : "Example change 1"));
                assert(detailText.includes("\u{1F4CB} TEAM CODE COMMIT DETAILS BY PERSON:"));
                assert(detailText.includes("\u{1F504} Commit:"));
            }
            const { targetId: reportTarget } = await cdp("Target.createTarget", {
                url: `data:text/html;base64,${Buffer.from(html).toString("base64")}`
            });
            try {
                const { sessionId: reportSession } = await cdp("Target.attachToTarget", { targetId: reportTarget, flatten: true });
                const report = await until(async () => {
                    const result = await cdp("Runtime.evaluate", {
                        returnByValue: true,
                        expression: `document.querySelector('table') && ({
                            title:document.querySelector('h1').textContent,
                            headers:[...document.querySelectorAll('table:first-of-type th')].map(cell=>cell.textContent),
                            tables:[...document.querySelectorAll('body > table')].map(table =>
                                [...table.querySelectorAll('tbody tr')].map(row => [...row.cells].map(cell=>cell.textContent))),
                            background:getComputedStyle(document.body).backgroundColor,
                            layout:getComputedStyle(document.querySelector('table')).tableLayout,
                            metadataColor:getComputedStyle(document.querySelector('.meta')).color,
                            compact:!document.querySelector('.report-context').open,
                            headings:[...document.querySelectorAll('body > h2')].map(heading=>heading.textContent)
                        })`
                    }, reportSession);
                    assert(!result.exceptionDetails, JSON.stringify(result.exceptionDetails));
                    return result.result.value;
                }, "rendered reference-style HTML report", 10000);
                assert(report.title.startsWith(workItems ? "Team Work Item Analysis"
                    : incidents ? "Team Incident Resolutions Analysis" : "Team Code Commit Analysis"));
                assert.deepEqual(report.headers, workItems
                    ? ["\u{1F464} Team Member", "\u{1F4C2} Area Path", "\u{1F4CB} Total Items", "\u{1F41E} Bugs",
                        "\u{1F527} Tasks", "\u{1F4E6} Product Backlog Items", "\u{1F9E9} Other"]
                    : incidents ? ["\u{1F464} Team Member", "\u{1F4C2} ICM Path", "\u{1F7E2} Active", "\u{1F195} Created",
                        "\u{1F527} Mitigated", "\u2705 Resolved", "\u{1F4AC} Contributed"]
                    : ["\u{1F464} Team Member", "\u{1F4C2} Repository", "\u{1F4BE} Commits",
                        "\u{1F500} PRs Merged", "\u2795 Lines", "\u2796 Lines",
                        "\u{1F4C4} Files Added", "\u{1F4DD} Files Modified", "\u{1F5D1} Files Deleted"]);
                for (const [index, rows] of report.tables.entries()) {
                    const columns = workItems ? 7 : incidents ? (index === 0 ? 7 : 6) : 9;
                    assert(rows.every(row => row.length === columns), "HTML rows must align with the reference headings");
                    const offset = workItems || incidents ? (index === 0 ? 2 : 1) : (index === 0 ? 6 : 5);
                    const length = workItems || incidents ? 5 : 3;
                    assert.deepEqual(rows.find(row => row[0] === "Example Contributor").slice(offset, offset + length),
                        workItems ? ["1", "0", "1", "0", "0"] : incidents ? incidentCounts
                            : ["0", String((fixture === "large" ? 5000 : 2) * 12), "0"],
                        "Incorrect contributor counts");
                    const zero = rows.find(row => row[0] === "Example Manager");
                    assert.deepEqual(zero.slice(offset, offset + length), Array(length).fill("0"), "Incorrect zero-activity counts");
                    if (workItems) assert.equal(zero[index === 0 ? 1 : 6], index === 0 ? "\u2014" : "");
                    if (incidents && index === 0) assert.equal(zero[1], "\u2014");
                }
                if (incidents) assert.deepEqual(report.tables[1].at(-1), ["\u{1F3C1} TOTAL", ...incidentCounts]);
                if (roleIconFixture)
                    for (const rows of report.tables)
                        assert(rows.some(row => row[0] === '\u{1F464} Example Contributor / \u270D\uFE0F Zoe Lead'),
                            "Both summary HTML tables must retain role icons and names without Author/Committer labels");
                assert(!detailText.includes("Files Added") && !detailText.includes("Files Modified") && !detailText.includes("Files Deleted"),
                    "Git-only HTML file-count columns changed another format");
                assert.equal(report.background, "rgb(30, 30, 30)");
                assert.equal(report.layout, "auto");
                assert.equal(report.metadataColor, "rgb(128, 128, 128)");
                assert.equal(report.compact, true);
                assert.equal(report.headings.length, 2);
            } finally {
                await cdp("Target.closeTarget", { targetId: reportTarget });
            }
        }
        if (fixture === "skipped-export") {
            assert(detailText.includes("Skipped sources - excluded from totals"));
            assert(html.includes("Activity is unavailable, not zero"));
            let previousName = name;
            for (let attempt = 0; attempt < 2; attempt++) {
                await until(() => evaluate("!document.querySelector('.analysis-export-btn').disabled"), "export action ready");
                await rm(join(downloads, previousName));
                const completedBeforeRetry = completedDownloads.length;
                await clickExport();
                const nextName = await until(async () => {
                    const failure = await evaluate("document.querySelector('[role=alert].report-export-status')?.textContent ?? ''");
                    assert(!failure, failure);
                    return completedDownloads.length > completedBeforeRetry
                        && (await readdir(downloads)).find(file => file.endsWith(".zip"));
                }, attempt === 0 ? "retry unavailable source" : "reuse recovered complete snapshot");
                previousName = nextName;
                const recovered = unzip(await readFile(join(downloads, nextName)));
                const recoveredDetails = [...recovered].find(([file]) => file.endsWith("_Detailed.html"))[1];
                assert(recoveredDetails.includes("Example change 1"), "Recovered source activity was not exported");
                assert(!recoveredDetails.includes("Skipped sources - excluded from totals"), "Unavailable snapshot was cached");
            }
            if (fixture === "gitCommits") {
                const previous = await readFile(join(downloads, name));
                const completed = completedDownloads.length;
                await evaluate("reportSaveFixture.mode = 'disk-error'");
                await clickExport();
                await until(() => evaluate("document.querySelector('[role=alert].report-export-status')?.textContent.includes('Fixture disk write denied')"), "disk failure surfaced");
                assert.equal(await evaluate("reportSaveFixture.aborts"), 1);
                assert.equal(completedDownloads.length, completed);
                assert.deepEqual(await readFile(join(downloads, name)), previous, "Failed save replaced an existing file");
                await rm(join(downloads, name));
                await evaluate("reportSaveFixture.mode = 'save'");
                await clickExport();
                await until(() => completedDownloads.length > completed, "save retry after disk failure");
                assert.equal(unzip(await readFile(join(downloads, name))).size, 2);
            }
        }
        assert.equal(await evaluate("document.querySelector('.analysis-summary-table').innerHTML"), before.html, "Export changed on-screen report markup");
        assert.deepEqual(await evaluate(onlineHierarchyExpression), onlineHierarchy,
            "Export must not modify the online hierarchy or omit its members");
    }
    if (fixture === "stale-module") {
        assert.deepEqual(interceptionErrors, []);
        const expectedVersion = createHash("sha256")
            .update(await readFile(new URL("../wwwroot/reportExport.module.js", import.meta.url))).digest("hex");
        assert(moduleRequests.some(request => request.search === ""), "Legacy module was not preloaded");
        assert(moduleRequests.some(request => request.searchParams.get("v")?.toLowerCase() === expectedVersion),
            "The working module import must be fingerprinted from its actual contents");
        assert.equal(await evaluate("import('./reportExport.module.js').then(module => typeof module.bindExportButton)"),
            "undefined", "The old module should remain cached while the versioned module works");
    }
    console.log(`PASS browser ${fixture}; trusted-click picker shim, ZIP/content/unchanged UX (${checks} responsiveness probes).`);
} finally {
    socket?.close();
    browser.kill();
    await new Promise(resolve => { if (browser.exitCode !== null) resolve(); else browser.once("exit", resolve); });
    await rm(profile, { recursive: true, force: true, maxRetries: 10, retryDelay: 300 });
}
