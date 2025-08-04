// =============================
// FRONTEND UI LOGIC (KEEP IN JS)
// =============================

// Tab switching functionality
document.querySelectorAll('.tab-btn').forEach(btn => {
    btn.addEventListener('click', () => {
        document.querySelectorAll('.tab-btn').forEach(b => b.classList.remove('active', 'border-blue-600', 'text-blue-600'));
        document.querySelectorAll('.tab-content').forEach(tab => tab.classList.remove('active'));
        btn.classList.add('active', 'border-blue-600', 'text-blue-600');
        const tabId = btn.getAttribute('data-tab');
        document.getElementById(tabId).classList.add('active');
    });
});

// File upload preview logic (KEEP IN JS)
const fileInput = document.getElementById('modelFile');
const fileInfo = document.getElementById('fileInfo');
const fileName = document.getElementById('fileName');
const filePreviewSection = document.getElementById('filePreviewSection');
const filePreview = document.getElementById('filePreview');
const modelSummary = document.getElementById('modelSummary');

fileInput.addEventListener('change', (e) => {
    const file = e.target.files[0];
    if (file) {
        fileName.textContent = file.name;
        fileInfo.classList.remove('hidden');

        const reader = new FileReader();
        reader.onload = (event) => {
            filePreview.textContent = event.target.result;
            filePreviewSection.classList.remove('hidden');
            updateModelSummary(event.target.result);
        };
        reader.readAsText(file);
    }
});

function updateModelSummary(content) {
    const lines = content.split('\n');
    if (lines.length < 2) {
        modelSummary.innerHTML = '<p class="text-red-500">Invalid file format. Please check the example.</p>';
        return;
    }

    const firstLine = lines[0].trim().split(/\s+/);
    const problemType = firstLine[0];
    const numVars = firstLine.length - 1;
    const numConstraints = lines.length - 2;

    let summaryHTML = `
        <p><span class="font-medium">Problem Type:</span> ${problemType === 'max' ? 'Maximization' : 'Minimization'}</p>
        <p><span class="font-medium">Variables:</span> ${numVars}</p>
        <p><span class="font-medium">Constraints:</span> ${numConstraints}</p>
        <p class="mt-3"><span class="font-medium">Variable Restrictions:</span> ${lines[lines.length-1].trim()}</p>
    `;

    modelSummary.innerHTML = summaryHTML;
}

// =============================
// SOLVER LOGIC (REPLACE SIMULATION WITH C#)
// =============================

const runSolverBtn = document.getElementById('runSolverBtn');
const solverOutput = document.getElementById('solverOutput');
const downloadResultsBtn = document.getElementById('downloadResultsBtn');
const resetSolverBtn = document.getElementById('resetSolverBtn');

runSolverBtn.addEventListener('click', () => {
    if (!fileInput.files[0]) {
        solverOutput.innerHTML = '<p class="text-red-500">Please upload a model file first.</p>';
        return;
    }

    runSolverBtn.disabled = true;
    runSolverBtn.innerHTML = '<i class="fas fa-spinner fa-spin mr-2"></i> Solving...';

    const formData = new FormData();
    formData.append("model", fileInput.files[0]);
    formData.append("algorithm", document.getElementById('algorithm').value);

    // CALL OUR C# BACKEND HERE (API endpoint e.g., /api/solve)
    fetch("http://localhost:5000/api/solve", {
        method: "POST",
        body: formData
    })
    .then(response => response.text())
    .then(result => {
        // need to change to our C# logic instead of simulated output
        solverOutput.textContent = result;
        runSolverBtn.disabled = false;
        runSolverBtn.innerHTML = '<i class="fas fa-play-circle mr-2"></i> Run Solver';
        downloadResultsBtn.disabled = false;

        // we could parse the response if it's JSON and update the solution fields dynamically
        document.getElementById('currentSolution').innerHTML = `
            <p><span class="font-medium">Objective Value:</span> (see output)</p>
            <p class="mt-2"><span class="font-medium">Status:</span> (see output)</p>
        `;
    })
    .catch(error => {
        solverOutput.textContent = '❌ Error: Could not contact the solver backend.';
        console.error(error);
        runSolverBtn.disabled = false;
        runSolverBtn.innerHTML = '<i class="fas fa-play-circle mr-2"></i> Run Solver';
    });
});

// Download Results (CAN STAY IN JS OR RETURN FILE FROM C#)
downloadResultsBtn.addEventListener('click', () => {
    const content = solverOutput.textContent;
    const blob = new Blob([content], { type: 'text/plain' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = 'lp_solution.txt';
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    URL.revokeObjectURL(url);
});

resetSolverBtn.addEventListener('click', () => {
    solverOutput.innerHTML = '<p class="text-gray-500 italic">Solution output will appear here after running the solver.</p>';
    downloadResultsBtn.disabled = true;
    document.getElementById('currentSolution').innerHTML = '<p class="text-gray-500 italic">No solution available yet. Please solve a model first.</p>';
});

// =============================
// SENSITIVITY ANALYSIS (C# BACKEND)
// =============================

//  call our C# backend 
document.querySelectorAll('.sensitivity-btn').forEach(btn => {
    btn.addEventListener('click', () => {
        if (solverOutput.textContent.includes('=== Optimal Solution ===')) {
            const sensitivityOutput = document.getElementById('sensitivityOutput');
            const analysisType = btn.textContent.trim();

            let result = `=== ${analysisType} ===\n\n`;

            // Replace this block with a C# backend call when real sensitivity analysis is available
            if (analysisType.includes('Range')) {
                result += `Allowable range for x₁: [2.5, 10.0]\n`;
                result += `Allowable range for x₂: [5.0, 15.0]\n`;
            } else if (analysisType.includes('RHS')) {
                result += `Current RHS values: [20, 30]\n`;
                result += `Allowable increase for constraint 1: +5.0\n`;
                result += `Allowable decrease for constraint 1: -2.5\n`;
            } else if (analysisType.includes('Shadow')) {
                result += `Shadow price for constraint 1: 1.0\n`;
                result += `Shadow price for constraint 2: 1.5\n`;
            } else {
                result += `Analysis for ${analysisType} would be performed here.\n`;
                result += `This would show how changes affect the optimal solution.\n`;
            }

            sensitivityOutput.textContent = result;
        } else {
            alert('Please solve a model first before performing sensitivity analysis.');
        }
    });
});
