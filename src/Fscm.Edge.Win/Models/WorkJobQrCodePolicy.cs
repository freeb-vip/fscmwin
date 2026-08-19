namespace Fscm.Edge.Win.Models;

public static class WorkJobQrCodePolicy
{
    public const string EndPayload = "FSCM_JOB:END";

    public static string BuildStartPayload(string operationTypeCode)
    {
        string code = operationTypeCode.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(code) || code == "END")
        {
            throw new ArgumentException("业务类型编码无效。", nameof(operationTypeCode));
        }

        return $"FSCM_JOB:{code}";
    }
}