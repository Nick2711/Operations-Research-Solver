// Enhanced Linear Programming Solver UI Functionality
window.initializeApp = () => {
    // Initialize tooltips
    const tooltipTriggerList = [].slice.call(document.querySelectorAll('[data-bs-toggle="tooltip"]'));
    tooltipTriggerList.map(function (tooltipTriggerEl) {
        return new bootstrap.Tooltip(tooltipTriggerEl);
    });

    // Tab Switching
    document.querySelectorAll('.tab-btn').forEach(btn => {
        btn.addEventListener('click', (e) => {
            e.preventDefault(); // Stops default anchor behavior
            e.stopPropagation(); // Prevents event bubbling

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
                filePreview.textContent = ev.target.result;
                filePreviewSection.classList.remove('hidden');
                updateModelSummary(ev.target.result);

                // Auto-switch to Solve tab if on Upload tab
                const currentTab = document.querySelector('.tab-btn.active');
                if (currentTab && currentTab.dataset.tab === 'upload') {
                    document.querySelector('[data-tab="solve"]').click();
                }
            };
            reader.readAsText(file);
        }

        function updateModelSummary(content) {
            const lines = content.split('\n');
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

    // Solver Logic
    const runBtn = document.getElementById('runSolverBtn');
    const outDiv = document.getElementById('solverOutput');
    const dlBtn = document.getElementById('downloadResultsBtn');
    const resetBtn = document.getElementById('resetSolverBtn');

    if (runBtn) {
        runBtn.addEventListener('click', () => {
            if (!fileInput?.files?.length) {
                showAlert('Please upload a model file first.', 'error');
                return;
            }

            const algorithm = document.getElementById('algorithm').value;
            runBtn.disabled = true;
            runBtn.innerHTML = '<i class="fas fa-spinner fa-spin mr-2"></i> Solving...';

            // Simulate API call with timeout
            setTimeout(() => {
                // This would be replaced with actual API call
                const solution = generateMockSolution(algorithm);
                outDiv.innerHTML = formatSolutionOutput(solution, algorithm);

                runBtn.disabled = false;
                runBtn.innerHTML = '<i class="fas fa-play-circle mr-2"></i> Run Solver';
                if (dlBtn) dlBtn.disabled = false;

                updateCurrentSolution(solution);
                showAlert('Model solved successfully!', 'success');
            }, 1500);
        });
    }

    function generateMockSolution(algorithm) {
        // Generate a mock solution for demonstration
        const solutions = {
            primal: {
                status: 'Optimal',
                objective: 42.5,
                variables: { x1: 3.5, x2: 2.0, x3: 0, x4: 1.0 },
                iterations: 4,
                tableau: [
                    "Initial Tableau:\nBasis | x1  x2  x3  x4  s1  s2  RHS\n------------------------------\n  s1  |  2   3   1   0   1   0   40\n  s2  |  1   1   0   1   0   1   20\n  z   | -2  -3  -3  -5   0   0    0",
                    "Iteration 1:\n...",
                    "Iteration 2:\n...",
                    "Final Tableau:\nBasis | x1   x2   x3   x4   s1   s2   RHS\n--------------------------------\n  x1  |  1    0  0.5 -0.5  0.5 -1.5   5\n  x2  |  0    1 -0.5  1.5 -0.5  2.5  10\n  z   |  0    0 -1.5 -0.5  0.5  1.5  42.5"
                ]
            },
            branch_bound_knapsack: {
                status: 'Optimal',
                objective: 42,
                variables: { x1: 3, x2: 2, x3: 0, x4: 1 },
                nodes: 7,
                iterations: [
                    "Node 0: LP Relaxation\nObj: 42.5\nx1=3.5, x2=2.0, x3=0, x4=1.0",
                    "Branching on x1 <= 3\nNode 1: Obj=41.0, x1=3, x2=2.33...",
                    "Branching on x2 <= 2\nNode 2: Obj=40.0, x1=3, x2=2, x4=1",
                    "Integer solution found at Node 2"
                ]
            }
        };

        return solutions[algorithm] || solutions.primal;
    }

    function formatSolutionOutput(solution, algorithm) {
        let output = `=== ${algorithm.toUpperCase().replace(/_/g, ' ')} SOLUTION ===\n\n`;
        output += `Status: ${solution.status}\n`;
        output += `Objective Value: ${solution.objective}\n\n`;
        output += `Decision Variables:\n`;

        for (const [varName, value] of Object.entries(solution.variables)) {
            output += `${varName} = ${value}\n`;
        }

        output += `\n=== SOLUTION DETAILS ===\n\n`;

        if (algorithm.includes('branch_bound')) {
            output += `Nodes explored: ${solution.nodes}\n\n`;
            output += `Branch and Bound Process:\n`;
            solution.iterations.forEach((step, i) => {
                output += `\nStep ${i + 1}:\n${step}\n`;
            });
        } else {
            output += `Iterations: ${solution.iterations}\n\n`;
            output += `Tableau Iterations:\n`;
            solution.tableau.forEach((tableau, i) => {
                output += `\n${tableau}\n`;
            });
        }

        return output;
    }

    function updateCurrentSolution(solution) {
        const currentSolutionDiv = document.getElementById('currentSolution');
        let variablesHtml = '';

        for (const [varName, value] of Object.entries(solution.variables)) {
            variablesHtml += `<p><span class="font-medium">${varName}:</span> ${value}</p>`;
        }

        currentSolutionDiv.innerHTML = `
            <div class="grid grid-cols-1 md:grid-cols-2 gap-4 mb-4">
                <div class="bg-white p-3 rounded-lg shadow">
                    <h4 class="font-bold text-gray-500 text-sm uppercase mb-1">Status</h4>
                    <p class="text-lg ${solution.status === 'Optimal' ? 'text-green-600' : 'text-yellow-600'}">${solution.status}</p>
                </div>
                <div class="bg-white p-3 rounded-lg shadow">
                    <h4 class="font-bold text-gray-500 text-sm uppercase mb-1">Objective</h4>
                    <p class="text-lg font-bold">${solution.objective}</p>
                </div>
            </div>
            <div class="bg-white p-3 rounded-lg shadow">
                <h4 class="font-bold text-gray-500 text-sm uppercase mb-2">Variable Values</h4>
                ${variablesHtml}
            </div>
        `;
    }

    // Download Results
    if (dlBtn) {
        dlBtn.addEventListener('click', () => {
            if (!outDiv.textContent || outDiv.textContent.includes('Solution output will appear here')) {
                showAlert('No solution to download yet.', 'warning');
                return;
            }

            const blob = new Blob([outDiv.textContent], { type: 'text/plain' });
            const url = URL.createObjectURL(blob);
            const a = document.createElement('a');
            a.href = url;
            a.download = 'lp_solution.txt';
            document.body.appendChild(a);
            a.click();
            document.body.removeChild(a);
            URL.revokeObjectURL(url);

            showAlert('Solution downloaded!', 'success');
        });
    }

    // Reset Solver
    if (resetBtn) {
        resetBtn.addEventListener('click', () => {
            outDiv.innerHTML = '<p class="text-gray-500 italic">Solution output will appear here after running the solver.</p>';
            if (dlBtn) dlBtn.disabled = true;
            document.getElementById('currentSolution').innerHTML = '<p class="text-gray-500 italic">No solution available yet. Please solve a model first.</p>';
        });
    }

    // Sensitivity Analysis
    document.querySelectorAll('.sensitivity-btn').forEach(btn => {
        btn.addEventListener('click', () => {
            const sensOut = document.getElementById('sensitivityOutput');
            if (!outDiv.textContent.includes('=== SOLUTION ===')) {
                showAlert('Please solve the model first before performing sensitivity analysis.', 'warning');
                return;
            }

            const analysisType = btn.dataset.analysis;
            sensOut.innerHTML = `<div class="flex items-center justify-between mb-2">
                <h4 class="font-bold text-gray-700">${btn.textContent.trim()}</h4>
                <span class="text-xs bg-blue-100 text-blue-800 px-2 py-1 rounded">${new Date().toLocaleTimeString()}</span>
            </div>`;

            // Add loading indicator
            const loadingDiv = document.createElement('div');
            loadingDiv.className = 'text-center py-4';
            loadingDiv.innerHTML = '<i class="fas fa-spinner fa-spin text-blue-500 mr-2"></i> Performing analysis...';
            sensOut.appendChild(loadingDiv);

            // Simulate analysis after delay
            setTimeout(() => {
                sensOut.removeChild(loadingDiv);
                sensOut.innerHTML += generateSensitivityAnalysis(analysisType);
                sensOut.scrollTop = sensOut.scrollHeight;
            }, 1000);
        });
    });

    function generateSensitivityAnalysis(type) {
        const analyses = {
            'range-nonbasic': `
                <div class="bg-white p-3 rounded-lg shadow mb-3">
                    <h5 class="font-medium text-gray-700 mb-1">Range for Non-Basic Variable x3</h5>
                    <p>Current coefficient: 3.0</p>
                    <p>Allowable increase: ∞</p>
                    <p>Allowable decrease: 1.5</p>
                    <p class="text-sm text-gray-600 mt-1">Variable remains non-basic in this range</p>
                </div>`,
            'range-basic': `
                <div class="bg-white p-3 rounded-lg shadow mb-3">
                    <h5 class="font-medium text-gray-700 mb-1">Range for Basic Variable x1</h5>
                    <p>Current coefficient: 2.0</p>
                    <p>Allowable increase: 0.5</p>
                    <p>Allowable decrease: 1.0</p>
                    <p class="text-sm text-gray-600 mt-1">Current basis remains optimal in this range</p>
                </div>`,
            'range-rhs': `
                <div class="bg-white p-3 rounded-lg shadow mb-3">
                    <h5 class="font-medium text-gray-700 mb-1">Range for Constraint 1 RHS</h5>
                    <p>Current value: 40</p>
                    <p>Allowable increase: 10</p>
                    <p>Allowable decrease: 5</p>
                    <p class="text-sm text-gray-600 mt-1">Shadow price: 0.5 (valid in this range)</p>
                </div>`,
            'shadow-prices': `
                <div class="bg-white p-3 rounded-lg shadow mb-3">
                    <h5 class="font-medium text-gray-700 mb-2">Shadow Prices</h5>
                    <table class="w-full text-sm">
                        <thead>
                            <tr class="bg-gray-100">
                                <th class="p-2 text-left">Constraint</th>
                                <th class="p-2 text-left">Shadow Price</th>
                            </tr>
                        </thead>
                        <tbody>
                            <tr class="border-b border-gray-200">
                                <td class="p-2">Constraint 1</td>
                                <td class="p-2">0.5</td>
                            </tr>
                            <tr class="border-b border-gray-200">
                                <td class="p-2">Constraint 2</td>
                                <td class="p-2">1.5</td>
                            </tr>
                        </tbody>
                    </table>
                    <p class="text-sm text-gray-600 mt-2">Shadow prices represent the change in objective value per unit increase in RHS</p>
                </div>`
        };

        return analyses[type] || `
            <div class="bg-white p-3 rounded-lg shadow">
                <h5 class="font-medium text-gray-700 mb-1">Analysis Completed</h5>
                <p>Detailed sensitivity analysis for ${type.replace(/-/g, ' ')} would be displayed here.</p>
                <p class="text-sm text-gray-600 mt-1">In a full implementation, this would show actual calculated values.</p>
            </div>`;
    }

    // Alert Notification System
    function showAlert(message, type = 'info') {
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
                <button class="ml-auto -mx-1.5 -my-1.5 p-1.5 rounded-lg inline-flex items-center justify-center h-8 w-8 ${colors[type].replace('bg-', 'bg-opacity-20 ')} hover:bg-opacity-30 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-current">
                    <span class="sr-only">Dismiss</span>
                    <i class="fas fa-times"></i>
                </button>
            </div>
        `;

        document.body.appendChild(alertDiv);

        // Auto-dismiss after 5 seconds
        setTimeout(() => {
            alertDiv.classList.add('translate-x-full', 'opacity-0');
            setTimeout(() => document.body.removeChild(alertDiv), 300);
        }, 5000);

        // Manual dismiss
        alertDiv.querySelector('button').addEventListener('click', () => {
            alertDiv.classList.add('translate-x-full', 'opacity-0');
            setTimeout(() => document.body.removeChild(alertDiv), 300);
        });
    }

    // Initialize first tab as active
    document.querySelector('.tab-btn').click();
};