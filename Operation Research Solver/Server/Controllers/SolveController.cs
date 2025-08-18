using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Shared.Models;

using Solver.Engine.IO;
using Solver.Engine.Simplex;
using Solver.Engine.Core;

namespace Server.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public sealed class SolveController : ControllerBase
    {
        [HttpPost]
        public ActionResult<SolveResponse> Post([FromBody] SolveRequest req, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();

            if (req is null)
                return BadRequest(new SolveResponse { OutputText = "Error: Request body is null." });

            if (string.IsNullOrWhiteSpace(req.ModelText))
                return BadRequest(new SolveResponse { OutputText = "Error: ModelText is empty." });

            // Time limit
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (req.Settings != null && req.Settings.TimeLimitSeconds > 0)
                cts.CancelAfter(TimeSpan.FromSeconds(req.Settings.TimeLimitSeconds));
            var token = cts.Token;

            try
            {
                // Parse text -> model (parser owns normalization/cleaning)
                var raw = req.ModelText ?? string.Empty;
                var model = ModelParser.Parse(raw);

                if (model == null)
                {
                    sw.Stop();
                    return Ok(new SolveResponse
                    {
                        Success = false,
                        OutputText = "Failed to parse model.",
                        RuntimeMs = sw.ElapsedMilliseconds
                    });
                }

                // Pick solver from enum
                ISolver solver = req.Algorithm switch
                {
                    Algorithm.PrimalSimplex => new PrimalSimplexSolver(),
                    Algorithm.RevisedSimplex => new RevisedSimplexSolver(),
                    _ => new PrimalSimplexSolver()
                };

                token.ThrowIfCancellationRequested();

                var result = solver.Solve(model);

                token.ThrowIfCancellationRequested();

                // Build output text
                var outText = new StringBuilder();
                outText.AppendLine($"Algorithm: {solver.Name}");

                if (result.Log != null)
                {
                    var linesToShow = (req.Settings?.Verbose ?? true)
                        ? result.Log
                        : result.Log.Take(10);
                    foreach (var l in linesToShow)
                        outText.AppendLine(l);
                }

                // Convert objective back to the user's original sense (we solve Max internally)
                double userObjective = result.Success
                    ? (model.Direction == OptimizeDirection.Max
                        ? result.ObjectiveValue
                        : -result.ObjectiveValue)
                    : 0.0;

                outText.AppendLine();
                if (result.Success)
                {
                    outText.AppendLine($"SUCCESS — Objective: {userObjective.ToString("0.###", CultureInfo.InvariantCulture)}");

                    if (result.X is { Length: > 0 })
                    {
                        var sol = string.Join(", ",
                            result.X.Select((v, i) => $"x{i + 1}={v.ToString("0.###", CultureInfo.InvariantCulture)}"));
                        outText.AppendLine("Solution: " + sol);
                    }
                }
                else
                {
                    outText.AppendLine(result.Unbounded ? "FAILED — Unbounded."
                                   : result.Infeasible ? "FAILED — Infeasible (Phase I needed)."
                                   : "FAILED — Unknown.");
                }

                sw.Stop();

                return Ok(new SolveResponse
                {
                    Success = result.Success,
                    Unbounded = result.Unbounded,
                    Infeasible = result.Infeasible,
                    Objective = result.Success ? Math.Round(userObjective, 3) : (double?)null,
                    SolutionSummary = result.Success && result.X is { Length: > 0 }
                        ? string.Join(", ", result.X.Select((v, i) => $"x{i + 1}={v.ToString("0.###", CultureInfo.InvariantCulture)}"))
                        : null,
                    OutputText = outText.ToString(),
                    RuntimeMs = sw.ElapsedMilliseconds
                });
            }
            catch (OperationCanceledException)
            {
                sw.Stop();
                return StatusCode(408, new SolveResponse { OutputText = "Timed out.", RuntimeMs = sw.ElapsedMilliseconds });
            }
            catch (Exception ex)
            {
                sw.Stop();
                return BadRequest(new SolveResponse
                {
                    OutputText = $"Error: {ex.Message}",
                    RuntimeMs = sw.ElapsedMilliseconds
                });
            }
        }
    }
}
