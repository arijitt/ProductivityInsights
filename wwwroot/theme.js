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
