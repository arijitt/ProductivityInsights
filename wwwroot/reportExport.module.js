const bindings = new Map();
const destinations = new Map();

function isCurrent(state) {
    return destinations.get(state.operation) === state
        && !state.binding.disposed
        && state.binding.button.isConnected
        && state.binding.button.dataset.reportRun === state.runId;
}

export function cancelDownload(operation) {
    const state = destinations.get(operation);
    if (!state) return;
    destinations.delete(operation);
    state.handle = null;
    state.cancel();
}

export function disposeBinding(id) {
    const binding = bindings.get(id);
    if (!binding) return;
    binding.disposed = true;
    binding.button.removeEventListener("click", binding.click);
    if (binding.operation) cancelDownload(binding.operation);
    binding.error?.remove();
    bindings.delete(id);
}

export function bindExportButton(id, button, callback) {
    if (!button?.isConnected) return false;
    const previous = bindings.get(id);
    if (previous?.button === button) return true;
    disposeBinding(id);
    const binding = { button, callback, disposed: false, operation: null, error: null };
    binding.click = () => {
        if (button.disabled || binding.disposed || binding.operation) return;
        const { reportRun: runId, reportFileName: fileName, reportReady } = button.dataset;
        if (reportReady !== "true" || !runId || !fileName) return;
        const operation = crypto.randomUUID();
        binding.operation = operation;
        binding.error?.remove();
        button.disabled = true;
        const state = { binding, operation, runId, handle: null };
        state.cancelled = new Promise(resolve => { state.cancel = () => resolve(false); });
        destinations.set(operation, state);
        try {
            if (!globalThis.isSecureContext || typeof globalThis.showSaveFilePicker !== "function")
                throw new Error("Choosing a report folder requires Microsoft Edge or Google Chrome on HTTPS or localhost. Open this page there and retry.");
            // Invoke synchronously in the native click, before any Blazor round trip consumes user activation.
            const picker = globalThis.showSaveFilePicker({
                id: "productivity-report-export",
                suggestedName: fileName,
                types: [{ description: "ZIP report archive", accept: { "application/zip": [".zip"] } }],
                excludeAcceptAllOption: true
            });
            state.selection = Promise.resolve(picker).then(handle => {
                if (!isCurrent(state)) return false;
                state.handle = handle;
                return true;
            }, error => {
                if (error.name !== "AbortError") state.error = error;
                return false;
            });
        } catch (error) {
            state.error = error;
            state.selection = Promise.resolve(false);
        }
        void (async () => {
            try {
                await callback.invokeMethodAsync("ExportReportsToFileAsync", runId, operation);
            } catch (error) {
                console.error("Report save could not communicate with the application.", error);
                if (!binding.disposed && button.isConnected) {
                    const notice = document.createElement("div");
                    notice.className = "alert alert-warning report-export-status";
                    notice.setAttribute("role", "alert");
                    notice.textContent = "Report save could not complete. Reconnect or reload the page and retry.";
                    button.parentElement.appendChild(notice);
                    binding.error = notice;
                }
            } finally {
                cancelDownload(operation);
                if (binding.operation === operation) {
                    binding.operation = null;
                    if (!binding.disposed) button.disabled = button.dataset.reportReady !== "true";
                }
            }
        })();
    };
    button.addEventListener("click", binding.click);
    bindings.set(id, binding);
    return true;
}

export async function waitForDestination(operation) {
    const state = destinations.get(operation);
    if (!state) return false;
    const selected = await Promise.race([state.selection, state.cancelled]);
    if (!isCurrent(state)) return false;
    if (state.error) throw state.error;
    return selected;
}

export async function saveReport(streamReference, operation) {
    const state = destinations.get(operation);
    if (!state || !isCurrent(state)) return false;
    if (!state.handle) throw new Error("Choose a ZIP destination before exporting.");
    let writable;
    let committed = false;
    try {
        const bytes = await streamReference.arrayBuffer();
        if (!isCurrent(state)) return false;
        writable = await state.handle.createWritable();
        if (!isCurrent(state)) return false;
        await writable.write(bytes);
        if (!isCurrent(state)) return false;
        // close() is the commit point; earlier failure/cancellation must leave an existing file intact.
        await writable.close();
        committed = true;
        return true;
    } finally {
        try {
            if (writable && !committed) await writable.abort();
        } finally {
            cancelDownload(operation);
        }
    }
}
