import assert from "node:assert/strict";

export async function runSourceSelectionBrowser({ url, evaluate, until, cdp, pageSession }) {
    const calls = async () => (await fetch(new URL("selection-fixture-state", url))).json();
    const incidentCompleted = async () => (await fetch(new URL("incident-selection-fixture-state", url))).json();
    const runButton = `[...document.querySelectorAll('button')].find(button=>button.textContent.trim()==='Run Analysis')`;
    const panel = `[...document.querySelectorAll('.rz-dropdown-panel,.rz-multiselect-panel')].find(node=>node.checkVisibility())`;
    const defaultSources = {
        "Work Items": "msdata : A365 : Cosmos on Azure Job Service : A365\\Cosmos Analytics\\Platforms\\Cosmos on Azure",
        "Git Commits": "msdata : A365 : BBC-VHD : master",
        "Incident Resolutions": "AZURESYNAPSEJOBSERVICE\\JobPlatform"
    };
    const click = async expression => {
        const point = await evaluate(`(() => {
            const node=${expression}; node.scrollIntoView({block:'center'});
            const rect=node.getBoundingClientRect(); return {x:rect.x+rect.width/2,y:rect.y+rect.height/2};
        })()`);
        await cdp("Input.dispatchMouseEvent", { type:"mousePressed", ...point, button:"left", clickCount:1 }, pageSession);
        await cdp("Input.dispatchMouseEvent", { type:"mouseReleased", ...point, button:"left", clickCount:1 }, pageSession);
    };
    const tab = async label => {
        await evaluate(`[...document.querySelectorAll('.nav-tabs button')].find(button=>button.textContent.trim()===${JSON.stringify(label)}).click()`);
        await until(() => evaluate(`[...document.querySelectorAll('.nav-tabs button.active')].some(button=>button.textContent.trim()===${JSON.stringify(label)})`), label);
    };
    const selected = async () => evaluate(`(() => {
        const popup=${panel}; return popup ? [...popup.querySelectorAll('[role=option]')]
            .filter(node=>node.getAttribute('aria-selected')==='true').map(node=>node.textContent.trim()) : null;
    })()`);
    const clickSource = source => click(`[...(${panel}).querySelectorAll('[role=option]')]
        .find(node=>node.textContent.trim()===${JSON.stringify(source)})`);
    const checkDropdownOrder = async label => {
        const options = await evaluate(`[...(${panel}).querySelectorAll('[role=option]')].map(node=>node.textContent.trim())`);
        for (let index = 1; index < options.length; index++)
            assert(options[index - 1].toUpperCase() <= options[index].toUpperCase(),
                `${label} must use case-insensitive alphabetical order: ${options[index - 1]} before ${options[index]}`);
    };
    const open = async () => {
        await click("document.querySelector('.team-dd [role=combobox]')");
        await until(() => selected(), "checkbox selector opened", 10000);
    };
    const close = async () => {
        await click("document.querySelector('.team-dd [role=combobox]')");
        await until(() => evaluate(`!(${panel})`), "selector closed", 10000);
    };
    const checkDropdownContrast = async label => {
        const samples = await evaluate(`(() => {
            const popup=${panel};
            const original=document.documentElement.getAttribute('data-bs-theme');
            const samples=[];
            const background=node=>{
                const layers=[];
                for(let current=node;current;current=current.parentElement) {
                    layers.push(getComputedStyle(current).backgroundColor.match(/[\\d.]+/g).map(Number));
                }
                let composite=[255,255,255];
                for(const layer of layers.reverse()) {
                    const alpha=layer[3]??1;
                    composite=composite.map((channel,index)=>layer[index]*alpha+channel*(1-alpha));
                }
                return 'rgb('+composite.join(',')+')';
            };
            try {
                for(const theme of ['light','dark']) {
                    document.documentElement.setAttribute('data-bs-theme',theme);
                    for(const node of popup.querySelectorAll('[role=option],.rz-multiselect-selectall')) {
                        const text=[...node.querySelectorAll('span')].find(span=>span.textContent.trim() && !span.closest('[role=checkbox]'))??node;
                        samples.push({theme,selected:node.getAttribute('aria-selected'),text:getComputedStyle(text).color,
                            background:background(node),classes:node.className});
                    }
                }
            } finally {
                if(original===null)document.documentElement.removeAttribute('data-bs-theme');
                else document.documentElement.setAttribute('data-bs-theme',original);
            }
            return samples;
        })()`);
        const luminance = color => {
            const channels = color.match(/[\d.]+/g).slice(0,3).map(Number).map(value => value / 255)
                .map(value => value <= 0.04045 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4);
            return channels[0] * 0.2126 + channels[1] * 0.7152 + channels[2] * 0.0722;
        };
        for (const sample of samples) {
            const text = luminance(sample.text), background = luminance(sample.background);
            const ratio = (Math.max(text, background) + 0.05) / (Math.min(text, background) + 0.05);
            assert(ratio >= 4.5, `${label} dropdown text must be readable: ${JSON.stringify({...sample,ratio})}`);
            if (sample.theme === "dark" && sample.selected === "false")
                assert(text < 0.2, `${label} dark-mode popup must use dark item text`);
        }
    };
    const checkDropdownInteractionContrast = async label => {
        await checkDropdownContrast(label);
        const point = await evaluate(`(() => {
            const node=(${panel}).querySelectorAll('[role=option]')[1];
            node.scrollIntoView({block:'nearest'});
            const rect=node.getBoundingClientRect();return {x:rect.x+rect.width/2,y:rect.y+rect.height/2};
        })()`);
        await cdp("Input.dispatchMouseEvent", { type:"mouseMoved", ...point }, pageSession);
        assert(await evaluate(`(${panel}).querySelectorAll('[role=option]')[1].matches(':hover')`));
        await checkDropdownContrast(`${label} hovered`);
        await evaluate(`(${panel}).querySelectorAll('[role=option]')[1].focus()`);
        assert(await evaluate(`(${panel}).querySelectorAll('[role=option]')[1].matches(':focus')`));
        await checkDropdownContrast(`${label} focused`);
    };
    const run = async count => {
        const before = (await calls()).length;
        await click(runButton);
        await until(() => evaluate("!!document.querySelector('#analysisModal.show')"), "confirmation shown");
        assert(await evaluate(`document.querySelector('.analysis-params').textContent.includes('Selections (${count})')`));
        assert.equal(await evaluate("document.querySelector('.analysis-params').textContent.includes('Manager Filter')"), false);
        await click("document.querySelector('#analysisModal .btn-primary')");
        await until(() => evaluate("!document.querySelector('#analysisModal.show')"), "analysis confirmed");
        await until(async () => (await calls()).length > before, "confirmed analysis started", 10000);
    };
    await until(() => evaluate(`Object.getOwnPropertyNames(${runButton} ?? {}).some(key=>key.startsWith('_blazorEvents_'))`), "hydrated selectors");
    for (const view of ["Details", "Trends"]) {
        await tab(view);
        for (const [analysis, total] of [["Work Items",17],["Git Commits",24],["Incident Resolutions",5]]) {
            const incidents = analysis === "Incident Resolutions";
            await tab(analysis);
            assert.equal(await evaluate("document.querySelectorAll('#teamSelect,#areaPathSelect,#gitRepositorySelect,#targetBranchSelect,#owningTeamSelect').length"), 0);
            await open();
            await checkDropdownInteractionContrast(`${view} ${analysis}`);
            await checkDropdownOrder(`${view} ${analysis}`);
            assert.deepEqual(await selected(), [defaultSources[analysis]],
                "Sorting must preserve the original Details/Trends default, even when it is not the first option");
            assert.equal(await evaluate(`(${panel}).querySelectorAll('[role=option]').length`), total);
            await clickSource(defaultSources[analysis]);
            await until(async () => (await selected())?.length === 0, "clear individual default");
            await close();
            assert(await evaluate(`${runButton}.disabled`), "An empty selection cannot run");
            await open();
            await click(`(${panel}).querySelector('[role=checkbox]')`);
            await until(async () => (await selected())?.length === total, "select all");
            await click(`(${panel}).querySelector('[role=checkbox]')`);
            await until(async () => (await selected())?.length === 0, "clear all");
            const first = analysis === "Work Items" ? (view === "Details"
                ? "msdata : HDInsight : Security : HDInsight\\Security"
                : "msdata : A365 : One Infra - Reliability : A365\\One Infra\\Reliability") : defaultSources[analysis];
            const second = incidents ? "HDINSIGHT\\ControlPlane" : analysis === "Work Items" ? (view === "Details"
                ? "msdata : HDInsight : HDInsight Team : HDInsight\\HDI CP Platform"
                : "msdata : HDInsight : HDI Product Management : HDInsight\\HDI Product Management")
                : "msdata : HDInsight : HDInsight-Hilo : master";
            await clickSource(first);
            await until(async () => (await selected())?.length === 1, "select one source");
            await clickSource(second);
            await until(async () => (await selected())?.length === 2, "select a second source");
            await close();
            const before = (await calls()).length;
            const completedBefore = incidents ? await incidentCompleted() : 0;
            await run(2);
            // Edit the input immediately after confirmation; the running request must stay at two sources.
            await open();
            await clickSource(first);
            await until(async () => (await selected())?.length === 1, "edit selection during collection");
            await close();
            await until(async () => {
                const recorded = (await calls()).slice(before);
                return recorded.filter(call=>call.startsWith(incidents ? "incidents:" : analysis === "Work Items" ? "work:" : "commits:")).length === (incidents ? 8 : 2)
                    && (!incidents || await incidentCompleted() === completedBefore + 8)
                    && (view !== "Trends" || analysis !== "Git Commits" || recorded.filter(call=>call.startsWith("details:")).length === 2);
            }, "both captured sources collected");
            if (analysis === "Git Commits")
                assert((await calls()).slice(before).includes("commits:msdata/HDInsight/HDInsight-Hilo/master"),
                    "HDInsight-Hilo collection must use master in Details and Trends");
            if (analysis === "Work Items") {
                const [project, area] = view === "Details"
                    ? ["HDInsight", "HDInsight\\HDI CP Platform"]
                    : ["HDInsight", "HDInsight\\HDI Product Management"];
                assert((await calls()).slice(before).includes(`work:msdata/${project}/${area}`),
                    "Each selection must query its recorded project and area path");
                if (view === "Details")
                    assert((await calls()).slice(before).includes("work:msdata/HDInsight/HDInsight\\Security"),
                        "HDI CP Platform must collect alongside the selected Security area");
                else
                    assert((await calls()).slice(before).includes("work:msdata/A365/A365\\One Infra\\Reliability"),
                        "Reliability must query its exact area alongside the selected HDInsight source");
            }
            if (view === "Details") {
                await until(() => evaluate("document.querySelector('.contributor-row')?.textContent.includes('2 ')"), "multi-source totals");
                assert(await evaluate(`document.querySelector('.table-title-header').textContent.includes(${JSON.stringify(incidents ? "2 owning team(s)" : "2 selection(s)")})`),
                    "Editing inputs must not relabel the completed result");
                await click("document.querySelector('.expand-btn')");
                await until(() => evaluate(`document.querySelector('.details-row')?.textContent.includes(${JSON.stringify(
                    incidents ? "Total Incidents: 2" : analysis === "Work Items" ? "Total Work Items Completed: 2" : "Total Commits: 2")})`), "source-correct contributor details");
                const detailCalls = (await calls()).slice(before);
                assert(incidents ? detailCalls.some(call=>call.includes("incidents:Resolved:HDINSIGHT"))
                    : analysis === "Work Items" ? detailCalls.includes("history:msdata")
                    : detailCalls.includes("details:msdata/HDInsight/HDInsight-Hilo/master"),
                    "Drill-down must use captured sources after form edits");
            } else if (analysis === "Git Commits") {
                await until(() => evaluate("document.querySelector('#analysisResultsContainer')?.textContent.includes('2 selection(s)')"), "combined Git trends");
            } else {
                assert(await evaluate("document.querySelector('#analysisResultsContainer').textContent.includes('trend visualization coming soon')"),
                    "Work Items and Incident Trends must retain their existing placeholders rather than invent charts");
            }
            assert.equal(await evaluate("document.querySelectorAll('.alert-danger').length"), 0);
        }
    }
    await tab("Details");
    await tab("Work Items");
    await open();
    assert.equal((await selected()).length, 1, "Each view retains its own edited selections");
    await close();
    assert(await evaluate("document.querySelector('.table-title-header').textContent.includes('2 selection(s)')"),
        "Returning to a cached result must restore its executed source context");
    await tab("Incident Resolutions");
    await open();
    assert.equal((await selected()).length, 1, "Incident Details retains its edited selection");
    await close();
    assert(await evaluate("document.querySelector('.table-title-header').textContent.includes('2 owning team(s)')"),
        "Restoring Incident Details must retain the executed owning teams");
    await tab("Work Items");
    await tab("Teams");
    for (const [analysis, total] of [["Work Items",17],["Git Commits",24],["Incident Resolutions",5]]) {
        await tab(analysis);
        await open();
        await checkDropdownInteractionContrast(`Teams ${analysis}`);
        await checkDropdownOrder(`Teams ${analysis}`);
        assert.equal((await selected()).length, total, "Non-Teams changes must not alter Teams Select All");
        await close();
    }
    await tab("Work Items");
    assert(await evaluate(`${runButton}.disabled`), "Teams still requires a fetched manager roster");
    await tab("Details");
    await tab("Git Commits");
    await open();
    assert.equal((await selected()).length, 1);
    await close();
    const beforeStale = (await calls()).length;
    await run(1);
    await until(() => evaluate("!!document.querySelector('#analysisResultsContainer .spinner-border')"),
        "pending Git analysis rendered before switching tabs", 10000);
    await tab("Work Items");
    await until(async () => (await calls()).length > beforeStale, "superseded run started");
    await new Promise(resolve => setTimeout(resolve, 900));
    assert(await evaluate("document.querySelector('.table-title-header').textContent.includes('Work Items')"),
        "A superseded Git run must not publish into Work Items");
    assert.equal(await evaluate("document.querySelectorAll('#analysisResultsContainer .spinner-border').length"), 0,
        "A superseded request must not leave the restored result loading");
    await tab("Incident Resolutions");
    await run(1);
    await until(() => evaluate("!!document.querySelector('#analysisResultsContainer .spinner-border')"),
        "pending Incident analysis rendered before switching tabs", 10000);
    await tab("Work Items");
    await new Promise(resolve => setTimeout(resolve, 900));
    assert(await evaluate("document.querySelector('.table-title-header').textContent.includes('Work Items')"),
        "A cancelled Incident run must not publish into another tab");
}
