using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Solver.Engine.Core;

namespace Solver.Engine.IO
{
    public static class ModelParser
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        
        /// <summary>
        /// Tolerant text parser for simple LPs.
        /// Format:
        /// - Objective line: 'max' or 'min' followed by coefficients.
        /// - Constraints: each constraint line has coefficients, relation, and RHS.
        /// - Sign line allows commas/semicolons as delimiters.
        /// </summary>
        public static LpModel Parse(string[] lines)
        {
            if (lines.Length < 2) throw new InvalidOperationException("File must contain objective, constraints, and sign restrictions.");

            int lineIdx = 0;
            var objective = lines[lineIdx++].Trim();
            var toks = objective.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (toks.Length < 2) throw new InvalidOperationException("Objective line must have direction and at least one coefficient.");

            OptimizeDirection dir = toks[0].ToLower() switch
            {
                "max" => OptimizeDirection.Max,
                "min" => OptimizeDirection.Min,
                _ => throw new InvalidOperationException("First token must be 'max' or 'min'.")
            };

            var model = new LpModel(dir);

            // Parse objective coefficients (sign+number tokens)
            var objCoeffs = new List<double>();
            for (int i = 1; i < toks.Length; i++)
                objCoeffs.Add(ParseSignedNumber(toks[i]));

            // We'll create variables now with placeholder names x1..xn (signs will be attached after parsing sign restrictions)
            for (int i = 0; i < objCoeffs.Count; i++)
                model.Variables.Add(new Variable($"x{i + 1}", objCoeffs[i], SignRestriction.Urs)); // temp sign

            // Parse constraints (all lines until the last line which is sign restrictions)
            var constraints = new List<Constraint>();
            while (lineIdx < lines.Length - 1) // keep last for sign restrictions
            {
                Relation rel = Relation.LessOrEqual; 
                double rhs = 0;
                string[] rels = new[] { "<=", ">=", "=" };
                
                var line = lines[lineIdx++].Trim();
                if (string.IsNullOrWhiteSpace(line)) continue;

                var ctoks = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
                // last 2 tokens must be relation + rhs
                if (ctoks.Count < model.NumVars + 2)
                    throw new InvalidOperationException("Constraint line too short.");

                // relation token is the one before last number; but your format places relation immediately before RHS (e.g. <=40)
                // We accept either split or glued (<= 40 or <=40)
                var lastTok = ctoks[^1];
                if (TrySplitRelationRhs(lastTok, out var relParsed, out var rhsParsed))
                {
                    rel = relParsed;
                    rhs = rhsParsed;
                }
                else
                {
                    var relTok = ctoks[^2];
                    rel = ParseRelation(relTok);
                    rhs = ParseNumber(ctoks[^1]);
                    ctoks.RemoveRange(ctoks.Count - 2, 2);
                }

                if (ctoks.Count != model.NumVars)
                    throw new InvalidOperationException("Constraint coefficients count doesn't match number of variables.");

                var coeffs = ctoks.Select(ParseSignedNumber).ToArray();
                constraints.Add(new Constraint(coeffs, rel, rhs));
            }

            model.Constraints.AddRange(constraints);

            // Parse sign restrictions (last non-empty line)
            string last = lines[^1].Trim();
            var srtoks = last.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (srtoks.Length != model.NumVars)
                throw new InvalidOperationException("Sign restriction count must equal number of variables.");

            for (int i = 0; i < srtoks.Length; i++)
                model.Variables[i].Sign = ParseSignRestriction(srtoks[i]);

            return new ParseResult(model);
        }

        private static double ParseSignedNumber(string s)
        {
            return ParseNumber(s);
        }

        private static double ParseNumber(string s)
        {
            if (double.TryParse(s.Replace("+", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var val))
                return val;
            throw new InvalidOperationException($"Invalid number token: {s}");
        }

        private static bool TrySplitRelationRhs(string token, out Relation rel, out double rhs)
        {
            rel = Relation.LessOrEqual; rhs = 0;
            string[] rels = new[] { "<=", ">=", "=" };
            foreach (var r in rels)
            {
                int idx = token.IndexOf(r, StringComparison.Ordinal);
                if (idx >= 0)
                {
                    rel = ParseRelation(r);
                    var rhsStr = token[(idx + r.Length)..];
                    rhs = ParseNumber(rhsStr);
                    return true;
                }
            }
            return false;
        }

        private static Relation ParseRelation(string s) => s switch
        {
            "<=" => Relation.LessOrEqual,
            ">=" => Relation.GreaterOrEqual,
            "=" or "==" => Relation.Equal,
            _ => throw new InvalidOperationException($"Invalid relation: {s}")
        };

        private static SignRestriction ParseSignRestriction(string s) => s.ToLower() switch
        {
            "+" => SignRestriction.Plus,
            "-" => SignRestriction.Minus,
            "urs" => SignRestriction.Urs,
            "int" => SignRestriction.Int,
            "bin" => SignRestriction.Bin,
            _ => throw new InvalidOperationException($"Invalid sign restriction: {s}")
        };
    }
}
