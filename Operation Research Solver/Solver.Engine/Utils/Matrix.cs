namespace Solver.Engine.Utils
{
    public static class Matrix
    {
        // In-place Gauss–Jordan inversion. Throws if singular.
        public static double[,] Invert(double[,] a)
        {
            int n = a.GetLength(0);
            if (n != a.GetLength(1)) throw new ArgumentException("Matrix must be square.");
            var aug = new double[n, 2 * n];

            // build [A | I]
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++) aug[i, j] = a[i, j];
                aug[i, n + i] = 1.0;
            }

            // pivot
            for (int col = 0; col < n; col++)
            {
                // find pivot
                int piv = col;
                double best = Math.Abs(aug[piv, col]);
                for (int r = col + 1; r < n; r++)
                {
                    double v = Math.Abs(aug[r, col]);
                    if (v > best) { best = v; piv = r; }
                }
                if (best < 1e-12) throw new InvalidOperationException("Matrix is singular.");

                // swap
                if (piv != col)
                    for (int c = 0; c < 2 * n; c++)
                        (aug[col, c], aug[piv, c]) = (aug[piv, c], aug[col, c]);

                // scale to 1
                double diag = aug[col, col];
                for (int c = 0; c < 2 * n; c++) aug[col, c] /= diag;

                // eliminate other rows
                for (int r = 0; r < n; r++)
                {
                    if (r == col) continue;
                    double f = aug[r, col];
                    if (Math.Abs(f) < 1e-18) continue;
                    for (int c = 0; c < 2 * n; c++) aug[r, c] -= f * aug[col, c];
                }
            }

            // extract right half
            var inv = new double[n, n];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    inv[i, j] = aug[i, n + j];

            return inv;
        }

        public static double[] Multiply(double[,] M, double[] v)
        {
            int m = M.GetLength(0), n = M.GetLength(1);
            if (n != v.Length) throw new ArgumentException("Dim mismatch");
            var res = new double[m];
            for (int i = 0; i < m; i++)
            {
                double s = 0; for (int j = 0; j < n; j++) s += M[i, j] * v[j];
                res[i] = s;
            }
            return res;
        }

        public static double[,] ExtractColumns(double[,] A, int[] cols)
        {
            int m = A.GetLength(0), nB = cols.Length;
            var B = new double[m, nB];
            for (int j = 0; j < nB; j++)
            {
                int src = cols[j];
                for (int i = 0; i < m; i++) B[i, j] = A[i, src];
            }
            return B;
        }
    }
}
