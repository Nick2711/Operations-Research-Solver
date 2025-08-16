// Enhanced Linear Programming Solver UI Functionality (frontend only)
window.initializeApp = () => {
    // Initialize tooltips
    const tooltipTriggerList = [].slice.call(document.querySelectorAll('[data-bs-toggle="tooltip"]'));
    tooltipTriggerList.map(function (tooltipTriggerEl) {
        return new bootstrap.Tooltip(tooltipTriggerEl);
    });

    // Tab Switching
    document.querySelectorAll('.tab-btn').forEach(btn => {
        btn.addEventListener('click', (e) => {
            e.preventDefault();
            e.stopPropagation();

            // Remove active classes from all tabs
            document.querySelectorAll('.tab-btn').forEach(b => {
                b.classList.remove('active', 'border-blue-600', 'text-blue-600');
            });

            // Add active class to clicked tab
            btn.classList.add('active', 'border-blue-600', 'text-blue-600');

            // Hide all tab contents
            document.querySelectorAll('.tab-content').forEach(c => {
                c.classList.remove('active');
                c.style.display = 'none';
            });

            // Show target content
            const target = btn.dataset.tab;
            const targetContent = document.getElementById(target);
            if (targetContent) {
                targetContent.style.display = 'block';
                setTimeout(() => {
                    targetContent.classList.add('active');
                }, 10);
            }
        });
    });

    // File Upload & Preview with Drag and Drop
    const fileInput = document.getElementById('modelFile');
    if (fileInput) {
        const fileInfo = document.getElementById('fileInfo');
        const fileName = document.getElementById('fileName');
        const filePreviewSection = document.getElementById('filePreviewSection');
        const filePreview = document.getElementById('filePreview');
        const modelSummary = document.getElementById('modelSummary');
        const dropZone = document.querySelector('.file-input-label');

        // Handle drag and drop
        ['dragenter', 'dragover', 'dragleave', 'drop'].forEach(eventName => {
            dropZone.addEventListener(eventName, preventDefaults, false);
        });

        function preventDefaults(e) {
            e.preventDefault();
            e.stopPropagation();
        }

        ['dragenter', 'dragover'].forEach(eventName => {
            dropZone.addEventListener(eventName, highlight, false);
        });

        ['dragleave', 'drop'].forEach(eventName => {
            dropZone.addEventListener(eventName, unhighlight, false);
        });

        function highlight() {
            dropZone.classList.add('border-blue-500', 'bg-blue-50');
        }

        function unhighlight() {
            dropZone.classList.remove('border-blue-500', 'bg-blue-50');
        }

        dropZone.addEventListener('drop', handleDrop, false);

        function handleDrop(e) {
            const dt = e.dataTransfer;
            const files = dt.files;
            if (files.length) {
                fileInput.files = files;
                handleFiles(files);
            }
        }

        fileInput.addEventListener('change', () => {
            if (fileInput.files.length) {
                handleFiles(fileInput.files);
            }
        });

        function handleFiles(files) {
            const file = files[0];
            if (!file) return;

            fileName.textContent = file.name;
            fileInfo.classList.remove('hidden');

            const reader = new FileReader();
            reader.onload = ev => {
                const content = ev.target.result;
                filePreview.textContent = content;
                filePreviewSection.classList.remove('hidden');
                updateModelSummary(content);

                // Notify Blazor (static C# callback)
                // IMPORTANT: the first argument MUST match your Client assembly name
                if (window.DotNet && typeof DotNet.invokeMethodAsync === 'function') {
                    DotNet.invokeMethodAsync('Operation_Research_Solver.Client', 'OnModelLoaded', file.name, content);
                } else {
                    console.warn('Blazor DotNet interop not available yet.');
                }

                // Auto-switch to Solve tab if on Upload tab
                const currentTab = document.querySelector('.tab-btn.active');
                if (currentTab && currentTab.dataset.tab === 'upload') {
                    const solveTabBtn = document.querySelector('[data-tab="solve"]');
                    if (solveTabBtn) solveTabBtn.click();
                }
            };
            reader.readAsText(file);
        }

        function updateModelSummary(content) {
            const lines = content.split('\n').filter(l => l.trim().length > 0);
            if (lines.length < 2) {
                modelSummary.innerHTML =
                    '<p class="text-red-500">Invalid file format. Please check the example.</p>';
                return;
            }

            const parts = lines[0].trim().split(/\s+/);
            const type = parts[0] === 'max' ? 'Maximization' : 'Minimization';
            const vars = parts.length - 1;
            const cons = lines.length - 2;
            const restrictions = lines[lines.length - 1].trim().split(/\s+/);

            // Create restriction badges
            const restrictionBadges = restrictions.map(r => {
                let color = 'bg-gray-200 text-gray-800';
                if (r === 'bin') color = 'bg-purple-200 text-purple-800';
                if (r === 'int') color = 'bg-blue-200 text-blue-800';
                if (r === 'urs') color = 'bg-green-200 text-green-800';
                if (r === '+') color = 'bg-green-100 text-green-800';
                if (r === '-') color = 'bg-red-100 text-red-800';
                return `<span class="inline-block px-2 py-1 text-xs font-semibold rounded-full ${color} mr-1 mb-1">${r}</span>`;
            }).join('');

            modelSummary.innerHTML = `
                <div class="grid grid-cols-1 md:grid-cols-3 gap-4">
                    <div class="bg-white p-3 rounded-lg shadow">
                        <h4 class="font-bold text-gray-500 text-sm uppercase mb-1">Problem Type</h4>
                        <p class="text-lg">${type}</p>
                    </div>
                    <div class="bg-white p-3 rounded-lg shadow">
                        <h4 class="font-bold text-gray-500 text-sm uppercase mb-1">Variables</h4>
                        <p class="text-lg">${vars}</p>
                    </div>
                    <div class="bg-white p-3 rounded-lg shadow">
                        <h4 class="font-bold text-gray-500 text-sm uppercase mb-1">Constraints</h4>
                        <p class="text-lg">${cons}</p>
                    </div>
                </div>
                <div class="mt-4 bg-white p-3 rounded-lg shadow">
                    <h4 class="font-bold text-gray-500 text-sm uppercase mb-2">Variable Restrictions</h4>
                    <div class="flex flex-wrap">${restrictionBadges}</div>
                </div>
            `;
        }
    }

    // ---------- Frontend-only utilities ----------
    window.showAlert = function (message, type = 'info') {
        const alertDiv = document.createElement('div');
        const colors = {
            info: 'bg-blue-100 text-blue-800',
            success: 'bg-green-100 text-green-800',
            warning: 'bg-yellow-100 text-yellow-800',
            error: 'bg-red-100 text-red-800'
        };

        alertDiv.className = `fixed top-4 right-4 p-4 rounded-lg shadow-lg ${colors[type]} max-w-md z-50 transition-all duration-300 transform translate-x-0 opacity-100`;
        alertDiv.innerHTML = `
            <div class="flex items-start">
                <div class="flex-shrink-0">
                    ${type === 'info' ? '<i class="fas fa-info-circle"></i>' : ''}
                    ${type === 'success' ? '<i class="fas fa-check-circle"></i>' : ''}
                    ${type === 'warning' ? '<i class="fas fa-exclamation-triangle"></i>' : ''}
                    ${type === 'error' ? '<i class="fas fa-times-circle"></i>' : ''}
                </div>
                <div class="ml-3">
                    <p class="text-sm font-medium">${message}</p>
                </div>
                <button class="ml-auto -mx-1.5 -my-1.5 p-1.5 rounded-lg inline-flex items-center justify-center h-8 w-8 bg-opacity-20 hover:bg-opacity-30 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-current">
                    <span class="sr-only">Dismiss</span>
                    <i class="fas fa-times"></i>
                </button>
            </div>
        `;

        document.body.appendChild(alertDiv);

        setTimeout(() => {
            alertDiv.classList.add('translate-x-full', 'opacity-0');
            setTimeout(() => document.body.removeChild(alertDiv), 300);
        }, 5000);

        alertDiv.querySelector('button').addEventListener('click', () => {
            alertDiv.classList.add('translate-x-full', 'opacity-0');
            setTimeout(() => document.body.removeChild(alertDiv), 300);
        });
    };

    // Initialize first tab as active
    const firstTabBtn = document.querySelector('.tab-btn');
    if (firstTabBtn) firstTabBtn.click();
};
