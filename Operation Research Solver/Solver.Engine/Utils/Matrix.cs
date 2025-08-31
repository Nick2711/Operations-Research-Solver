using System;

namespace Solver.Engine.Utils
{
    public static class Matrix
    {
        // Invert a square matrix with Gauss-Jordan elimination
        public static double[,] Inverse(double[,] A)
        {
            int n = A.GetLength(0);
            if (n != A.GetLength(1))
                throw new ArgumentException("Matrix must be square to invert.");

            // Copy A into aug
            var aug = new double[n, 2 * n];
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++)
                    aug[i, j] = A[i, j];
                aug[i, n + i] = 1.0; // identity on the right
            }

            // Forward elimination
            for (int i = 0; i < n; i++)
            {
                // Find pivot
                double pivot = aug[i, i];
                if (Math.Abs(pivot) < 1e-12)
                {
                    // try to swap with a lower row
                    int swap = i + 1;
                    while (swap < n && Math.Abs(aug[swap, i]) < 1e-12) swap++;
                    if (swap == n) throw new InvalidOperationException("Matrix is singular.");
                    for (int j = 0; j < 2 * n; j++)
                    {
                        double tmp = aug[i, j];
                        aug[i, j] = aug[swap, j];
                        aug[swap, j] = tmp;
                    }
                    pivot = aug[i, i];
                }

                // Normalize row
                for (int j = 0; j < 2 * n; j++) aug[i, j] /= pivot;

                // Eliminate other rows
                for (int k = 0; k < n; k++)
                {
                    if (k == i) continue;
                    double factor = aug[k, i];
                    if (Math.Abs(factor) < 1e-12) continue;
                    for (int j = 0; j < 2 * n; j++)
                        aug[k, j] -= factor * aug[i, j];
                }
            }

            // Extract inverse
            var inv = new double[n, n];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    inv[i, j] = aug[i, n + j];

            return inv;
        }
    }
}


