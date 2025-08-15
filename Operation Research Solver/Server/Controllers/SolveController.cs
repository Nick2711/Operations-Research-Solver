using Microsoft.AspNetCore.Mvc;
using Shared.Models;
using Solver.Engine.Core;
using Solver.Engine.IO;
using Solver.Engine.Simplex;
using System.Diagnostics;

namespace Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SolveController : ControllerBase
{
    [HttpPost]
    public ActionResult<SolveResponse> Post([FromBody] SolveRequest req, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        if (string.IsNullOrWhiteSpace(req.ModelText))
            return BadRequest(new SolveResponse { OutputText = "Error: ModelText is empty." });

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (req.Settings.TimeLimitSeconds > 0)
            cts.CancelAfter(TimeSpan.FromSeconds(req.Settings.TimeLimitSeconds));
        var token = cts.Token;

        try
        {
            var lines = req.ModelText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var parsed = ModelParser.Parse(lines);

            ISolver solver = req.Algorithm switch
            {
                "Primal Simplex" => new PrimalSimplexSolver(),
                "Revised Simplex" => new RevisedSimplexSolverStub(), // replace with real impl later
                // "B&B Simplex"   => new BranchAndBoundSimplex(...),
                // "Gomory Cuts"   => new GomoryCuttingPlane(...),
                // "Knapsack B&B"  => new KnapsackBranchAndBound(...),
                _ => new PrimalSimplexSolver()
            };

            token.ThrowIfCancellationRequested();
            var res = solver.Solve(parsed.Model);
            token.ThrowIfCancellationRequested();

            var logs = new List<string> { $"Algorithm: {solver.Name}" };
            logs.AddRange(req.Settings.Verbose ? res.Log : res.Log.Take(10));
            logs.Add(res.Success
                ? $"SUCCESS — Objective: {res.ObjectiveValue:0.###}\nSolution: {string.Join(", ", res.X.Select((v, i) => $"x{i + 1}={v:0.###}"))}"
                : res.Unbounded ? "FAILED — Unbounded."
                : res.Infeasible ? "FAILED — Infeasible or needs Phase I."
                : "FAILED — Unknown.");

            sw.Stop();

            return Ok(new SolveResponse
            {
                Success = res.Success,
                Unbounded = res.Unbounded,
                Infeasible = res.Infeasible,
                Objective = res.Success ? res.ObjectiveValue : null,
                SolutionSummary = res.Success ? string.Join(", ", res.X.Select((v, i) => $"x{i + 1}={v:0.###}")) : null,
                OutputText = string.Join(Environment.NewLine, logs),
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
            return BadRequest(new SolveResponse { OutputText = $"Error: {ex.Message}", RuntimeMs = sw.ElapsedMilliseconds });
        }
    }
}
