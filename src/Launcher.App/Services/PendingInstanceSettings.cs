namespace Launcher.App.Services;

/// <summary>
/// Carries a one-shot request to open an instance's settings after navigation.
/// </summary>
/// <remarks>
/// The launch page's gear needs the instances page to be on screen first, but the
/// instance itself lives in the instances list, which the launch page does not
/// own. Rather than duplicating instance data across pages, the launch page
/// records its stable id here, navigates, and the instances page picks the request
/// up as it loads. The request is cleared on read so it fires exactly once.
/// </remarks>
public sealed class PendingInstanceSettings
{
    private string? _instanceId;

    /// <summary>
    /// Records the instance whose settings should open once the instances page is
    /// shown.
    /// </summary>
    public void Request(string instanceId) => _instanceId = instanceId;

    /// <summary>
    /// Takes the pending request, if any, and clears it.
    /// </summary>
    public string? Take()
    {
        var id = _instanceId;
        _instanceId = null;
        return id;
    }
}
