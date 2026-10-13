namespace AutoPrompt.Services;

internal sealed record AudioInputDeviceOption(string Id, int DeviceNumber, string DisplayName)
{
    public const string SystemDefaultId = "system-default";

    public override string ToString() => DisplayName;
}
