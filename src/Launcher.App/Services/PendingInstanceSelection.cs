namespace Launcher.App.Services;

/// <summary>
/// Carries a one-shot request to select a specific instance after navigation.
/// Requests use the stable instance identity instead of a display name so
/// same-named versions in different folders cannot be confused.
/// </summary>
public sealed class PendingInstanceSelection
{
    private string? _instanceId;

    public void Request(string instanceId) => _instanceId = instanceId;

    public string? Take()
    {
        var id = _instanceId;
        _instanceId = null;
        return id;
    }
}
