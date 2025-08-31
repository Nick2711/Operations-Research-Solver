using System;
using System.Collections.Generic;
using System.Linq;
using Solver.Engine.Core;     // <<— use the payload we just added
using Solver.Engine.Utils;

namespace Solver.Engine.Sensitivity
{
    public sealed class SensitivityAnalyzer
    {
        private readonly SensitivityPayload _s;
        private readonly double[,] _Binv;

        public SensitivityAnalyzer(SensitivityPayload s)
        {
            _s = s;
            //_Binv = Matrix.Inverse(s.B);
        }

        public Dictionary<int, double> ShadowPrices()
        {
            // y = cB^T B^{-1}
            var y = MulRowMat(_s.cB, _Binv);
            var dict = new Dictionary<int, double>();
            for (int i = 0; i < y.Length; i++) dict[i] = y[i]; // key = constraint index
            return dict;



        }

        public List<(int constraint, double rhs, (double lo, double hi) range)> RhsRanges()
        {
            var xB = MulMatCol(_Binv, _s.b); // current basic solution
            int m = xB.Length;
            var items = new List<(int, double, (double, double))>(m);

            for (int i = 0; i < m; i++)
            {
                double lo = double.NegativeInfinity, hi = double.PositiveInfinity;
                for (int k = 0; k < m; k++)
                {
                    double dir = _Binv[k, i]; // column i of B^{-1}
                    if (Math.Abs(dir) < 1e-12) continue;
                    double bound = -xB[k] / dir;
                    if (dir > 0) lo = Math.Max(lo, bound);
                    else hi = Math.Min(hi, bound);
                }
                items.Add((i, _s.b[i], (lo, hi)));
            }
            return items;
        }

        // helpers
        private static double[] MulRowMat(double[] row, double[,] M)
        {
            int r = M.GetLength(0), c = M.GetLength(1);
            var y = new double[c];
            for (int j = 0; j < c; j++) { double s = 0; for (int i = 0; i < r; i++) s += row[i] * M[i, j]; y[j] = s; }
            return y;
        }
        private static double[] MulMatCol(double[,] M, double[] v)
        {
            int r = M.GetLength(0), c = M.GetLength(1);
            var y = new double[r];
            for (int i = 0; i < r; i++) { double s = 0; for (int j = 0; j < c; j++) s += M[i, j] * v[j]; y[i] = s; }
            return y;
        }
    }
}
