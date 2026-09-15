namespace BannerlordEnvironmentManager.Core.Instances;

public enum DownloadStage
{
    Starting,
    AwaitingSignIn,
    Downloading,
    Done,
    Failed
}

public sealed record DownloadProgress(DownloadStage Stage, string Message, string QrBlock, int Percent);
