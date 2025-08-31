using Microsoft.AspNetCore.Mvc;
using Solver.Engine.Sensitivity;
using Solver.Engine.Simplex;   // for SolverResult
using Solver.Engine.Core;      // for SensitivityPayload

namespace OperationResearchSolver.Server.Controllers
{
    [ApiController]
    [Route("api/sensitivity")]
    public class SensitivityController : ControllerBase
    {
        private readonly ILastSolveCache _cache;

        public SensitivityController(ILastSolveCache cache)
        {
            _cache = cache;
        }

        private SensitivityAnalyzer? Analyzer()
        {
            var payload = _cache.LastResult?.Sensitivity;
            if (payload == null) return null;
            return new SensitivityAnalyzer(payload);
        }

        [HttpGet("shadow-prices")]
        public IActionResult ShadowPrices()
        {
            var an = Analyzer();
            if (an == null) return NotFound("No solved model in memory.");
            return Ok(an.ShadowPrices());
        }

        [HttpGet("rhs-ranges")]
        public IActionResult RhsRanges()
        {
            var an = Analyzer();
            if (an == null) return NotFound("No solved model in memory.");

            var items = an.RhsRanges()
              .Select(t => new {
                  constraint = t.constraint,
                  currentRhs = t.rhs,
                  lower = t.range.lo,
                  upper = t.range.hi
              });
            return Ok(items);

        }

    }
}
