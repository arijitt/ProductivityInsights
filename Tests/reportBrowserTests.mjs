import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { existsSync } from "node:fs";
import { mkdtemp, readFile, readdir, rm, mkdir, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { inflateRawSync } from "node:zlib";
import { createHash } from "node:crypto";
import { pathToFileURL } from "node:url";
import { runFeatureRegistryBrowser } from "./featureRegistryBrowserTests.mjs";

const url = process.argv[2] ?? "http://127.0.0.1:5187/";
const fixture = process.argv[3] ?? "gitCommits";
const collectedIncidentFixture = fixture === "incident-available-export" || fixture === "incident-unkeyed-export";
const activeIncidentFixture = fixture === "incident-active-groups";
const incidentFixture = fixture === "incidentManagement" || collectedIncidentFixture || activeIncidentFixture;
const personSelector = incidentFixture ? "main > details.report-owner-details" : "details.report-person-details";
const personContentSelector = incidentFixture ? ".report-owner-content" : ".report-person-content";
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
    if (fixture === "teams-select-all") {
        const navigate = async label => {
            await until(() => evaluate(`([...document.querySelectorAll('.nav-tabs button')].find(button=>
                button.textContent.trim()===${JSON.stringify(label)})?.click(),
                [...document.querySelectorAll('.nav-tabs button.active')].some(button=>button.textContent.trim()===${JSON.stringify(label)}))`),
                "selected tab " + label);
        };
        const popupExpression = `[...document.querySelectorAll('.rz-dropdown-panel,.rz-multiselect-panel')].find(panel=>panel.checkVisibility())`;
        const selection = async () => evaluate(`(() => {
            const panel=${popupExpression};
            if (!panel) return null;
            const options=[...panel.querySelectorAll('[role="option"]')];
            return {total:options.length,selected:options.filter(option=>option.getAttribute('aria-selected')==='true').length,
                all:panel.querySelector('[role="checkbox"]')?.getAttribute('aria-checked')};
        })()`);
        const open = async label => {
            await navigate(label);
            await evaluate("document.querySelector('.team-dd [role=\"combobox\"]').click()");
            await until(() => selection(), "open Teams selection " + label, 10000);
        };
        await navigate("Teams");
        const tabs = [["Work Items",5],["Git Commits",23],["Incident Resolutions",5]];
        for (const [label, total] of tabs) {
            await open(label);
            assert.deepEqual(await selection(), {total,selected:total,all:"true"}, label + " must default to Select All");
            await evaluate(`(${popupExpression}).querySelector('[role="option"]').click()`);
            await until(async () => (await selection())?.selected === total - 1, "deselected one " + label);
        }
        for (const [label, total] of tabs) {
            await open(label);
            assert.equal((await selection()).selected, total - 1, "Tab switching must retain the user's " + label + " selection");
            await evaluate(`(${popupExpression}).querySelector('[role="checkbox"]').click()`);
            await until(async () => (await selection())?.selected === total, "selected all " + label);
            await evaluate(`(${popupExpression}).querySelector('[role="checkbox"]').click()`);
            await until(async () => (await selection())?.selected === 0, "cleared all " + label);
        }
        await navigate("Feature Registry");
        assert.equal(await evaluate("document.querySelectorAll('.team-dd').length"), 0,
            "Feature Registry must retain its fixed source rather than gain a selection list");
        await navigate("Details");
        await navigate("Work Items");
        await evaluate(`(() => { const team=document.querySelector('#teamSelect'); team.value='FastSpark';
            team.dispatchEvent(new Event('change',{bubbles:true})); })()`);
        await navigate("Teams");
        for (const [label, total] of tabs) {
            await open(label);
            assert.deepEqual(await selection(), {total,selected:0,all:"false"},
                "Cleared " + label + " selections must survive view changes and Details edits");
        }
        assert.equal(completedDownloads.length, 0, "Changing defaults must not start analysis or download reports");
    } else if (fixture.startsWith("feature-")) {
        await runFeatureRegistryBrowser({ fixture, url, profile, downloads, completedDownloads,
            evaluate, cdp, until, clickExport, unzip });
    } else {
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
    assert(before.heading.includes(incidentFixture ? (activeIncidentFixture ? "2 owning team" : "1 owning team") : "1 selection"),
        "Teams heading lost its source count");
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
    if (collectedIncidentFixture)
        await evaluate(`(() => {
            const state=window.reportProgressFixture={messages:[]};
            state.observer=new MutationObserver(()=>{
                const message=document.querySelector('[role=status].report-export-status')?.textContent.trim();
                if(message && state.messages.at(-1)!==message) state.messages.push(message);
            });
            state.observer.observe(document.body,{subtree:true,childList:true,characterData:true});
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
        if (collectedIncidentFixture) {
            const messages=await evaluate(`(() => {
                window.reportProgressFixture.observer.disconnect();
                return window.reportProgressFixture.messages.filter(message=>message.includes('ICM'));
            })()`);
            assert.deepEqual(messages, [
                "Collecting available ICM incidents (queue 1 of 1): AZURESYNAPSEJOBSERVICE\\JobPlatform",
                "Collecting recorded ICM actions (queue 1 of 1): AZURESYNAPSEJOBSERVICE\\JobPlatform"
            ], "Online export feedback must identify the captured ICM queue, ordinal and query stage");
        }
        const name = (await readdir(downloads)).find(name => name.endsWith(".zip"));
        assert.equal(name, "Chosen report.zip", "The user's selected filename was not used");
        assert.equal(await evaluate("reportSaveFixture.names.every(name => name.endsWith('.zip') && name !== 'Chosen report.zip')"), true);
        const entries = unzip(await readFile(join(downloads, name)));
        assert.equal(entries.size, 2);
        assert([...entries.keys()].every(name => name.endsWith(".html")), "Markdown was not replaced by detailed HTML");
        const [detailedName, detailed] = [...entries].find(([name]) => name.endsWith("_Detailed.html"));
        const [htmlName, html] = [...entries].find(([name]) => !name.endsWith("_Detailed.html"));
        const suggestedName = await evaluate("reportSaveFixture.names.at(-1)");
        const archiveParts = /^(Git_Commits|Work_Items|Incident_Resolutions)_teams_(\d{8})_to_(\d{8})_([0-9a-f]{32})\.zip$/.exec(suggestedName);
        assert(archiveParts, "The ZIP Save As suggestion must retain analysis, dates and run GUID");
        assert.equal(htmlName, `${archiveParts[1]}_teams_${archiveParts[2]}_to_${archiveParts[3]}.html`,
            "HTML filenames inside the ZIP must omit the GUID, independently of the chosen ZIP name");
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
                    expression: `document.readyState === 'complete' && document.querySelector(${JSON.stringify(personSelector)}) && (() => {
                        const sections = [...document.querySelectorAll('details')];
                        return {collapsed:sections.every(section => !section.open && !section.hasAttribute('open')
                                && !section.querySelector(':scope > div').checkVisibility()),
                            headingsVisible:[...document.querySelectorAll(${JSON.stringify(personSelector + " > summary")})].every(summary => summary.checkVisibility()),
                            contexts:document.querySelectorAll('details.report-context-details').length,
                            contextHeadingVisible:document.querySelector('details.report-context-details > summary')?.checkVisibility()};
                    })()`, returnByValue:true
                }, detailSession);
                assert(!result.exceptionDetails, JSON.stringify(result.exceptionDetails));
                return result.result.value;
            }, "initially collapsed detailed HTML", 30000);
            assert.deepEqual(initialFolds, {collapsed:true,headingsVisible:true,contexts:1,contextHeadingVisible:true},
                "People, items and independent context must start collapsed with their top-level headings visible");
            if (activeIncidentFixture) for (const categoryIndex of [0, 1, 2, 3, 4]) {
                const activeFolding = await cdp("Runtime.evaluate", {
                    expression: `(() => {
                        const queues=[...document.querySelectorAll('main > details.report-queue-details')];
                        const first=queues[${categoryIndex}];
                        first.querySelector(':scope > summary').click();
                        const owners=[...first.querySelectorAll('details.report-owner-details')];
                        const headingsOnly=owners.every(owner=>!owner.open
                            && owner.querySelector(':scope > summary').checkVisibility()
                            && !owner.querySelector(':scope > div').checkVisibility());
                        owners[0].querySelector(':scope > summary').click();
                        const expanded=owners[0].querySelector('h6.report-record-heading').checkVisibility();
                        first.querySelector(':scope > summary').click();
                        const hidden=!owners[0].querySelector('h6.report-record-heading').checkVisibility();
                        first.querySelector(':scope > summary').click();
                        const result={queues:queues.length,owners:owners.length,headingsOnly,expanded,hidden,
                            preserved:owners[0].open && owners[0].querySelector('h6.report-record-heading').checkVisibility(),
                            independent:owners.slice(1).every(owner=>!owner.open) && queues.filter(queue=>queue!==first).every(queue=>!queue.open)};
                        owners[0].querySelector(':scope > summary').click();
                        first.querySelector(':scope > summary').click();
                        return result;
                    })()`, returnByValue:true
                }, detailSession);
                assert.deepEqual(activeFolding.result.value, {queues:10,owners:2,headingsOnly:true,expanded:true,
                    hidden:true,preserved:true,independent:true},
                    "Incident categories and contact/contributor groups must expand independently, retaining group state when reopened");
            }
            if (incidentFixture) {
                const individualFolding = await cdp("Runtime.evaluate", {
                    expression: `(() => {
                        const owners=[...document.querySelectorAll('main > details.report-owner-details')];
                        const first=owners[0];
                        first.querySelector(':scope > summary').click();
                        const categories=[...first.querySelectorAll('details.report-queue-details')];
                        const headingsOnly=categories.every(category=>!category.open
                            && category.querySelector(':scope > summary').checkVisibility()
                            && !category.querySelector(':scope > div').checkVisibility());
                        const statistics=first.querySelectorAll(':scope > .report-owner-content > .report-table-scroll');
                        const statisticsVisible=statistics.length===4&&[...statistics].every(table=>table.checkVisibility());
                        const populated=categories.find(category=>category.querySelector('.report-record-heading'));
                        populated.querySelector(':scope > summary').click();
                        const expanded=populated.querySelector('.report-record-heading').checkVisibility();
                        first.querySelector(':scope > summary').click();
                        const hidden=!populated.querySelector('.report-record-heading').checkVisibility()
                            && [...statistics].every(table=>!table.checkVisibility());
                        first.querySelector(':scope > summary').click();
                        const result={categories:categories.length,headingsOnly,statisticsVisible,expanded,hidden,
                            preserved:populated.open&&populated.querySelector('.report-record-heading').checkVisibility(),
                            independent:owners.slice(1).every(owner=>!owner.open)
                                && categories.filter(category=>category!==populated).every(category=>!category.open)};
                        populated.querySelector(':scope > summary').click();
                        first.querySelector(':scope > summary').click();
                        return result;
                    })()`, returnByValue:true
                }, detailSession);
                assert(!individualFolding.exceptionDetails, JSON.stringify(individualFolding.exceptionDetails));
                assert.deepEqual(individualFolding.result.value, {categories:5,headingsOnly:true,statisticsVisible:true,expanded:true,
                    hidden:true,preserved:true,independent:true},
                    "Individual owners and their cross-path categories must fold independently and preserve child state");
            }
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
            assert.deepEqual(initialItems.result.value, fixture === "skipped-export" || incidentFixture
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
                        contextIsLast:document.querySelector('main').lastElementChild.matches('.report-context-details'),
                        collectedCountsWarningVisible:[...document.querySelectorAll('main > p')].some(paragraph=>
                            paragraph.textContent.includes('Person headings count collected commits only. Activity from skipped sources is unavailable, not zero.')
                            && paragraph.checkVisibility()),
                        contextStyle:(() => {
                            const context=document.querySelector('.report-context-details');
                            const heading=context.querySelector('summary > h2');
                            const content=context.querySelector('.report-context-content');
                            return {fontSize:getComputedStyle(content).fontSize,color:getComputedStyle(content).color,
                                weight:getComputedStyle(content).fontWeight,headingSize:getComputedStyle(heading).fontSize,
                                headingWeight:getComputedStyle(heading).fontWeight,headingColor:getComputedStyle(heading).color,
                                tableSize:getComputedStyle(content.querySelector('table')).fontSize,
                                labelWeight:getComputedStyle(content.querySelector('strong')).fontWeight,
                                independent:context.parentElement.tagName==='MAIN'};
                        })(),
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
                            const content = heading.closest('details.report-person-details')?.querySelector(':scope > .report-person-content');
                            const section = content?.querySelector(':scope > .report-lines');
                            return section?.matches('.report-lines') && section.textContent.trim().startsWith('\u{1F4CA} Summary for ')
                                ? {person:heading.textContent,tables:[...content.querySelectorAll('.report-table-scroll table')]
                                    .filter(table=>!table.closest('details.report-item-details'))
                                    .map(table=>[...table.querySelectorAll('tbody tr')].map(row=>[...row.cells].map(cell=>cell.textContent)))} : null;
                        }).filter(Boolean),
                        zeroWorkItemPeople:[...document.querySelectorAll('h3.report-person-heading')]
                            .filter(heading=>heading.textContent.endsWith(': 0 work item(s) completed'))
                            .map(heading=>({
                                heading:heading.textContent,
                                content:heading.closest('.report-person-details').querySelector(':scope > .report-person-content').textContent.trim(),
                                linked:[...document.querySelectorAll('.report-member-link')].some(link=>link.hash==='#'+heading.id)
                            })),
                        zeroGitPeople:[...document.querySelectorAll('h3.report-person-heading')]
                            .filter(heading=>heading.textContent.endsWith(': 0 commit'))
                            .map(heading=>({
                                heading:heading.textContent,
                                content:heading.closest('.report-person-details').querySelector(':scope > .report-person-content').textContent.trim(),
                                linked:[...document.querySelectorAll('.report-roster-member .report-member-link')].some(link=>link.hash==='#'+heading.id),
                                beforeSummary:!!(heading.compareDocumentPosition([...document.querySelectorAll('h2')].find(section=>
                                    section.textContent.includes('TEAM CODE COMMIT SUMMARY BY REPOSITORY')))&Node.DOCUMENT_POSITION_FOLLOWING)
                            })),
                        personSectionCount:document.querySelectorAll('.report-person-details').length,
                        itemSectionCount:document.querySelectorAll('.report-item-details').length,
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
                        commitHeadings:[...document.querySelectorAll('h4.report-record-heading')]
                            .filter(heading=>/^\u{1F504} (?:Commit:|PR #)/.test(heading.textContent)).map(heading=>heading.textContent),
                        commitRoleFields:[...document.querySelectorAll('div.report-line')].map(node => ({
                            label:node.querySelector(':scope > .report-label')?.textContent,
                            icon:node.querySelector(':scope > .report-icon')?.textContent
                        })).filter(field => field.label === 'Author:' || field.label === 'Committer:'),
                        personHeadings:[...document.querySelectorAll('h3.report-person-heading')].map(node => node.textContent),
                        gitPersonList:(() => {
                            const summary=[...document.querySelectorAll('main > h2')].find(heading=>
                                heading.textContent.includes('CODE COMMIT SUMMARY BY REPOSITORY'));
                            if(!summary) return null;
                            const people=[...document.querySelectorAll('details.report-person-details')];
                            return {
                                allBeforeSummary:people.every(person=>person.parentElement.tagName==='MAIN'
                                    && !!(person.compareDocumentPosition(summary)&Node.DOCUMENT_POSITION_FOLLOWING)),
                                allUniform:people.every(person=>person.querySelector(':scope > summary > h3.report-person-heading')),
                                rosterTargetIndices:[...document.querySelectorAll('.report-roster-member .report-member-link')]
                                    .map(link=>people.indexOf(document.getElementById(link.hash.slice(1))?.closest('details.report-person-details')))
                            };
                        })(),
                        rosterHierarchy:(() => {
                            const nodes = [...document.querySelectorAll('.report-roster-hierarchy li')];
                            return nodes.map(node => ({
                                name:node.querySelector(':scope > .report-roster-member a').textContent,
                                parent:nodes.indexOf(node.parentElement.closest('li')),
                                left:node.querySelector(':scope > .report-roster-member').getBoundingClientRect().left
                            }));
                        })(),
                        memberIncidentDestinations:${incidentFixture} ? [...document.querySelectorAll('.report-roster-member a.report-member-link')].map(link => {
                            const target = document.getElementById(link.hash.slice(1));
                            return {member:link.textContent,tag:target.tagName,
                                individual:!!target.closest('main > details.report-owner-details'),
                                beforeContext:!!(target.compareDocumentPosition(document.querySelector('details.report-context-details'))&Node.DOCUMENT_POSITION_FOLLOWING),
                                cells:target.tagName==='TR'?[...target.cells].map(cell=>cell.textContent):[]};
                        }) : [],
                        icmLinks:document.querySelectorAll('a.report-icm-link').length,
                        incidentStatisticHeadings:${incidentFixture} ? (() => {
                            const headings=start=>{
                                const result=[];
                                for(let node=start?.nextElementSibling;node&&node.tagName!=='H2';node=node.nextElementSibling)
                                    if(node.tagName==='H4') result.push(node.textContent);
                                return result;
                            };
                            return {
                                active:headings([...document.querySelectorAll('main > h3')].find(node=>node.textContent==='Active')),
                                overall:headings([...document.querySelectorAll('main > h2')].find(node=>
                                    node.textContent==='Distinct incident totals and weighted duration statistics'))
                            };
                        })() : null,
                        activeTeamTables:[...document.querySelectorAll('h4')]
                            .filter(heading=>['Active severity by owning team','Active TTD by owning team and severity'].includes(heading.textContent))
                            .map(heading=>[...heading.nextElementSibling.querySelectorAll('tbody tr')].map(row=>[...row.cells].map(cell=>cell.textContent))),
                        activeTeamLinks:[...document.querySelectorAll('h4')]
                            .filter(heading=>['Active severity by owning team','Active TTD by owning team and severity'].includes(heading.textContent))
                            .map(heading=>[...heading.nextElementSibling.querySelectorAll('tbody tr')]
                                .map(row=>row.cells[0].querySelector('a.report-icm-link')?.getAttribute('href')??null)),
                        incidentIndividuals:${incidentFixture} ? (() => {
                            const heading=[...document.querySelectorAll('main > h2')].find(node=>node.textContent.includes('TEAM INCIDENT DETAILS BY INDIVIDUALS:'));
                            const paths=[...document.querySelectorAll('main > h2')].find(node=>node.textContent.includes('TEAM INCIDENT DETAILS BY ICM PATH:'));
                            const owners=[...document.querySelectorAll('main > details.report-owner-details')];
                            return {beforePaths:!!(heading?.compareDocumentPosition(paths)&Node.DOCUMENT_POSITION_FOLLOWING),
                                bounded:owners.every(owner=>!!(heading.compareDocumentPosition(owner)&Node.DOCUMENT_POSITION_FOLLOWING)
                                    && !!(owner.compareDocumentPosition(paths)&Node.DOCUMENT_POSITION_FOLLOWING)),
                                owners:owners.map(owner=>({
                                    heading:owner.querySelector(':scope > summary').textContent.trim(),
                                    statistics:{
                                        fields:[...owner.querySelectorAll(':scope > .report-owner-content > ul > li')].map(item=>item.textContent.trim()),
                                        headings:[...owner.querySelectorAll(':scope > .report-owner-content > h4')].map(node=>node.textContent),
                                        tables:[...owner.querySelectorAll(':scope > .report-owner-content > .report-table-scroll table')]
                                            .map(table=>[...table.querySelectorAll('tbody tr')].map(row=>[...row.cells].map(cell=>cell.textContent))),
                                        beforeCategories:[...owner.querySelectorAll(':scope > .report-owner-content > .report-table-scroll')]
                                            .every(table=>!!(table.compareDocumentPosition(owner.querySelector('details.report-queue-details'))&Node.DOCUMENT_POSITION_FOLLOWING))
                                    },
                                    categories:[...owner.querySelectorAll('details.report-queue-details')].map(category=>({
                                        heading:category.querySelector(':scope > summary').textContent.trim(),
                                        ids:[...category.querySelectorAll('.report-line > .report-label')].filter(label=>label.textContent==='ID:')
                                            .map(label=>label.nextSibling.textContent.trim()),
                                        silos:[...category.querySelectorAll('div.report-line')].map(line=>line.textContent.trim())
                                            .filter(line=>line.startsWith('Silo:')).map(line=>line.slice('Silo:'.length).trim()),
                                        links:category.querySelectorAll('a.report-incident-link').length
                                    }))
                                }))};
                        })() : null,
                        incidentCategories:[...document.querySelectorAll('main > details.report-queue-details')].map(queue=>({
                            heading:queue.querySelector(':scope > summary').textContent.trim(),
                            owners:[...queue.querySelectorAll('details.report-owner-details')].map(owner=>({
                                heading:owner.querySelector(':scope > summary').textContent.trim(),
                                titles:[...owner.querySelectorAll('h6.report-record-heading')].map(heading=>heading.textContent),
                                ids:[...owner.querySelectorAll('.report-line > .report-label')]
                                    .filter(label=>label.textContent==='ID:').map(label=>label.nextSibling.textContent.trim())
                            }))
                        })),
                        compactIncidentHeadings:[...document.querySelectorAll('.report-record-heading')]
                            .filter(heading=>heading.textContent.startsWith('Incident Details:')).map(heading=>heading.textContent),
                        compactIncidentUrls:[...document.querySelectorAll('div.report-line')]
                            .filter(line=>line.textContent.trim().startsWith('URL:')).map(line=>({
                                text:line.querySelector('a')?.textContent,href:line.querySelector('a')?.getAttribute('href')
                            })),
                        incidentConsolidation:${incidentFixture} ? (() => {
                            const statistics=[...document.querySelectorAll('main > h2')].find(node=>node.textContent==='Incident statistics');
                            const after=node=>!!(statistics.compareDocumentPosition(node)&Node.DOCUMENT_POSITION_FOLLOWING);
                            return {
                                repeatedRecords:[...document.querySelectorAll('.report-record-heading')].filter(after).length,
                                repeatedMetadata:[...document.querySelectorAll('h6')].filter(node=>after(node)
                                    && ['Visible incident metadata','Mitigation text'].includes(node.textContent)).length,
                                repeatedUrls:[...document.querySelectorAll('li > strong')].filter(node=>after(node)
                                    && node.textContent==='Recorded URL:').length,
                                oldHeading:[...document.querySelectorAll('h2')].some(node=>after(node)&&node.textContent==='Incident details'),
                                globalActions:[...document.querySelectorAll('h2')].some(node=>
                                    node.textContent==='Recorded incident action history and contribution evidence'),
                                globalActionTables:[...document.querySelectorAll('table')].filter(table=>
                                    !table.closest('details.report-person-details')
                                    && [...table.querySelectorAll('th')].some(header=>header.textContent==='Action ID')).length,
                                activeFollowedByTotals:(() => {
                                    const title=[...document.querySelectorAll('h4')].find(node=>after(node)&&node.textContent==='Active TTD by owning team and severity');
                                    const next=title?.nextElementSibling?.nextElementSibling;
                                    return next?.tagName==='H2'&&next.textContent==='Distinct incident totals and weighted duration statistics';
                                })(),
                                laterIndividuals:document.querySelectorAll('details.report-person-details,details.report-item-details').length,
                                recordTables:[...document.querySelectorAll('table')].filter(table=>[...table.querySelectorAll('th')]
                                    .some(header=>header.textContent.trim().toLowerCase()==='incident record')).length,
                                links:[...document.querySelectorAll('a.report-incident-link')].length,
                                linksPointEarlier:[...document.querySelectorAll('a.report-incident-link')].every(link=>{
                                    const target=document.getElementById(link.hash.slice(1));
                                    return target?.matches('.report-record-heading')&&!after(target);
                                })
                            };
                        })() : null,
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
                            return !target || !target.matches('h3,h4,h6,tr') || !target.textContent.trim();
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
            assert.deepEqual(detailView.contextStyle, {
                fontSize:"12px",color:"rgb(180, 196, 217)",weight:"400",headingSize:"13px",headingWeight:"500",
                headingColor:"rgb(180, 196, 217)",tableSize:"11px",labelWeight:"500",independent:true
            }, "Captured context must use smaller, lighter typography in its own independent disclosure");
            assert.deepEqual(detailView.palette, {
                scheme: "dark", body: "rgb(11, 18, 32)", main: "rgb(17, 28, 46)", text: "rgb(230, 237, 247)"
            }, "Only detailed screen presentation should use the dark palette");
            assert(detailView.roster > 0);
            assert.equal(detailView.rosterLinks, detailView.roster, "Every roster member needs an actionable detail link");
            assert.equal(detailView.brokenLinks, 0, "Offline report links must resolve to real detail headings or evidence rows");
            assert.equal(detailView.duplicateIds, 0, "Each anchor ID must appear only once");
            if (fixture === "gitCommits" || roleIconFixture || fixture === "skipped-export") {
                assert(!detailView.text.includes("Completed PR evidence") && !detailView.text.includes("Source-qualified PR"),
                    "Teams Git detailed downloads must omit the repeated completed-PR evidence section and table");
                assert(detailView.contextIsLast,
                    "Captured inputs and export query context must be the final Teams Git report section, after totals, trends and every person");
                assert(detailView.gitPersonList.allBeforeSummary && detailView.gitPersonList.allUniform,
                    "Every Git person, including unavailable members, must stay in the main list with the same heading style");
                const indices = detailView.gitPersonList.rosterTargetIndices;
                assert(indices.every((value,index)=>value>=0 && (index===0 || value>=indices[index-1])),
                    "Person destinations must follow the captured hierarchy order, never a separate trailing list");
                assert(!detailView.text.includes("No collected detail section is attributed to this roster member"),
                    "Teams Git must not retain the late per-member fallback sections");
            }
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
                assert.deepEqual(detailView.personHeadings, [
                    '\u{1F464} Example Manager: 0 commit',
                    roleIconFixture ? '\u{1F464} Author: Example Contributor / \u270D\uFE0F Committer: Zoe Lead: 1 commit'
                        : '\u{1F464} Zoe Lead: 0 commit',
                    '\u{1F464} Example Contributor: 2 commits',
                    '\u{1F464} Aaron Peer: 0 commit'
                ], "Active and zero-commit people must share one hierarchy-ordered list with consistent name/count headers");
                assert.deepEqual(detailView.zeroGitPeople,
                    (roleIconFixture ? ["Example Manager","Aaron Peer"] : ["Example Manager","Zoe Lead","Aaron Peer"]).map(name=>({
                        heading:`\u{1F464} ${name}: 0 commit`,content:"",linked:true,beforeSummary:true
                    })), "Zero-commit members need compact, normal person headings with working hierarchy links, without duplicated pair participants");
                assert.equal(detailView.personSectionCount, 4,
                    "Each real contribution group and otherwise unrepresented roster member must have exactly one person section");
                if (fixture === "gitCommits") {
                    assert.deepEqual(detailView.commitHeadings,
                        ['\u{1F504} PR #42: Example pull request <safe> & [title]', '\u{1F504} Commit: 00000000...'],
                        "Matched PRs must use the captured PR number/title while non-PR commits keep their commit heading");
                    assert(detailView.text.includes("Merged PR 42: Original title") && detailView.text.includes("Full recorded merge body"),
                        "The original commit message must remain in the PR-headed item's details");
                }
            }
            if (roleIconFixture) {
                const pair = '\u{1F464} Author: Example Contributor / \u270D\uFE0F Committer: Zoe Lead';
                assert.equal(detailView.cells.filter(cell => cell === pair).length, 2,
                    "Both detailed summary tables must distinguish the author/committer pair");
                assert(detailView.personHeadings.some(heading => heading.includes(pair)),
                    "The shared contribution heading must preserve both role icons");
                assert.equal(detailView.commitHeadings[0], '\u{1F504} PR #73: Shared change <safe> & [title]',
                    "Shared commits must use their recorded merge-subject PR number/title even without a captured PR completion");
                assert(detailView.text.includes("Merged PR 73: Shared change <safe> & [title]")
                    && detailView.text.includes("Recorded shared merge body"),
                    "The PR-heading fallback must preserve the complete shared commit message");
            }
            if (incidentFixture) {
                assert.deepEqual(detailView.incidentStatisticHeadings, {
                    active:["Severity counts","TTD by severity",...(activeIncidentFixture
                        ? ["Active severity by owning team","Active TTD by owning team and severity"] : [])],
                    overall:["Severity counts","TTD by severity","TTM by severity","TTR by severity"]
                }, "Only the Teams global Active section must omit TTM/TTR; overall duration tables remain unchanged");
                assert(detailView.incidentIndividuals.beforePaths && detailView.incidentIndividuals.bounded,
                    "The individuals section must group all owners before the existing ICM-path section");
                for (const owner of detailView.incidentIndividuals.owners) {
                    assert.deepEqual(owner.statistics.headings, ["Severity counts", "TTD by severity", "TTM by severity", "TTR by severity"]);
                    assert.equal(owner.statistics.tables.length, 4);
                    assert(owner.statistics.beforeCategories, "Each individual's summary must precede their collapsed incident categories");
                    assert.equal(owner.categories.length, 5);
                    for (const category of owner.categories) {
                        assert.equal(category.ids.length, Number(/: ([0-9]+) incident\(s\)$/.exec(category.heading)[1]));
                        assert.equal(category.links, category.ids.length, "Every individual incident must link to its full ICM-path evidence");
                        assert.equal(new Set(category.ids.map((id,index)=>category.silos[index]+':'+id)).size, category.ids.length,
                            "An owner/category must deduplicate selected paths without merging different silos");
                    }
                }
                if (activeIncidentFixture) {
                    assert.equal(detailView.activeTeamTables.length, 2);
                    for (const table of detailView.activeTeamTables)
                        assert.deepEqual(table.map(row=>row[0]), [
                            '-\\AZURESYNAPSEJOBSERVICE\\JobPlatform', '-\\SYNTHETIC\\SecondQueue'
                        ],
                        "Both Active team tables must omit the silo suffix without changing tenant/team captions or row order");
                    assert.deepEqual(detailView.activeTeamLinks[0], detailView.activeTeamLinks[1],
                        "Active severity owning teams must link to the same ICM-path destinations as Active TTD");
                    assert.equal(detailView.activeTeamLinks[0].length, 2);
                    assert(detailView.activeTeamLinks[0].every(href=>/^#report-icm-[0-9]+$/.test(href))
                        && new Set(detailView.activeTeamLinks[0]).size === 2,
                        "Each owning team must have its own resolved offline ICM-path link");
                    for (const owner of detailView.incidentIndividuals.owners) {
                        const credited=owner.heading.includes("Example Contributor");
                        assert.deepEqual(owner.statistics.fields, [
                            "Distinct closed incidents in group: "+(credited ? 5 : 0),
                            "Currently resolved: 0", "Currently mitigated: 0"
                        ], "Moving the tables must preserve actor attribution and the existing current-status calculations");
                        assert.deepEqual(owner.statistics.tables, credited ? [
                            [["2","5"]],
                            [["2","5","1.0 hours","1.0 hours","1.0 hours","1.0","1.0","1.0"]],
                            [["2","5","1.0 hours","1.0 hours","1.0 hours","1.0","1.0","1.0"]],
                            [["2","0","-","-","-","-","-","-"]]
                        ] : [[],[],[],[]], "Individual tables must retain complete severity/duration measures and empty-group semantics");
                    }
                    assert.deepEqual(detailView.incidentIndividuals.owners.map(owner=>({
                        heading:owner.heading, categories:owner.categories.map(category=>category.ids.map((id,index)=>category.silos[index]+':'+id))
                    })), [
                        {heading:"\u{1F464} Aaron Peer (1 incident(s))",
                            categories:[["2:43"],["2:43"],["2:43"],["2:43"],[]]},
                        {heading:"\u{1F464} Example Contributor (3 incident(s))",
                            categories:[["1:43","1:44"],["1:42","1:43","1:44"],["1:42","1:43","1:44"],["1:42","1:43","1:44"],[]]},
                        {heading:"\u{1F464} Example Manager (2 incident(s))",
                            categories:[[],[],[],[],["1:90","2:90"]]},
                        {heading:"\u{1F464} Zoe Lead (1 incident(s))",
                            categories:[["1:45"],["1:45"],["1:45"],["1:45"],[]]}
                    ], "Cross-path owner groups must combine paths and aliases while leaving recorded contributor credit unchanged");
                }
                assert(detailView.incidentConsolidation.links > 0);
                assert.deepEqual({...detailView.incidentConsolidation,links:0},
                    {repeatedRecords:0,repeatedMetadata:0,repeatedUrls:0,oldHeading:false,globalActions:false,globalActionTables:0,
                        activeFollowedByTotals:activeIncidentFixture,laterIndividuals:0,recordTables:0,links:0,linksPointEarlier:true},
                    "Incident Record tables must be absent; action evidence must retain inline links without repeated records or metadata");
                assert(detailView.icmLinks > 0, "ICM paths must link to their report details");
                assert.equal(detailView.memberIncidentDestinations.length, detailView.roster);
                for (const member of detailView.memberIncidentDestinations) {
                    const ownsIncidents=activeIncidentFixture||member.member.includes("Example Contributor");
                    assert(member.beforeContext, "Roster links must not recreate the removed later individual sections");
                    assert.equal(member.tag, ownsIncidents ? "H3" : "TR");
                    assert.equal(member.individual, ownsIncidents);
                    if (!ownsIncidents) assert.deepEqual(member.cells.slice(1), ["0","0","0","0","0"],
                        "Members without owned incidents retain their own overall summary row as a navigation destination");
                }
                assert.equal(detailView.viewIncidentLinks, 0, "Redundant View Incident links must be removed");
                assert(detailView.recordedUrls.length > 0 && detailView.recordedUrls.every(link =>
                    /^https?:/.test(link.href ?? "") && link.text === link.href),
                    "Recorded incident URLs must be clickable at the field itself");
                assert(detailView.compactIncidentHeadings.length > 0
                    && detailView.compactIncidentHeadings.every(heading=>heading.trim().length > "Incident Details:".length),
                    "Every compact Incident Details header must include the captured title");
                assert.equal(detailView.compactIncidentUrls.length, detailView.compactIncidentHeadings.length);
                assert(detailView.compactIncidentUrls.every(link=>/^https?:/.test(link.href ?? "") && link.text===link.href),
                    "Each compact incident URL must be clickable at its recorded value");
                if (activeIncidentFixture) {
                    assert.deepEqual(detailView.incidentCategories, [
                        {heading:"\u{1F7E2} Active incidents: 3",owners:[
                            {heading:"\u{1F464} Owning Contact: Example Contributor (2 incident(s))",
                                titles:["Incident Details: Active incident 1 <safe> & [title]","Incident Details: Active incident 2 <safe> & [title]"],
                                ids:["43","44"]},
                            {heading:"\u{1F464} Owning Contact: Zoe Lead (1 incident(s))",
                                titles:["Incident Details: Active incident 3 <safe> & [title]"],ids:["45"]}
                        ]},
                        {heading:"\u{1F195} Created incidents: 4",owners:[
                            {heading:"\u{1F464} Owning Contact: Example Contributor (3 incident(s))",
                                titles:["Incident Details: Example incident","Incident Details: Active incident 1 <safe> & [title]",
                                    "Incident Details: Active incident 2 <safe> & [title]"],ids:["42","43","44"]},
                            {heading:"\u{1F464} Owning Contact: Zoe Lead (1 incident(s))",
                                titles:["Incident Details: Active incident 3 <safe> & [title]"],ids:["45"]}
                        ]},
                        {heading:"\u{1F527} Mitigated incidents: 4",owners:[
                            {heading:"\u{1F464} Owning Contact: Example Contributor (3 incident(s))",
                                titles:["Incident Details: Example incident","Incident Details: Active incident 1 <safe> & [title]",
                                    "Incident Details: Active incident 2 <safe> & [title]"],ids:["42","43","44"]},
                            {heading:"\u{1F464} Owning Contact: Zoe Lead (1 incident(s))",
                                titles:["Incident Details: Active incident 3 <safe> & [title]"],ids:["45"]}
                        ]},
                        {heading:"\u2705 Resolved incidents: 4",owners:[
                            {heading:"\u{1F464} Owning Contact: Example Contributor (3 incident(s))",
                                titles:["Incident Details: Example incident","Incident Details: Active incident 1 <safe> & [title]",
                                    "Incident Details: Active incident 2 <safe> & [title]"],ids:["42","43","44"]},
                            {heading:"\u{1F464} Owning Contact: Zoe Lead (1 incident(s))",
                                titles:["Incident Details: Active incident 3 <safe> & [title]"],ids:["45"]}
                        ]},
                        {heading:"\u{1F4AC} Contributed Incidents: 1 incident(s)",owners:[
                            {heading:"\u{1F464} Contributor: Example Contributor (1 incident(s))",
                                titles:["Incident Details: Contributed incident <safe> & [title]"],ids:["90"]},
                            {heading:"\u{1F464} Contributor: Zoe Lead (1 incident(s))",
                                titles:["Incident Details: Contributed incident <safe> & [title]"],ids:["90"]}
                        ]},
                        {heading:"\u{1F7E2} Active incidents: 1",owners:[
                            {heading:"\u{1F464} Owning Contact: Aaron Peer (1 incident(s))",
                                titles:["Incident Details: Active incident 4 <safe> & [title]"],ids:["43"]}
                        ]},
                        {heading:"\u{1F195} Created incidents: 1",owners:[
                            {heading:"\u{1F464} Owning Contact: Aaron Peer (1 incident(s))",
                                titles:["Incident Details: Active incident 4 <safe> & [title]"],ids:["43"]}
                        ]},
                        {heading:"\u{1F527} Mitigated incidents: 1",owners:[
                            {heading:"\u{1F464} Owning Contact: Aaron Peer (1 incident(s))",
                                titles:["Incident Details: Active incident 4 <safe> & [title]"],ids:["43"]}
                        ]},
                        {heading:"\u2705 Resolved incidents: 1",owners:[
                            {heading:"\u{1F464} Owning Contact: Aaron Peer (1 incident(s))",
                                titles:["Incident Details: Active incident 4 <safe> & [title]"],ids:["43"]}
                        ]},
                        {heading:"\u{1F4AC} Contributed Incidents: 1 incident(s)",owners:[
                            {heading:"\u{1F464} Contributor: Aaron Peer (1 incident(s))",
                                titles:["Incident Details: Contributed incident in second silo"],ids:["90"]},
                            {heading:"\u{1F464} Contributor: Example Contributor (1 incident(s))",
                                titles:["Incident Details: Contributed incident in second silo"],ids:["90"]}
                        ]}
                    ], "Each queue must preserve contact-based categories and repeat each remaining incident once per recorded contributor, not per action or owner");
                    assert(!detailView.text.includes("Other selected (including action-only):"),
                        "The former category must be renamed, not retained as an additional duplicate list");
                }
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
                    const sections = [...document.querySelectorAll(${JSON.stringify(personSelector)})];
                    const first = sections[0];
                    const summary = first?.querySelector(':scope > summary');
                    const content = first?.querySelector(${JSON.stringify(":scope > " + personContentSelector)});
                    const expanded = sections.length > 0 && sections.every(section => section.open && section.querySelector(${JSON.stringify(":scope > " + personContentSelector)}).checkVisibility());
                    summary?.click();
                    summary?.focus();
                    return {expanded,collapsed:!first?.open && !content?.checkVisibility(),
                        independent:sections.slice(1).every(section => section.open),
                        headingVisible:summary?.checkVisibility(),focused:document.activeElement === summary,
                        noNestedSections:sections.every(section => !section.querySelector(${JSON.stringify(personSelector)}))};
                })()`, returnByValue: true
            }, detailSession);
            assert.deepEqual(folding.result.value, { expanded:true,collapsed:true,independent:true,
                headingVisible:true,focused:true,noNestedSections:true },
                "Clicking one user's heading must fold only that complete section, keeping the heading visible");
            await cdp("Page.bringToFront", {}, detailSession);
            const checkDisclosureKeys = async (selector, index = 0) => {
                for (const [key, code, expected] of [["Enter", "Enter", true], [" ", "Space", false]]) {
                    await cdp("Input.dispatchKeyEvent", { type:"keyDown", key, code, text:key === "Enter" ? "\r" : " ",
                        windowsVirtualKeyCode:key === "Enter" ? 13 : 32 }, detailSession);
                    await cdp("Input.dispatchKeyEvent", { type:"keyUp", key, code, windowsVirtualKeyCode:key === "Enter" ? 13 : 32 }, detailSession);
                    const keyboard = await cdp("Runtime.evaluate", {
                        expression: `document.querySelectorAll(${JSON.stringify(selector)})[${index}].open`, returnByValue:true
                    }, detailSession);
                    assert.equal(keyboard.result.value, expected, "Enter and Space must toggle the focused disclosure offline");
                }
            };
            await checkDisclosureKeys(personSelector);
            const contextFolding = await cdp("Runtime.evaluate", {
                expression: `(() => {
                    const context=document.querySelector('details.report-context-details');
                    const people=[...document.querySelectorAll(${JSON.stringify(personSelector)})].map(section=>section.open);
                    const summary=context.querySelector(':scope > summary');
                    summary.click();
                    summary.focus();
                    return {closed:!context.open,hidden:!context.querySelector('.report-context-content').checkVisibility(),
                        independent:[...document.querySelectorAll(${JSON.stringify(personSelector)})].every((section,index)=>section.open===people[index])};
                })()`, returnByValue:true
            }, detailSession);
            assert.deepEqual(contextFolding.result.value, {closed:true,hidden:true,independent:true},
                "Collapsing captured context must not change any person's disclosure");
            await checkDisclosureKeys('details.report-context-details');
            if (activeIncidentFixture) for (const categoryIndex of [0, 1, 2, 3, 4]) {
                await cdp("Runtime.evaluate", {
                    expression: `(() => {
                        const queue=document.querySelectorAll('main > details.report-queue-details')[${categoryIndex}];
                        queue.querySelector(':scope > summary').click();
                        queue.querySelector(':scope > summary').focus();
                    })()`
                }, detailSession);
                await checkDisclosureKeys('main > details.report-queue-details', categoryIndex);
                await cdp("Runtime.evaluate", {
                    expression: `(() => {
                        const queue=document.querySelectorAll('main > details.report-queue-details')[${categoryIndex}];
                        queue.querySelector(':scope > summary').click();
                        const owner=queue.querySelector('details.report-owner-details');
                        owner.querySelector(':scope > summary').click();
                        owner.querySelector(':scope > summary').focus();
                    })()`
                }, detailSession);
                await checkDisclosureKeys('main > details.report-queue-details details.report-owner-details', categoryIndex * 2);
            }
            if (incidentFixture) {
                await cdp("Runtime.evaluate", {
                    expression: `(() => {
                        const owner=document.querySelector('main > details.report-owner-details');
                        owner.open=false;
                        owner.querySelector(':scope > summary').focus();
                    })()`
                }, detailSession);
                await checkDisclosureKeys('main > details.report-owner-details');
                await cdp("Runtime.evaluate", {
                    expression: `(() => {
                        const owner=document.querySelector('main > details.report-owner-details');
                        owner.open=true;
                        const category=owner.querySelector('details.report-queue-details');
                        category.open=false;
                        category.querySelector(':scope > summary').focus();
                    })()`
                }, detailSession);
                await checkDisclosureKeys('main > details.report-owner-details details.report-queue-details');
            }
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
                        .filter(heading => /^(?:\\u{1F4CB} Work Item #|Work item |\\u{1F504} (?:Commit:|PR #)|Incident |Recorded actions for incident )/.test(heading.textContent))
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
                const names = ["Example Manager","Zoe Lead","Example Contributor","Aaron Peer"];
                assert.deepEqual(detailView.personHeadings, names.map(name=>`\u{1F464} ${name}: 0 commit`),
                    "Members without collected commits must use the same zero-count header in the main hierarchy-ordered list");
                assert.deepEqual(detailView.zeroGitPeople, names.map(name=>({
                    heading:`\u{1F464} ${name}: 0 commit`,
                    content:"No commits were collected for this person. Data is incomplete because one or more selected sources were skipped; this is not evidence of zero activity.",
                    linked:true,beforeSummary:true
                })), "Zero collected-commit counts must retain source-availability disclosures and correct roster destinations");
                assert(detailView.collectedCountsWarningVisible,
                    "The collected-count qualification must remain visible outside collapsed person and context sections");
            } else if (incidentFixture) {
                assert.deepEqual(itemFolding.result.value, {count:0}, "Removed Incident individual sections must not leave action-item folds");
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
                expression: `document.querySelectorAll(${JSON.stringify(personSelector+"[open]")}).forEach(section => section.querySelector(':scope > summary').click())`
            }, detailSession);
            for (const selector of [
                ".report-roster-member a.report-member-link",
                ...(incidentFixture ? ["a.report-icm-link", "a.report-incident-link"] : [])
            ]) {
                const navigation = await cdp("Runtime.evaluate", {
                    expression: `(() => {
                        const link = document.querySelector(${JSON.stringify(selector)});
                        if (${JSON.stringify(selector)}==='a.report-incident-link') {
                            document.querySelectorAll('details.report-queue-details,details.report-owner-details').forEach(section=>section.open=false);
                            for(let parent=link.parentElement;parent;parent=parent.parentElement)
                                if(parent.tagName==='DETAILS') parent.open=true;
                        }
                        link.click();
                        const target = document.getElementById(link.hash.slice(1));
                        const rect = target.getBoundingClientRect();
                        return {hash:location.hash === link.hash, visible:rect.top < innerHeight && rect.bottom > 0,
                            focused:document.activeElement === target, highlighted:getComputedStyle(target).outlineStyle};
                    })()`,
                    returnByValue: true
                }, detailSession);
                assert.deepEqual(navigation.result.value, { hash: true, visible: true, focused: true, highlighted: "solid" },
                    "Roster, ICM and incident references must reveal, focus and highlight their detail destination offline");
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
                expression: `({background:getComputedStyle(document.body).backgroundColor,shadow:getComputedStyle(document.querySelector('main')).boxShadow,overflow:getComputedStyle(document.querySelector('.report-table-scroll')).overflowX,scheme:getComputedStyle(document.documentElement).colorScheme,heading:getComputedStyle(document.querySelector(${JSON.stringify(personSelector+" h3")})).color})`,
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
            const incidentCounts = collectedIncidentFixture ? ["0", "1", "0", "1", "1"]
                : activeIncidentFixture ? ["2", "3", "5", "5", "3"] : ["0", "1", "1", "1", "1"];
            if (workItems) {
                assert.equal(detailView.workItemSummaries.length, 1, "Only the member with work items needs extra summary statistics");
                const summary = detailView.workItemSummaries.find(summary => summary.person.includes('Example Contributor'));
                assert(summary && onlineWorkSummary.length > 0);
                assert.deepEqual(summary.tables, [
                    [["Task","1"]],
                    [["Total (known positive durations)","1.0"],["Minimum","1.0"],["Average","1.0"],["Maximum","1.0"],["Median","1.0"],
                        ["Work items with positive active-work time","1 of 1"],
                        ["Work items with unavailable / single-revision active-work duration","0"]]
                ], "The former audit summary tables must appear under the person's completion heading");
                const measures = Object.fromEntries(summary.tables[1]);
                for (const [measure, label] of [
                    ["Total (known positive durations)","Total Days"],["Minimum","Minimum Days"],["Average","Average Days"],
                    ["Maximum","Maximum Days"],["Median","Median Days"],["Work items with positive active-work time","Work Items with Active Work Time"]
                ])
                    assert(onlineWorkSummary.includes(label + ": " + measures[measure]), "Relocated statistics must preserve the on-screen calculation: " + label);
                assert.deepEqual(detailView.zeroWorkItemPeople,
                    ["Aaron Peer","Example Manager","Zoe Lead"].map(name=>({
                        heading:`\u{1F464} ${name}: 0 work item(s) completed`,content:"",linked:true
                    })), "Zero-item members need matching counted headings without extra details or identity-heavy fallback sections");
                assert.equal(detailView.personSectionCount, 4, "Every member must have exactly one completion section");
                assert.equal(detailView.itemSectionCount, 1, "Work items must not have duplicate audit sections");
                assert.equal(detailView.workItemHistories.length, 2, "The readable timeline and full recorded history table must both remain");
                assert(detailView.workItemHistories.every(history => history.includes('Example Contributor')
                    && history.includes('email: dev@example.test') && !history.includes('principal:') && !history.includes('ID:')),
                    "State histories must retain names and emails without Principal or ID annotations");
                assert.equal(detailView.viewWorkItemLinks, 0, "Work-item details must omit separate View Work Item links");
                assert.equal(detailView.recordedUrls.length, 1, "Recorded URL must appear once in the consolidated work item");
                assert(detailView.recordedUrls.every(link => link.href === 'https://example.test/item/42' && link.text === link.href),
                    "Each work item's Recorded URL value must itself be clickable");
                assert.equal(detailView.workItemEntries.length, 1, "Each work item needs a detailed entry under its member");
                assert.equal(detailView.workItemEntries[0].heading, '\u{1F4CB} Work Item #42 - Example item (Task)');
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
                assert(!detailText.includes("All contributor work item details"), "The separate contributor audit section must be removed");
                assert(!detailText.includes("Captured manager / report roster"), "The duplicate captured roster section must be removed");
                assert(detailText.indexOf("TEAM WORK ITEM SUMMARY BY AREA PATH") < detailText.indexOf("Captured inputs and export query context"));
                const totalsPosition = detailText.indexOf("Distinct work item totals and active-work time");
                assert(totalsPosition >= 0 && totalsPosition < detailText.indexOf("Captured inputs and export query context")
                    && detailView.contextIsLast, "Captured context must be the final Work Items report section, after distinct totals");
            } else if (incidents) {
                assert(detailText.includes("TEAM INCIDENT DETAILS BY ICM PATH") && detailText.includes("Incident Details:"));
                assert(detailText.includes("Contributors with recorded in-period actions"));
                assert(detailText.includes("TTD by severity"));
                if (collectedIncidentFixture) {
                    assert(detailText.includes("Synthetic mitigation"));
                    assert(!detailView.cells.includes("Action ID"), "The removed personal action tables must not remain in the export");
                    for (const text of [detailText, html]) {
                        assert(text.includes("Available warehouse data"), "Available-data coverage disclosure missing");
                        assert(!text.includes("Verified history window"), "Export invented coverage verification");
                        if (fixture === "incident-unkeyed-export") {
                            assert(text.includes("no HistoryId is invented"), "Missing-ID handling was not disclosed");
                        }
                    }
                } else {
                    assert(detailText.includes("Example mitigation") && !detailText.includes("action-1"));
                }
                assert(detailText.indexOf("TEAM INCIDENT SUMMARY BY ICM PATH") < detailText.indexOf("Captured inputs and export query context"));
                const totalsPosition = detailText.indexOf("Distinct incident totals and weighted duration statistics");
                assert(totalsPosition >= 0 && totalsPosition < detailText.indexOf("Captured inputs and export query context")
                    && detailView.contextIsLast, "Captured context must be the final Teams Incident section, after statistics and navigation fallbacks");
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
                    const contributorIncidentCounts = activeIncidentFixture && index === 0 ? ["2", "3", "4", "4", "2"] : incidentCounts;
                    assert.deepEqual(rows.find(row => row[0] === "Example Contributor").slice(offset, offset + length),
                        workItems ? ["1", "0", "1", "0", "0"] : incidents ? contributorIncidentCounts
                            : ["0", String((fixture === "large" ? 5000 : 2) * 12), "0"],
                        "Incorrect contributor counts");
                    if (activeIncidentFixture)
                        for (const owner of ["Zoe Lead", "Aaron Peer"])
                            assert.deepEqual(rows.find(row=>row[0]===owner).slice(offset, offset + length), ["1", "1", "0", "0", "1"],
                                "Owning-contact presentation must not transfer another actor's mitigation or resolution credit to the owner");
                    const zero = rows.find(row => row[0] === "Example Manager");
                    assert.deepEqual(zero.slice(offset, offset + length), Array(length).fill("0"), "Incorrect zero-activity counts");
                    if (workItems) assert.equal(zero[index === 0 ? 1 : 6], index === 0 ? "\u2014" : "");
                    if (incidents && index === 0) assert.equal(zero[1], "\u2014");
                }
                if (incidents) assert.deepEqual(report.tables[1].at(-1),
                    ["\u{1F3C1} TOTAL", ...(activeIncidentFixture ? ["4", "5", "5", "5", "5"] : incidentCounts)]);
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
    }
    console.log(fixture === "teams-select-all"
        ? "PASS browser teams-select-all; all defaults, deselection, clearing, fixed Feature Registry source and tab/view persistence."
        : `PASS browser ${fixture}; trusted-click picker shim, ZIP/content/unchanged UX (${checks} responsiveness probes).`);
} catch (error) {
    if (socket?.readyState === WebSocket.OPEN)
        console.error(await evaluate("document.body.innerText.slice(-6000)"));
    throw error;
} finally {
    socket?.close();
    browser.kill();
    await new Promise(resolve => { if (browser.exitCode !== null) resolve(); else browser.once("exit", resolve); });
    await rm(profile, { recursive: true, force: true, maxRetries: 10, retryDelay: 300 });
}
