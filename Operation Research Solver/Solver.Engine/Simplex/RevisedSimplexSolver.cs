using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Solver.Engine.Core;
using Solver.Engine.IO;
using static Solver.Engine.Core.Numeric;

namespace Solver.Engine.Simplex
{
    public sealed class RevisedSimplexSolver : ISolver
    {
        public string Name => "Revised Primal Simplex (Stub)";

        private const int MAX_ITERS = 10000;
        private static readonly ITableauPrinter Printer = new DefaultTableauPrinter();

        public SolverResult Solve(LpModel model)
            => new(false, 0, Array.Empty<double>(), new List<string> { "Revised Simplex not yet implemented." });
    }
}
