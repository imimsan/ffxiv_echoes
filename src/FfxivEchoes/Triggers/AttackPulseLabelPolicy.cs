namespace FfxivEchoes.Triggers;

public static class AttackPulseLabelPolicy
{
    public static string Format(string prefix, string? label)
    {
        if (!string.IsNullOrWhiteSpace(prefix))
        {
            return prefix.Trim();
        }

        return "Attack";
    }
}
