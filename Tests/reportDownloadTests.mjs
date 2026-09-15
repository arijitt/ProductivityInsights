import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";

const source = await readFile(new URL("../wwwroot/reportExport.module.js", import.meta.url), "utf8");
const module = await import(`data:text/javascript;base64,${Buffer.from(source).toString("base64")}`);
const originals = {
    document: globalThis.document, isSecureContext: globalThis.isSecureContext,
    showSaveFilePicker: globalThis.showSaveFilePicker
};
globalThis.document = {
    createElement: () => ({ setAttribute() {}, remove() {} })
};
globalThis.isSecureContext = true;
let cases = 0;
function deferred() {
    let resolve;
    const promise = new Promise(done => { resolve = done; });
    return { promise, resolve };
}
function fixture(options = {}) {
    const id = `binding-${++cases}`;
    const events = [];
    const button = new EventTarget();
    button.dataset = { reportRun: "run-1", reportFileName: "suggested.zip", reportReady: "true" };
    button.isConnected = true;
    button.disabled = false;
    button.parentElement = { appendChild() { events.push("notice"); } };
    button.click = () => button.dispatchEvent(new Event("click"));
    let contents = "existing file contents";
    let pending;
    let task;
    let operation;
    let collections = 0;
    const handle = {
        async createWritable() {
            events.push("open");
            if (options.openError) throw options.openError;
            if (options.openGate) await options.openGate.promise;
            return {
                async write(bytes) {
                    events.push("write");
                    pending = Buffer.from(bytes).toString();
                    if (options.writeGate) await options.writeGate.promise;
                    if (options.writeError) throw options.writeError;
                },
                async close() {
                    events.push("close");
                    if (options.closeError) throw options.closeError;
                    contents = pending;
                },
                async abort() { events.push("abort"); pending = undefined; }
            };
        }
    };
    globalThis.showSaveFilePicker = config => {
        events.push("picker");
        assert.equal(config.suggestedName, "suggested.zip");
        assert.deepEqual(config.types[0].accept, { "application/zip": [".zip"] });
        assert.equal(config.excludeAcceptAllOption, true);
        if (options.pickerError) return Promise.reject(options.pickerError);
        return options.pickerGate?.promise ?? Promise.resolve(handle);
    };
    const callback = {
        invokeMethodAsync(name, runId, key) {
            events.push("dotnet");
            assert.equal(name, "ExportReportsToFileAsync");
            assert.equal(runId, button.dataset.reportRun);
            operation = key;
            task = (async () => {
                try {
                    if (!await module.waitForDestination(key)) return "cancelled";
                    collections++;
                    events.push("collect");
                    if (options.collectError) throw options.collectError;
                    return await module.saveReport({
                        async arrayBuffer() {
                            events.push("read");
                            if (options.readError) throw options.readError;
                            if (options.readGate) await options.readGate.promise;
                            return new TextEncoder().encode("complete ZIP").buffer;
                        }
                    }, key);
                } catch (error) {
                    return error;
                } finally {
                    module.cancelDownload(key);
                }
            })();
            return task;
        }
    };
    assert(module.bindExportButton(id, button, callback));
    assert(module.bindExportButton(id, button, callback), "Rebinding must not duplicate listeners");
    return {
        id, button, events, handle,
        get operation() { return operation; },
        get contents() { return contents; },
        get collections() { return collections; },
        async finish() {
            const result = await task;
            await Promise.resolve();
            return result;
        },
        dispose() { module.disposeBinding(id); }
    };
}
async function reached(test) {
    for (let index = 0; index < 30; index++) {
        if (test()) return;
        await Promise.resolve();
    }
    assert.fail("Expected asynchronous boundary was not reached");
}
try {
    const success = fixture();
    success.button.click();
    success.button.click();
    assert.deepEqual(success.events, ["picker", "dotnet"], "Picker must open synchronously before .NET and only once");
    assert.equal(await success.finish(), true);
    assert.deepEqual(success.events, ["picker", "dotnet", "collect", "read", "open", "write", "close"]);
    assert.equal(success.contents, "complete ZIP");
    assert.equal(success.button.disabled, false);
    assert.equal(await module.waitForDestination(success.operation), false, "Saved handles must be released");
    success.dispose();
    success.button.click();
    assert.equal(success.collections, 1, "Disposed listener invoked .NET");

    const cancel = fixture({ pickerError: new DOMException("Cancelled", "AbortError") });
    cancel.button.click();
    assert.equal(await cancel.finish(), "cancelled");
    assert.equal(cancel.collections, 0, "Cancelling a picker collected data");
    assert.equal(cancel.contents, "existing file contents");
    assert.equal(cancel.button.disabled, false);
    cancel.dispose();

    for (const secure of [true, false]) {
        const unsupported = fixture();
        globalThis.isSecureContext = secure;
        if (secure) globalThis.showSaveFilePicker = undefined;
        unsupported.button.click();
        assert.match((await unsupported.finish()).message, /Edge or Google Chrome on HTTPS or localhost/);
        assert.equal(unsupported.collections, 0);
        assert(!unsupported.events.includes("open"), "Unsupported browser opened a file");
        unsupported.dispose();
        globalThis.isSecureContext = true;
    }

    for (const point of ["picker", "collect", "read", "open", "write", "close"]) {
        const failure = fixture({ [`${point}Error`]: new Error(`${point} failed`) });
        failure.button.click();
        assert.match((await failure.finish()).message, new RegExp(`${point} failed`));
        assert.equal(failure.contents, "existing file contents", `${point} failure overwrote the destination`);
        assert.equal(failure.events.includes("abort"), ["write", "close"].includes(point));
        assert.equal(await module.waitForDestination(failure.operation), false);
        failure.dispose();
    }

    for (const point of ["picker", "read", "open", "write"]) {
        const gate = deferred();
        const stale = fixture({ [`${point}Gate`]: gate });
        stale.button.click();
        await reached(() => stale.events.includes(point));
        module.cancelDownload(stale.operation);
        if (point === "picker") {
            assert.equal(await stale.finish(), "cancelled", "Cancellation must release the waiter without closing the OS dialog");
            assert.equal(stale.collections, 0);
        }
        gate.resolve(point === "picker" ? stale.handle : undefined);
        if (point !== "picker") assert.equal(await stale.finish(), false);
        assert.equal(stale.contents, "existing file contents", "Cancelled save modified the destination");
        assert(!stale.events.includes("close"));
        assert.equal(stale.events.includes("abort"), ["open", "write"].includes(point));
        stale.dispose();
    }

    for (const invalidate of [
        value => { value.button.dataset.reportRun = "new-run"; },
        value => { value.button.isConnected = false; },
        value => value.dispose()
    ]) {
        const gate = deferred();
        const stale = fixture({ pickerGate: gate });
        stale.button.click();
        invalidate(stale);
        gate.resolve(stale.handle);
        assert.equal(await stale.finish(), "cancelled");
        assert.equal(stale.collections, 0, "A superseded/disposed picker started collection");
        stale.dispose();
    }

    const denied = fixture({ pickerError: new DOMException("Permission denied", "NotAllowedError") });
    denied.button.click();
    assert.match((await denied.finish()).message, /Permission denied/);
    denied.dispose();
    console.log(`PASS ${cases} save-dialog cases: gesture/order, cancellation, unsupported context, errors, atomic writes, stale runs, and cleanup.`);
} finally {
    for (const [name, value] of Object.entries(originals)) {
        if (value === undefined) delete globalThis[name];
        else globalThis[name] = value;
    }
}
