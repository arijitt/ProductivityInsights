window.toggleTheme = function() {
    console.log('toggleTheme called');
    const htmlElement = document.documentElement;
    const currentTheme = htmlElement.getAttribute('data-bs-theme');
    console.log('Current theme:', currentTheme);
    const isDark = currentTheme === 'dark';
    const newTheme = isDark ? 'light' : 'dark';
    htmlElement.setAttribute('data-bs-theme', newTheme);
    localStorage.setItem('theme', newTheme);
    console.log('New theme set to:', newTheme);
    
    // Force a style recalculation
    document.body.style.display = 'none';
    document.body.offsetHeight; // Trigger reflow
    document.body.style.display = '';
    
    return newTheme === 'dark';
};

window.isDarkMode = function() {
    const htmlElement = document.documentElement;
    const savedTheme = localStorage.getItem('theme');
    if (savedTheme) {
        htmlElement.setAttribute('data-bs-theme', savedTheme);
        return savedTheme === 'dark';
    } else {
        const prefersDark = window.matchMedia('(prefers-color-scheme: dark)').matches;
        const theme = prefersDark ? 'dark' : 'light';
        htmlElement.setAttribute('data-bs-theme', theme);
        return prefersDark;
    }
};

window.initializeTheme = function() {
    const savedTheme = localStorage.getItem('theme');
    if (savedTheme) {
        document.documentElement.setAttribute('data-bs-theme', savedTheme);
    } else if (window.matchMedia('(prefers-color-scheme: dark)').matches) {
        document.documentElement.setAttribute('data-bs-theme', 'dark');
    }
};

window.showModal = function(modalId) {
    const modalElement = document.getElementById(modalId);
    if (modalElement) {
        modalElement.classList.add('show');
        modalElement.style.display = 'block';
        modalElement.setAttribute('aria-modal', 'true');
        modalElement.removeAttribute('aria-hidden');
        
        // Add backdrop
        const backdrop = document.createElement('div');
        backdrop.className = 'modal-backdrop fade show';
        backdrop.id = modalId + '-backdrop';
        document.body.appendChild(backdrop);
        document.body.classList.add('modal-open');
        
        // Handle backdrop click to close
        backdrop.addEventListener('click', function() {
            window.hideModal(modalId);
        });
        
        // Initialize draggable functionality
        window.makeDraggable(modalId);
    }
};

window.makeDraggable = function(modalId) {
    const modalElement = document.getElementById(modalId);
    if (!modalElement) return;
    
    const modalDialog = modalElement.querySelector('.modal-dialog');
    const modalHeader = modalElement.querySelector('.modal-header');
    
    if (!modalDialog || !modalHeader) return;
    
    // Skip if already initialized
    if (modalHeader.dataset.draggable === 'true') return;
    modalHeader.dataset.draggable = 'true';
    
    let isDragging = false;
    let startX = 0;
    let startY = 0;
    let translateX = 0;
    let translateY = 0;
    
    function getTranslateValues(element) {
        const style = window.getComputedStyle(element);
        const matrix = style.transform || style.webkitTransform || style.mozTransform;
        
        if (matrix === 'none' || !matrix) {
            return { x: 0, y: 0 };
        }
        
        const matrixValues = matrix.match(/matrix.*\((.+)\)/)[1].split(', ');
        return {
            x: parseFloat(matrixValues[4]) || 0,
            y: parseFloat(matrixValues[5]) || 0
        };
    }
    
    function onMouseDown(e) {
        // Don't drag if clicking on close button or buttons
        if (e.target.closest('.btn-close') || e.target.closest('button')) return;
        
        isDragging = true;
        
        const currentTranslate = getTranslateValues(modalDialog);
        translateX = currentTranslate.x;
        translateY = currentTranslate.y;
        
        startX = e.clientX - translateX;
        startY = e.clientY - translateY;
        
        modalDialog.style.transition = 'none';
        modalHeader.style.cursor = 'grabbing';
        
        e.preventDefault();
    }
    
    function onMouseMove(e) {
        if (!isDragging) return;
        
        e.preventDefault();
        
        translateX = e.clientX - startX;
        translateY = e.clientY - startY;
        
        modalDialog.style.transform = `translate(${translateX}px, ${translateY}px)`;
    }
    
    function onMouseUp(e) {
        if (!isDragging) return;
        
        isDragging = false;
        modalDialog.style.transition = '';
        modalHeader.style.cursor = 'move';
    }
    
    modalHeader.addEventListener('mousedown', onMouseDown);
    document.addEventListener('mousemove', onMouseMove);
    document.addEventListener('mouseup', onMouseUp);
    
    // Store cleanup function
    modalElement._cleanupDraggable = function() {
        modalHeader.removeEventListener('mousedown', onMouseDown);
        document.removeEventListener('mousemove', onMouseMove);
        document.removeEventListener('mouseup', onMouseUp);
        delete modalHeader.dataset.draggable;
    };
};

