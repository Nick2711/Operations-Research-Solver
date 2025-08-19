using System;

namespace Solver.Engine.Core
{
    /// <summary>
    /// Canonical form of an LP as produced by Canonicalizer.
    /// Backward compatible with earlier engine usage (A,b,c,z0,BasicIdx,NonBasicIdx),
    /// plus additional Phase-I metadata for future steps.
    /// </summary>
    public sealed class CanonicalForm
    {
        // ===== Phase II fields (existing usage) =====
        public double[,] A { get; init; } = new double[0, 0];   // m x n
        public double[] b { get; init; } = Array.Empty<double>();
        public double[] c { get; init; } = Array.Empty<double>(); // length n
        public double z0 { get; init; }                         // usually 0
        public int[] BasicIdx { get; init; } = Array.Empty<int>(); // length m
        public int[] NonBasicIdx { get; init; } = Array.Empty<int>();

        // ===== Phase I scaffolding / metadata (new) =====
        /// <summary>True if '=' or '≥' rows required artificials during canonicalization.</summary>
        public bool PhaseIRequired { get; init; }

        /// <summary>Phase I objective coefficients (length = cols of A). 1 for artificials, 0 otherwise.</summary>
        public double[] cPhaseI { get; init; } = Array.Empty<double>();

        /// <summary>Indices (column positions) of artificial variables in A.</summary>
        public int[] ArtificialIdx { get; init; } = Array.Empty<int>();

        /// <summary>Indices of slack variables (+1 on their row) added for '≤' constraints.</summary>
        public int[] SlackIdx { get; init; } = Array.Empty<int>();

        /// <summary>Indices of surplus variables (−1 on their row) added for '≥' constraints.</summary>
        public int[] SurplusIdx { get; init; } = Array.Empty<int>();

        /// <summary>Total numbers for quick reference.</summary>
        public int NumRows { get; init; }
        public int NumCols { get; init; }
        public int NumVarsOriginal { get; init; }  // decision variables before adding slacks/surplus/artificial
        public int NumSlack { get; init; }
        public int NumArtificial { get; init; }

        /// <summary>Names for rows/columns and simple mappings.</summary>
        public NameMap Map { get; init; } = NameMap.Empty;

        public CanonicalForm() { }

        /// <summary>
        /// Back-compat constructor used by existing code paths.
        /// </summary>
        public CanonicalForm(
            double[,] A,
            double[] b,
            double[] c,
            double z0,
            int[] basicIdx,
            int[] nonBasicIdx)
        {
            this.A = A ?? new double[0, 0];
            this.b = b ?? Array.Empty<double>();
            this.c = c ?? Array.Empty<double>();
            this.z0 = z0;
            this.BasicIdx = basicIdx ?? Array.Empty<int>();
            this.NonBasicIdx = nonBasicIdx ?? Array.Empty<int>();

            // Derive simple counts
            this.NumRows = this.A.GetLength(0);
            this.NumCols = this.A.GetLength(1);

            // Phase I defaults (not used yet; Step 7 will populate)
            this.PhaseIRequired = false;
            this.cPhaseI = Array.Empty<double>();
            this.ArtificialIdx = Array.Empty<int>();
            this.SlackIdx = Array.Empty<int>();
            this.SurplusIdx = Array.Empty<int>();
            this.NumVarsOriginal = 0;
            this.NumSlack = 0;
            this.NumArtificial = 0;
            this.Map = NameMap.Empty;
        }
    }
}
