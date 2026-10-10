using System.Linq;

/// <summary>
/// The outcome of one engine check of the smoke test (SmokeMain.Step): <see cref="Failure"/> is null when every condition
/// held. Top-level so that the checks of the samples under Samples/ can return one.
/// </summary>
public readonly struct Verdict
{
    public readonly string Failure;
    public readonly string Detail;

    public Verdict(string failure, string detail)
    {
        Failure = failure;
        Detail = detail;
    }

    /// <summary>Passes when every check holds; otherwise the failure lists the ones that did not.</summary>
    public static Verdict Of(string detail, params (bool ok, string what)[] checks)
    {
        var failed = checks.Where(c => !c.ok).Select(c => c.what).ToArray();
        return new Verdict(failed.Length == 0 ? null : "failed: " + string.Join("; ", failed), detail);
    }
}
