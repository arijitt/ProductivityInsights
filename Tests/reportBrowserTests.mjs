import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { existsSync } from "node:fs";
import { mkdtemp, readFile, readdir, rm, mkdir } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { inflateRawSync } from "node:zlib";
import { createHash } from "node:crypto";

const url = process.argv[2] ?? "http://127.0.0.1:5187/";
const fixture = process.argv[3] ?? "gitCommits";
const collectedIncidentFixture = fixture === "incident-available-export" || fixture === "incident-unkeyed-export";
const incidentFixture = fixture === "incidentManagement" || collectedIncidentFixture;
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
    await until(() => evaluate("!!document.querySelector('.commit-summary-card') || (document.querySelector('.analysis-summary-table button')?.click(), false)"), "interactive contributor expansion");
    const before = await evaluate("({html:document.querySelector('.analysis-summary-table').innerHTML,heading:document.querySelector('.table-title-header').textContent})");
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
        const [markdownName, markdown] = [...entries].find(([name]) => name.endsWith(".md"));
        const [htmlName, html] = [...entries].find(([name]) => name.endsWith(".html"));
        assert.equal(markdownName.slice(0, -3), htmlName.slice(0, -5));
        assert(html.includes(markdownName));
        assert(!/<script\b|<img\b/i.test(html), "Export contains external/script/image content");
        assert(markdown.includes("Example Contributor"));
        if (fixture === "gitCommits" || fixture === "large" || fixture === "workItems" || incidentFixture) {
            const workItems = fixture === "workItems";
            const incidents = incidentFixture;
            const incidentCounts = collectedIncidentFixture ? ["0", "1", "0", "1", "1"] : ["0", "1", "1", "1", "1"];
            if (workItems) {
                assert(markdown.includes("\u{1F3C6} TEAM WORK ITEM COMPLETION DETAILS BY PERSON"));
                assert(markdown.includes("New \u2192 Active by Example Contributor on 2026-07-09"));
                assert(markdown.includes("State change history") && markdown.includes("Total time by state"));
                assert(markdown.indexOf("TEAM WORK ITEM SUMMARY BY AREA PATH") < markdown.indexOf("Captured inputs and export query context"));
            } else if (incidents) {
                assert(markdown.includes("TEAM INCIDENT DETAILS BY ICM PATH") && markdown.includes("Incident Details:"));
                assert(markdown.includes("Contributors with recorded in-period actions"));
                assert(markdown.includes("TTD by severity"));
                if (collectedIncidentFixture) {
                    assert(markdown.includes("Synthetic mitigation"));
                    assert(markdown.includes(fixture === "incident-unkeyed-export" ? "| Not provided |" : "| 100 |"));
                    for (const text of [markdown, html]) {
                        assert(text.includes("Available warehouse data"), "Available-data coverage disclosure missing");
                        assert(!text.includes("Verified history window"), "Export invented coverage verification");
                        if (fixture === "incident-unkeyed-export") {
                            assert(text.includes("no HistoryId is invented"), "Missing-ID handling was not disclosed");
                        }
                    }
                } else {
                    assert(markdown.includes("Example mitigation") && markdown.includes("action-1"));
                }
                assert(markdown.indexOf("TEAM INCIDENT SUMMARY BY ICM PATH") < markdown.indexOf("Captured inputs and export query context"));
            } else {
                assert(markdown.replaceAll("\\_", "_").includes("file_11.cs"), "Full changed-file collection missing");
                assert(markdown.includes(fixture === "large" ? "Example change 4999" : "Example change 1"));
                assert(markdown.includes("\u{1F4CB} TEAM CODE COMMIT DETAILS BY PERSON:"));
                assert(markdown.includes("\u{1F504} Commit:"));
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
                assert(!markdown.includes("Files Added") && !markdown.includes("Files Modified") && !markdown.includes("Files Deleted"),
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
            assert(markdown.includes("Skipped sources - excluded from totals"));
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
                const recoveredMarkdown = [...recovered].find(([file]) => file.endsWith(".md"))[1];
                assert(recoveredMarkdown.includes("Example change 1"), "Recovered source activity was not exported");
                assert(!recoveredMarkdown.includes("Skipped sources - excluded from totals"), "Unavailable snapshot was cached");
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
