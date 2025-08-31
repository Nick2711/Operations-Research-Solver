using System;
using Solver.Engine.Simplex;  // SolverResult

namespace Operation_Research_Solver.Server.Services
{
    /// <summary>
    /// Lightweight snapshot the UI/controllers can safely use without depending on engine internals.
    /// </summary>
    public sealed class SolveSnapshot
    {
        public bool IsMaximization { get; set; }

        // Objective coefficients (c) as parsed from the text model
        public double[] ObjectiveCoefficients { get; set; } = Array.Empty<double>();

        // Optional helper arrays if your solver populates them elsewhere
        public double[] ReducedCosts { get; set; } = Array.Empty<double>();
        public int[] BasicIndices { get; set; } = Array.Empty<int>();
        public int[] NonBasicIndices { get; set; } = Array.Empty<int>();
        public string[] VariableLabels { get; set; } = Array.Empty<string>();

        // Handy strings for the UI
        public string SolutionSummary { get; set; } = string.Empty;
        public string OutputText { get; set; } = string.Empty;
    }

    /// <summary>
    /// Cross-request cache of the latest solve. Registered as a singleton in Program.cs.
    /// </summary>
    public interface ILastSolveCache
    {
        // Raw engine result from the latest successful solve (null if none/failed)
        SolverResult? LastResult { get; set; }

        // Lightweight snapshot for UI (safe to read from anywhere)
        SolveSnapshot Snapshot { get; set; }

        // The raw model text that produced LastResult / Snapshot (used by sensitivity, dual, edits, etc.)
        string? LastModelText { get; set; }

        void Clear();
    }

    public sealed class LastSolveCache : ILastSolveCache
    {
        public SolverResult? LastResult { get; set; }
        public SolveSnapshot Snapshot { get; set; } = new SolveSnapshot();
        public string? LastModelText { get; set; }

        public void Clear()
        {
            LastResult = null;
            Snapshot = new SolveSnapshot();
            LastModelText = null;
        }
    }
}