window.hideModal = function(modalId) {
    const modalElement = document.getElementById(modalId);
    if (modalElement) {
        modalElement.classList.remove('show');
        modalElement.style.display = 'none';
        modalElement.setAttribute('aria-hidden', 'true');
        modalElement.removeAttribute('aria-modal');
        
        // Reset modal position
        const modalDialog = modalElement.querySelector('.modal-dialog');
        if (modalDialog) {
            modalDialog.style.transform = '';
            modalDialog.style.transition = '';
        }
        
        // Cleanup draggable listeners
        if (modalElement._cleanupDraggable) {
            modalElement._cleanupDraggable();
            delete modalElement._cleanupDraggable;
        }
        
        // Remove backdrop
        const backdrop = document.getElementById(modalId + '-backdrop');
        if (backdrop) {
            backdrop.remove();
        }
        document.body.classList.remove('modal-open');
    }
};

window.scrollToBottom = function(smooth = true) {
    window.scrollTo({
        top: document.body.scrollHeight,
        behavior: smooth ? 'smooth' : 'auto'
    });
};

window._loadScript = function (src) {
    return new Promise(function (resolve, reject) {
        var script = document.createElement('script');
        script.src = src;
        script.onload = resolve;
        script.onerror = function () { reject(new Error('Failed to load ' + src)); };
        document.head.appendChild(script);
    });
};

window.downloadResultsAsPdf = async function (elementId, fileName) {
    const element = document.getElementById(elementId);
    if (!element) {
        alert('PDF Error: Results container not found.');
        return 'Element not found';
    }

    // Dynamically load libraries if they failed to load from script tags
    if (typeof html2canvas === 'undefined') {
        try {
            await window._loadScript('https://cdnjs.cloudflare.com/ajax/libs/html2canvas/1.4.1/html2canvas.min.js');
        } catch (e) {
            alert('PDF Error: html2canvas library failed to load. Check your network connection and try again.');
            return 'html2canvas not loaded';
        }
    }

    if (!window.jspdf) {
        try {
            await window._loadScript('https://cdnjs.cloudflare.com/ajax/libs/jspdf/2.5.1/jspdf.umd.min.js');
        } catch (e) {
            alert('PDF Error: jsPDF library failed to load. Check your network connection and try again.');
            return 'jsPDF not loaded';
        }
    }

    try {
        // Temporarily remove scroll constraints for full capture
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

        // Restore original styles
        element.style.maxHeight = originalMaxHeight;
        element.style.overflow = originalOverflow;

        if (!canvas || canvas.width === 0 || canvas.height === 0) {
            alert('PDF Error: Failed to capture page content.');
            return 'Canvas capture failed';
        }

        const imgData = canvas.toDataURL('image/png');
        const { jsPDF } = window.jspdf;

        // Calculate PDF dimensions maintaining aspect ratio
        const imgWidth = canvas.width;
        const imgHeight = canvas.height;
        const margin = 10;

        // Use landscape for wide content, portrait for tall content
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

        // Paginate if content exceeds one page
        while (yOffset < scaledHeight) {
            if (yOffset > 0) {
                pdf.addPage();
            }
            pdf.addImage(imgData, 'PNG', margin, margin - yOffset, contentWidth, scaledHeight);
            yOffset += pageHeight;
        }

        // Show Save As dialog using File System Access API, fallback to auto-download
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
                // User cancelled the dialog
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
};
