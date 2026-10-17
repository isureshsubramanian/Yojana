using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Claims;

/// <summary>The P4 formula implementation. Measured and pro-rata formulas remain behind the port for MOD-22.</summary>
public sealed class FullSettlementFormula : ISettlementFormulaPort
{
    public Task<SettlementFormulaResult> CalculateAsync(SettlementFormulaQuestion question, CancellationToken cancellationToken)
    {
        if (!string.Equals(question.Formula, "full", StringComparison.Ordinal))
        {
            throw new CommandRefusedException("settlement_formula_not_available",
                $"Settlement formula '{question.Formula}' is not available; P4 supports full only.",
                "configure the policy with settlement_formula full, or wait for the measured formula module");
        }

        var amount = decimal.Round(question.RequestedAmount * question.CoveragePct / 100m, 2, MidpointRounding.AwayFromZero);
        return Task.FromResult(new SettlementFormulaResult(amount));
    }
}

