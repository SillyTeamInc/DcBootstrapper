namespace DcBootstrapper.Utils;

public class NotifyUtil
{
    public static void Notify(string title, string body)
    {
        ProcessUtil.RunProcess("notify-send", $"-u normal \"{title}\" \"{body}\" --app-name \"Discord {ConfigManager.CurrentConfig?.ProperBranch} Bootstrapper {Updater.GetCurrentTag()}\"", notify: false);
    }
}
