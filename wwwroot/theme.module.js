// ES module wrapper for Blazor JS isolation (IJSObjectReference)
// Loads html2canvas and jsPDF dynamically if not already available.

function loadScript(src) {
    return new Promise(function (resolve, reject) {
        var script = document.createElement('script');
        script.src = src;
        script.onload = resolve;
        script.onerror = function () { reject(new Error('Failed to load ' + src)); };
        document.head.appendChild(script);
    });
}

async function ensureDependencies() {
    if (typeof html2canvas === 'undefined') {
        await loadScript('https://cdnjs.cloudflare.com/ajax/libs/html2canvas/1.4.1/html2canvas.min.js');
    }
    if (!window.jspdf) {
        await loadScript('https://cdnjs.cloudflare.com/ajax/libs/jspdf/2.5.1/jspdf.umd.min.js');
    }
}

export async function downloadResultsAsPdf(elementId, fileName) {
    // If theme.js already defined the function on window, delegate to it
    if (typeof window.downloadResultsAsPdf === 'function') {
        return await window.downloadResultsAsPdf(elementId, fileName);
    }

    // Otherwise, load dependencies and run inline
    const element = document.getElementById(elementId);
    if (!element) {
        alert('PDF Error: Results container not found.');
        return 'Element not found';
    }

    await ensureDependencies();

    if (typeof html2canvas === 'undefined') {
        alert('PDF Error: html2canvas library failed to load.');
        return 'html2canvas not loaded';
    }
    if (!window.jspdf) {
        alert('PDF Error: jsPDF library failed to load.');
        return 'jsPDF not loaded';
    }

    try {
        const originalMaxHeight = element.style.maxHeight;
        const originalOverflow = element.style.overflow;
        element.style.maxHeight = 'none';
        element.style.overflow = 'visible';

        const canvas = await html2canvas(element, {
            scale: 2,
            useCORS: true,
            logging: false,
            allowTaint: true,
            backgroundColor: getComputedStyle(document.documentElement)
                .getPropertyValue('--bs-body-bg')?.trim() || '#ffffff'
        });

        element.style.maxHeight = originalMaxHeight;
        element.style.overflow = originalOverflow;

        if (!canvas || canvas.width === 0 || canvas.height === 0) {
            alert('PDF Error: Failed to capture page content.');
            return 'Canvas capture failed';
        }

        const imgData = canvas.toDataURL('image/png');
        const { jsPDF } = window.jspdf;

        const imgWidth = canvas.width;
        const imgHeight = canvas.height;
        const margin = 10;
        const isLandscape = imgWidth > imgHeight;
        const pdf = new jsPDF({
            orientation: isLandscape ? 'landscape' : 'portrait',
            unit: 'mm',
            format: 'a4'
        });

        const pdfPageWidth = pdf.internal.pageSize.getWidth();
        const pdfPageHeight = pdf.internal.pageSize.getHeight();
        const contentWidth = pdfPageWidth - 2 * margin;
        const scaleFactor = contentWidth / imgWidth;
        const scaledHeight = imgHeight * scaleFactor;
        const pageHeight = pdfPageHeight - 2 * margin;

        let yOffset = 0;
        while (yOffset < scaledHeight) {
            if (yOffset > 0) {
                pdf.addPage();
            }
            pdf.addImage(imgData, 'PNG', margin, margin - yOffset, contentWidth, scaledHeight);
            yOffset += pageHeight;
        }

        const pdfBlob = pdf.output('blob');
        if (window.showSaveFilePicker) {
            try {
                const handle = await window.showSaveFilePicker({
                    suggestedName: fileName,
                    types: [{
                        description: 'PDF Document',
                        accept: { 'application/pdf': ['.pdf'] }
                    }]
                });
                const writable = await handle.createWritable();
                await writable.write(pdfBlob);
                await writable.close();
            } catch (pickerError) {
                if (pickerError.name !== 'AbortError') {
                    pdf.save(fileName);
                }
            }
        } else {
            pdf.save(fileName);
        }
        return '';
    } catch (error) {
        console.error('PDF generation failed:', error);
        alert('PDF generation failed: ' + error.message);
        return error.message;
    }
}
