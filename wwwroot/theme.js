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
