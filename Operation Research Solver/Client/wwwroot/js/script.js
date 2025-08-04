// wwwroot/js/script.js

//  ────────────────────────────────────────────────────
//  Expose a single entry point for Blazor’s JS interop
//  ────────────────────────────────────────────────────
window.initializeApp = () => {
    // =============================
    // 1) TAB SWITCHING
    // =============================
    document.querySelectorAll('.tab-btn').forEach(btn => {
        btn.addEventListener('click', () => {
            document.querySelectorAll('.tab-btn').forEach(b =>
                b.classList.remove('active', 'border-blue-600', 'text-blue-600')
            );
            document.querySelectorAll('.tab-content').forEach(c =>
                c.classList.remove('active')
            );
            btn.classList.add('active', 'border-blue-600', 'text-blue-600');
            const target = btn.dataset.tab;
            document.getElementById(target)?.classList.add('active');
        });
    });

    // =============================
    // 2) FILE UPLOAD & PREVIEW
    // =============================
    const fileInput = document.getElementById('modelFile');
    if (fileInput) {
        const fileInfo = document.getElementById('fileInfo');
        const fileName = document.getElementById('fileName');
        const filePreviewSection = document.getElementById('filePreviewSection');
        const filePreview = document.getElementById('filePreview');
        const modelSummary = document.getElementById('modelSummary');

        fileInput.addEventListener('change', e => {
            const file = e.target.files?.[0];
            if (!file) return;

            fileName.textContent = file.name;
            fileInfo.classList.remove('hidden');

            const reader = new FileReader();
            reader.onload = ev => {
                filePreview.textContent = ev.target.result;
                filePreviewSection.classList.remove('hidden');
                updateModelSummary(ev.target.result);
            };
            reader.readAsText(file);
        });

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

            modelSummary.innerHTML = `
        <p><span class="font-medium">Problem Type:</span> ${type}</p>
        <p><span class="font-medium">Variables:</span> ${vars}</p>
        <p><span class="font-medium">Constraints:</span> ${cons}</p>
        <p class="mt-3"><span class="font-medium">Restrictions:</span> ${lines[lines.length - 1].trim()}</p>
      `;
        }
    }

    // =============================
    // 3) SOLVER LOGIC (RUN / DOWNLOAD / RESET)
    // =============================
    const runBtn = document.getElementById('runSolverBtn');
    const outDiv = document.getElementById('solverOutput');
    const dlBtn = document.getElementById('downloadResultsBtn');
    const resetBtn = document.getElementById('resetSolverBtn');

    if (runBtn) {
        runBtn.addEventListener('click', () => {
            if (!fileInput?.files?.length) {
                outDiv.innerHTML = '<p class="text-red-500">Upload a model file first.</p>';
                return;
            }
            runBtn.disabled = true;
            runBtn.innerHTML = '<i class="fas fa-spinner fa-spin mr-2"></i> Solving...';

            const form = new FormData();
            form.append('model', fileInput.files[0]);
            form.append('algorithm', document.getElementById('algorithm')?.value);

            fetch('/api/solve', { method: 'POST', body: form })
                .then(r => r.text())
                .then(txt => {
                    outDiv.textContent = txt;
                    runBtn.disabled = false;
                    runBtn.innerHTML = '<i class="fas fa-play-circle mr-2"></i> Run Solver';
                    if (dlBtn) dlBtn.disabled = false;
                    document.getElementById('currentSolution').innerHTML = `
            <p><span class="font-medium">Objective Value:</span> (see output)</p>
            <p class="mt-2"><span class="font-medium">Status:</span> (see output)</p>`;
                })
                .catch(err => {
                    console.error(err);
                    outDiv.textContent = '❌ Error contacting solver.';
                    runBtn.disabled = false;
                    runBtn.innerHTML = '<i class="fas fa-play-circle mr-2"></i> Run Solver';
                });
        });
    }

    if (dlBtn) {
        dlBtn.addEventListener('click', () => {
            const blob = new Blob([outDiv.textContent], { type: 'text/plain' });
            const url = URL.createObjectURL(blob);
            const a = document.createElement('a');
            a.href = url;
            a.download = 'lp_solution.txt';
            document.body.appendChild(a);
            a.click();
            document.body.removeChild(a);
            URL.revokeObjectURL(url);
        });
    }

    if (resetBtn) {
        resetBtn.addEventListener('click', () => {
            outDiv.innerHTML =
                '<p class="text-gray-500 italic">Solution output will appear here.</p>';
            if (dlBtn) dlBtn.disabled = true;
            document.getElementById('currentSolution').innerHTML =
                '<p class="text-gray-500 italic">No solution yet.</p>';
        });
    }

    // =============================
    // 4) SENSITIVITY ANALYSIS STUBS
    // =============================
    document.querySelectorAll('.sensitivity-btn').forEach(btn => {
        btn.addEventListener('click', () => {
            const sensOut = document.getElementById('sensitivityOutput');
            if (!outDiv.textContent.includes('=== Optimal Solution ===')) {
                return alert('Solve first before doing sensitivity analysis.');
            }
            sensOut.textContent = `=== ${btn.textContent.trim()} ===\n\n…`;
        });
    });
};
