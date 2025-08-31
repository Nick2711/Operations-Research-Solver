using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Solver.Engine.Core
{
    public sealed class FinalTableauSnapshot
    {
        // final tableau (m+1) x (n+1)
        public double[,] Tableau { get; init; } = new double[0,0];
        public int[] Basis { get; init; } = Array.Empty<int>();
        public int M { get; init; }
        public int N { get; init; }            // number of columns excluding RHS (n)
        public double[,] A { get; init; } = new double[0, 0];
        public double[] B { get; init; } = Array.Empty<double>();
        public double[] C { get; init; } = Array.Empty<double>();
        public bool IsMax { get; init; } = true;
    }

    // A tiny global holder for the last solved tableau (simple and effective for dev)
    public static class TableauHolder
    {
        // Note: set from solver when an optimal solution is reached
        public static FinalTableauSnapshot? LastSnapshot { get; set; }
    }
}
