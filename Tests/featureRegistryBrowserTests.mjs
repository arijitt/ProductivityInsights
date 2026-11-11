import assert from "node:assert/strict";
import { mkdir, readFile, readdir, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { pathToFileURL } from "node:url";

export async function runFeatureRegistryBrowser({ fixture, url, profile, downloads, completedDownloads,
    evaluate, cdp, until, clickExport, unzip }) {
    const state = async () => await (await fetch(new URL("feature-fixture-state", url))).json();
    const rowButtons = `[...document.querySelectorAll('.nav-tabs:not(.nav-tabs-vertical) button')]`;
    const viewButtons = `[...document.querySelectorAll('.nav-tabs-vertical button')]`;
    const clickTab = async label => await evaluate(`${rowButtons}.find(button=>button.textContent.trim()===${JSON.stringify(label)}).click()`);
    const clickView = async label => await evaluate(`${viewButtons}.find(button=>button.textContent.trim()===${JSON.stringify(label)}).click()`);
    const runButton = `[...document.querySelectorAll('button')].find(button=>button.textContent.trim()==='Run Analysis')`;
    await until(() => evaluate(`Object.getOwnPropertyNames(${runButton} ?? {}).some(key=>key.startsWith('_blazorEvents_'))`), "form hydration");
    assert.deepEqual(await evaluate(`${rowButtons}.map(button=>button.textContent.trim())`),
        ["Work Items", "Git Commits", "Incident Resolutions", "Feature Registry"]);
    assert.equal(await evaluate(`document.querySelectorAll('.teams-manager-warning').length`), 0,
        "The preloaded, completed manager roster must not show a prerequisite warning");
    const dateStyles = async ids => await evaluate(`(async () => {
        const inputs=${JSON.stringify(ids)}.map(id=>document.getElementById(id));
        const properties=['display','padding','marginBottom','fontFamily','fontSize','fontWeight','color',
            'backgroundColor','border','borderRadius','boxShadow','backdropFilter','transform','transitionDuration','gap','position','isolation','content'];
        const styles=(element,pseudo)=>Object.fromEntries(properties.map(key=>[key,getComputedStyle(element,pseudo)[key]]));
        const group=inputs[0].closest('.datetime-group');
        const settle=async()=>{
            group.getBoundingClientRect();
            for(const animation of group.getAnimations({subtree:true}))
                if(animation instanceof CSSTransition)animation.finish();
            await new Promise(requestAnimationFrame);
        };
        const result={};
        const previousTheme=document.documentElement.getAttribute('data-bs-theme');
        for(const theme of ['light','dark']) {
            document.documentElement.setAttribute('data-bs-theme',theme);
            inputs.forEach(input=>input.blur());
            await settle();
            const fields=inputs.map(input=>{
                const label=document.querySelector('label[for="'+input.id+'"]');
                return {label:label.textContent,type:input.type,input:styles(input),wrapper:styles(input.parentElement),
                    labelStyle:styles(label),icon:styles(label,'::before')};
            });
            const normal={group:styles(group),fields};
            inputs[0].focus();
            await settle();
            result[theme]={normal,focused:{input:styles(inputs[0]),wrapper:styles(inputs[0].parentElement,'::after'),
                group:styles(group,'::before')}};
            inputs[0].blur();
        }
        if(previousTheme===null)document.documentElement.removeAttribute('data-bs-theme');
        else document.documentElement.setAttribute('data-bs-theme',previousTheme);
        return result;
    })()`);
    const gitDateStyles = await dateStyles(["startDateTime", "endDateTime"]);
    const resultStyles = async expanded => await evaluate(`(async () => {
        const root=document.querySelector('#analysisResultsContainer');
        const table=root.querySelector('.analysis-summary-table');
        const properties=['display','padding','margin','fontFamily','fontSize','fontWeight','lineHeight','letterSpacing',
            'color','backgroundColor','backgroundImage','borderTopWidth','borderTopColor','borderBottomWidth','borderBottomColor',
            'borderRadius','boxShadow','backdropFilter','tableLayout','overflowWrap','textAlign'];
        const selectors=['.table-responsive','.analysis-summary-table','.table-title-header','thead tr:last-child',
            'thead tr:last-child th:first-child','.contributor-row','.contributor-row td:first-child',
            '.contributor-row td:nth-child(2)','.contributor-row td:last-child','.expand-btn','.expand-btn span',
            ...(${expanded} ? ['.details-row','.details-row td','.commit-details'] : [])];
        const previousTheme=document.documentElement.getAttribute('data-bs-theme');
        const themes={};
        try {
            for(const theme of ['light','dark']) {
                document.documentElement.setAttribute('data-bs-theme',theme);
                root.getBoundingClientRect();
                for(const animation of root.getAnimations({subtree:true}))
                    if(animation instanceof CSSTransition || animation instanceof CSSAnimation)animation.finish();
                await new Promise(requestAnimationFrame);
                themes[theme]=selectors.map(selector=>{
                    const node=root.querySelector(selector);
                    if(!node)return null;
                    const style=getComputedStyle(node);
                    return {tag:node.tagName,classes:node.className,
                        style:Object.fromEntries(properties.map(key=>[key,style[key]]))};
                });
            }
            return {themes,columns:[...table.querySelectorAll('col')].map(col=>col.style.width),
                headings:[...table.querySelectorAll('thead tr:last-child th')].map(th=>th.textContent),
                spacing:getComputedStyle(table.parentElement.parentElement).marginTop};
        } finally {
            if(previousTheme===null)document.documentElement.removeAttribute('data-bs-theme');
            else document.documentElement.setAttribute('data-bs-theme',previousTheme);
        }
    })()`);
    const gitResultStyles = await resultStyles(false);
    await evaluate(`document.querySelector('.analysis-summary-table .expand-btn').click()`);
    await until(() => evaluate(`!!document.querySelector('.analysis-summary-table .details-row')`), "reference Git detail layout");
    const gitExpandedResultStyles = await resultStyles(true);
    await evaluate(`document.querySelector('.analysis-summary-table .expand-btn').click()`);
    await until(() => evaluate(`!document.querySelector('.analysis-summary-table .details-row')`), "reference Git collapse");
    const original = await evaluate(`({
        html:document.querySelector('.analysis-summary-table').innerHTML,
        run:document.querySelector('.analysis-export-btn').dataset.reportRun,
        start:document.querySelector('#startDateTime').value,end:document.querySelector('#endDateTime').value
    })`);
    await clickTab("Feature Registry");
    await until(() => evaluate(`!!document.querySelector('#featureRegistryStart')`), "dedicated PR form");
    assert.deepEqual(await dateStyles(["featureRegistryStart", "featureRegistryEnd"]), gitDateStyles,
        "Feature Registry date controls must match Git labels, scoped styles, calendar icons and focus effects in both themes");
    const view = fixture === "feature-details" ? "Details" : fixture === "feature-trends" ? "Trends" : "Teams";
    if (view !== "Teams") await clickView(view);
    await until(() => evaluate(`${viewButtons}.find(button=>button.classList.contains('active')).textContent.trim()===${JSON.stringify(view)}`), "selected PR view");
    assert.equal(await evaluate(`document.querySelector('#gitRepositorySelect,#targetBranchSelect,#trendDailyGC,.team-dd') === null`), true,
        "Feature Registry must not reuse ordinary Git selection controls");
    assert.equal((await state()).calls, 0);
    await evaluate(`(() => {
        window.featureScrollCalls=0;
        const scroll=window.scrollToBottom;
        window.scrollToBottom=(...args)=>{window.featureScrollCalls++;return scroll(...args);};
        if(${fixture === "feature-slow" || fixture === "feature-late"})
            document.querySelector('.feature-registry-panel').style.minHeight='calc(100vh + 300px)';
    })()`);
    const gitConfirmationHtml = (await state()).gitConfirmationHtml;
    const checkConfirmation = async () => {
        const result = await evaluate(`(async () => {
            const modal=document.querySelector('#analysisModal');
            const body=modal.querySelector('.modal-body');
            const reference=document.createElement('div');
            reference.className='analysis-params';
            reference.innerHTML=${JSON.stringify(gitConfirmationHtml)};
            reference.style.visibility='hidden';
            body.append(reference);
            const actual=body.querySelector('.analysis-params');
            const selectors=['.parameter-section','.parameter-section h6','table','tbody tr:first-child td:first-child','tbody tr:first-child td:last-child'];
            const properties=['fontFamily','fontSize','fontWeight','color','backgroundColor','padding','marginBottom',
                'borderTopWidth','borderTopStyle','borderTopColor','borderRadius','borderCollapse','boxShadow','textAlign'];
            const capture=root=>selectors.map(selector=>{
                const node=root.querySelector(selector);
                if(!node)return null;
                const style=getComputedStyle(node);
                return {tag:node.tagName,classes:node.className,style:Object.fromEntries(properties.map(key=>[key,style[key]]))};
            });
            const previousTheme=document.documentElement.getAttribute('data-bs-theme');
            const themes={};
            try {
                for(const theme of ['light','dark']) {
                    document.documentElement.setAttribute('data-bs-theme',theme);
                    modal.getBoundingClientRect();
                    for(const animation of modal.getAnimations({subtree:true}))
                        if(animation instanceof CSSTransition)animation.finish();
                    await new Promise(requestAnimationFrame);
                    themes[theme]={actual:capture(actual),reference:capture(reference)};
                }
                return {themes,heading:actual.querySelector('h6')?.textContent,
                    labelWidth:actual.querySelector('td')?.style.width,
                    fields:Object.fromEntries([...actual.querySelectorAll('tr')].map(row=>[row.cells[0].textContent,row.cells[1].textContent]))};
            } finally {
                reference.remove();
                if(previousTheme===null)document.documentElement.removeAttribute('data-bs-theme');
                else document.documentElement.setAttribute('data-bs-theme',previousTheme);
            }
        })()`);
        for (const { actual, reference } of Object.values(result.themes))
            assert.deepEqual(actual, reference, "Feature Registry confirmation must match Git's heading, compact table, borders and typography");
        assert.equal(result.heading, "Analysis Type: Feature Registry Analysis");
        assert.equal(result.labelWidth, "200px");
        assert.equal(result.fields["Git Repository"], "FeatureRegistry");
        assert.equal(result.fields["Target Branch"], "All target branches");
        assert.equal(result.fields["Start Date Time"], "2026-07-01 00:00");
        assert.equal(result.fields["End Date Time"], "2026-07-31 00:00");
        assert.equal("Manager Filter" in result.fields, view === "Teams");
        assert(!("Trend Interval" in result.fields));
    };
    const run = async () => {
        await evaluate(`${runButton}.click()`);
        await until(() => evaluate(`document.querySelector('#analysisModal.show .analysis-params h6')?.textContent
            === 'Analysis Type: Feature Registry Analysis'`), "PR confirmation content");
        await checkConfirmation();
        await evaluate(`window.scrollTo({top:0,behavior:'instant'})`);
        await evaluate(`document.querySelector('#analysisModal .btn-primary').click()`);
    };
    await run();
    if (fixture === "feature-slow" || fixture === "feature-late") {
        await until(() => evaluate(`!!document.querySelector('.feature-registry-results [role=status]')`), "PR collection started");
        await until(() => evaluate(`(() => {
            const loading=document.querySelector('.feature-registry-results [role=status]');
            const rect=loading?.getBoundingClientRect();
            return !!rect && window.scrollY>0 && rect.top>=0 && rect.bottom<=window.innerHeight
                && !document.querySelector('#analysisModal.show');
        })()`), "automatic scroll to the visible PR loading indicator", 1500);
        assert.equal(await evaluate(`window.featureScrollCalls`), 1, "Each PR run must scroll once after rendering");
        await clickTab("Git Commits");
        await new Promise(resolve => setTimeout(resolve, 2400));
        assert.equal(await evaluate(`document.querySelector('.feature-registry-results') === null`), true);
        assert.equal(await evaluate(`document.querySelector('.analysis-summary-table').innerHTML`), original.html);
        if (fixture === "feature-slow") assert.equal((await state()).calls, 0);
        else assert((await state()).calls > 0, "Late fixture must ignore cancellation to exercise publication guard");
        await clickTab("Feature Registry");
        await until(() => evaluate(`!!document.querySelector('#featureRegistryStart')`), "return after cancelled PR run");
        assert.equal(await evaluate(`document.querySelector('.feature-registry-results') === null`), true, "Late result must not be cached");
        return;
    }
    if (fixture === "feature-failed" || fixture === "feature-files-failed") {
        await until(() => evaluate(`document.querySelector('.feature-registry-results [role=alert]')?.textContent.includes('403')`), "atomic PR failure");
        assert.equal(await evaluate(`document.querySelectorAll('.feature-registry-results .contributor-row').length`), 0);
        assert.equal(await evaluate(`document.querySelector('.analysis-export-btn').disabled`), true);
        await run();
    }
    const empty = fixture === "feature-empty";
    await until(() => evaluate(`!!document.querySelector('.feature-registry-results')
        && document.querySelector('.analysis-export-btn')?.dataset.reportReady==='true'`), "complete PR snapshot");
    await until(async () => await evaluate(`window.featureScrollCalls`) === (await state()).runs,
        "one automatic scroll for each confirmed PR run");
    assert.equal(await evaluate(`(() => {
        const text=document.querySelector('.feature-registry-results').textContent;
        return ['msdata/A365/FeatureRegistry - all target branches.',
            'Open PRs are grouped by author;', 'Collected ', 'Export preserves this exact snapshot;']
            .some(phrase=>text.includes(phrase));
    })()`), false, "PR result footer text must not be displayed");
    assert.equal(await evaluate(`document.querySelectorAll('.feature-registry-results .analysis-summary-table').length`), 1);
    assert.equal(await evaluate(`document.querySelector('.table-title-header').textContent.includes('Feature Registry Pull Requests')`), true);
    assert.equal(await evaluate(`document.querySelectorAll('.feature-registry-results .details-row').length`), 0, "PR details start collapsed like Git");
    if (empty) {
        assert.equal(await evaluate(`!!document.querySelector('.feature-registry-results td.text-center.py-4.text-muted')`), true);
        assert.equal(await evaluate(`document.querySelectorAll('.feature-registry-results .contributor-row').length`), 0);
    } else {
        assert.deepEqual(await resultStyles(false), gitResultStyles, "PR summary must match Git spacing, columns, header, rows and expand controls in both themes");
        assert.deepEqual(await evaluate(`[...document.querySelectorAll('.feature-registry-results .contributor-row')].map(row=>
            [row.cells[0].textContent.trim(),row.cells[1].textContent.replace(/\\s+/g,' ').trim()])`),
            [["Alice Author", "Open PRs: 2; Completed PRs: 0."], ["Bob Closer", "Open PRs: 0; Completed PRs: 2."]]);
        const beforeExpansion = await state();
        await evaluate(`document.querySelector('.feature-registry-results .expand-btn').click()`);
        await until(() => evaluate(`document.querySelectorAll('.feature-registry-results .details-row').length===1`), "PR contributor expansion");
        assert.deepEqual(await resultStyles(true), gitExpandedResultStyles, "Expanded PR detail container must match Git in both themes");
        await evaluate(`document.querySelectorAll('.feature-registry-results .expand-btn')[1].click()`);
        await until(() => evaluate(`document.querySelectorAll('.feature-registry-results .details-row').length===2`), "independent PR contributor expansion");
        const dividers = await evaluate(`(() => {
            const previousTheme=document.documentElement.getAttribute('data-bs-theme');
            try {
                return ['light','dark'].map(theme=>{
                    document.documentElement.setAttribute('data-bs-theme',theme);
                    return [...document.querySelectorAll('.feature-registry-results .commit-details')].map(section=>{
                        const divider=section.querySelector('.feature-registry-pr-divider');
                        if(!divider)return null;
                        const style=getComputedStyle(divider);
                        const children=[...section.children], index=children.indexOf(divider);
                        return {width:style.borderTopWidth,style:style.borderTopStyle,opacity:style.opacity,
                            visible:divider.checkVisibility(),
                            followedByCompleted:divider.nextElementSibling?.textContent.startsWith('Completed PRs'),
                            open:children.slice(0,index).filter(node=>node.matches('.commit-entry')).length,
                            completed:children.slice(index+1).filter(node=>node.matches('.commit-entry')).length};
                    });
                });
            } finally {
                if(previousTheme===null)document.documentElement.removeAttribute('data-bs-theme');
                else document.documentElement.setAttribute('data-bs-theme',previousTheme);
            }
        })()`);
        for (const theme of dividers)
            assert.deepEqual(theme, [[2, 0], [0, 2]].map(([open, completed]) => ({
                width: "3px", style: "solid", opacity: "1", visible: true, followedByCompleted: true, open, completed
            })), "A bold divider must separate open and completed PR lists in both themes, including empty groups");
        assert.deepEqual(await evaluate(`[...document.querySelectorAll('.feature-registry-results [data-pr-key]')].map(node=>node.dataset.prKey.split(':').at(-1))`),
            ["1", "2", "4", "3"], "All captured PRs remain accessible in their original within-group order");
        assert.equal(await evaluate(`document.querySelectorAll('.feature-registry-results script').length`), 0);
        assert.equal(await evaluate(`[...document.querySelectorAll('.feature-registry-results .commit-entry a')].every(a=>
            a.hostname==='dev.azure.com' && a.rel.includes('noopener') && a.textContent.includes('<script>unsafe()</script>'))`), true);
        assert.equal(await evaluate(`document.querySelector('.feature-registry-results .commit-entry').textContent.includes('(draft)')`), true);
        assert.deepEqual(await state(), beforeExpansion, "PR expansion must use the snapshot without source calls");
        await evaluate(`[...document.querySelectorAll('.feature-registry-results .expand-btn')].forEach(button=>button.click())`);
        await until(() => evaluate(`document.querySelectorAll('.feature-registry-results .details-row').length===0`), "PR contributor collapse");
    }
    const before = await evaluate(`document.querySelector('.feature-registry-results').textContent`);
    assert(!before.includes("Changed Files"), "PR file lists must not change the online report format");
    const capturedRun = await evaluate(`document.querySelector('.analysis-export-btn').dataset.reportRun`);
    const collected = await state();
    await evaluate(`${runButton}.click()`);
    await until(() => evaluate(`!!document.querySelector('#analysisModal.show')`), "rerun confirmation");
    await evaluate(`document.querySelector('#analysisModal .btn-close').click()`);
    await until(() => evaluate(`!document.querySelector('#analysisModal.show')`), "cancel confirmation");
    assert.equal(await evaluate(`document.querySelector('.feature-registry-results').textContent`), before);
    await evaluate(`(() => {const date=document.querySelector('#featureRegistryStart');date.value='2027-01-01T00:00';date.dispatchEvent(new Event('change',{bubbles:true}));})()`);
    await until(() => evaluate(`${runButton}.disabled`), "invalid completed range");
    await clickView("Teams");
    await clickTab("Git Commits");
    await until(() => evaluate(`!document.querySelector('.feature-registry-results')
        && !!document.querySelector('#startDateTime') && !!document.querySelector('.analysis-summary-table')`), "ordinary Git cache restored");
    assert.deepEqual(await evaluate(`({
        html:document.querySelector('.analysis-summary-table').innerHTML,
        run:document.querySelector('.analysis-export-btn').dataset.reportRun,
        start:document.querySelector('#startDateTime').value,end:document.querySelector('#endDateTime').value
    })`), original, "PR form/execution must not alter the existing Git result, inputs or export run");
    await clickTab("Feature Registry");
    if (view !== "Teams") await clickView(view);
    await until(() => evaluate(`!!document.querySelector('.feature-registry-results')`), "PR snapshot cache restore");
    assert.equal(await evaluate(`document.querySelector('.feature-registry-results').textContent`), before);
    assert.equal(await evaluate(`document.querySelector('.analysis-export-btn').dataset.reportRun`), capturedRun);
    await evaluate(`window.showSaveFilePicker = options => {
        if(!navigator.userActivation.isActive) throw new Error('Missing trusted click');
        window.featureSuggestedName=options.suggestedName;
        return Promise.resolve({async createWritable() {let bytes;return {
            async write(value){bytes=value;},
            async close(){const url=URL.createObjectURL(new Blob([bytes],{type:'application/zip'}));const a=document.createElement('a');a.href=url;a.download='FeatureRegistry.zip';a.click();setTimeout(()=>URL.revokeObjectURL(url),1000);},
            async abort(){}
        };}});
    }`);
    let entries;
    for (let index = 0; index < 2; index++) {
        const completed = completedDownloads.length;
        await clickExport();
        await until(async () => {
            const error = await evaluate(`document.querySelector('[role=alert].report-export-status')?.textContent ?? ''`);
            assert(!error, error);
            return completedDownloads.length > completed;
        }, "PR ZIP download without recollection");
        const files = (await readdir(downloads)).filter(name => name.endsWith(".zip"));
        entries = unzip(await readFile(join(downloads, files[0])));
        assert.equal(entries.size, 2);
        const suggestedName = await evaluate(`featureSuggestedName`);
        const stem = `Feature_Registry_${view.toLowerCase()}_20260701_to_20260731`;
        assert.match(suggestedName, new RegExp(`^${stem}_[0-9a-f]{32}\\.zip$`),
            "Save As must retain the run-specific ZIP filename");
        assert.deepEqual([...entries.keys()].sort(), [stem + ".html", stem + "_Detailed.html"].sort());
        for (const [name, text] of entries) {
            assert(text.includes("Feature Registry") && text.includes("Open PRs") && text.includes("Completed PRs"));
            assert(text.includes("2026-07"));
            if (name.endsWith("_Detailed.html")) {
                for (const label of ["Captured inputs and export query context", "Collection started", "Collection finished", "Run Analysis snapshot"])
                    assert.equal(text.includes(label), view === "Teams",
                        "Only Teams detailed PR reports must include the final captured-input section: " + label);
            } else {
                assert(text.includes("Run Analysis snapshot") && text.includes("Collection started"),
                    "Separate summary HTML must retain its snapshot context");
                assert(!text.includes("file_12.cs") && !text.includes("Changed Files"),
                    "PR file lists belong only in the detailed report");
            }
            assert(!text.includes("2027-01") && !text.includes("<script>"));
            assert(empty ? !text.includes("PR 1") : text.includes("PR 1") && text.includes("PR 4") && !text.includes("PR 5"));
        }
        assert.deepEqual(await state(), collected, "Exports/tab restores must make no new source calls");
        assert.equal(await evaluate(`window.featureScrollCalls`), collected.runs, "Rerenders, exports and cache restores must not repeat the run-start scroll");
    }
    const extracted = join(profile, "feature-reports");
    await mkdir(extracted);
    for (const [name, text] of entries) await writeFile(join(extracted, name), text);
    if (view === "Teams") {
        const summaryName = [...entries.keys()].find(name => !name.endsWith("_Detailed.html"));
        const reference = await (await fetch(new URL("feature-summary-reference", url))).text();
        assert.equal(entries.get(summaryName).match(/<style>([\s\S]*?)<\/style>/)[1].replaceAll("\r\n", "\n").trim(),
            reference.match(/<style>([\s\S]*?)<\/style>/)[1].replaceAll("\r\n", "\n").trim(),
            "Teams Feature Registry must reuse the exact Git summary styles");
        await writeFile(join(extracted, "Git_reference.html"), reference);
        const panels = [];
        try {
            for (const name of [summaryName, "Git_reference.html"]) {
                const { targetId } = await cdp("Target.createTarget", { url: pathToFileURL(join(extracted, name)).href });
                panels.push({ targetId });
                const { sessionId } = await cdp("Target.attachToTarget", { targetId, flatten: true });
                panels.at(-1).sessionId = sessionId;
                await until(async () => (await cdp("Runtime.evaluate", {
                    expression: "document.readyState==='complete' && !!document.querySelector('table')", returnByValue: true
                }, sessionId)).result.value, "offline summary");
            }
            const inspect = async (index, expression) => {
                const result = await cdp("Runtime.evaluate", { expression, returnByValue: true }, panels[index].sessionId);
                assert(!result.exceptionDetails, JSON.stringify(result.exceptionDetails));
                return result.result.value;
            };
            const styles = `(() => {
                const selectors=['body','h1','body > .meta','body > h2','body > table','th','td',
                    'tbody tr:nth-child(odd)','tbody tr:nth-child(even)','.report-context','.report-context summary'];
                const properties=['color','backgroundColor','fontFamily','fontSize','fontWeight','padding',
                    'margin','borderBottom','borderCollapse','tableLayout','whiteSpace','overflowWrap'];
                return selectors.map(selector=>{
                    const style=getComputedStyle(document.querySelector(selector));
                    return Object.fromEntries(properties.map(key=>[key,style[key]]));
                });
            })()`;
            for (const width of [1280, 600]) {
                for (const panel of panels)
                    await cdp("Emulation.setDeviceMetricsOverride", { width, height:900, deviceScaleFactor:1, mobile:false }, panel.sessionId);
                assert.deepEqual(await inspect(0, styles), await inspect(1, styles),
                    "Summary typography, spacing, tables and context must match Git on desktop and narrow screens");
            }
            assert.deepEqual(await inspect(0, `({
                title:document.querySelector('h1').textContent,
                tables:document.querySelectorAll('body > table').length,
                contextOpen:document.querySelector('.report-context').open,
                hiddenEvidence:!document.querySelector('.report-context table')?.checkVisibility(),
                scripts:document.scripts.length,
                rows:[...document.querySelectorAll('body > table')].map(table=>[...table.querySelectorAll('tbody tr')]
                    .map(row=>[...row.cells].map(cell=>cell.textContent)))
            })`), {
                title:"Team Feature Registry Pull Request Analysis (2026-07-01 to 2026-07-31)",
                tables:2,contextOpen:false,hiddenEvidence:true,scripts:0,
                rows: empty ? [
                    [["Alice Author","—","0","0"],["Bob Closer","—","0","0"],["No activity","—","0","0"]],
                    [["Alice Author","0","0",""],["Bob Closer","0","0",""],["No activity","0","0",""]]
                ] : [
                    [["Bob Closer","msdata/A365/FeatureRegistry","0","2"],["Alice Author","msdata/A365/FeatureRegistry","2","0"],["No activity","—","0","0"]],
                    [["Bob Closer","0","2","FeatureRegistry"],["Alice Author","2","0","FeatureRegistry"],["No activity","0","0",""]]
                ]
            }, "Summary must be compact by default and retain correct PR attribution, ordering and every roster member");
            await inspect(0, `document.querySelector('.report-context > summary').click()`);
            assert.equal(await inspect(0, `document.querySelector('.report-context').open &&
                [...document.querySelectorAll('.report-context td a')].every(link=>link.checkVisibility())`), true,
                "All linked PR records must remain available in the offline expandable evidence");
            const detailLink = await inspect(0, `[...document.querySelectorAll('.report-context a')].find(link=>
                link.textContent==='Read the detailed HTML report')?.getAttribute('href')`);
            assert.equal(decodeURIComponent(detailLink), "./" + [...entries.keys()].find(name=>name.endsWith("_Detailed.html")));
            for (const panel of panels)
                await cdp("Emulation.setEmulatedMedia", { media:"print" }, panel.sessionId);
            assert.deepEqual(await inspect(0, styles), await inspect(1, styles),
                "Summary print presentation must also match the Git reference");
        } finally {
            for (const panel of panels) await cdp("Target.closeTarget", { targetId:panel.targetId });
        }
    }
    const detailedName = [...entries.keys()].find(name => name.endsWith("_Detailed.html"));
    const { targetId } = await cdp("Target.createTarget", { url: pathToFileURL(join(extracted, detailedName)).href });
    try {
        const { sessionId } = await cdp("Target.attachToTarget", { targetId, flatten: true });
        const inspect = async expression => (await cdp("Runtime.evaluate", { expression, returnByValue: true }, sessionId)).result.value;
        const checkPrDividers = async print => {
            assert.deepEqual(await inspect(`(() => {
                const people=[...document.querySelectorAll('.report-person-details')];
                const open=people.map(person=>person.open);
                try {
                    if(!${print})people.forEach(person=>person.open=true);
                    return [...document.querySelectorAll('.report-pr-divider')].map(divider=>{
                        const block=divider.parentElement;
                        const container=block.parentElement;
                        const siblings=[...container.children];
                        const style=getComputedStyle(divider);
                        return {
                            width:style.borderTopWidth,style:style.borderTopStyle,opacity:style.opacity,
                            color:style.borderTopColor===getComputedStyle(container).color,
                            visible:divider.checkVisibility(),outsidePr:!divider.closest('.report-item-details'),
                            afterOpen:siblings.slice(0,siblings.indexOf(block)).some(node=>node.tagName==='H4'
                                && node.textContent.startsWith('Open PRs (')),
                            beforeCompleted:block.nextElementSibling?.textContent.startsWith('Completed PRs (')
                        };
                    });
                } finally {people.forEach((person,index)=>person.open=open[index]);}
            })()`), view === "Teams" ? Array.from({length:3}, () => ({
                width:"3px",style:"solid",opacity:"1",color:true,visible:true,
                outsidePr:true,afterOpen:true,beforeCompleted:true
            })) : [], "Bold dividers must separate each person's open/completed lists on screen and in print, including empty lists");
        };
        await until(() => inspect(`document.readyState==='complete' && !!document.querySelector('main')`), "offline PR report");
        assert.equal(await inspect(`document.scripts.length`), 0);
        assert.equal(await inspect(`[...document.querySelectorAll('a[href^="#"]')].every(a=>!!document.getElementById(a.hash.slice(1)))`), true);
        await until(() => inspect(`document.fonts.status==='loaded'`), "embedded report font");
        assert.deepEqual(await inspect(`({
            scheme:getComputedStyle(document.documentElement).colorScheme,
            background:getComputedStyle(document.body).backgroundColor,
            font:document.fonts.check('14px Montserrat'),
            titleSize:getComputedStyle(document.querySelector('h1')).fontSize,
            banner:document.querySelector('.report-section-heading')!==null,
            tableRadius:getComputedStyle(document.querySelector('.report-table-scroll')).borderRadius,
            tableFont:getComputedStyle(document.querySelector('table')).fontSize,
            columns:[...document.querySelectorAll('table')].filter(table=>!table.closest('.report-context-details'))
                .map(table=>table.querySelectorAll('thead th').length),
            records:document.querySelectorAll('.report-record-heading').length,
            overallSummary:[...document.querySelectorAll('h2')].some(heading=>heading.textContent.includes('FEATURE REGISTRY PULL REQUEST SUMMARY (')),
            title:document.querySelector('h1').textContent
        })`), { scheme: "dark", background: "rgb(11, 18, 32)", font: true, titleSize: "28px",
            banner: true, tableRadius: "10px", tableFont: "12.5px", columns: [4], records: empty ? 0 : 4, overallSummary: false,
            title: `${view === "Teams" ? "Team " : ""}Feature Registry Pull Request Analysis (2026-07-01 to 2026-07-31)` },
        "PR exports must use the Git reference typography, section banners, compact tables and per-record details");
        assert.deepEqual(await inspect(`[...document.querySelectorAll('.report-record-heading')].map(heading=>heading.textContent)`),
            empty ? [] : [1, 2, 4, 3].map(id => view === "Teams"
                ? `🔄 Pull Request: PR ${id} <script>unsafe()</script> | [text]` : `🔄 Pull Request: #${id}`),
            "Teams PR headings must use full encoded titles; Details and Trends retain their numbered headings");
        if (view === "Teams") {
            assert.deepEqual(await inspect(`(() => {
                const context=document.querySelector('.report-context-details');
                const heading=context?.querySelector('summary > h2');
                const content=context?.querySelector('.report-context-content');
                return {count:document.querySelectorAll('.report-context-details').length,
                    last:document.querySelector('main').lastElementChild===context,closed:!context.open,
                    title:heading.textContent,hidden:!content.checkVisibility(),size:getComputedStyle(content).fontSize,
                    color:getComputedStyle(content).color,weight:getComputedStyle(content).fontWeight,
                    headingSize:getComputedStyle(heading).fontSize,headingWeight:getComputedStyle(heading).fontWeight,
                    capturedRun:content.textContent.replaceAll('-','').includes(${JSON.stringify(capturedRun)}),
                    notes:content.textContent.includes('Exact successful Run Analysis snapshot'),
                    tables:content.querySelectorAll('table').length};
            })()`), {count:1,last:true,closed:true,title:"Captured inputs and export query context",hidden:true,size:"12px",
                color:"rgb(180, 196, 217)",weight:"400",headingSize:"13px",headingWeight:"500",
                capturedRun:true,notes:true,tables:2},
            "Teams context must be the final, independently collapsed section with lighter/smaller styling and the executed snapshot");
            assert.equal(await inspect(`(() => {
                const context=document.querySelector('.report-context-details');
                const summary=context.querySelector('summary');
                summary.click();
                const visible=context.querySelector('.report-context-content').checkVisibility();
                const independent=[...document.querySelectorAll('.report-person-details,.report-item-details')].every(node=>!node.open);
                summary.click();
                return visible && independent && !context.open;
            })()`), true, "Opening final context must not expand any person or PR");
            assert.deepEqual(await inspect(`[...document.querySelectorAll('.report-pr-title-value')].map(node=>node.textContent)`),
                empty ? [] : [1, 2, 4, 3].map(id => `PR ${id} <script>unsafe()</script> | [text]`),
                "Teams titles must preserve the actual PR text in inline title fields");
            assert.equal(await inspect(`document.querySelectorAll('pre,code').length`), 0);
            assert.equal(await inspect(`document.querySelector('main').textContent.includes('(Full PR title)')`), false);
            assert.deepEqual(await inspect(`[...document.querySelectorAll('.report-item-details')].map(item=>({
                id:Number([...item.querySelectorAll('.report-label')].find(label=>label.textContent==='PR ID:').nextSibling.textContent.trim()),
                files:[...item.querySelectorAll('.report-file')].map(file=>file.textContent.trim()),
                comparison:item.textContent.includes('iteration 3') && item.textContent.includes('compareTo=0'),
                count:item.textContent.includes('Changed Files (14)')
            }))`), empty ? [] : [1, 2, 4, 3].map(id => ({
                id,
                files: [
                    ...Array.from({length: 12}, (_, index) => `📄 /pr-${id}/file_${String(index + 1).padStart(2, "0")}.cs [${index % 2 === 0 ? "add" : "edit"}]`),
                    `📄 /pr-${id}/old <file> & [x].cs → /pr-${id}/new <file> & [x].cs [rename, edit]`,
                    "📄 /shared.cs [delete]"
                ],
                comparison: true, count: true
            })), "Each open and completed PR must retain its full file list, original rename path and change types inside its fold");
            assert.equal(await inspect(`document.querySelectorAll('.report-roster-member').length`), 3);
            assert.deepEqual(await inspect(`(() => {
                const nodes=[...document.querySelectorAll('.report-roster-hierarchy li')];
                return nodes.map(node=>{const parent=node.parentElement.closest('li');return parent?nodes.indexOf(parent):null;});
            })()`), [null, 0, 0], "Roster hierarchy must match the captured tree, not display-name guesses");
            assert.equal(await inspect(`(() => {const a=document.querySelector('.report-member-link');a.click();
                const target=document.querySelector(location.hash);return target?.tagName==='H3' && target.checkVisibility();})()`), true);
            assert.deepEqual(await inspect(`({
                people:document.querySelectorAll('.report-person-details').length,
                items:document.querySelectorAll('.report-item-details').length,
                closed:[...document.querySelectorAll('details')].every(node=>!node.open),
                global:[...document.querySelectorAll('h2,table')].filter(node=>!node.closest('.report-context-details'))
                    .every(node=>!node.closest('details'))
            })`), { people: 3, items: empty ? 0 : 4, closed: true, global: true });
            const members = [
                ["Alice Author", empty ? 0 : 2, 0],
                ["Bob Closer", 0, empty ? 0 : 2],
                ["No activity", 0, 0]
            ];
            assert.deepEqual(await inspect(`[...document.querySelectorAll('.report-person-heading')].map(heading=>heading.textContent)`),
                members.map(([name, open, completed]) => `👤 ${name} - ${open} open PRs, ${completed} completed PRs`),
                "Active and zero-PR members must retain names and counts without the Contributor: prefix");
            assert.deepEqual(await inspect(`[...document.querySelectorAll('table tbody tr')].filter(row=>!row.closest('.report-context-details')).slice(0,-1).map(row=>
                [...row.cells].map(cell=>cell.textContent))`),
                members.map(([name, open, completed]) => [name, "msdata/A365/FeatureRegistry", String(open), String(completed)]),
                "Repository summary must include every captured team member, including zeros");
            assert.deepEqual(await inspect(`(() => {
                const summary=[...document.querySelectorAll('h2')].find(heading=>heading.textContent.includes('SUMMARY BY REPOSITORY'));
                return [...document.querySelectorAll('.report-roster-member .report-member-link')].map(link=>{
                    link.click();
                    const heading=document.querySelector(location.hash);
                    return {text:heading.textContent,visible:heading.checkVisibility(),
                        inMainList:!!(heading.compareDocumentPosition(summary)&Node.DOCUMENT_POSITION_FOLLOWING)};
                });
            })()`), members.map(([name, open, completed]) => ({
                text: `👤 ${name} - ${open} open PRs, ${completed} completed PRs`,
                visible: true, inMainList: true
            })), "Every hierarchy link must land on its own visible person heading before the summary");
            if (!empty) {
                assert.deepEqual(await inspect(`(() => {
                    const people=[...document.querySelectorAll('.report-person-details')];
                    const person=people[0];person.querySelector('summary').click();
                    const items=[...person.querySelectorAll('.report-item-details')];
                    const item=items[0];item.querySelector('summary').click();
                    const visible=item.querySelector('.report-item-content').checkVisibility();
                    const title=item.querySelector('.report-pr-title-value');
                    const label=item.querySelector('.report-pr-title .report-label').getBoundingClientRect();
                    const range=document.createRange();
                    range.setStart(title.firstChild,0);range.setEnd(title.firstChild,2);
                    const text=range.getBoundingClientRect();
                    const inlineTitle=getComputedStyle(title).display==='inline' && Math.abs(text.top-label.top)<2 && text.left>=label.right;
                    person.querySelector('summary').click();person.querySelector('summary').click();
                    return {visible,inlineTitle,preserved:item.open,independent:people.slice(1).every(node=>!node.open)
                        && items.slice(1).every(node=>!node.open)};
                })()`), { visible: true, inlineTitle: true, preserved: true, independent: true });
                await inspect(`document.querySelector('.report-item-details > summary').click();
                    document.querySelector('.report-item-details > summary').focus()`);
                await cdp("Page.bringToFront", {}, sessionId);
                for (const [key, code, virtualKey, open] of [["Enter", "Enter", 13, true], [" ", "Space", 32, false]]) {
                    await cdp("Input.dispatchKeyEvent", { type: "keyDown", key, code,
                        text: key === "Enter" ? "\r" : " ", windowsVirtualKeyCode: virtualKey }, sessionId);
                    await cdp("Input.dispatchKeyEvent", { type: "keyUp", key, code, windowsVirtualKeyCode: virtualKey }, sessionId);
                    assert.equal(await inspect(`document.querySelector('.report-item-details').open`), open,
                        "Native PR disclosures must support keyboard activation offline");
                }
            }
            await inspect(`[...document.querySelectorAll('details[open]')].reverse().forEach(node=>node.querySelector('summary').click())`);
        } else {
            assert.equal(await inspect(`document.querySelectorAll('details').length`), 0);
            assert.equal(await inspect(`document.querySelectorAll('.report-file').length`), 0, "Details/Trends exports must not gain Teams file lists");
        }
        await checkPrDividers(false);
        await cdp("Emulation.setEmulatedMedia", { media: "print" }, sessionId);
        await checkPrDividers(true);
        assert.deepEqual(await inspect(`({
            scheme:getComputedStyle(document.documentElement).colorScheme,
            background:getComputedStyle(document.body).backgroundColor,
            shadow:getComputedStyle(document.querySelector('main')).boxShadow,
            overflow:getComputedStyle(document.querySelector('.report-table-scroll')).overflowX,
            complete:[...document.querySelectorAll('details')].every(node=>!node.open
                && node.querySelector(':scope > div').checkVisibility()
                && [...node.querySelector(':scope > div').children].every(child=>child.checkVisibility()))
        })`), { scheme: "light", background: "rgb(255, 255, 255)", shadow: "none", overflow: "visible", complete: true },
        "Print must match the Git reference and include all PR details even when collapsed on screen");
        assert.equal(await inspect(`[...document.querySelectorAll('.report-file')].every(file=>file.checkVisibility())`), true,
            "Printing must include every file while PR and person sections remain collapsed");
    } finally {
        await cdp("Target.closeTarget", { targetId });
    }
    if (view === "Teams") {
        await evaluate(`document.querySelector('#featureRegistryStart').value='2026-07-01T00:00';document.querySelector('#featureRegistryStart').dispatchEvent(new Event('change',{bubbles:true}))`);
        await until(() => evaluate(`!${runButton}.disabled`), "valid rerun dates");
        await run();
        await until(() => evaluate(`!!document.querySelector('.feature-registry-results')
            && document.querySelector('.analysis-export-btn')?.dataset.reportReady==='true'
            && document.querySelector('.analysis-export-btn').dataset.reportRun !== '${capturedRun}'`), "fresh PR run");
        assert.equal((await state()).runs, collected.runs + 1);
        if (fixture === "feature-teams") {
            for (const label of ["Work Items", "Git Commits", "Incident Resolutions", "Feature Registry"]) {
                await clickTab(label);
                await until(() => evaluate(`${rowButtons}.find(button=>button.classList.contains('active')).textContent.trim()
                    ===${JSON.stringify(label)}`), "ready Teams selection");
                assert.equal(await evaluate(`document.querySelectorAll('.teams-manager-warning').length`), 0,
                    "Completed roster must suppress the warning for " + label);
            }
        }
        await evaluate(`[...document.querySelectorAll('.manager-selection button')].find(button=>button.textContent.includes('Clear')).click()`);
        await until(() => evaluate(`${runButton}.disabled`), "Fetch Team prerequisite");
        if (fixture === "feature-teams") {
            for (const label of ["Work Items", "Git Commits", "Incident Resolutions", "Feature Registry"]) {
                await clickTab(label);
                await until(() => evaluate(`document.querySelector('.teams-manager-warning')?.textContent
                    ===${JSON.stringify(`Select a manager and finish Fetch Team before running ${label}.`)}`), "Teams prerequisite warning for " + label);
                assert.equal(await evaluate(`document.querySelectorAll('.tab-pane.active .alert-warning[role=alert]').length`), 1,
                    "Each Teams selection must show one manager warning without duplicating Feature Registry validation");
                for (const otherView of ["Details", "Trends"]) {
                    await clickView(otherView);
                    await until(() => evaluate(`!document.querySelector('.manager-selection')`), "non-Teams view");
                    assert.equal(await evaluate(`document.querySelectorAll('.teams-manager-warning').length`), 0);
                }
                await clickView("Teams");
                await until(() => evaluate(`!!document.querySelector('.teams-manager-warning')`), "return to Teams prerequisites");
            }
        }
    }
}
