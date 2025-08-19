using System;
using static Solver.Engine.Core.Numeric;

namespace Solver.Engine.Simplex
{
    /// <summary>
    /// Shared elementary row operations for simplex tableaus.
    /// Reused by Primal (tableau), Phase I, and any tableau-based routines.
    /// </summary>
    public static class TableauOps
    {
        public static void AddScaledRow(double[,] T, int target, int src, double factor, int width)
        {
            for (int j = 0; j < width; j++) T[target, j] += factor * T[src, j];
        }

        public static void ScaleRow(double[,] T, int row, double factor, int width)
        {
            for (int j = 0; j < width; j++) T[row, j] *= factor;
        }

        public static void Pivot(double[,] T, int prow, int pcol, int width)
        {
            double piv = T[prow, pcol];
            if (Math.Abs(piv) < EPS) throw new InvalidOperationException("Pivot is ~0.");

            // Normalize pivot row
            ScaleRow(T, prow, 1.0 / piv, width);

            // Eliminate pivot column
            int height = T.GetLength(0);
            for (int r = 0; r < height; r++)
            {
                if (r == prow) continue;
                double k = T[r, pcol];
                if (Math.Abs(k) > EPS) AddScaledRow(T, r, prow, -k, width);
            }
        }
    }
}
